using DotnetVM.Devices;
using DotnetVM.IL;
using DotnetVM.Host;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using System.Globalization;

namespace DotnetVM.Runtime.Execution;

public sealed partial class Interpreter {
    /// <summary>文字列プール (VM ファサードから参照用)。</summary>
    public VmStringPool Strings => _services.Strings;

    /// <summary>型ローダ (ホスト API が intrinsic ファサード型を解決するのに使う)。</summary>
    public TypeLoader Loader => _services.Loader;

    /// <summary>VM ヒープ (アロケーション計上の唯一の入口。ホスト API のインスタンス生成からも使う)。</summary>
    public VmHeap Heap => _services.Heap;

    internal GcStatistics CollectGarbage() {
        _shared.ThrowIfDisposed();
        using (_coordinator.StopTheWorld())
            return _heap.Collect();
    }

    /// <summary>値を指定の型としてボックス化する (box 命令と同じセマンティクス)。</summary>
    public StackSlot Box(VmType type, in StackSlot value) {
        var fields = value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue sv
            ? sv.Clone().Fields
            : [value];
        return StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields)));
    }

    /// <summary>インスタンスを生成して .ctor を実行する (newobj 相当。VM ホスト API 用)。</summary>
    public VmClassInstance CreateInstance(VmClassType type, StackSlot[] constructorArgs) {
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && !m.IsStatic &&
                m.Signature.ParamTypes.Length == constructorArgs.Length && m.Body is not null)
            ?? throw new ArgumentException($"型 {type.FullName} に引数 {constructorArgs.Length} 個の .ctor がありません。");
        _objectEngine.EnsureInitialized(type);
        var instance = _services.Heap.Allocate(new VmClassInstance(type,
            _services.Objects.CreateInstanceStorage(type, _services.Loader)));
        var args = new StackSlot[constructorArgs.Length + 1];
        args[0] = StackSlot.OfObject(instance);
        constructorArgs.CopyTo(args, 1);
        Invoke(ctor, args);
        return instance;
    }

    /// <summary>メソッドを実行し戻り値を得る (void は Kind=Empty)。</summary>
    public StackSlot Invoke(VmMethod method, StackSlot[] arguments) => Invoke(method, arguments, null);

    /// <summary>メソッドを実行し戻り値を得る (void は Kind=Empty)。context は呼出元のジェネリック実引数。</summary>
    public StackSlot Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context) {
        _shared.ThrowIfDisposed();
        VmLifetime.EnsureLiveForGuest(method);
        foreach (var argument in arguments)
            VmLifetime.EnsureLiveForGuest(argument);
        using var allocationScope = _heap.EnterGuestAllocationScope();
        var state = CurrentState;
        RegisterExecutionState(state);
        var timeoutStarted = BeginExecutionTimeout(state);
        using var cultureScope = state.Depth == 0 ? new GuestCultureScope(_shared) : null;
        if (Interlocked.Exchange(ref _running, 1) == 0) {
            _services.Intrinsics.Seal(); // 実行開始後の intrinsic 登録を禁止
        }
        var entered = false;
        try {
            method = PrepareInvocation(method, arguments, context);
            if (method.Body is null)
                ThrowNoBody(method);
            if (state.Depth >= _memory.MaxRecursionDepth)
                throw new UnhandledGuestException("System.StackOverflowException",
                    $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");
                state.Depth++;
                entered = true;
                try {
                    var engines = EnginesFor(method);
                    CloneStructArgs(method, arguments);
                    var prepared = engines.Preparer.Prepare(method);
                    // Primitive control-flow methods do not contain managed
                    // references or byrefs. Let their specialized JIT run
                    // without allocating/registering an InterpreterFrame;
                    // the execution state and quota/safepoint guards still
                    // remain active in ScalarIntJitContext.
                    var compiled = CanUseJit && !IsMethodActive(method) && engines.Preparer.IsCached(method)
                        ? engines.Jit.TryGetCompiled(method, prepared, prepared.Code, engines.Preparer)
                        : null;
                    if (state.Depth == 1 && context is null && compiled is { HasDirectScalar: true }) {
                        if (_tracer is { } directTracer)
                            directTracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                        return compiled.InvokeDirectScalar(this, arguments);
                    }
                var frame = RentFrame(method, arguments, prepared, prepared.MaxStack);
                frame.Context = context; // FixupStructLocals が !n ローカルを実引数で初期化する
                if (!_coordinator.IsInsideGuestInstruction)
                    using (_coordinator.EnterRead())
                        AddFrameRoot(state, frame);
                else
                    AddFrameRoot(state, frame);
                // 実行トレース: IL 本体を実行したフレームのみ記録する
                // (intrinsic / ランタイムバインドへの委譲は IL フレームを持たないため記録されない)
                if (_tracer is { } tracer)
                    tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                try {
                    if (prepared.RequiresStructLocalFixup) {
                        if (_coordinator.IsInsideGuestInstruction)
                            FixupStructLocals(frame);
                        else
                            using (_coordinator.EnterRead())
                                FixupStructLocals(frame);
                    }
                    // Dynamic expression compilation is host work rather than a
                    // guest instruction.  Do not perform it after the guest has
                    // already exhausted its instruction budget; the first
                    // interpreter instruction will report the normal quota error.
                    return compiled is null
                        ? RunFrameWithTailCalls(ref frame, state)
                        : compiled.Invoke(this, engines.Services, frame);
                } finally {
                    if (!_coordinator.IsInsideGuestInstruction)
                        using (_coordinator.EnterRead())
                            RemoveFrameRoot(state, frame);
                    else
                        RemoveFrameRoot(state, frame);
                    ReturnFrame(frame);
                    _debugger?.FrameExited(Environment.CurrentManagedThreadId, state.Depth);
                }
            } finally {
                state.Depth--;
                if (state.Depth == 0) {
                    try {
                        FlushPendingAssemblyContextCaches();
                    } finally {
                        // A ThreadLocal retains its value until the host thread
                        // exits. Do not retain completed thread states in the
                        // VM-wide root registry for the lifetime of a long-lived VM.
                        _executionStates.TryRemove(state, out _);
                        state.Registered = false;
                    }
                }
            }
        } finally {
            if (timeoutStarted)
                state.DeadlineTimestamp = 0;
            if (state.Depth == 0)
                ReleaseInstructionCharge(state);
            // Preparation/type initialization can fail before Depth is entered.
            // Those failed attempts must not leave an otherwise idle state behind.
            if (!entered && state.Depth == 0) {
                _executionStates.TryRemove(state, out _);
                state.Registered = false;
            }
        }

    }

    /// <summary>
    /// Execute a guest callback requested by an intrinsic. The callback is
    /// already inside an active guest instruction batch, so a promoted method
    /// can skip the public host-boundary setup. Uncompiled methods and static
    /// methods still use the normal path so preparation and type initialization
    /// semantics remain unchanged.
    /// </summary>
    private StackSlot InvokeGuestFromIntrinsic(VmMethod method, StackSlot[] arguments,
        GenericContext? context) {
        if (IsInsideGuestInstruction) {
            if (TryInvokeCompiledNestedResolved(method, arguments, context,
                    allowStatic: true, out var nestedResult))
                return nestedResult;
            if (TryInvokeCompiled(method, arguments, context, argumentsAlreadyValidated: true,
                    out var compiledResult))
                return compiledResult;
        }
        return Invoke(method, arguments, context);
    }

    /// <summary>Borrow caller slots for an already promoted synchronous leaf.</summary>
    internal bool TryInvokeCompiledLeaf(VmMethod method, Span<StackSlot> arguments, out StackSlot result) {
        result = default;
        VmLifetime.EnsureLiveForGuest(method);
        foreach (ref readonly var argument in arguments)
            VmLifetime.EnsureLiveForGuest(argument);
        if (!CanUseJit)
            return false;
        if (IsMethodActive(method))
            return false;
        var compiled = EnginesFor(method).Jit.GetCompiled(method);
        if (compiled is not { HasLeaf: true })
            return false;
        if (!ReferenceEquals(PrepareInvocation(method, arguments, null), method) || method.Body is null)
            return false;
        var state = CurrentState;
        if (state.Depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");
        RegisterExecutionState(state);
        state.Depth++;
        try {
            // Leaf parameters are primitive values; a constructor's this is
            // retained in the caller's active stack throughout the invocation.
            if (_tracer is { } tracer)
                tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            compiled.TryInvokeLeaf(this, null, arguments, out result);
            return true;
        } finally {
            state.Depth--;
            if (state.Depth == 0) {
                try {
                    FlushPendingAssemblyContextCaches();
                } finally {
                    _executionStates.TryRemove(state, out _);
                    state.Registered = false;
                }
            }
        }
    }

    /// <summary>
    /// Execute a leaf from an already active JIT frame.  The caller has
    /// already entered the guest execution state and owns the instruction
    /// batch, so repeating invocation registration and recursion bookkeeping
    /// here would only add overhead to every direct call in a hot loop.
    /// </summary>
    internal bool TryInvokeCompiledLeafNested(VmMethod method, JitCompiledMethod compiled,
        Span<StackSlot> arguments, out StackSlot result) =>
        TryInvokeCompiledLeafNested(method, compiled, null, arguments, out result);

    internal bool TryInvokeCompiledLeafNested(VmMethod method, JitCompiledMethod compiled,
        GenericContext? context, Span<StackSlot> arguments, out StackSlot result) {
        result = default;
        // The caller is already executing under the bounded guest read lease;
        // the loader/context cannot be unloaded until that lease is released.
        // Repeating the recursive lifetime walk for every primitive leaf call
        // only adds boundary overhead on the common JIT path.
        if (!IsInsideGuestInstruction) {
            VmLifetime.EnsureLiveForGuest(method);
            foreach (ref readonly var argument in arguments)
                VmLifetime.EnsureLiveForGuest(argument);
        }
        if (!CanUseJit || IsMethodActive(method) || !compiled.HasLeaf)
            return false;
        compiled.TryInvokeLeaf(this, context, arguments, out result);
        return true;
    }

    /// <summary>
    /// Resolve a leaf once for a generated direct-call site.  CoreLib surface
    /// substitutions stay on the normal call path because they may rewrite
    /// the target and normalize the receiver.
    /// </summary>
    internal JitCompiledMethod? GetNestedLeaf(VmMethod method) {
        if (!CanUseJit || method.Body is null || method.Signature.GenericParamCount != 0)
            return null;
        if (_services.CoreLibSurfaces?.Substitute(method) is not null)
            return null;
        var compiled = GetNestedCompiled(method);
        return compiled is { HasLeaf: true } ? compiled : null;
    }

    /// <summary>
    /// Use a method that has already crossed the normal JIT promotion
    /// threshold.  Nested calls use the established promotion path so the
    /// normal CoreLib surface and receiver checks remain in control.
    /// </summary>
    internal JitCompiledMethod? GetNestedCompiled(VmMethod method) {
        if (!CanUseJit || IsMethodActive(method) || method.Body is null)
            return null;
        var engines = EnginesFor(method);
        return engines.Jit.GetCompiled(method);
    }

    /// <summary>
    /// Resolve a CoreLib helper reached from an already compiled frame. The
    /// helper still uses the same loader-local cache and resource budget; this
    /// only allows the first compiled caller to promote a trusted IL target
    /// before falling back through the full interpreter boundary. Guest
    /// methods retain the normal invocation-count promotion policy.
    /// </summary>
    internal JitCompiledMethod? GetOrPromoteNestedCompiled(VmMethod method) {
        if (!CanUseJit || IsMethodActive(method) || method.Body is null)
            return null;
        var engines = EnginesFor(method);
        if (engines.Jit.GetCompiled(method) is { } existing)
            return existing;
        if (method.Loader?.IsTrustedCoreLib != true &&
            method.Loader?.IsTrustedVmCoreLib != true &&
            method.Loader?.IsTrustedBcl != true)
            return null;
        var prepared = engines.Preparer.Prepare(method);
        return engines.Jit.TryGetCompiled(method, prepared, prepared.Code, engines.Preparer,
            promoteImmediately: true);
    }


    /// <summary>
    /// Promote only a constructor candidate reached by newobj.  Unlike a
    /// regular nested call, this promotion is restricted to the constructor
    /// leaf compiler, so it cannot recursively promote an arbitrary BCL call
    /// graph while a JIT frame is running.
    /// </summary>
    internal JitCompiledMethod? GetNestedConstructorLeaf(VmMethod method) {
        if (!CanUseJit || method.Body is null || method.Signature.GenericParamCount != 0 ||
            method.Name != ".ctor" || !method.Signature.HasThis ||
            method.Signature.ReturnType.Kind != SigKind.Void)
            return null;
        var engines = EnginesFor(method);
        var prepared = engines.Preparer.Prepare(method);
        if (prepared.LocalTypes.Length != 0 || !prepared.Code.Any(instruction => instruction.Op == ILOp.Stfld))
            return null;
        var compiled = engines.Jit.TryGetCompiled(method, prepared, prepared.Code, engines.Preparer);
        return compiled is { HasLeaf: true } ? compiled : null;
    }

    /// <summary>
    /// Execute an already promoted guest method without repeating the public
    /// invocation plumbing on every nested call. This is only a fast path for
    /// a delegate already present in the loader-local JIT cache; unpromoted
    /// methods continue through the normal Invoke path.
    /// </summary>
    internal bool TryInvokeCompiled(VmMethod method, StackSlot[] arguments,
        GenericContext? context, out StackSlot result) {
        return TryInvokeCompiled(method, arguments, context, argumentsAlreadyValidated: false, out result);
    }

    internal bool TryInvokeCompiled(VmMethod method, StackSlot[] arguments,
        GenericContext? context, bool argumentsAlreadyValidated, out StackSlot result) {
        result = default;
        if (!argumentsAlreadyValidated) {
            VmLifetime.EnsureLiveForGuest(method);
            foreach (var argument in arguments)
                VmLifetime.EnsureLiveForGuest(argument);
        }
        if (!CanUseJit)
            return false;
        if (IsMethodActive(method))
            return false;
        var engines = EnginesFor(method);
        var compiled = engines.Jit.GetCompiled(method);
        if (compiled is null)
            return false;

        var preparedMethod = PrepareInvocation(method, arguments, context);
        if (!ReferenceEquals(preparedMethod, method) || method.Body is null)
            return false;
        var state = CurrentState;
        if (state.Depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");

        RegisterExecutionState(state);
        state.Depth++;
        try {
            CloneStructArgs(method, arguments);
            if (context is null && compiled.HasLeaf) {
                if (_tracer is { } leafTracer)
                    leafTracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                compiled.TryInvokeLeaf(this, context, arguments, out result);
                return true;
            }
            var frame = RentFrame(method, arguments, compiled.Prepared, compiled.Prepared.MaxStack);
            frame.Context = context;
            // The caller is executing under an instruction-batch read lease,
            // so a collector cannot observe this half-registered frame. The
            // root list therefore needs no monitor on the hot nested path.
            AddFrameRoot(state, frame);
            if (_tracer is { } tracer)
                tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            try {
                if (compiled.Prepared.RequiresStructLocalFixup)
                    FixupStructLocals(frame);
                result = compiled.Invoke(this, engines.Services, frame);
                return true;
            } finally {
                RemoveFrameRoot(state, frame);
                ReturnFrame(frame);
                _debugger?.FrameExited(Environment.CurrentManagedThreadId, state.Depth);
            }
        } finally {
            state.Depth--;
            if (state.Depth == 0) {
                FlushPendingAssemblyContextCaches();
                lock (state.Gate) {
                    if (state.Frames.Count == 0) {
                        _executionStates.TryRemove(state, out _);
                        state.Registered = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Execute a promoted instance method from an already active guest frame.
    /// The caller has resolved the exact method and performed the dispatch and
    /// binding checks, so repeating host-boundary preparation on every small
    /// instance helper is unnecessary. Static methods are excluded because
    /// their type initializer must still be checked at each ordinary entry.
    /// CoreLib surface substitutions also stay on the full preparation path.
    /// </summary>
    internal bool TryInvokeCompiledNestedResolved(VmMethod method, StackSlot[] arguments,
        GenericContext? context, out StackSlot result) {
        return TryInvokeCompiledNestedResolved(method, arguments, context,
            allowStatic: false, out result);
    }

    internal bool TryInvokeCompiledNestedResolved(VmMethod method, StackSlot[] arguments,
        GenericContext? context, bool allowStatic, out StackSlot result) {
        result = default;
        if (!CanUseJit || IsMethodActive(method) || !IsInsideGuestInstruction ||
            (!allowStatic && method.IsStatic) || method.Body is null ||
            _services.CoreLibSurfaces?.Substitute(method) is not null)
            return false;

        var engines = EnginesFor(method);
        var compiled = engines.Jit.GetCompiled(method);
        if (compiled is null)
            return false;

        return TryInvokeCompiledNestedResolved(method, compiled, arguments, context,
            allowStatic, out result);
    }

    internal bool TryInvokeCompiledNestedResolved(VmMethod method, JitCompiledMethod compiled,
        StackSlot[] arguments, GenericContext? context, out StackSlot result) {
        return TryInvokeCompiledNestedResolved(method, compiled, arguments, context,
            allowStatic: false, out result);
    }

    internal bool TryInvokeCompiledNestedResolved(VmMethod method, JitCompiledMethod compiled,
        StackSlot[] arguments, GenericContext? context, bool allowStatic, out StackSlot result) {
        result = default;
        if (!CanUseJit || IsMethodActive(method) || !IsInsideGuestInstruction ||
            (!allowStatic && method.IsStatic) || method.Body is null ||
            _services.CoreLibSurfaces?.Substitute(method) is not null)
            return false;

        if (allowStatic && method.IsStatic)
            EnsureStaticMethodTypeInitialized(method, context);
        if (context is null && compiled.HasDirectScalar) {
            result = compiled.InvokeDirectScalar(this, arguments);
            return true;
        }
        if (context is null && compiled.HasLeaf) {
            if (_tracer is { } leafTracer)
                leafTracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            compiled.TryInvokeLeaf(this, context, arguments, out result);
            return true;
        }
        var state = CurrentState;
        if (state.Depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");
        state.Depth++;
        var activeJitCount = state.ActiveJitMethods.Count;
        state.ActiveJitMethods.Add(method);
        try {
            CloneStructArgs(method, arguments);
            var frame = RentFrame(method, arguments, compiled.Prepared, compiled.Prepared.MaxStack);
            frame.Context = context;
            if (_tracer is { } tracer)
                tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            try {
                if (compiled.Prepared.RequiresStructLocalFixup)
                    FixupStructLocals(frame);
                result = compiled.Invoke(this, compiled.Services ?? EnginesFor(method).Services, frame);
                return true;
            } finally {
                ReturnFrame(frame);
                _debugger?.FrameExited(Environment.CurrentManagedThreadId, state.Depth);
            }
        } finally {
            state.ActiveJitMethods.RemoveAt(activeJitCount);
            state.Depth--;
        }
    }

    /// <summary>
    /// Execute a promoted nested method while its arguments remain on the
    /// caller's evaluation stack. The child frame aliases that argument span,
    /// so ordinary CoreLib helper calls avoid copying a small StackSlot array.
    /// </summary>
    internal bool TryInvokeCompiledNestedResolvedFromStack(VmMethod method,
        JitCompiledMethod compiled, InterpreterFrame caller, int arity,
        GenericContext? context, bool allowStatic, out StackSlot result) {
        return TryInvokeCompiledNestedResolvedFromStack(method, compiled, caller,
            caller.Stack.Count - arity, arity, arity, context, allowStatic, out result);
    }

    /// <summary>
    /// Invoke a compiled target from an arbitrary contiguous range at the top
    /// of the caller stack. Delegate Invoke has one extra dispatcher object on
    /// that stack, so its static target arguments start one slot later while
    /// the complete dispatcher call still drops all operands on success.
    /// </summary>
    internal bool TryInvokeCompiledNestedResolvedFromStack(VmMethod method,
        JitCompiledMethod compiled, InterpreterFrame caller, int argumentOffset,
        int arity, int dropCount, GenericContext? context, bool allowStatic,
        out StackSlot result) {
        result = default;
        if (!CanUseJit || IsMethodActive(method) || !IsInsideGuestInstruction ||
            (!allowStatic && method.IsStatic) || method.Body is null ||
            _services.CoreLibSurfaces?.Substitute(method) is not null ||
            compiled.HasDirectScalar || arity < 0 || dropCount < arity ||
            argumentOffset < 0 || argumentOffset + arity > caller.Stack.Count ||
            dropCount > caller.Stack.Count)
            return false;

        if (allowStatic && method.IsStatic)
            EnsureStaticMethodTypeInitialized(method, context);

        var arguments = caller.Stack.RootSlots.AsSpan(argumentOffset, arity);
        CloneStructArgs(method, arguments);
        if (compiled.HasLeaf) {
            compiled.TryInvokeLeaf(this, context, arguments, out result);
            caller.Stack.DropArguments(arity);
            return true;
        }

        var state = CurrentState;
        if (state.Depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");

        state.Depth++;
        var activeJitCount = state.ActiveJitMethods.Count;
        state.ActiveJitMethods.Add(method);
        var frame = RentAliasedFrame(method, caller.Stack.RootSlots,
            argumentOffset, arity, compiled.Prepared, compiled.Prepared.MaxStack);
        frame.Context = context;
        try {
            if (compiled.Prepared.RequiresStructLocalFixup)
                FixupStructLocals(frame);
            result = compiled.Invoke(this, compiled.Services ?? EnginesFor(method).Services, frame);
            caller.Stack.DropArguments(dropCount);
            return true;
        } finally {
            ReturnAliasedFrame(frame);
            _debugger?.FrameExited(Environment.CurrentManagedThreadId, state.Depth);
            state.ActiveJitMethods.RemoveAt(activeJitCount);
            state.Depth--;
        }
    }

    internal void StoreLeafField(VmMethod method, int token, StackSlot receiver, StackSlot value) {
        StoreLeafField(method, null, token, receiver, value);
    }

    internal void StoreLeafField(VmMethod method, GenericContext? context, int token,
        StackSlot receiver, StackSlot value) {
        JitObjectsFor(method).StoreLeafField(method, context, token, receiver, value);
    }

    internal StackSlot ReadLeafField(VmMethod method, GenericContext? context, int token, StackSlot receiver) =>
        JitObjectsFor(method).ReadLeafField(method, context, token, receiver);

    /// <summary>命令トレース/ブレークポイント有効時は JIT を迂回して可観測性を保つ。</summary>
    private bool CanUseJit => _enableJit && HasInstructionBudget &&
        _tracer?.CapturesInstructions != true && _debugger?.IsActive != true;

    /// <summary>外側の guest 呼出の間だけ、ホスト thread の ambient culture を VM 設定に合わせる。</summary>
    private sealed class GuestCultureScope : IDisposable {
        private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

        public GuestCultureScope(VmSharedState shared) {
            CultureInfo.CurrentCulture = shared.CurrentCulture;
            CultureInfo.CurrentUICulture = shared.CurrentUICulture;
        }

        public void Dispose() {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }

    private VmMethod PrepareInvocation(VmMethod method, Span<StackSlot> arguments, GenericContext? context) {
        if (_services.CoreLibSurfaces is { } surfaces && surfaces.Substitute(method) is { } substituted) {
            // instance 面 → static 実装への差し替えでは受信者 (this) を生スロットへ正規化する
            if (method.Signature.HasThis && !substituted.Signature.HasThis && arguments.Length > 0)
                arguments[0] = arguments[0].ObjectValue switch {
                    VmByRef byRef => byRef.Slot,
                    VmBoxedValue boxed => boxed.Fields[0],
                    _ => arguments[0],
                };
            method = substituted;
        }
        // Validate guest IL before running a static constructor. Otherwise a
        // malformed method could initialize guest state before fail-closed
        // preparation has a chance to reject it.
        if (method.Body is not null)
            EnginesFor(method).Preparer.Prepare(method);
        EnsureStaticMethodTypeInitialized(method, context);
        return method;
    }

    private StackSlot RunFrameWithTailCalls(ref InterpreterFrame frame, ExecutionState state) {
        while (true) {
            try {
                return EnginesFor(frame.Method).Exceptions.RunFrame(frame);
            } catch (TailCallTransfer transfer) {
                var request = transfer.Request;
                using (_coordinator.EnterRead()) {
                    lock (state.Gate)
                        state.TemporaryRoots.Add(request.Arguments);
                }
                try {
                    var method = PrepareInvocation(request.Method, request.Arguments, request.Context);
                    if (method.Body is null)
                        ThrowNoBody(method);
                    var engines = EnginesFor(method);
                    CloneStructArgs(method, request.Arguments);
                    var nextPrepared = engines.Preparer.Prepare(method);
                    var nextFrame = RentFrame(method, request.Arguments,
                        nextPrepared, nextPrepared.MaxStack);
                    nextFrame.Context = request.Context;
                    var replaced = false;
                    try {
                        using (_coordinator.EnterRead()) {
                            lock (state.Gate) {
                                var index = state.Frames.IndexOf(frame);
                                if (index < 0)
                                    throw new InvalidOperationException("末尾呼出で置き換える実行フレームが見つかりません。");
                                state.Frames[index] = nextFrame;
                            }
                        }
                        var previousFrame = frame;
                        frame = nextFrame;
                        replaced = true;
                        ReturnFrame(previousFrame);
                    } finally {
                        if (!replaced)
                            ReturnFrame(nextFrame);
                    }
                    using (_coordinator.EnterRead()) {
                        if (nextPrepared.RequiresStructLocalFixup)
                            FixupStructLocals(frame);
                        if (_tracer is { } tracer)
                            tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                    }
                } finally {
                    using (_coordinator.EnterRead()) {
                        lock (state.Gate)
                            state.TemporaryRoots.Remove(request.Arguments);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 静的メソッド呼出しの入口でも CLR の型初期化規約を適用する。
    /// 静的フィールドを直接参照しない .cctor でも、明示的 static constructor は最初の
    /// static method 呼出し前に実行される必要がある。.cctor 自身は再入を避けて除外する。
    /// </summary>
    private void EnsureStaticMethodTypeInitialized(VmMethod method, GenericContext? context) {
        if (!method.IsStatic || method.Name == ".cctor" || method.DeclaringType is not VmClassType definition)
            return;

        var objects = EnginesFor(method).Objects;
        if (definition.GenericParamCount > 0 && context?.ClassArgs is { Length: > 0 } classArgs &&
            classArgs.Length == definition.GenericParamCount) {
            objects.EnsureConstructedInitialized(new VmConstructedType {
                Definition = definition,
                TypeArguments = classArgs,
            });
        } else {
            objects.EnsureInitialized(definition);
        }
    }

    // サービス群からの再帰呼出入口 (循環依存をインターフェースで切る)
    StackSlot IGuestInvoker.Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context) =>
        Invoke(method, arguments, context);

    bool IGuestInvoker.TryCreateTailCall(InterpreterFrame caller, VmMethod method,
        StackSlot[] arguments, GenericContext? context, out TailCallRequest? request) {
        request = null;
        if (method.Body is null || caller.Stack.Count != 0 ||
            !Equals(method.Signature.ReturnType, caller.Method.Signature.ReturnType))
            return false;
        if (arguments.Any(argument => ContainsCurrentFrameByRef(argument, caller, new HashSet<object>(ReferenceEqualityComparer.Instance))))
            return false;
        request = new TailCallRequest(method, arguments, context);
        return true;
    }

    private static bool ContainsCurrentFrameByRef(in StackSlot slot, InterpreterFrame caller, HashSet<object> visited) {
        if (slot.Kind == StackKind.TypedByRef)
            return true;
        if (slot.ObjectValue is VmByRef byRef)
            return ReferenceEquals(byRef.Container, caller.Arguments) || ReferenceEquals(byRef.Container, caller.Locals);
        if (slot.ObjectValue is VmStructValue value && visited.Add(value))
            return value.Fields.Any(field => ContainsCurrentFrameByRef(field, caller, visited));
        if (slot.ObjectValue is VmBoxedValue boxed && visited.Add(boxed))
            return boxed.Fields.Any(field => ContainsCurrentFrameByRef(field, caller, visited));
        return false;
    }
}

internal sealed class TailCallTransfer(TailCallRequest request) : Exception {
    public TailCallRequest Request { get; } = request;
}
