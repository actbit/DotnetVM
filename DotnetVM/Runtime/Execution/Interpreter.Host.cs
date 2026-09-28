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
        var state = CurrentState;
        using var cultureScope = state.Depth == 0 ? new GuestCultureScope(_shared.Culture) : null;
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
                var frame = InterpreterFrame.Create(method, arguments, prepared, prepared.MaxStack);
                frame.Context = context; // FixupStructLocals が !n ローカルを実引数で初期化する
                using (_coordinator.EnterRead()) {
                    lock (state.Gate)
                        state.Frames.Add(frame);
                }
                // 実行トレース: IL 本体を実行したフレームのみ記録する
                // (intrinsic / ランタイムバインドへの委譲は IL フレームを持たないため記録されない)
                if (_tracer is { } tracer)
                    tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                try {
                    using (_coordinator.EnterRead())
                        FixupStructLocals(frame);
                    // Dynamic expression compilation is host work rather than a
                    // guest instruction.  Do not perform it after the guest has
                    // already exhausted its instruction budget; the first
                    // interpreter instruction will report the normal quota error.
                    var compiled = CanUseJit && engines.Preparer.IsCached(method)
                        ? engines.Jit.TryGetCompiled(method, prepared, frame.Code)
                        : null;
                    return compiled is null
                        ? RunFrameWithTailCalls(ref frame, state)
                        : compiled.Invoke(this, engines.Services, frame);
                } finally {
                    using (_coordinator.EnterRead()) {
                        lock (state.Gate)
                            state.Frames.Remove(frame);
                    }
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
                    }
                }
            }
        } finally {
            // Preparation/type initialization can fail before Depth is entered.
            // Those failed attempts must not leave an otherwise idle state behind.
            if (!entered && state.Depth == 0)
                _executionStates.TryRemove(state, out _);
        }

    }

    /// <summary>
    /// Execute an already promoted guest method without repeating the public
    /// invocation plumbing on every nested call. This is only a fast path for
    /// a delegate already present in the loader-local JIT cache; unpromoted
    /// methods continue through the normal Invoke path.
    /// </summary>
    internal bool TryInvokeCompiled(VmMethod method, StackSlot[] arguments,
        GenericContext? context, out StackSlot result) {
        result = default;
        VmLifetime.EnsureLiveForGuest(method);
        foreach (var argument in arguments)
            VmLifetime.EnsureLiveForGuest(argument);
        if (!CanUseJit)
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

        state.Depth++;
        try {
            CloneStructArgs(method, arguments);
            if (context is null && compiled.HasLeaf) {
                if (_tracer is { } leafTracer)
                    leafTracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
                compiled.TryInvokeLeaf(this, arguments, out result);
                return true;
            }
            var frame = InterpreterFrame.Create(method, arguments, compiled.Prepared, compiled.Prepared.MaxStack);
            frame.Context = context;
            // The caller is executing under an instruction-batch read lease,
            // so a collector cannot observe this half-registered frame.
            lock (state.Gate)
                state.Frames.Add(frame);
            if (_tracer is { } tracer)
                tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            try {
                FixupStructLocals(frame);
                result = compiled.Invoke(this, engines.Services, frame);
                return true;
            } finally {
                lock (state.Gate)
                    state.Frames.Remove(frame);
                _debugger?.FrameExited(Environment.CurrentManagedThreadId, state.Depth);
            }
        } finally {
            state.Depth--;
            if (state.Depth == 0)
                FlushPendingAssemblyContextCaches();
        }
    }

    internal void StoreLeafField(VmMethod method, int token, StackSlot receiver, StackSlot value) {
        var objects = JitObjectsFor(method);
        var field = objects.ResolveFieldToken(token, null, method.DynamicTokens);
        if (field.IsInitOnly && method.Name is not (".ctor" or ".cctor"))
            throw new UnhandledGuestException("System.FieldAccessException",
                $"readonly フィールド {field} はコンストラクター外から書き込めません。");
        if (!objects.TryStoreStringField(receiver, field, value))
            objects.WriteField(receiver, field, value);
    }

    /// <summary>命令トレース/ブレークポイント有効時は JIT を迂回して可観測性を保つ。</summary>
    private bool CanUseJit => HasInstructionBudget &&
        _tracer?.CapturesInstructions != true && _debugger?.IsActive != true;

    /// <summary>外側の guest 呼出の間だけ、ホスト thread の ambient culture を VM 設定に合わせる。</summary>
    private sealed class GuestCultureScope : IDisposable {
        private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

        public GuestCultureScope(CultureInfo culture) {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose() {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }

    private VmMethod PrepareInvocation(VmMethod method, StackSlot[] arguments, GenericContext? context) {
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
                    var nextFrame = InterpreterFrame.Create(method, request.Arguments,
                        nextPrepared, nextPrepared.MaxStack);
                    nextFrame.Context = request.Context;

                    using (_coordinator.EnterRead()) {
                        lock (state.Gate) {
                            var index = state.Frames.IndexOf(frame);
                            if (index < 0)
                                throw new InvalidOperationException("末尾呼出で置き換える実行フレームが見つかりません。");
                            state.Frames[index] = nextFrame;
                        }
                    }
                    frame = nextFrame;
                    using (_coordinator.EnterRead())
                        FixupStructLocals(frame);
                    if (_tracer is { } tracer)
                        tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
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
