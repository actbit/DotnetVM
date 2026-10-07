using System.Linq.Expressions;
using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal delegate StackSlot JitLeaf(Interpreter interpreter, GenericContext? context,
    ReadOnlySpan<StackSlot> arguments);
internal delegate StackSlot JitScalar(Interpreter interpreter, InterpreterServices services, InterpreterFrame frame);
internal delegate StackSlot JitScalarDirect(Interpreter interpreter, StackSlot[] arguments);

/// <summary>
/// A compiled entry point for the small JIT.  The delegate deliberately receives
/// a VM frame instead of exposing CLR values to generated code; all values still
/// cross the VM boundary as <see cref="StackSlot"/> instances.
/// </summary>
internal sealed class JitCompiledMethod(Func<JitFrame, StackSlot>? entry,
    PreparedMethod prepared,
    JitLeaf? leaf = null,
    JitScalar? scalar = null,
    JitScalarDirect? scalarDirect = null) {
    private readonly Func<JitFrame, StackSlot>? _entry = entry;
    private readonly JitLeaf? _leaf = leaf;
    private readonly JitScalar? _scalar = scalar;
    private readonly JitScalarDirect? _scalarDirect = scalarDirect;
    private readonly ConcurrentDictionary<JitCallRouteKey, JitCallRouteEntry> _callRoutes = new();
    private readonly JitCallRouteEntry?[] _directCallRoutes = new JitCallRouteEntry?[prepared.Code.Length];
    private readonly ConcurrentDictionary<JitVirtualTargetKey, VmMethod> _virtualTargets = new();
    // The compiled delegate is loader-local. Retain the matching service
    // bundle so nested calls do not look the loader up again just to invoke
    // an already resolved delegate.
    internal InterpreterServices? Services { get; set; }

    internal PreparedMethod Prepared { get; } = prepared;
    internal bool HasLeaf => _leaf is not null;
    internal bool HasDirectScalar => _scalarDirect is not null;

    internal bool TryGetCachedCallRoute(int site, int token, bool isCallvirt, int constrainedToken,
        GenericContext? context, out CallTarget target, out JitCallRoute route) {
        if (context is null && (uint)site < (uint)_directCallRoutes.Length &&
            _directCallRoutes[site] is { } direct &&
            direct.Matches(token, isCallvirt, constrainedToken)) {
            target = direct.Target;
            route = direct.Route;
            return true;
        }
        if (_callRoutes.TryGetValue(new(token, isCallvirt, constrainedToken, context), out var entry)) {
            target = entry.Target;
            route = entry.Route;
            return true;
        }
        target = null!;
        route = JitCallRoute.None;
        return false;
    }

    internal void CacheCallRoute(int site, int token, bool isCallvirt, int constrainedToken,
        GenericContext? context, CallTarget target, JitCallRoute route) {
        if (route == JitCallRoute.None)
            return;
        var entry = new JitCallRouteEntry(token, isCallvirt, constrainedToken, context,
            target, route);
        if (context is null && (uint)site < (uint)_directCallRoutes.Length)
            _directCallRoutes[site] = entry;
        _callRoutes[new(token, isCallvirt, constrainedToken, context)] = entry;
    }

    internal bool TryGetCachedVirtualTarget(int token, VmType receiverType,
        GenericContext? context, out VmMethod target) =>
        _virtualTargets.TryGetValue(new(token, receiverType, context), out target!);

    internal void CacheVirtualTarget(int token, VmType receiverType,
        GenericContext? context, VmMethod target) =>
        _virtualTargets[new(token, receiverType, context)] = target;


    internal StackSlot InvokeDirectScalar(Interpreter interpreter, StackSlot[] arguments) {
        try {
            return _scalarDirect is { } scalar ? scalar(interpreter, arguments) :
                throw new InvalidOperationException("ダイレクト整数 JIT が存在しません。");
        } catch (Exception host) when (HostExceptionBoundary.IsNormalizable(host)) {
            throw HostExceptionBoundary.InvalidProgram(host);
        }
    }

    internal bool TryInvokeLeaf(Interpreter interpreter, GenericContext? context,
        ReadOnlySpan<StackSlot> arguments, out StackSlot result) {
        if (_leaf is null) {
            result = default;
            return false;
        }
        try {
            result = _leaf(interpreter, context, arguments);
        } catch (Exception host) when (HostExceptionBoundary.IsNormalizable(host)) {
            throw HostExceptionBoundary.InvalidProgram(host);
        }
        return true;
    }

    public StackSlot Invoke(Interpreter interpreter, InterpreterServices services, InterpreterFrame frame) {
        try {
            return _scalar is { } scalar
                ? scalar(interpreter, services, frame)
                : _entry!(new JitFrame(interpreter, services, frame, this));
        } catch (Exception host) when (HostExceptionBoundary.IsNormalizable(host)) {
            throw HostExceptionBoundary.InvalidProgram(host);
        }
    }
}

internal readonly record struct JitCallRouteKey(int Token, bool IsCallvirt,
    int ConstrainedToken, GenericContext? Context);

internal sealed record JitCallRouteEntry(int Token, bool IsCallvirt, int ConstrainedToken,
    GenericContext? Context, CallTarget Target, JitCallRoute Route) {
    internal bool Matches(int token, bool isCallvirt, int constrainedToken) =>
        Token == token && IsCallvirt == isCallvirt && ConstrainedToken == constrainedToken;
}

internal readonly record struct JitVirtualTargetKey(int Token, VmType ReceiverType,
    GenericContext? Context);

/// <summary>
/// VM-local hot method cache.  Compiled delegates are kept per loader so a
/// collectible assembly can be unloaded without leaving a delegate rooted by a
/// different loader or VM instance.
/// </summary>
internal sealed class JitResourceBudget(int maxEntries, int maxCompiledMethods) {
    private readonly int _maxEntries = maxEntries;
    private readonly int _maxCompiledMethods = maxCompiledMethods;
    private readonly object _gate = new();
    private int _entries;
    private int _compiledMethods;

    public bool TryReserveEntry() {
        lock (_gate) {
            if (_entries >= _maxEntries)
                return false;
            _entries++;
            return true;
        }
    }

    public bool TryReserveCompiledMethod() {
        lock (_gate) {
            if (_compiledMethods >= _maxCompiledMethods)
                return false;
            _compiledMethods++;
            return true;
        }
    }

    public void ReleaseCompiledMethod() {
        lock (_gate) {
            if (_compiledMethods > 0)
                _compiledMethods--;
        }
    }

    public void ReleaseEntry(bool compiled) {
        lock (_gate) {
            if (_entries > 0)
                _entries--;
            if (compiled && _compiledMethods > 0)
                _compiledMethods--;
        }
    }
}

internal sealed class JitCodeCache(
    bool enabled,
    int promotionThreshold,
    VmHeap heap,
    MemoryPolicy memory,
    JitResourceBudget resourceBudget,
    InterpreterServices services) {
    private sealed class Entry {
        public int InvocationCount;
        public JitCompiledMethod? Compiled;
        public bool Rejected;
        public bool Compiling;
        public bool CompiledReserved;
    }

    private readonly bool _enabled = enabled;
    private readonly int _promotionThreshold = promotionThreshold;
    private readonly VmHeap _heap = heap;
    private readonly MemoryPolicy _memory = memory;
    private readonly JitResourceBudget _resourceBudget = resourceBudget;
    private readonly bool _useFastExecution = !memory.InstructionChargingEnabled ||
        memory.InstructionQuota == long.MaxValue;
    private readonly ConcurrentDictionary<VmMethod, Entry> _entries = new();
    private readonly object _gate = new();

    public JitCompiledMethod? TryGetCompiled(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, MethodPreparer preparer, bool promoteImmediately = false) {
        if (!_enabled)
            return null;

        if (_entries.TryGetValue(method, out var published) &&
            Volatile.Read(ref published.Compiled) is { } ready)
            return ready;

        Entry entry;
        lock (_gate) {
            if (!_entries.TryGetValue(method, out entry!)) {
                if (!_resourceBudget.TryReserveEntry())
                    return null;
                _entries.TryAdd(method, entry = new Entry());
            }

            if (entry.Compiled is not null || entry.Rejected || entry.Compiling)
                return entry.Compiled;

            if (entry.InvocationCount < int.MaxValue)
                entry.InvocationCount++;
            if (!promoteImmediately && entry.InvocationCount < _promotionThreshold)
                return null;
            entry.Compiling = true;
        }

        // Do not hold the loader-wide cache lock while Expression.Compile runs.
        // A per-entry Compiling state above prevents duplicate promotions.
        JitMethodCompiler.CompilationCost? estimate;
        try {
            estimate = JitMethodCompiler.TryEstimate(method, prepared, code, _memory);
        } catch (Exception) {
            estimate = null;
        }
        JitCompiledMethod? compiled = null;
        var reservedCompiled = false;
        if (estimate is { } cost && _resourceBudget.TryReserveCompiledMethod()) {
            if (_heap.TryChargeJitCompilation(cost.WorkUnits, cost.HostMemoryBytes)) {
                reservedCompiled = true;
                try {
                    compiled = JitMethodCompiler.TryCompile(method, prepared, code, preparer,
                        _useFastExecution);
                    if (compiled is not null)
                        compiled.Services = services;
                } catch (Exception) {
                    compiled = null;
                }
            } else {
                _resourceBudget.ReleaseCompiledMethod();
            }
        }

        lock (_gate) {
            // Clear() may have detached the entry while compilation was in
            // progress during an unload. Never publish a detached delegate.
            if (!_entries.TryGetValue(method, out var current) || !ReferenceEquals(current, entry)) {
                if (reservedCompiled)
                    _resourceBudget.ReleaseCompiledMethod();
                // The loader may already be collectible. Returning a delegate
                // compiled from its metadata would let an in-flight invocation
                // execute after unload and keep the detached loader alive.
                return null;
            }
            entry.Compiling = false;
            if (compiled is null) {
                entry.Rejected = true;
                if (reservedCompiled)
                    _resourceBudget.ReleaseCompiledMethod();
                return null;
            }
            entry.CompiledReserved = true;
            Volatile.Write(ref entry.Compiled, compiled);
        return compiled;
        }
    }

    internal int InvocationCount(VmMethod method) {
        lock (_gate)
            return _entries.TryGetValue(method, out var entry) ? entry.InvocationCount : 0;
    }

    internal bool IsCompiled(VmMethod method) => GetCompiled(method) is not null;

    internal JitCompiledMethod? GetCompiled(VmMethod method) {
        if (!_enabled)
            return null;
        return _entries.TryGetValue(method, out var entry) ? Volatile.Read(ref entry.Compiled) : null;
    }

    public void Clear() {
        lock (_gate) {
            foreach (var entry in _entries.Values)
                _resourceBudget.ReleaseEntry(entry.CompiledReserved);
            _entries.Clear();
        }
    }

}

/// <summary>
/// Mutable state used by a generated expression tree.  It intentionally uses
/// the same InterpreterFrame, EvaluationStack and SlotOps as the interpreter;
/// this keeps VM object ownership, copying rules, exceptions and GC roots
/// identical on both execution paths.
/// </summary>
internal struct JitFrame {
    private readonly Interpreter _interpreter;
    private readonly Interpreter.ExecutionState _executionState;
    private readonly InterpreterServices _services;
    private readonly InterpreterFrame _frame;
    private readonly ObjectEngine _objects;
    private readonly CallEngine _calls;
    private readonly JitCompiledMethod _compiled;
    private readonly StackSlot[] _jitStack;
    private VmExecutionCoordinator.InstructionBatchLease _instructionBatch;
    private bool _ownsInstructionBatch;
    private int _jitStackPointer;
    private int _initialJitStackPointer;
    private int _batchInstructions;
    private int _pendingFastInstructions;
    private int _pendingFastCost;
    private int _cachedDirectCallToken;
    private VmMethod? _cachedDirectCallTarget;
    private JitCompiledMethod? _cachedDirectCallLeaf;
    private bool _hasCachedDirectCall;
    private int _cachedStringConstructorToken;
    private SigType[]? _cachedStringConstructorParameters;
    private bool _hasCachedStringConstructor;
    private int _elidedGetterToken;
    private int _cachedElidedConstructorToken;
    private int _cachedElidedGetterToken;
    private int _cachedElidedInstructionCost;
    private bool _hasCachedElidedField;
    private int _cachedJitCallToken0;
    private int _cachedJitCallToken1;
    private int _cachedJitCallToken2;
    private int _cachedJitCallToken3;
    private int _cachedJitCallToken4;
    private int _cachedJitCallToken5;
    private int _cachedJitCallToken6;
    private int _cachedJitCallToken7;
    private int _cachedJitCallConstrained0;
    private int _cachedJitCallConstrained1;
    private int _cachedJitCallConstrained2;
    private int _cachedJitCallConstrained3;
    private int _cachedJitCallConstrained4;
    private int _cachedJitCallConstrained5;
    private int _cachedJitCallConstrained6;
    private int _cachedJitCallConstrained7;
    private bool _cachedJitCallVirtual0;
    private bool _cachedJitCallVirtual1;
    private bool _cachedJitCallVirtual2;
    private bool _cachedJitCallVirtual3;
    private bool _cachedJitCallVirtual4;
    private bool _cachedJitCallVirtual5;
    private bool _cachedJitCallVirtual6;
    private bool _cachedJitCallVirtual7;
    private CallTarget? _cachedJitCallTarget0;
    private CallTarget? _cachedJitCallTarget1;
    private CallTarget? _cachedJitCallTarget2;
    private CallTarget? _cachedJitCallTarget3;
    private CallTarget? _cachedJitCallTarget4;
    private CallTarget? _cachedJitCallTarget5;
    private CallTarget? _cachedJitCallTarget6;
    private CallTarget? _cachedJitCallTarget7;
    private JitCallRoute _cachedJitCallRoute0;
    private JitCallRoute _cachedJitCallRoute1;
    private JitCallRoute _cachedJitCallRoute2;
    private JitCallRoute _cachedJitCallRoute3;
    private JitCallRoute _cachedJitCallRoute4;
    private JitCallRoute _cachedJitCallRoute5;
    private JitCallRoute _cachedJitCallRoute6;
    private JitCallRoute _cachedJitCallRoute7;
    private int _cachedJitCallCursor;
    private int _cachedFieldToken0;
    private int _cachedFieldToken1;
    private int _cachedFieldToken2;
    private int _cachedFieldToken3;
    private VmField? _cachedField0;
    private VmField? _cachedField1;
    private VmField? _cachedField2;
    private VmField? _cachedField3;
    private bool _hasCachedField0;
    private bool _hasCachedField1;
    private bool _hasCachedField2;
    private bool _hasCachedField3;
    private int _cachedFieldCursor;
    private VmClassType? _cachedFieldReceiverType0;
    private VmClassType? _cachedFieldReceiverType1;
    private VmClassType? _cachedFieldReceiverType2;
    private VmClassType? _cachedFieldReceiverType3;
    private VmField? _cachedFieldIndexField0;
    private VmField? _cachedFieldIndexField1;
    private VmField? _cachedFieldIndexField2;
    private VmField? _cachedFieldIndexField3;
    private int _cachedFieldIndex0;
    private int _cachedFieldIndex1;
    private int _cachedFieldIndex2;
    private int _cachedFieldIndex3;
    private int _cachedFieldIndexCursor;

    public JitFrame(Interpreter interpreter, InterpreterServices services,
        InterpreterFrame frame, JitCompiledMethod compiled) {
        _interpreter = interpreter;
        _executionState = interpreter.CurrentExecutionState;
        _services = services;
        _frame = frame;
        _objects = interpreter.JitObjectsFor(frame.Method);
        _calls = interpreter.JitCallsFor(frame.Method);
        _compiled = compiled;
        _jitStack = frame.Stack.RootSlots;
    }

    public int InstructionPointer => _frame.Ip;
    public bool Returned { get; private set; }
    public StackSlot ReturnValue { get; private set; }

    public void BeginExecution() {
        // Nested compiled calls already execute inside the caller's bounded
        // guest read lease. Reacquiring that lock for every small helper is a
        // major part of the common call cost, so only the outer frame owns it.
        _ownsInstructionBatch = !_interpreter.IsInsideGuestInstruction;
        if (_ownsInstructionBatch) {
            _interpreter.CheckJitSafepoint();
            _instructionBatch = _interpreter.EnterJitInstructionBatch();
        } else {
            _instructionBatch = default;
        }
        _jitStackPointer = _frame.Stack.Count;
        _initialJitStackPointer = _jitStackPointer;
        _batchInstructions = 0;
        _pendingFastInstructions = 0;
        _pendingFastCost = 0;
    }

    public void EndExecution() {
        SyncJitStack();
        FlushFastInstructions();
        if (_ownsInstructionBatch)
            _instructionBatch.Dispose();
        _instructionBatch = default;
        _ownsInstructionBatch = false;
    }

    /// <summary>
    /// Fast-tier accounting for an entire basic block. The generated code
    /// still observes bounded safepoints, but avoids two helper calls and a
    /// try/finally around every IL operation when no finite quota reservation
    /// is required. Finite quotas retain the instruction-granular path below.
    /// </summary>
    public void FastBlock(int cost, int instructionCount) {
        _pendingFastCost = checked(_pendingFastCost + cost);
        _pendingFastInstructions = checked(_pendingFastInstructions + instructionCount);
        if (_pendingFastInstructions < Interpreter.SafepointInterval)
            return;
        SyncJitStack();
        FlushFastInstructions();
        if (_ownsInstructionBatch) {
            _instructionBatch.Dispose();
            _instructionBatch = default;
            _interpreter.CheckJitSafepoint();
            _instructionBatch = _interpreter.EnterJitInstructionBatch();
        } else {
            _interpreter.CheckJitSafepoint();
        }
    }

    private void FlushFastInstructions() {
        if (_pendingFastInstructions == 0)
            return;
        var cost = _pendingFastCost;
        _pendingFastInstructions = 0;
        _pendingFastCost = 0;
        _interpreter.ConsumeJitInstructionForState(_executionState, cost);
    }

    internal void ConsumeFusedInstruction(int cost) {
        if (_interpreter.InstructionChargingEnabled && _interpreter.HasUnboundedInstructionQuota)
            FastBlock(cost, 1);
        else
            _interpreter.ConsumeJitInstruction(cost);
    }

    // The surrounding batch owns the coordinator depth. These methods retain
    // the quota and bounded-safepoint boundaries without doing a ThreadStatic
    // enter/exit for every generated instruction.
    public void BeginInstruction(int cost) {
        if (_batchInstructions >= Interpreter.SafepointInterval) {
            SyncJitStack();
            if (_ownsInstructionBatch) {
                _instructionBatch.Dispose();
                _instructionBatch = default;
                _interpreter.CheckJitSafepoint();
                _instructionBatch = _interpreter.EnterJitInstructionBatch();
            } else {
                // The outer frame owns the read lease; its next boundary will
                // perform the actual lease transition.
                _interpreter.CheckJitSafepoint();
            }
            _batchInstructions = 0;
        }
        _interpreter.ConsumeJitInstructionForState(_executionState, cost);
    }

    public void EndInstruction() {
        _batchInstructions++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(in StackSlot value) {
        _jitStack[_jitStackPointer++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackSlot Pop() {
        var index = --_jitStackPointer;
        var value = _jitStack[index];
        _jitStack[index] = default;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref StackSlot Peek() => ref _jitStack[_jitStackPointer - 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SyncJitStack() => _frame.Stack.SetCountForJit(_jitStackPointer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NoOp(int next) => _frame.Ip = next;

    /// <summary>
    /// Reset the transient evaluation stack before a generated finally block.
    /// An exception abandons the protected IL evaluation stack; the finally
    /// handler starts with the empty stack state required by ECMA-335.
    /// </summary>
    public void BeginFinally() {
        _jitStackPointer = _initialJitStackPointer;
        _frame.PendingConstrained = 0;
        _frame.PendingReadonly = false;
        _frame.PendingVolatile = false;
        _frame.PendingTail = false;
    }

    /// <summary>
    /// Execute the common compiler-generated foreach cleanup sequence without
    /// materializing a second JIT control-flow loop for the finally handler.
    /// The caller has already verified the exact ldloc/brfalse/ldloc/callvirt
    /// shape and supplies the Dispose call token.
    /// </summary>
    public void FinallyDisposeLocal(int localIndex, int disposeToken, int next) {
        BeginFinally();
        if (_frame.Locals[localIndex].ObjectValue is null) {
            _frame.Ip = next;
            return;
        }
        Push(SlotOps.PushCopyOfValue(_frame.Locals[localIndex]));
        Call(disposeToken, isCallvirt: true, next);
        _jitStackPointer = _frame.Stack.Count;
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadArgument(int index, int next) {
        var argument = _frame.ArgumentAt(index);
        Push(argument);
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreArgument(int index, int next) {
        _frame.SetArgument(index, Pop());
        _frame.Ip = next;
    }

    public void LoadArgumentAddress(int index, int next) {
        Push(StackSlot.OfByRef(_frame.ArgumentByRef(index)));
        _frame.Ip = next;
    }

    public void LoadLocal(int index, int next) {
        Push(SlotOps.PushCopyOfValue(_frame.Locals[index]));
        _frame.Ip = next;
    }

    public void StoreLocal(int index, int next) {
        _frame.Locals[index] = SlotOps.StoreCopyOfValue(Pop());
        _frame.Ip = next;
    }

    public void LoadLocalAddress(int index, int next) {
        Push(StackSlot.OfByRef(_frame.LocalByRef(index)));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadNull(int next) {
        Push(StackSlot.Null);
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadInt32(int value, int next) {
        Push(StackSlot.OfInt32(value));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadInt64(long value, int next) {
        Push(StackSlot.OfInt64(value));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadFloat(double value, int next) {
        Push(StackSlot.OfFloat(value));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Duplicate(int next) {
        Push(SlotOps.PushCopyOfValue(Peek()));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drop(int next) {
        _ = Pop();
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReadonly(int next) {
        _frame.PendingReadonly = true;
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetConstrained(int token, int next) {
        _frame.PendingConstrained = token;
        _frame.Ip = next;
    }

    public void FusedLocalOperation(IlFusion fusion, int next) {
        IlFusionRuntime.ExecuteVerified(_frame, fusion);
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BranchAlways(int target) => _frame.Ip = target;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BranchUnary(bool branchIfTrue, int target, int next) {
        var condition = SlotOps.IsTrue(Pop());
        _frame.Ip = condition == branchIfTrue ? target : next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Branch(ILOp op, int target, int next) {
        var branch = op switch {
            ILOp.Br or ILOp.Br_S => true,
            ILOp.BrTrue or ILOp.BrTrue_S => SlotOps.IsTrue(Pop()),
            ILOp.BrFalse or ILOp.BrFalse_S => !SlotOps.IsTrue(Pop()),
            _ => CompareBranch(op),
        };
        _frame.Ip = branch ? target : next;
    }

    private bool CompareBranch(ILOp op) {
        var right = Pop();
        var left = Pop();
        return SlotOps.CompareBranch(op, left, right);
    }

    public void Switch(int[] targets, int next) {
        var index = Pop().AsInt32;
        _frame.Ip = (uint)index < (uint)targets.Length ? targets[index] : next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Compare(ILOp op, int next) {
        var right = Pop();
        var left = Pop();
        Push(StackSlot.OfInt32(SlotOps.Compare(op, left, right) ? 1 : 0));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Binary(ILOp op, int next) {
        var right = Pop();
        var left = Pop();
        Push(MemoryOps.TryPointerArithmetic(op, left, right)
            ?? SlotOps.BinaryArithmetic(op, left, right));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unary(ILOp op, int next) {
        Push(SlotOps.UnaryArithmetic(op, Pop()));
        _frame.Ip = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Convert(ILOp op, int next) {
        Push(SlotOps.ConvertValue(op, Pop()));
        _frame.Ip = next;
    }

    public void LoadString(int token, int next) {
        var value = _frame.Method.DynamicStrings is { } dynamicStrings &&
            dynamicStrings.TryGetValue(unchecked((uint)token), out var dynamicString)
                ? dynamicString
                : _services.Loader.Image.GetUserString(token & 0xFFFFFF);
        Push(StackSlot.OfObject(_services.Strings.GetOrNew(value)));
        _frame.Ip = next;
    }

    public void Call(int token, bool isCallvirt, int next) {
        SyncJitStack();
        if (!isCallvirt && _elidedGetterToken == token) {
            _elidedGetterToken = 0;
            _frame.Ip = next;
            return;
        }
        if (isCallvirt && _calls.TryInvokeCachedMetadataAttributes(token, _frame, out var cachedAttributes)) {
            _jitStackPointer = _frame.Stack.Count;
            if (cachedAttributes is { } cachedValue)
                Push(cachedValue);
            _frame.Ip = next;
            return;
        }
        var metadataAttributeKey = _calls.TryGetMetadataAttributeKey(token, _frame, out var capturedMetadataKey)
            ? capturedMetadataKey : default;
        var hasMetadataAttributeKey = metadataAttributeKey != default;
        var constrainedToken = _frame.PendingConstrained;
        _frame.PendingConstrained = 0;
        if (_frame.Method.DynamicTokens is null &&
            _compiled.TryGetCachedCallRoute(next - 1, token, isCallvirt, constrainedToken, _frame.Context,
                out var publishedTarget, out var publishedRoute)) {
            var publishedFast = publishedRoute == JitCallRoute.CachedVirtual
                ? TryInvokePublishedVirtual(publishedTarget, token, out var publishedResult)
                : _calls.TryInvokeCachedJitCall(publishedRoute, token, _frame, isCallvirt,
                    constrainedToken, publishedTarget, out publishedResult);
            if (publishedFast) {
                if (hasMetadataAttributeKey)
                    _calls.CacheMetadataAttributes(metadataAttributeKey, publishedResult);
                _jitStackPointer = _frame.Stack.Count;
                if (publishedResult is { } publishedValue) Push(publishedValue);
                _frame.Ip = next;
                return;
            }
        }
        if (_frame.Method.DynamicTokens is null &&
            TryGetCachedJitCall(token, isCallvirt, constrainedToken,
                out var cachedTarget, out var cachedRoute)) {
            var cachedFast = cachedRoute == JitCallRoute.CachedVirtual
                ? TryInvokePublishedVirtual(cachedTarget, token, out var cachedResult)
                : _calls.TryInvokeCachedJitCall(cachedRoute, token, _frame, isCallvirt,
                    constrainedToken, cachedTarget, out cachedResult);
            if (cachedFast) {
                if (hasMetadataAttributeKey)
                    _calls.CacheMetadataAttributes(metadataAttributeKey, cachedResult);
                _jitStackPointer = _frame.Stack.Count;
                if (cachedResult is { } cachedValue)
                    Push(cachedValue);
                _frame.Ip = next;
                return;
            }
        }
        CallTarget? resolvedTarget = null;
        if (_frame.Method.DynamicTokens is null)
            resolvedTarget = _calls.ResolveCallTargetForFrame(_frame, token);
        VmType? virtualReceiverType = null;
        if (isCallvirt && resolvedTarget is { } receiverTarget &&
            _frame.Stack.Count >= receiverTarget.Arity &&
            _frame.Stack.ArgumentSlots(receiverTarget.Arity)[0].ObjectValue is VmClassInstance receiver)
            virtualReceiverType = receiver.ClassType;
        if (_calls.TryInvokeJitCall(token, _frame, isCallvirt, constrainedToken,
            resolvedTarget, out var fastResult, out var route)) {
            if (resolvedTarget is not null && route != JitCallRoute.None)
                CacheJitCall(token, isCallvirt, constrainedToken, resolvedTarget, route);
            if (resolvedTarget is not null && route != JitCallRoute.None)
                _compiled.CacheCallRoute(next - 1, token, isCallvirt, constrainedToken,
                    _frame.Context, resolvedTarget, route);
            if (route == JitCallRoute.CachedVirtual && virtualReceiverType is not null &&
                _frame.TryGetCachedVirtualTarget(token, virtualReceiverType, out var selectedVirtual))
                _compiled.CacheVirtualTarget(token, virtualReceiverType, _frame.Context, selectedVirtual);
            _jitStackPointer = _frame.Stack.Count;
            if (hasMetadataAttributeKey)
                _calls.CacheMetadataAttributes(metadataAttributeKey, fastResult);
            if (fastResult is { } fastValue)
                Push(fastValue);
            _frame.Ip = next;
            return;
        }
        var result = _calls.Call(token, _frame, isCallvirt,
            constrainedToken, tailCallAllowed: false, out _, validateArguments: false,
            resolvedTarget: resolvedTarget);
        _jitStackPointer = _frame.Stack.Count;
        if (result is { } value)
            Push(value);
        if (hasMetadataAttributeKey)
            _calls.CacheMetadataAttributes(metadataAttributeKey, result);
        _frame.Ip = next;
    }

    /// <summary>
    /// Fast path for a non-virtual MethodDef call from generated code.  The
    /// target is still required to be an already compiled, non-generic guest
    /// method; all other cases return through the regular call gate.
    /// </summary>
    public void CallDirect(int token, int next) {
        SyncJitStack();
        if (_elidedGetterToken == token) {
            _elidedGetterToken = 0;
            _frame.Ip = next;
            return;
        }
        if (_frame.Context is not null ||
            _frame.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true) {
            Call(token, isCallvirt: false, next);
            return;
        }
        if (_calls.TryInvokeCommonRuntimeMetadataJit(token, _frame, out var metadataResult)) {
            _jitStackPointer = _frame.Stack.Count;
            if (metadataResult is { } metadataValue)
                Push(metadataValue);
            _frame.Ip = next;
            return;
        }

        if (!_hasCachedDirectCall || _cachedDirectCallToken != token) {
            _cachedDirectCallToken = token;
            _cachedDirectCallTarget = _services.Loader.GetMethodByToken((uint)token);
            _cachedDirectCallLeaf = _cachedDirectCallTarget is { Body: not null,
                Signature.GenericParamCount: 0 } targetForLeaf
                ? _interpreter.GetNestedLeaf(targetForLeaf)
                : null;
            _hasCachedDirectCall = true;
        }

        var target = _cachedDirectCallTarget;
        if (target is null || target.Body is null || target.Signature.GenericParamCount != 0 || target.IsVirtual) {
            if (_calls.TryInvokeJitStaticIntrinsic(token, _frame, out var intrinsicValue)) {
                if (intrinsicValue is { } intrinsicResultValue)
                    Push(intrinsicResultValue);
                _frame.Ip = next;
                return;
            }
            Call(token, isCallvirt: false, next);
            return;
        }
        if (_calls.TryInvokePrimitiveFastJit(_frame, target, out var primitiveResult)) {
            _jitStackPointer = _frame.Stack.Count;
            if (primitiveResult is { } primitiveValue)
                Push(primitiveValue);
            _frame.Ip = next;
            return;
        }
        if (_calls.TryInvokeCachedBindingJit(token, _frame, constrainedToken: 0, out var boundResult)) {
            _jitStackPointer = _frame.Stack.Count;
            if (boundResult is { } boundValue)
                Push(boundValue);
            _frame.Ip = next;
            return;
        }
        // A promoted CoreLib method that has no runtime representation
        // substitution can use the same aliased stack entry as a guest
        // MethodDef.  This is deliberately guarded by the surface check and
        // intrinsic check above; runtime-layout accessors still fall through
        // to the ordinary resolver below.
        if (_services.CoreLibSurfaces?.Substitute(target) is null &&
            _interpreter.GetNestedCompiled(target) is { } compiledTarget &&
            compiledTarget.Prepared.LocalTypes.All(static local => local.Kind != SigKind.ByRef) &&
            _interpreter.TryInvokeCompiledNestedResolvedFromStack(target, compiledTarget, _frame,
                arity: target.Signature.ParamTypes.Length + (target.Signature.HasThis ? 1 : 0),
                context: null, allowStatic: target.IsStatic, out var directResult)) {
            _jitStackPointer = _frame.Stack.Count;
            if (SlotOps.SignatureReturnsValue(target.Signature))
                Push(directResult);
            _frame.Ip = next;
            return;
        }

        // The first iteration can reach this site before the callee crosses
        // its promotion threshold. Probe again on later iterations so a
        // newly published primitive leaf is not hidden by the initial null
        // cache result. Once found, the leaf remains valid for this JIT frame.
        if (_cachedDirectCallLeaf is null) {
            _cachedDirectCallLeaf = _interpreter.GetNestedLeaf(target);
        }

        var arity = target.Signature.ParamTypes.Length + (target.Signature.HasThis ? 1 : 0);
        if (_cachedDirectCallLeaf is { } leaf &&
            _interpreter.TryInvokeCompiledLeafNested(target, leaf,
                _frame.Stack.ArgumentSlots(arity), out var leafValue)) {
            _frame.Stack.DropArguments(arity);
            _jitStackPointer = _frame.Stack.Count;
            if (SlotOps.SignatureReturnsValue(target.Signature))
                Push(leafValue);
            _frame.Ip = next;
            return;
        }
        using var argumentLease = _frame.BorrowCallArguments(arity);
        var arguments = argumentLease.Arguments;
        if (!_interpreter.TryInvokeCompiled(target, arguments, null, out var value)) {
            _jitStackPointer = _frame.Stack.Count;
            for (var i = 0; i < arguments.Length; i++)
                Push(arguments[i]);
            Call(token, isCallvirt: false, next);
            return;
        }
        _jitStackPointer = _frame.Stack.Count;
        if (SlotOps.SignatureReturnsValue(target.Signature))
            Push(value);
        _frame.Ip = next;
    }

    private bool TryInvokePublishedVirtual(CallTarget target, int token,
        out StackSlot? result) {
        result = null;
        if (_frame.Stack.Count < target.Arity ||
            _frame.Stack.ArgumentSlots(target.Arity)[0].ObjectValue is not VmClassInstance receiver ||
            !_compiled.TryGetCachedVirtualTarget(token, receiver.ClassType, _frame.Context,
                out var cachedVirtual))
            return false;
        return _calls.TryInvokePublishedCompiledVirtualJit(_frame, target, cachedVirtual, out result);
    }

    private bool TryGetCachedJitCall(int token, bool isCallvirt, int constrainedToken,
        out CallTarget target, out JitCallRoute route) {
        if (_cachedJitCallToken0 == token && _cachedJitCallVirtual0 == isCallvirt &&
            _cachedJitCallConstrained0 == constrainedToken &&
            _cachedJitCallRoute0 != JitCallRoute.None && _cachedJitCallTarget0 is { } target0) {
            target = target0; route = _cachedJitCallRoute0; return true;
        }
        if (_cachedJitCallToken1 == token && _cachedJitCallVirtual1 == isCallvirt &&
            _cachedJitCallConstrained1 == constrainedToken &&
            _cachedJitCallRoute1 != JitCallRoute.None && _cachedJitCallTarget1 is { } target1) {
            target = target1; route = _cachedJitCallRoute1; return true;
        }
        if (_cachedJitCallToken2 == token && _cachedJitCallVirtual2 == isCallvirt &&
            _cachedJitCallConstrained2 == constrainedToken &&
            _cachedJitCallRoute2 != JitCallRoute.None && _cachedJitCallTarget2 is { } target2) {
            target = target2; route = _cachedJitCallRoute2; return true;
        }
        if (_cachedJitCallToken3 == token && _cachedJitCallVirtual3 == isCallvirt &&
            _cachedJitCallConstrained3 == constrainedToken &&
            _cachedJitCallRoute3 != JitCallRoute.None && _cachedJitCallTarget3 is { } target3) {
            target = target3; route = _cachedJitCallRoute3; return true;
        }
        if (_cachedJitCallToken4 == token && _cachedJitCallVirtual4 == isCallvirt &&
            _cachedJitCallConstrained4 == constrainedToken &&
            _cachedJitCallRoute4 != JitCallRoute.None && _cachedJitCallTarget4 is { } target4) {
            target = target4; route = _cachedJitCallRoute4; return true;
        }
        if (_cachedJitCallToken5 == token && _cachedJitCallVirtual5 == isCallvirt &&
            _cachedJitCallConstrained5 == constrainedToken &&
            _cachedJitCallRoute5 != JitCallRoute.None && _cachedJitCallTarget5 is { } target5) {
            target = target5; route = _cachedJitCallRoute5; return true;
        }
        if (_cachedJitCallToken6 == token && _cachedJitCallVirtual6 == isCallvirt &&
            _cachedJitCallConstrained6 == constrainedToken &&
            _cachedJitCallRoute6 != JitCallRoute.None && _cachedJitCallTarget6 is { } target6) {
            target = target6; route = _cachedJitCallRoute6; return true;
        }
        if (_cachedJitCallToken7 == token && _cachedJitCallVirtual7 == isCallvirt &&
            _cachedJitCallConstrained7 == constrainedToken &&
            _cachedJitCallRoute7 != JitCallRoute.None && _cachedJitCallTarget7 is { } target7) {
            target = target7; route = _cachedJitCallRoute7; return true;
        }
        target = null!;
        route = JitCallRoute.None;
        return false;
    }

    private void CacheJitCall(int token, bool isCallvirt, int constrainedToken,
        CallTarget target, JitCallRoute route) {
        switch (_cachedJitCallCursor++ & 7) {
            case 0:
                _cachedJitCallToken0 = token; _cachedJitCallVirtual0 = isCallvirt;
                _cachedJitCallConstrained0 = constrainedToken; _cachedJitCallTarget0 = target;
                _cachedJitCallRoute0 = route; break;
            case 1:
                _cachedJitCallToken1 = token; _cachedJitCallVirtual1 = isCallvirt;
                _cachedJitCallConstrained1 = constrainedToken; _cachedJitCallTarget1 = target;
                _cachedJitCallRoute1 = route; break;
            case 2:
                _cachedJitCallToken2 = token; _cachedJitCallVirtual2 = isCallvirt;
                _cachedJitCallConstrained2 = constrainedToken; _cachedJitCallTarget2 = target;
                _cachedJitCallRoute2 = route; break;
            case 3:
                _cachedJitCallToken3 = token; _cachedJitCallVirtual3 = isCallvirt;
                _cachedJitCallConstrained3 = constrainedToken; _cachedJitCallTarget3 = target;
                _cachedJitCallRoute3 = route; break;
            case 4:
                _cachedJitCallToken4 = token; _cachedJitCallVirtual4 = isCallvirt;
                _cachedJitCallConstrained4 = constrainedToken; _cachedJitCallTarget4 = target;
                _cachedJitCallRoute4 = route; break;
            case 5:
                _cachedJitCallToken5 = token; _cachedJitCallVirtual5 = isCallvirt;
                _cachedJitCallConstrained5 = constrainedToken; _cachedJitCallTarget5 = target;
                _cachedJitCallRoute5 = route; break;
            case 6:
                _cachedJitCallToken6 = token; _cachedJitCallVirtual6 = isCallvirt;
                _cachedJitCallConstrained6 = constrainedToken; _cachedJitCallTarget6 = target;
                _cachedJitCallRoute6 = route; break;
            default:
                _cachedJitCallToken7 = token; _cachedJitCallVirtual7 = isCallvirt;
                _cachedJitCallConstrained7 = constrainedToken; _cachedJitCallTarget7 = target;
                _cachedJitCallRoute7 = route; break;
        }
    }

    public void NewObject(int token, int next) {
        SyncJitStack();
        if (!_hasCachedStringConstructor || _cachedStringConstructorToken != token) {
            _cachedStringConstructorToken = token;
            _hasCachedStringConstructor = true;
            _cachedStringConstructorParameters = _frame.Method.DynamicTokens?.ContainsKey(
                unchecked((uint)token)) == true ? null :
                _objects.TryGetStringConstructorSignature(token, out var parameters) ? parameters : null;
        }
        if (_cachedStringConstructorParameters is { } stringParameters) {
            var stringValue = _objects.NewStringFromCtorJit(stringParameters, _frame);
            _jitStackPointer = _frame.Stack.Count;
            Push(stringValue);
            _frame.Ip = next;
            return;
        }
        // A new object immediately consumed by a trivial instance getter is
        // non-escaping.  Let the object model prove the structural shape and
        // keep only the loaded field value on the evaluation stack.
        if ((uint)next < (uint)_frame.Code.Length &&
            _frame.Code[next].Op == ILOp.Call &&
            (TableKind)((uint)_frame.Code[next].IntOperand >> 24) == TableKind.MethodDef) {
            var getterToken = _frame.Code[next].IntOperand;
            StackSlot elidedValue;
            int elidedInstructionCost;
            if (_hasCachedElidedField &&
                token == _cachedElidedConstructorToken && getterToken == _cachedElidedGetterToken) {
                elidedValue = SlotOps.StoreCopyOfValue(_frame.Stack.Peek());
                _frame.Stack.DropArguments(1);
                elidedInstructionCost = _cachedElidedInstructionCost;
                ConsumeFusedInstruction(elidedInstructionCost);
            } else if (_objects.TryNewObjectAndGetField(token, getterToken, _frame,
                    _interpreter, out elidedValue, out elidedInstructionCost)) {
                _cachedElidedConstructorToken = token;
                _cachedElidedGetterToken = getterToken;
                _cachedElidedInstructionCost = elidedInstructionCost;
                _hasCachedElidedField = true;
            } else {
                elidedValue = default;
                elidedInstructionCost = 0;
            }
            if (elidedInstructionCost > 0) {
                _jitStackPointer = _frame.Stack.Count;
                Push(elidedValue);
                _elidedGetterToken = getterToken;
                _frame.Ip = next + 1;
                return;
            }
        }
        if (_objects.TryNewArrayBackedValueType(token, _frame, out var arrayBackedValue,
            out var nestedInstructionCost)) {
            if (nestedInstructionCost > 0)
                _interpreter.ConsumeJitInstruction(nestedInstructionCost);
            _jitStackPointer = _frame.Stack.Count;
            Push(arrayBackedValue);
            _frame.Ip = next;
            return;
        }
        if (_objects.TryNewObjectLeaf(token, _frame, _interpreter, out var leafValue)) {
            _jitStackPointer = _frame.Stack.Count;
            Push(leafValue);
            _frame.Ip = next;
            return;
        }
        var value = _objects.NewObject(token, _frame);
        _jitStackPointer = _frame.Stack.Count;
        if (value is { })
            Push(value.Value);
        _frame.Ip = next;
    }

    public void NewArray(int token, int next) {
        var count = Pop().AsInt32;
        if (count < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        var objects = _objects;
        var elementType = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        using var reservation = _services.Heap.ReserveArray(count);
        var elements = new StackSlot[count];
        for (var i = 0; i < count; i++)
            elements[i] = _services.Objects.DefaultForType(elementType, _services.Loader);
        Push(StackSlot.OfObject(reservation.Commit(
            new VmArray(new VmArrayType { ElementType = elementType }, elements))));
        _frame.Ip = next;
    }

    public void LoadArrayAddress(int next) {
        var index = Pop().AsInt32;
        var array = MemoryOps.GetArray(Pop());
        MemoryOps.CheckArrayBounds(array, index);
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        Push(StackSlot.OfByRef(VmByRef.ArrayElement(array, index, isReadOnly)));
        _frame.Ip = next;
    }

    /// <summary>
    /// Fused ldelema/ldfld for the common array-of-struct path.  The ordinary
    /// IL sequence materializes a VmByRef object only to read one field from
    /// the element immediately afterwards.  Keeping the element slot local
    /// preserves the same copy semantics without allocating that transient
    /// reference.
    /// </summary>
    public void LoadArrayElementField(int fieldToken, int next) {
        var index = Pop().AsInt32;
        var array = MemoryOps.GetArray(Pop());
        MemoryOps.CheckArrayBounds(array, index);
        var field = ResolveField(fieldToken);
        var element = array.Elements[index];
        var value = element.ObjectValue is VmStructValue structure &&
            DefinitionOf(structure.StructType) is { } structureType
                ? structure.Fields[GetCachedFieldIndex(structureType, field)]
                : element.ObjectValue is VmClassInstance instance
                    ? instance.Fields[GetCachedFieldIndex(instance.ClassType, field)]
                    : _objects.ReadField(element, field);
        _frame.PendingReadonly = false;
        Push(SlotOps.PushCopyOfValue(value));
        _frame.Ip = next;
    }

    /// <summary>Fused ldelema/stobj for an array element.</summary>
    public void StoreArrayElementValue(int next) {
        var value = Pop();
        var index = Pop().AsInt32;
        var array = MemoryOps.GetArray(Pop());
        MemoryOps.CheckArrayBounds(array, index);
        if (_frame.PendingReadonly)
            throw new UnhandledGuestException("System.InvalidProgramException",
                "readonly. で作られた配列要素参照には書き込めません。");
        _frame.PendingReadonly = false;
        array.Elements[index] = SlotOps.StoreCopyOfValue(value);
        _frame.Ip = next;
    }

    public void LoadArray(MemoryOps.ArrayElementKind kind, int next) {
        var index = Pop().AsInt32;
        var array = MemoryOps.GetArray(Pop());
        MemoryOps.CheckArrayBounds(array, index);
        var slot = array.Elements[index];
        Push(kind switch {
            MemoryOps.ArrayElementKind.Int32 => StackSlot.OfInt32((int)slot.Int64Value),
            MemoryOps.ArrayElementKind.SignedByte => StackSlot.OfInt32((sbyte)slot.Int64Value),
            MemoryOps.ArrayElementKind.UnsignedByte => StackSlot.OfInt32((byte)slot.Int64Value),
            MemoryOps.ArrayElementKind.SignedShort => StackSlot.OfInt32((short)slot.Int64Value),
            MemoryOps.ArrayElementKind.UnsignedShort => StackSlot.OfInt32((ushort)slot.Int64Value),
            MemoryOps.ArrayElementKind.UnsignedInt32 => StackSlot.OfInt32(unchecked((int)(uint)slot.Int64Value)),
            MemoryOps.ArrayElementKind.Int64 => StackSlot.OfInt64(slot.Int64Value),
            MemoryOps.ArrayElementKind.NativeInt => StackSlot.OfNativeInt(slot.Int64Value),
            MemoryOps.ArrayElementKind.Float => StackSlot.OfFloat(array.ArrayType.ElementType.FullName == "System.Single"
                ? (float)slot.DoubleValue : slot.DoubleValue),
            _ => SlotOps.PushCopyOfValue(slot),
        });
        _frame.Ip = next;
    }

    public void LoadArrayByType(int token, int next) {
        var objects = _objects;
        LoadArray(MemoryOps.ElementKindFromType(objects.ResolveTypeToken(token, _frame.Context,
            _frame.Method.DynamicTokens)), next);
    }

    public void StoreArray(MemoryOps.ArrayElementKind kind, int next) {
        var value = Pop();
        var index = Pop().AsInt32;
        var array = MemoryOps.GetArray(Pop());
        MemoryOps.CheckArrayBounds(array, index);
        if (kind == MemoryOps.ArrayElementKind.Object && value.ObjectValue is not null &&
            !TypeChecks.IsAssignableToType(value.ObjectValue, array.ArrayType.ElementType, _services.StringType))
            throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                $"{SlotOps.Describe(value)} を {array.ArrayType.ElementType.FullName}[] に格納できません。");
        array.Elements[index] = kind switch {
            MemoryOps.ArrayElementKind.Int32 or MemoryOps.ArrayElementKind.UnsignedInt32 =>
                StackSlot.OfInt32(unchecked((int)(uint)value.Int64Value)),
            MemoryOps.ArrayElementKind.SignedByte or MemoryOps.ArrayElementKind.UnsignedByte =>
                StackSlot.OfInt32(unchecked((byte)value.Int64Value)),
            MemoryOps.ArrayElementKind.SignedShort or MemoryOps.ArrayElementKind.UnsignedShort =>
                StackSlot.OfInt32(unchecked((ushort)value.Int64Value)),
            MemoryOps.ArrayElementKind.Int64 => StackSlot.OfInt64(value.Int64Value),
            MemoryOps.ArrayElementKind.NativeInt => StackSlot.OfNativeInt(value.Int64Value),
            MemoryOps.ArrayElementKind.Float => StackSlot.OfFloat(array.ArrayType.ElementType.FullName == "System.Single"
                ? (float)value.DoubleValue : value.DoubleValue),
            _ => SlotOps.StoreCopyOfValue(value),
        };
        _frame.Ip = next;
    }

    public void StoreArrayByType(int token, int next) {
        var objects = _objects;
        StoreArray(MemoryOps.ElementKindFromType(objects.ResolveTypeToken(token, _frame.Context,
            _frame.Method.DynamicTokens)), next);
    }

    public void LoadArrayLength(int next) {
        Push(StackSlot.OfNativeInt(MemoryOps.GetArray(Pop()).Length));
        _frame.Ip = next;
    }

    public void LoadField(int token, int next) {
        var objects = _objects;
        var field = ResolveField(token);
        var receiver = Pop();
        var value = TryReadCachedClassField(receiver, field, out var cachedValue)
            ? cachedValue
            : TryReadCachedByRefField(receiver, field, out var byRefValue)
                ? byRefValue
                : objects.ReadField(receiver, field);
        Push(SlotOps.PushCopyOfValue(value));
        _frame.Ip = next;
    }

    public void StoreField(int token, int next) {
        var objects = _objects;
        var field = ResolveField(token);
        EnsureFieldWritable(field);
        var value = Pop();
        var receiver = Pop();
        if (!objects.TryStoreStringField(receiver, field, value) &&
            !TryWriteCachedClassField(receiver, field, value) &&
            !TryWriteCachedByRefField(receiver, field, value))
            objects.WriteField(receiver, field, value);
        _frame.Ip = next;
    }

    public void LoadFieldAddress(int token, int next) {
        var objects = _objects;
        var field = ResolveField(token);
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        var receiver = Pop();
        if (!TryCreateCachedFieldAddress(receiver, field, isReadOnly, out var address))
            address = objects.FieldAddress(receiver, field, isReadOnly);
        Push(address);
        _frame.Ip = next;
    }

    public void LoadStaticField(int token, int next) {
        var objects = _objects;
        Push(SlotOps.PushCopyOfValue(objects.StaticFieldLocation(token, _frame.Context,
            _frame.Method.DynamicTokens).Read()));
        _frame.Ip = next;
    }

    public void StoreStaticField(int token, int next) {
        var objects = _objects;
        var field = ResolveField(token);
        EnsureFieldWritable(field);
        objects.StaticFieldLocation(token, _frame.Context, _frame.Method.DynamicTokens)
            .Write(Pop());
        _frame.Ip = next;
    }

    public void LoadStaticFieldAddress(int token, int next) {
        var objects = _objects;
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        Push(objects.TryGetStaticFieldRvaAddress(token) ??
            StackSlot.OfByRef(objects.StaticFieldLocation(token, _frame.Context,
                _frame.Method.DynamicTokens, isReadOnly)));
        _frame.Ip = next;
    }

    public void LoadIndirect(ILOp op, int next) {
        Push(MemoryOps.LoadIndirectJit(op, Pop()));
        _frame.Ip = next;
    }

    public void StoreIndirect(ILOp op, int next) {
        var value = Pop();
        MemoryOps.StoreIndirectJit(op, Pop(), value);
        _frame.Ip = next;
    }

    public void LoadObject(int token, int next) {
        var objects = _objects;
        var address = Pop();
        if (address.ObjectValue is VmNativePointer native) {
            var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "ldobj");
            Push(MemoryOps.ReadPointerValue(native, type));
        } else if (address.ObjectValue is VmByRef byRef) {
            Push(SlotOps.PushCopyOfValue(byRef.Slot));
        } else {
            throw InvalidAddress("ldobj", address);
        }
        _frame.Ip = next;
    }

    public void StoreObject(int token, int next) {
        var objects = _objects;
        var value = Pop();
        var address = Pop();
        if (address.ObjectValue is VmNativePointer native) {
            var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "stobj", writable: true);
            MemoryOps.WritePointerValue(native, type, value);
        } else if (address.ObjectValue is VmByRef byRef) {
            byRef.Write(SlotOps.StoreCopyOfValue(value));
        } else {
            throw InvalidAddress("stobj", address);
        }
        _frame.Ip = next;
    }

    public void CopyObject(int token, int next) {
        var objects = _objects;
        var source = Pop();
        var destination = Pop();
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        if (source.ObjectValue is VmNativePointer sourceNative &&
            destination.ObjectValue is VmNativePointer destinationNative) {
            MemoryOps.CopyMemoryBlock(destination, source, MemoryOps.SizeOfRawType(type));
        } else if (source.ObjectValue is VmNativePointer sourceOnly) {
            if (destination.ObjectValue is not VmByRef destinationByRef)
                throw InvalidAddress("cpobj", destination);
            destinationByRef.Write(SlotOps.StoreCopyOfValue(MemoryOps.ReadPointerValue(sourceOnly, type)));
        } else if (destination.ObjectValue is VmNativePointer destinationOnly) {
            if (source.ObjectValue is not VmByRef sourceByRef)
                throw InvalidAddress("cpobj", source);
            MemoryOps.WritePointerValue(destinationOnly, type, sourceByRef.Read());
        } else if (source.ObjectValue is VmByRef sourceByRef &&
                   destination.ObjectValue is VmByRef destinationByRef) {
            destinationByRef.Write(SlotOps.StoreCopyOfValue(sourceByRef.Read()));
        } else {
            throw InvalidAddress("cpobj", destination);
        }
        _frame.Ip = next;
    }

    public void InitObject(int token, int next) {
        var objects = _objects;
        var address = Pop();
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        if (address.ObjectValue is VmNativePointer native) {
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "initobj", writable: true);
            Array.Clear(native.Bytes, native.ByteOffset, size);
            native.Memory.ClearReferences(native.ByteOffset, size);
        } else if (address.ObjectValue is VmByRef byRef) {
            byRef.Write(_services.Objects.DefaultForType(type, _services.Loader));
        } else {
            throw InvalidAddress("initobj", address);
        }
        _frame.Ip = next;
    }

    private static void EnsureNativeRange(VmNativePointer pointer, int size, string operation,
        bool writable = false) {
        if (writable)
            pointer.EnsureWritable();
        pointer.EnsureBounds(size);
    }

    private static UnhandledGuestException InvalidAddress(string operation, in StackSlot address) =>
        new("System.InvalidProgramException",
            $"{operation} のアドレスがマネージ参照または有効な unmanaged ポインタではありません: {SlotOps.Describe(address)}");

    public void Unbox(int token, int next) {
        var objects = _objects;
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = Pop();
        if (value.ObjectValue is not VmBoxedValue boxed || !TypeChecks.IsExactUnboxType(boxed.Type, target))
            throw new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} を {target.FullName} として unbox できません。");
        Push(StackSlot.OfByRef(VmByRef.BoxedValue(boxed)));
        _frame.Ip = next;
    }

    private void EnsureFieldWritable(VmField field) {
        if (!field.IsInitOnly)
            return;
        var allowed = _frame.Method.CanWriteInitOnly(field);
        if (!allowed)
            throw new UnhandledGuestException("System.FieldAccessException",
                $"readonly フィールド {field} はコンストラクター外から書き込めません。");
    }

    private VmField ResolveField(int token) {
        if (_frame.Context is null && _frame.Method.DynamicTokens is null &&
            _frame.TryGetCachedField(token, null, out var persistentField))
            return persistentField;
        if (_frame.Method.DynamicTokens is null &&
            _frame.TryGetCachedField(token, _frame.Context, out persistentField))
            return persistentField;
        // A generated frame is tied to one method/loader. Cache the resolved
        // field at the call site so repeated field access in a hot loop does
        // not repeat the metadata cache lookup. Contextual/dynamic tokens are
        // deliberately excluded because their target can vary per invocation.
        if (_frame.Context is null && _frame.Method.DynamicTokens is null) {
            if (_hasCachedField0 && _cachedFieldToken0 == token) return _cachedField0!;
            if (_hasCachedField1 && _cachedFieldToken1 == token) return _cachedField1!;
            if (_hasCachedField2 && _cachedFieldToken2 == token) return _cachedField2!;
            if (_hasCachedField3 && _cachedFieldToken3 == token) return _cachedField3!;
        }
        var field = _objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        if (_frame.Method.DynamicTokens is null)
            _frame.CacheField(token, _frame.Context, field);
        if (_frame.Context is null && _frame.Method.DynamicTokens is null) {
            switch (_cachedFieldCursor++ & 3) {
                case 0: _cachedFieldToken0 = token; _cachedField0 = field; _hasCachedField0 = true; break;
                case 1: _cachedFieldToken1 = token; _cachedField1 = field; _hasCachedField1 = true; break;
                case 2: _cachedFieldToken2 = token; _cachedField2 = field; _hasCachedField2 = true; break;
                default: _cachedFieldToken3 = token; _cachedField3 = field; _hasCachedField3 = true; break;
            }
        }
        return field;
    }

    private int GetCachedFieldIndex(VmClassType type, VmField field) {
        if (_frame.TryGetCachedFieldIndex(type, field, out var persistentIndex))
            return persistentIndex;
        if (ReferenceEquals(type, _cachedFieldReceiverType0) &&
            ReferenceEquals(field, _cachedFieldIndexField0)) return _cachedFieldIndex0;
        if (ReferenceEquals(type, _cachedFieldReceiverType1) &&
            ReferenceEquals(field, _cachedFieldIndexField1)) return _cachedFieldIndex1;
        if (ReferenceEquals(type, _cachedFieldReceiverType2) &&
            ReferenceEquals(field, _cachedFieldIndexField2)) return _cachedFieldIndex2;
        if (ReferenceEquals(type, _cachedFieldReceiverType3) &&
            ReferenceEquals(field, _cachedFieldIndexField3)) return _cachedFieldIndex3;

        var index = _objects.GetInstanceFieldIndexForJit(type, field);
        _frame.CacheFieldIndex(type, field, index);
        switch (_cachedFieldIndexCursor++ & 3) {
            case 0:
                _cachedFieldReceiverType0 = type; _cachedFieldIndexField0 = field; _cachedFieldIndex0 = index; break;
            case 1:
                _cachedFieldReceiverType1 = type; _cachedFieldIndexField1 = field; _cachedFieldIndex1 = index; break;
            case 2:
                _cachedFieldReceiverType2 = type; _cachedFieldIndexField2 = field; _cachedFieldIndex2 = index; break;
            default:
                _cachedFieldReceiverType3 = type; _cachedFieldIndexField3 = field; _cachedFieldIndex3 = index; break;
        }
        return index;
    }

    private bool TryReadCachedClassField(in StackSlot receiver, VmField field, out StackSlot value) {
        if (receiver.ObjectValue is not VmClassInstance instance) {
            value = default;
            return false;
        }
        var type = instance.ClassType;
        // Generated code always runs under the coordinator's guest read
        // lease. GC cannot scan or move VM storage until that lease ends, and
        // normal guest field races have the same relaxed visibility as CLR
        // ordinary fields, so the per-access monitor would only serialize
        // every CoreLib field load.
        value = instance.Fields[GetCachedFieldIndex(type, field)];
        return true;
    }

    private bool TryWriteCachedClassField(in StackSlot receiver, VmField field, in StackSlot value) {
        if (receiver.ObjectValue is not VmClassInstance instance) {
            return false;
        }
        var type = instance.ClassType;
        instance.Fields[GetCachedFieldIndex(type, field)] = SlotOps.StoreCopyOfValue(value);
        return true;
    }

    private bool TryReadCachedByRefField(in StackSlot receiver, VmField field, out StackSlot value) {
        if (receiver.ObjectValue is not VmByRef byRef)
            goto no;
        var target = byRef.Slot;
        if (target.ObjectValue is VmStructValue structure &&
            DefinitionOf(structure.StructType) is { } structType) {
            value = structure.Fields[GetCachedFieldIndex(structType, field)];
            return true;
        }
        if (target.ObjectValue is VmClassInstance instance) {
            value = instance.Fields[GetCachedFieldIndex(instance.ClassType, field)];
            return true;
        }
    no:
        value = default;
        return false;
    }

    private bool TryWriteCachedByRefField(in StackSlot receiver, VmField field, in StackSlot value) {
        if (receiver.ObjectValue is not VmByRef byRef)
            goto no;
        byRef.EnsureWritable();
        var target = byRef.Slot;
        if (target.ObjectValue is VmStructValue structure &&
            DefinitionOf(structure.StructType) is { } structType) {
            structure.Fields[GetCachedFieldIndex(structType, field)] =
                SlotOps.StoreCopyOfValue(value);
            return true;
        }
        if (target.ObjectValue is VmClassInstance instance) {
            instance.Fields[GetCachedFieldIndex(instance.ClassType, field)] =
                SlotOps.StoreCopyOfValue(value);
            return true;
        }
    no:
        return false;
    }

    private bool TryCreateCachedFieldAddress(in StackSlot receiver, VmField field,
        bool isReadOnly, out StackSlot address) {
        if (receiver.ObjectValue is VmClassInstance instance) {
            address = StackSlot.OfByRef(VmByRef.OwnedStorage(instance, instance.Fields,
                GetCachedFieldIndex(instance.ClassType, field), isReadOnly));
            return true;
        }
        if (receiver.ObjectValue is VmBoxedValue boxed) {
            if (DefinitionOf(boxed.Type) is { } boxedType) {
                address = StackSlot.OfByRef(VmByRef.OwnedStorage(boxed, boxed.Fields,
                    GetCachedFieldIndex(boxedType, field), isReadOnly));
                return true;
            }
            address = StackSlot.OfByRef(VmByRef.BoxedValue(boxed, isReadOnly: isReadOnly));
            return true;
        }
        if (receiver.Kind == StackKind.ValueType && receiver.ObjectValue is VmStructValue structure &&
            DefinitionOf(structure.StructType) is { } structureType) {
            address = StackSlot.OfByRef(new VmByRef(structure.Fields,
                GetCachedFieldIndex(structureType, field), isReadOnly,
                owner: null, elementType: MemoryOps.FixedBufferStorage(structureType)?.ElementType));
            return true;
        }
        if (receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef outer) {
            var target = outer.Slot;
            if (target.ObjectValue is VmStructValue nested &&
                DefinitionOf(nested.StructType) is { } nestedType) {
                address = StackSlot.OfByRef(new VmByRef(nested.Fields,
                    GetCachedFieldIndex(nestedType, field), isReadOnly || outer.IsReadOnly,
                    outer.Owner, MemoryOps.FixedBufferStorage(nestedType)?.ElementType,
                    outer.IsFrameStorage));
                return true;
            }
            if (target.ObjectValue is VmClassInstance nestedInstance) {
                address = StackSlot.OfByRef(VmByRef.OwnedStorage(nestedInstance,
                    nestedInstance.Fields, GetCachedFieldIndex(nestedInstance.ClassType, field),
                    isReadOnly || outer.IsReadOnly));
                return true;
            }
        }
        address = default;
        return false;
    }

    private static VmClassType? DefinitionOf(VmType type) => type switch {
        VmClassType cls => cls,
        VmConstructedType constructed => constructed.Definition as VmClassType,
        _ => null,
    };

    public void Box(int token, int next) {
        var objects = _objects;
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = Pop();
        if (!type.IsValueType) {
            Push(value);
            _frame.Ip = next;
            return;
        }
        var fields = value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue sv
            ? sv.Clone().Fields
            : [value];
        Push(StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields))));
        _frame.Ip = next;
    }

    /// <summary>
    /// Fused box + brtrue/brfalse.  Generic CoreLib code frequently boxes a
    /// value only to test whether the result is null.  A value-type box is
    /// always non-null and a reference-type box preserves the original
    /// reference, so the allocation is unobservable at this call site.
    /// </summary>
    public void BoxNullBranch(int branchTarget, int fallthrough, bool branchIfTrue,
        int branchInstructionCost) {
        BeginInstruction(branchInstructionCost);
        try {
            var value = Pop();
            // Primitive/value slots have no ObjectValue payload, but boxing a
            // value type still produces a non-null object.  Only an actual
            // null object reference is null at this fused test.
            var nonNull = value.Kind != StackKind.Object || value.ObjectValue is not null;
            _frame.Ip = nonNull == branchIfTrue ? branchTarget : fallthrough;
        } finally {
            EndInstruction();
        }
    }

    /// <summary>Fast-accounting counterpart of <see cref="BoxNullBranch"/>.</summary>
    public void BoxNullBranchFast(int branchTarget, int fallthrough, bool branchIfTrue) {
        var value = Pop();
        var nonNull = value.Kind != StackKind.Object || value.ObjectValue is not null;
        _frame.Ip = nonNull == branchIfTrue ? branchTarget : fallthrough;
    }

    public void UnboxAny(int token, int next) {
        var objects = _objects;
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = Pop();
        if (target.IsValueType) {
            if (value.ObjectValue is not VmBoxedValue boxed || !TypeChecks.IsExactUnboxType(boxed.Type, target))
                throw new UnhandledGuestException("System.InvalidCastException",
                    $"{SlotOps.Describe(value)} を {target.FullName} に unbox.any できません。");
            if (VmPrimitiveTypes.IsSlotPrimitive(target.FullName))
                Push(boxed.Fields[0]);
            else if (target is VmClassType or VmConstructedType) {
                var args = boxed.Type is VmConstructedType constructed ? constructed.TypeArguments : null;
                Push(StackSlot.OfValueType(new VmStructValue(target,
                    (StackSlot[])boxed.Fields.Clone(), args)));
            } else
                Push(boxed.Fields[0]);
        } else {
            var ok = value.ObjectValue is null ||
                TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
            if (!ok)
                throw new UnhandledGuestException("System.InvalidCastException",
                    $"{SlotOps.Describe(value)} を {target.FullName} に変換できません。");
            Push(value);
        }
        _frame.Ip = next;
    }

    public void LoadToken(int token, int next) {
        if (_frame.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true) {
            VmObject handle = dynamicReference switch {
                VmType dynamicType => _objects.GetCachedDynamicTypeHandle(dynamicType),
                VmMethod dynamicMethod => _services.Heap.Allocate(new VmMethodHandle { Target = dynamicMethod }),
                VmField dynamicField => _services.Heap.Allocate(new VmFieldHandle { Target = dynamicField }),
                _ => throw new NotSupportedException("動的 ldtoken の参照種別は未対応です."),
            };
            Push(StackSlot.OfObject(handle));
            _frame.Ip = next;
            return;
        }

        var loader = _services.Loader;
        var tokenTable = (TableKind)((uint)token >> 24);
        var tokenRid = (int)((uint)token & 0xFFFFFF);
        switch (tokenTable) {
            case TableKind.Field: {
                var rva = loader.Image.GetFieldRva(tokenRid);
                if (rva == 0)
                    throw new BadImageFormatException($"Field rid {tokenRid} に FieldRVA エントリがありません。");
                var field = _objects.ResolveFieldToken(
                    token, _frame.Context, _frame.Method.DynamicTokens);
                var fieldType = field.FieldType
                    ?? throw new BadImageFormatException($"FieldRVA {field} の型を解決できません。");
                var fieldSize = MemoryOps.SizeOfType(fieldType);
                var imageData = loader.Image.GetRvaDataToEnd(rva);
                if (fieldSize > imageData.Length)
                    throw new BadImageFormatException(
                        $"FieldRVA {field} のデータ長 {imageData.Length} が型サイズ {fieldSize} 未満です。");
                var handle = _services.Heap.Allocate(new VmFieldRvaData {
                    Data = imageData[..fieldSize].ToArray(), OwnerLoader = loader,
                });
                Push(StackSlot.OfObject(handle));
                break;
            }
            case TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec: {
                Push(StackSlot.OfObject(_objects.GetCachedTypeHandle(
                    token, _frame.Context, _frame.Method.DynamicTokens)));
                break;
            }
            case TableKind.MethodDef: {
                var method = loader.GetMethodByToken((uint)token)
                    ?? throw new BadImageFormatException($"MethodDef rid {tokenRid} を解決できません。");
                Push(StackSlot.OfObject(
                    _services.Heap.Allocate(new VmMethodHandle { Target = method })));
                break;
            }
            default:
                throw new NotSupportedException(
                    $"ldtoken は Field/Type/Method トークンのみ対応しています (要求: {tokenTable})。");
        }
        _frame.Ip = next;
    }

    public void Cast(int token, bool isInst, int next) {
        var objects = _objects;
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = Pop();
        var ok = value.ObjectValue is null ||
            TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
        if (!ok && !isInst)
            throw new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} を {target.FullName} にキャストできません。");
        Push(ok ? value : StackSlot.Null);
        _frame.Ip = next;
    }

    public void Throw() => throw _interpreter.JitExceptionsFor(_frame.Method)
        .MakeGuestThrow(Pop());

    public void CheckFinite(int next) {
        var value = Pop();
        if (value.Kind == StackKind.Float &&
            (double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue)))
            throw new UnhandledGuestException("System.ArithmeticException", null);
        Push(value);
        _frame.Ip = next;
    }

    public void SizeOf(int token, int next) {
        var objects = _objects;
        Push(StackSlot.OfInt32(MemoryOps.SizeOfType(
            objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens))));
        _frame.Ip = next;
    }

    public void Return(bool hasValue) {
        ReturnValue = hasValue ? Pop() : default;
        Returned = true;
    }
}

/// <summary>
/// Expression-tree compiler for the first JIT tier.  Calls, EH, unmanaged
/// pointers, typed references and runtime-dependent instructions deliberately
/// fall back to the interpreter. Managed ByRefs use the VM's slot containers,
/// and the supported tier reuses VM call/object/array helpers so ordinary
/// non-EH methods can be promoted without exposing CLR objects.
/// </summary>
internal static class JitMethodCompiler {
    private const int MaxInstructions = 4096;

    internal readonly record struct CompilationCost(long WorkUnits, long HostMemoryBytes,
        long ExpressionNodes, long SwitchTargets);

    public static CompilationCost? TryEstimate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, MemoryPolicy memory) {
        if (method.Body is null || method.Body.IlCode.Length > memory.MaxJitMethodBodyBytes ||
            !CanCompile(method, prepared, code)) {
            return null;
        }

        long switchTargets = 0;
        foreach (var instruction in code)
            if (instruction.SwitchTargets is { } targets)
                switchTargets = checked(switchTargets + targets.Length);

        // A switch target is represented by a constant array and each IL
        // instruction expands to several expression nodes (try/finally,
        // dispatch case, constants and helper call). Count both before any
        // target array or expression node is allocated.
        var expressionNodes = checked(16L + code.Length * 12L + switchTargets * 2L);
        // A primitive straight-line leaf also gets a second, frame-free
        // delegate.  Reserve its expression and compile work up front rather
        // than letting the specialization bypass JIT resource accounting.
        if (IsSlotLeafCandidate(method, prepared, code) || IsConstructorLeafCandidate(method, prepared, code))
            expressionNodes = checked(expressionNodes + 8L + code.Length * 8L);
        if (expressionNodes > memory.MaxJitExpressionNodes)
            return null;
        var workUnits = checked(expressionNodes + method.Body.IlCode.Length + switchTargets);
        var hostMemoryBytes = checked(4096L + expressionNodes * 64L + switchTargets * 8L);
        return new CompilationCost(workUnits, hostMemoryBytes, expressionNodes, switchTargets);
    }

    public static JitCompiledMethod? TryCompile(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, MethodPreparer preparer, bool fastExecution = false) {
        if (!CanCompile(method, prepared, code))
            return null;
        try {
            // Keep both entries for scalar methods: the frame-free entry is
            // safe only at the outer host boundary, while a nested call must
            // still have a frame-backed entry so its arguments/locals remain
            // visible to GC and the normal execution state.
            var scalarCandidate = ScalarIntJit.TryCreate(method, prepared, code, preparer);
            if (scalarCandidate is { } scalar) {
                var directScalar = ScalarIntJit.TryCreateDirect(method, prepared, code, preparer);
                JitLeaf? scalarLeaf = null;
                try {
                    scalarLeaf = TryCompileLeaf(method, prepared, code);
                } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or NotSupportedException or PlatformNotSupportedException) {
                    // Scalar/frame execution remains valid when the optional
                    // allocation-free leaf expression is unavailable.
                }
                return new JitCompiledMethod(null, prepared, scalarLeaf, scalar, directScalar);
            }

            var frame = Expression.Parameter(typeof(JitFrame), "frame");
            var cases = new SwitchCase[code.Length];
            var offsets = new Dictionary<int, int>(code.Length * 2);
            for (var i = 0; i < code.Length; i++)
                offsets[code[i].Offset] = i;
            var branchTargets = new HashSet<int>();
            foreach (var instruction in code) {
                if (instruction.BranchTargetIndex >= 0)
                    branchTargets.Add(instruction.BranchTargetIndex);
                if (instruction.SwitchTargetIndices is { } switchIndices)
                    foreach (var target in switchIndices)
                        branchTargets.Add(target);
            }

            var fusedBoxBranches = new bool[code.Length];
            for (var i = 0; i < code.Length; i++) {
                if (TryGetBoxNullBranch(code, i, branchTargets, offsets, out var boxBranch)) {
                    fusedBoxBranches[i] = true;
                    var fusedBoxOperation = BuildBoxNullBranch(frame, code[i], boxBranch, fastExecution);
                    cases[i] = Expression.SwitchCase(fusedBoxOperation, Expression.Constant(i));
                    continue;
                }
                if (i > 0 && fusedBoxBranches[i - 1])
                    continue;
                var instruction = code[i];
                var fused = TryGetArrayFusion(code, i, branchTargets, out var fusion);
                var next = i + 1 + (fused ? 1 : 0);
                var operation = BuildOperation(frame, instruction, next, offsets,
                    SlotOps.SignatureReturnsValue(method.Signature), fusion);
                var instructionBody = fastExecution
                    ? operation
                    : Expression.TryFinally(
                        Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction)),
                                Constant(instruction.InstructionCost)), operation),
                        Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
                cases[i] = Expression.SwitchCase(instructionBody, Expression.Constant(i));
            }

            Expression execution;
            if (IsStraightLine(code)) {
                var operations = new List<Expression>(code.Length * 2 + 2) {
                    Expression.Call(frame, Method(nameof(JitFrame.BeginExecution))),
                };
                if (fastExecution) {
                    operations.Add(Expression.Call(frame, Method(nameof(JitFrame.FastBlock)),
                        Constant(code.Sum(instruction => instruction.InstructionCost)),
                        Constant(code.Length)));
                    for (var i = 0; i < code.Length; i++) {
                        if (i > 0 && (TryGetArrayFusion(code, i - 1, branchTargets, out _) || fusedBoxBranches[i - 1]))
                            continue;
                        if (TryGetBoxNullBranch(code, i, branchTargets, offsets, out var boxBranch)) {
                            operations.Add(BuildBoxNullBranch(frame, code[i], boxBranch, fastExecution: true));
                            continue;
                        }
                        var fused = TryGetArrayFusion(code, i, branchTargets, out var fusion);
                        operations.Add(BuildOperation(frame, code[i], i + 1 + (fused ? 1 : 0), offsets,
                            SlotOps.SignatureReturnsValue(method.Signature), fusion));
                    }
                } else {
                    for (var i = 0; i < code.Length; i++) {
                        if (i > 0 && (TryGetArrayFusion(code, i - 1, branchTargets, out _) || fusedBoxBranches[i - 1]))
                            continue;
                        if (TryGetBoxNullBranch(code, i, branchTargets, offsets, out var boxBranch)) {
                            var fusedBoxOperation = BuildBoxNullBranch(frame, code[i], boxBranch, fastExecution: false);
                            operations.Add(fusedBoxOperation);
                            continue;
                        }
                        var fused = TryGetArrayFusion(code, i, branchTargets, out var fusion);
                        var instructionBody = Expression.TryFinally(
                            Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction)),
                                    Constant(code[i].InstructionCost)),
                                BuildOperation(frame, code[i], i + 1 + (fused ? 1 : 0), offsets,
                                    SlotOps.SignatureReturnsValue(method.Signature), fusion)),
                            Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
                        operations.Add(instructionBody);
                    }
                }
                operations.Add(Expression.Property(frame, nameof(JitFrame.ReturnValue)));
                execution = Expression.Block(operations);
            } else {
                var blockStarts = BuildBlockStarts(code, offsets);
                var blockCosts = new int[code.Length];
                var blockCounts = new int[code.Length];
                for (var start = 0; start < code.Length;) {
                    if (!blockStarts[start]) {
                        start++;
                        continue;
                    }
                    var end = start + 1;
                    while (end < code.Length && !blockStarts[end])
                        end++;
                    var cost = 0;
                    for (var i = start; i < end; i++)
                        cost = checked(cost + code[i].InstructionCost);
                    for (var i = start; i < end; i++) {
                        blockCosts[i] = cost;
                        blockCounts[i] = end - start;
                    }
                    start = end;
                }
                if (fastExecution) {
                    for (var i = 0; i < cases.Length; i++) {
                        if (!blockStarts[i])
                            continue;
                        var end = i + 1;
                        while (end < code.Length && !blockStarts[end])
                            end++;
                        var operations = new List<Expression>(end - i + 1) {
                            Expression.Call(frame, Method(nameof(JitFrame.FastBlock)),
                                Constant(blockCosts[i]), Constant(blockCounts[i])),
                        };
                        for (var j = i; j < end; j++) {
                            if (j > i && (TryGetArrayFusion(code, j - 1, branchTargets, out _) ||
                                          TryGetBoxNullBranch(code, j - 1, branchTargets, offsets, out _)))
                                continue;
                            if (TryGetBoxNullBranch(code, j, branchTargets, offsets, out var boxBranch)) {
                                operations.Add(BuildBoxNullBranch(frame, code[j], boxBranch, fastExecution: true));
                                break;
                            }
                            var fused = TryGetArrayFusion(code, j, branchTargets, out var fusion);
                            operations.Add(BuildOperation(frame, code[j], j + 1 + (fused ? 1 : 0), offsets,
                                SlotOps.SignatureReturnsValue(method.Signature), fusion));
                        }
                        var operation = Expression.Block(operations);
                        cases[i] = Expression.SwitchCase(
                            operation,
                            Expression.Constant(i));
                    }
                }
                var returnLabel = Expression.Label(typeof(StackSlot), "jitReturn");
                var invalidIp = Expression.Throw(Expression.New(
                    typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                    Expression.Constant($"JIT フレームの命令位置が不正です: {method}")));
                var loopBody = Expression.Block(
                    Expression.Switch(Expression.Property(frame, nameof(JitFrame.InstructionPointer)),
                        invalidIp, null, cases.Where(@case => @case is not null).ToArray()),
                    Expression.IfThen(Expression.Property(frame, nameof(JitFrame.Returned)),
                        Expression.Break(returnLabel, Expression.Property(frame, nameof(JitFrame.ReturnValue)))));
                var loop = Expression.Loop(loopBody, returnLabel);
                execution = Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginExecution))), loop);
            }
            if (prepared.Clauses is { Length: 1 } clauses &&
                TryBuildFinallyBlock(frame, clauses[0], code, offsets, fastExecution, out var finallyBlock))
                execution = Expression.TryFinally(execution, finallyBlock);
            var body = Expression.TryFinally(
                execution,
                Expression.Call(frame, Method(nameof(JitFrame.EndExecution))));
            var lambda = Expression.Lambda<Func<JitFrame, StackSlot>>(body, frame).Compile();
            JitLeaf? leaf = null;
            try {
                leaf = TryCompileLeaf(method, prepared, code);
            } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or NotSupportedException or PlatformNotSupportedException) {
                // The frame-based delegate remains valid even when the
                // allocation-free leaf specialization is not expressible.
            }
            return new JitCompiledMethod(lambda, prepared, leaf);
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NotSupportedException or PlatformNotSupportedException) {
            // An expression compiler limitation is a normal JIT rejection, not
            // a guest failure.  The cache records the rejection and the caller
            // continues through the interpreter.
            return null;
        }
    }

    private static bool CanCompile(VmMethod method, PreparedMethod prepared, DecodedInstruction[] code) {
        if (method.Body is null || code.Length == 0 || code.Length > MaxInstructions ||
            prepared.Clauses is { Length: > 0 } &&
            (!IsAsyncStateMachineMoveNext(method) && !CanCompileFinally(prepared.Clauses, code))) {
            return false;
        }
        if (ContainsUnsupportedType(method.Signature.ReturnType) ||
            method.Signature.ParamTypes.Any(ContainsUnsupportedType) ||
            prepared.LocalTypes.Any(ContainsUnsupportedType)) {
            return false;
        }

        var offsets = new HashSet<int>(code.Select(instruction => instruction.Offset));
        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            if (instruction.Fusion.Kind != IlFusionKind.None &&
                !IlFusionValidation.IsValid(instruction.Fusion, prepared.LocalTypes.Length, out _))
                return false;
            if (!IsSupported(instruction.Op))
                return false;
            if (instruction.Op is ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3
                && (int)(instruction.Op - ILOp.Ldarg_0) >= method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0))
                return false;
            if (instruction.Op is ILOp.Ldarg or ILOp.Ldarg_S or ILOp.Ldarga or ILOp.Ldarga_S
                or ILOp.Starg or ILOp.Starg_S)
                if ((uint)instruction.IntOperand >= (uint)(method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0)))
                    return false;
            if (instruction.Op is ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3
                && (int)(instruction.Op - ILOp.Ldloc_0) >= prepared.LocalTypes.Length)
                return false;
            if (instruction.Op is ILOp.Ldloc or ILOp.Ldloc_S or ILOp.Ldloca or ILOp.Ldloca_S
                or ILOp.Stloc or ILOp.Stloc_S)
                if ((uint)instruction.IntOperand >= (uint)prepared.LocalTypes.Length)
                    return false;
            if (instruction.Op == ILOp.Readonly &&
                (i + 1 >= code.Length || code[i + 1].Op is not
                    (ILOp.Ldelema or ILOp.Ldflda or ILOp.Ldsflda)))
                return false;
            if (instruction.OperandKind is IlOperandKind.ShortBrTarget or IlOperandKind.BrTarget &&
                !offsets.Contains(instruction.IntOperand))
                return false;
            if (instruction.Op == ILOp.Switch && (instruction.SwitchTargets is null ||
                instruction.SwitchTargets.Any(target => !offsets.Contains(target))))
                return false;
        }
        return true;
    }

    private static bool ContainsUnsupportedType(SigType type) => type.Kind switch {
        // Generic signatures are still represented by StackSlot at runtime;
        // field/array/token helpers resolve their concrete types through the
        // active GenericContext. Keep the actual unsafe representations out
        // of this tier, but do not reject ordinary generic BCL methods solely
        // because their open signature contains !T or List<T>.
        SigKind.GenericVar or SigKind.GenericMethodVar => false,
        SigKind.GenericInst => type.Args is null || type.Args.Any(ContainsUnsupportedType),
        SigKind.Pointer or SigKind.TypedByRef => true,
        SigKind.ByRef => type.Inner is not null && ContainsUnsupportedType(type.Inner),
        SigKind.SzArray or SigKind.Array => type.Inner is not null && ContainsUnsupportedType(type.Inner),
        _ => false,
    };

    private static bool IsStraightLine(DecodedInstruction[] code) {
        if (code.Length == 0 || code[^1].Op != ILOp.Ret)
            return false;
        for (var i = 0; i < code.Length - 1; i++)
            if (code[i].OperandKind is IlOperandKind.ShortBrTarget or IlOperandKind.BrTarget ||
                code[i].Op == ILOp.Switch || code[i].Op == ILOp.Ret)
                return false;
        return true;
    }

    private static bool CanCompileFinally(PreparedClause[] clauses, DecodedInstruction[] code) {
        if (clauses.Length != 1 || clauses[0].Kind != ExceptionClauseKind.Finally)
            return false;
        var clause = clauses[0];
        if (clause.HandlerStart < 0 || clause.HandlerEnd <= clause.HandlerStart ||
            clause.HandlerEnd > code.Length || code[clause.HandlerEnd - 1].Op != ILOp.Endfinally)
            return false;
        if (TryGetFinallyDisposePattern(clause, code, out _, out _))
            return true;
        for (var i = clause.HandlerStart; i < clause.HandlerEnd - 1; i++) {
            var instruction = code[i];
            if (!IsSupported(instruction.Op) || IsControlFlow(instruction) ||
                instruction.Op is ILOp.Endfilter or ILOp.Ret)
                return false;
        }
        // A normal branch into a finally handler would execute it twice: once
        // through the generated control flow and once through the host finally.
        for (var i = 0; i < code.Length; i++) {
            if (i >= clause.HandlerStart && i < clause.HandlerEnd)
                continue;
            var target = code[i].BranchTargetIndex;
            if (target == clause.HandlerStart)
                return false;
        }
        return true;
    }

    private static bool TryGetFinallyDisposePattern(PreparedClause clause,
        DecodedInstruction[] code, out int localIndex, out int disposeToken) {
        localIndex = -1;
        disposeToken = 0;
        // ldloc; brfalse handler-end; ldloc; callvirt Dispose; endfinally
        if (clause.HandlerEnd - clause.HandlerStart != 5)
            return false;
        var load = code[clause.HandlerStart];
        var branch = code[clause.HandlerStart + 1];
        var reload = code[clause.HandlerStart + 2];
        var call = code[clause.HandlerStart + 3];
        if (load.Op is not (ILOp.Ldloc or ILOp.Ldloc_S or ILOp.Ldloc_0 or ILOp.Ldloc_1 or
                ILOp.Ldloc_2 or ILOp.Ldloc_3) ||
            branch.Op is not (ILOp.BrFalse or ILOp.BrFalse_S) ||
            reload.Op != load.Op || call.Op != ILOp.Callvirt ||
            branch.BranchTargetIndex != clause.HandlerEnd - 1)
            return false;
        localIndex = LocalIndex(load);
        if (localIndex < 0 || LocalIndex(reload) != localIndex)
            return false;
        disposeToken = call.IntOperand;
        return true;

        static int LocalIndex(DecodedInstruction instruction) => instruction.Op switch {
            ILOp.Ldloc_0 => 0,
            ILOp.Ldloc_1 => 1,
            ILOp.Ldloc_2 => 2,
            ILOp.Ldloc_3 => 3,
            ILOp.Ldloc or ILOp.Ldloc_S => instruction.IntOperand,
            _ => -1,
        };
    }

    private static bool TryBuildFinallyBlock(ParameterExpression frame, PreparedClause clause,
        DecodedInstruction[] code, IReadOnlyDictionary<int, int> offsets, bool fastExecution,
        out Expression block) {
        block = null!;
        if (!CanCompileFinally([clause], code))
            return false;

        if (TryGetFinallyDisposePattern(clause, code, out var localIndex, out var disposeToken)) {
            block = Expression.Call(frame, Method(nameof(JitFrame.FinallyDisposeLocal)),
                Constant(localIndex), Constant(disposeToken), Constant(clause.HandlerEnd));
            return true;
        }

        var statements = new List<Expression>(clause.HandlerEnd - clause.HandlerStart + 2) {
            Expression.Call(frame, Method(nameof(JitFrame.BeginFinally))),
        };
        var handlerCost = 0;
        var handlerCount = 0;
        for (var i = clause.HandlerStart; i < clause.HandlerEnd - 1; i++) {
            var instruction = code[i];
            handlerCost = checked(handlerCost + instruction.InstructionCost);
            handlerCount++;
        }
        if (fastExecution && handlerCount > 0)
            statements.Add(Expression.Call(frame, Method(nameof(JitFrame.FastBlock)),
                Constant(handlerCost), Constant(handlerCount)));
        for (var i = clause.HandlerStart; i < clause.HandlerEnd - 1; i++) {
            var instruction = code[i];
            var next = i + 1;
            var operation = BuildOperation(frame, instruction, next, offsets,
                hasReturnValue: false);
            if (fastExecution) {
                statements.Add(operation);
            } else {
                var guarded = Expression.TryFinally(
                    Expression.Block(
                        Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction)),
                            Constant(instruction.InstructionCost)), operation),
                    Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
                statements.Add(guarded);
            }
        }
        block = Expression.Block(statements);
        return true;
    }

    private static bool[] BuildBlockStarts(DecodedInstruction[] code,
        IReadOnlyDictionary<int, int> offsets) {
        var starts = new bool[code.Length + 1];
        starts[0] = true;
        for (var i = 0; i < code.Length; i++) {
            if (IsControlFlow(code[i]) || code[i].Op == ILOp.Ret)
                starts[i + 1] = true;
            if (IsControlFlow(code[i])) {
                var target = code[i].BranchTargetIndex >= 0
                    ? code[i].BranchTargetIndex : offsets[code[i].IntOperand];
                starts[target] = true;
            }
        }
        return starts;
    }

    private static bool IsControlFlow(DecodedInstruction instruction) =>
        instruction.Op is ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or
        ILOp.BrFalse or ILOp.BrFalse_S or ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or
        ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or ILOp.Bge_Un_S or
        ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble or
        ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
        ILOp.Blt_Un or ILOp.Blt_Un_S or ILOp.Switch or ILOp.Leave or ILOp.Leave_S;

    private static bool IsAsyncStateMachineMoveNext(VmMethod method) =>
        method.Name == "MoveNext" && method.DeclaringType.Interfaces.Any(iface =>
            iface.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine");

    // Expression trees cannot consume a ref-returning span indexer directly.
    // Copy the single slot at the IL load, without allocating an argument array.
    public static StackSlot ReadLeafArgument(ReadOnlySpan<StackSlot> arguments, int index) => arguments[index];

    /// <summary>Leaf JIT 用の配列長/要素参照。通常の IL helper と同じ境界検査を保つ。</summary>
    public static StackSlot ReadLeafArrayLength(StackSlot arraySlot) =>
        StackSlot.OfNativeInt(MemoryOps.GetArray(arraySlot).Length);

    public static StackSlot ReadLeafArrayAddress(StackSlot arraySlot, StackSlot indexSlot) {
        var array = MemoryOps.GetArray(arraySlot);
        var index = indexSlot.AsInt32;
        MemoryOps.CheckArrayBounds(array, index);
        return StackSlot.OfByRef(VmByRef.ArrayElement(array, index, isReadOnly: false));
    }

    /// <summary>
    /// HashHelpers.FastMod is a pure arithmetic CoreLib helper. Inline this
    /// general primitive shape while compiling a byref-producing leaf so a
    /// tiny address helper does not need a nested guest frame.
    /// </summary>
    public static StackSlot LeafFastMod(StackSlot value, StackSlot divisor, StackSlot multiplier) {
        var product = unchecked((ulong)(uint)value.Int64Value * (ulong)multiplier.Int64Value);
        var scaled = unchecked(((product >> 32) + 1UL) * (uint)divisor.Int64Value);
        var result = unchecked((int)(scaled >> 32));
        return StackSlot.OfInt32(result);
    }

    private static JitLeaf? TryCompileLeaf(VmMethod method,
        PreparedMethod prepared, DecodedInstruction[] code) {
        if (IsConstructorLeafCandidate(method, prepared, code))
            return TryCompileConstructorLeaf(method, code);
        if (!IsSlotLeafCandidate(method, prepared, code))
            return null;

        var interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
        var context = Expression.Parameter(typeof(GenericContext), "context");
        var arguments = Expression.Parameter(typeof(ReadOnlySpan<StackSlot>), "arguments");
        var readArgument = typeof(JitMethodCompiler).GetMethod(nameof(ReadLeafArgument))!;
        var defaultLocal = typeof(JitMethodCompiler).GetMethod(nameof(DefaultLeafLocal))!;
        var statements = new List<Expression>(code.Length + prepared.LocalTypes.Length + 1);
        var locals = Enumerable.Range(0, prepared.LocalTypes.Length)
            .Select(i => Expression.Variable(typeof(StackSlot), $"local{i}"))
            .ToArray();
        for (var i = 0; i < locals.Length; i++)
            statements.Add(Expression.Assign(locals[i], Expression.Call(defaultLocal,
                Expression.Constant(prepared.LocalTypes[i].Kind))));
        var stack = new List<Expression>();
        var consume = typeof(Interpreter).GetMethod(nameof(Interpreter.ConsumeJitInstruction),
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int)])!;
        var binary = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafBinaryArithmetic))!;
        var unary = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafUnaryArithmetic))!;
        var convert = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafConvertValue))!;
        var compare = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafCompare))!;
        var readField = typeof(Interpreter).GetMethod(nameof(Interpreter.ReadLeafField),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var readArrayLength = typeof(JitMethodCompiler).GetMethod(nameof(ReadLeafArrayLength))!;
        var readArrayAddress = typeof(JitMethodCompiler).GetMethod(nameof(ReadLeafArrayAddress))!;
        var fastMod = typeof(JitMethodCompiler).GetMethod(nameof(LeafFastMod))!;
        var storeField = typeof(Interpreter).GetMethod(nameof(Interpreter.StoreLeafField),
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(VmMethod), typeof(GenericContext), typeof(int), typeof(StackSlot), typeof(StackSlot)])!;
        var ofInt32 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt32), [typeof(int)])!;
        var ofInt64 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt64), [typeof(long)])!;
        var ofFloat = typeof(StackSlot).GetMethod(nameof(StackSlot.OfFloat), [typeof(double)])!;
        var nullProperty = typeof(StackSlot).GetProperty(nameof(StackSlot.Null))!;
        var aggregateQuota = CanAggregateLeafQuota(code);
        if (aggregateQuota)
            statements.Add(Expression.Call(interpreter, consume,
                Expression.Constant(code.Sum(instruction => instruction.InstructionCost))));

        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            if (!aggregateQuota)
                statements.Add(Expression.Call(interpreter, consume,
                    Expression.Constant(instruction.InstructionCost)));
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                    break;
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    stack.Add(Expression.Call(readArgument, arguments,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldarg_0))));
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    stack.Add(Expression.Call(readArgument, arguments, Expression.Constant(instruction.IntOperand)));
                    break;
                case ILOp.Ldnull:
                    stack.Add(Expression.Property(null, nullProperty));
                    break;
                case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                    var localIndex0 = (int)(instruction.Op - ILOp.Ldloc_0);
                    if ((uint)localIndex0 >= (uint)locals.Length)
                        return null;
                    stack.Add(locals[localIndex0]);
                    break;
                case ILOp.Ldloc_S or ILOp.Ldloc:
                    if ((uint)instruction.IntOperand >= (uint)locals.Length)
                        return null;
                    stack.Add(locals[instruction.IntOperand]);
                    break;
                case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                    var localIndex1 = (int)(instruction.Op - ILOp.Stloc_0);
                    if (stack.Count == 0 || (uint)localIndex1 >= (uint)locals.Length)
                        return null;
                    var localValue1 = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    statements.Add(Expression.Assign(locals[localIndex1], localValue1));
                    break;
                case ILOp.Stloc_S or ILOp.Stloc:
                    if (stack.Count == 0 || (uint)instruction.IntOperand >= (uint)locals.Length)
                        return null;
                    var localValue = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    statements.Add(Expression.Assign(locals[instruction.IntOperand], localValue));
                    break;
                case ILOp.Ldc_I4_M1:
                    stack.Add(Expression.Call(ofInt32, Expression.Constant(-1)));
                    break;
                case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                    stack.Add(Expression.Call(ofInt32,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldc_I4_0))));
                    break;
                case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                    stack.Add(Expression.Call(ofInt32, Expression.Constant(instruction.IntOperand)));
                    break;
                case ILOp.Ldc_I8:
                    stack.Add(Expression.Call(ofInt64, Expression.Constant(instruction.LongOperand)));
                    break;
                case ILOp.Ldc_R4 or ILOp.Ldc_R8:
                    stack.Add(Expression.Call(ofFloat, Expression.Constant(instruction.DoubleOperand)));
                    break;
                case ILOp.Pop:
                    if (stack.Count == 0)
                        return null;
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case ILOp.Ldfld:
                    if (stack.Count == 0)
                        return null;
                    var fieldReceiver = stack[^1];
                    stack[^1] = Expression.Call(interpreter, readField,
                        Expression.Constant(method), context,
                        Expression.Constant(instruction.IntOperand), fieldReceiver);
                    break;
                case ILOp.Ldlen:
                    if (stack.Count == 0)
                        return null;
                    stack[^1] = Expression.Call(readArrayLength, stack[^1]);
                    break;
                case ILOp.Ldelema:
                    if (stack.Count < 2)
                        return null;
                    var elementIndex = stack[^1];
                    var elementArray = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(Expression.Call(readArrayAddress, elementArray, elementIndex));
                    break;
                case ILOp.Call:
                    if (!IsHashHelpersFastMod(method.Loader, instruction.IntOperand) || stack.Count < 3)
                        return null;
                    var fastModMultiplier = stack[^1];
                    var fastModDivisor = stack[^2];
                    var fastModValue = stack[^3];
                    stack.RemoveRange(stack.Count - 3, 3);
                    stack.Add(Expression.Call(fastMod, fastModValue, fastModDivisor, fastModMultiplier));
                    break;
                case ILOp.Stfld:
                    if (stack.Count < 2)
                        return null;
                    var fieldValue = stack[^1];
                    var fieldReceiverForStore = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    statements.Add(Expression.Call(interpreter, storeField,
                        Expression.Constant(method), context,
                        Expression.Constant(instruction.IntOperand), fieldReceiverForStore, fieldValue));
                    break;
                case ILOp.Neg or ILOp.Not:
                    if (stack.Count == 0)
                        return null;
                    var unaryValue = stack[^1];
                    stack[^1] = Expression.Call(unary, Expression.Constant(instruction.Op), unaryValue);
                    break;
                case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un
                    or ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
                    or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un
                    or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un:
                    if (stack.Count < 2)
                        return null;
                    var right = stack[^1];
                    var left = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(Expression.Call(binary, Expression.Constant(instruction.Op), left, right));
                    break;
                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                    if (stack.Count < 2)
                        return null;
                    var compareRight = stack[^1];
                    var compareLeft = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    var compared = Expression.Call(compare, Expression.Constant(instruction.Op),
                        compareLeft, compareRight);
                    stack.Add(Expression.Call(ofInt32, Expression.Condition(compared,
                        Expression.Constant(1), Expression.Constant(0))));
                    break;
                case ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8 or ILOp.Conv_R4
                    or ILOp.Conv_R8 or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4 or ILOp.Conv_U8
                    or ILOp.Conv_I or ILOp.Conv_U or ILOp.Conv_R_Un:
                    if (stack.Count == 0)
                        return null;
                    var converted = stack[^1];
                    stack[^1] = Expression.Call(convert, Expression.Constant(instruction.Op), converted);
                    break;
                case ILOp.Ret:
                    if (i != code.Length - 1)
                        return null;
                    var returnsValue = method.Signature.ReturnType.Kind != SigKind.Void;
                    if (stack.Count != (returnsValue ? 1 : 0))
                        return null;
                    statements.Add(returnsValue ? stack[^1] : Expression.Default(typeof(StackSlot)));
                    return Expression.Lambda<JitLeaf>(
                        Expression.Block(locals, statements), interpreter, context, arguments).Compile();
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool IsLeafType(SigType type) => type.Kind is
        SigKind.Void or SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1 or
        SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4 or SigKind.I8 or SigKind.U8 or
        SigKind.I or SigKind.U or SigKind.R4 or SigKind.R8 or SigKind.ByRef;

    public static StackSlot DefaultLeafLocal(SigKind kind) => kind switch {
        SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1 or SigKind.I2 or SigKind.U2 or
            SigKind.I4 or SigKind.U4 => StackSlot.OfInt32(0),
        SigKind.I8 or SigKind.U8 => StackSlot.OfInt64(0),
        SigKind.I or SigKind.U => StackSlot.OfNativeInt(0),
        SigKind.R4 or SigKind.R8 => StackSlot.OfFloat(0),
        _ => StackSlot.Null,
    };

    // A frame-free leaf is deliberately defined by the operations it can
    // lower, rather than by a whitelist of primitive method signatures.  This
    // lets ordinary getters/setters and other small object/array helpers use
    // the same allocation-free path without giving any collection a special
    // route.  Unsupported IL still causes TryCompileLeaf to return null.
    private static bool IsSlotLeafCandidate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) =>
        IsStraightLine(code) && prepared.LocalTypes.All(IsLeafType) &&
        !ContainsUnsupportedType(method.Signature.ReturnType) &&
        method.Signature.ParamTypes.All(type => !ContainsUnsupportedType(type));

    // A straight-line primitive leaf containing only non-throwing operations
    // can charge its original IL cost once.  Division/remainder and checked
    // arithmetic remain instruction-granular so quota exhaustion and guest
    // exceptions keep their original ordering.
    private static bool CanAggregateLeafQuota(DecodedInstruction[] code) => code.All(instruction =>
            instruction.Op is ILOp.Nop or ILOp.Break or
        ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg or
        ILOp.Ldnull or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or
        ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or
        ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Ldc_I8 or ILOp.Ldc_R4 or ILOp.Ldc_R8 or
        ILOp.Pop or ILOp.Neg or ILOp.Not or ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.And or ILOp.Or or
        ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
        ILOp.Clt or ILOp.Clt_Un or ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8 or
        ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4 or ILOp.Conv_U8 or
        ILOp.Conv_I or ILOp.Conv_U or ILOp.Conv_R_Un or ILOp.Ret);

    private static bool IsHashHelpersFastMod(TypeLoader? loader, int token) {
        if (loader is null || (TableKind)((uint)token >> 24) != TableKind.MemberRef)
            return false;
        var rid = token & 0xFFFFFF;
        return loader.GetMemberRefName(rid) == "FastMod" &&
            loader.GetMemberRefParentTypeName(rid) == "System.Collections.HashHelpers";
    }

    // Capture-free instance lambdas use only primitive parameters. A leaf
    // must not load the receiver or derive an address from it.
    private static bool LoadsReceiver(DecodedInstruction instruction) => instruction.Op == ILOp.Ldarg_0 ||
        instruction.Op is ILOp.Ldarg or ILOp.Ldarg_S or ILOp.Ldarga or ILOp.Ldarga_S && instruction.IntOperand == 0;

    private static bool IsConstructorLeafCandidate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) =>
        method.Name == ".ctor" && method.Signature.HasThis &&
        method.Signature.ReturnType.Kind == SigKind.Void && prepared.LocalTypes.Length == 0 &&
        method.Signature.ParamTypes.All(IsLeafType) && IsStraightLine(code) &&
        code.Any(instruction => instruction.Op == ILOp.Stfld);

    private static JitLeaf? TryCompileConstructorLeaf(
        VmMethod method, DecodedInstruction[] code) {
        var interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
        var context = Expression.Parameter(typeof(GenericContext), "context");
        var arguments = Expression.Parameter(typeof(ReadOnlySpan<StackSlot>), "arguments");
        var readArgument = typeof(JitMethodCompiler).GetMethod(nameof(ReadLeafArgument))!;
        var statements = new List<Expression>(code.Length + 1);
        var stack = new List<Expression>();
        var consume = typeof(Interpreter).GetMethod(nameof(Interpreter.ConsumeJitInstruction),
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int)])!;
        var storeField = typeof(Interpreter).GetMethod(nameof(Interpreter.StoreLeafField),
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(VmMethod), typeof(StackSlot), typeof(StackSlot)])!;
        var ofInt32 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt32), [typeof(int)])!;
        var ofInt64 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt64), [typeof(long)])!;
        var ofFloat = typeof(StackSlot).GetMethod(nameof(StackSlot.OfFloat), [typeof(double)])!;

        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            statements.Add(Expression.Call(interpreter, consume,
                Expression.Constant(instruction.InstructionCost)));
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                    break;
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    stack.Add(Expression.Call(readArgument, arguments,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldarg_0))));
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    stack.Add(Expression.Call(readArgument, arguments, Expression.Constant(instruction.IntOperand)));
                    break;
                case ILOp.Ldc_I4_M1:
                    stack.Add(Expression.Call(ofInt32, Expression.Constant(-1)));
                    break;
                case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                    stack.Add(Expression.Call(ofInt32,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldc_I4_0))));
                    break;
                case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                    stack.Add(Expression.Call(ofInt32, Expression.Constant(instruction.IntOperand)));
                    break;
                case ILOp.Ldc_I8:
                    stack.Add(Expression.Call(ofInt64, Expression.Constant(instruction.LongOperand)));
                    break;
                case ILOp.Ldc_R4 or ILOp.Ldc_R8:
                    stack.Add(Expression.Call(ofFloat, Expression.Constant(instruction.DoubleOperand)));
                    break;
                case ILOp.Pop:
                    if (stack.Count == 0)
                        return null;
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case ILOp.Stfld:
                    if (stack.Count < 2)
                        return null;
                    var fieldValue = stack[^1];
                    var fieldReceiver = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    statements.Add(Expression.Call(interpreter, storeField,
                        Expression.Constant(method), Expression.Constant(instruction.IntOperand),
                        fieldReceiver, fieldValue));
                    break;
                case ILOp.Ret:
                    if (i != code.Length - 1 || stack.Count != 0)
                        return null;
                    statements.Add(Expression.Default(typeof(StackSlot)));
                    return Expression.Lambda<JitLeaf>(
                        Expression.Block(statements), interpreter, context, arguments).Compile();
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool IsSupported(ILOp op) => op switch {
        ILOp.Nop or ILOp.Break or
        ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg
            or ILOp.Ldarga_S or ILOp.Ldarga or
        ILOp.Starg_S or ILOp.Starg or
        ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or ILOp.Ldloc_S or ILOp.Ldloc
            or ILOp.Ldloca_S or ILOp.Ldloca or
        ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3 or ILOp.Stloc_S or ILOp.Stloc or
        ILOp.Ldnull or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2
            or ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7
            or ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Ldc_I8 or ILOp.Ldc_R4 or ILOp.Ldc_R8
        or ILOp.Dup or ILOp.Pop or
        ILOp.Br_S or ILOp.BrFalse_S or ILOp.BrTrue_S or ILOp.Beq_S or ILOp.Bge_S or ILOp.Bgt_S
            or ILOp.Ble_S or ILOp.Blt_S or ILOp.Bne_Un_S or ILOp.Bge_Un_S or ILOp.Bgt_Un_S
            or ILOp.Ble_Un_S or ILOp.Blt_Un_S or ILOp.Br or ILOp.BrFalse or ILOp.BrTrue or ILOp.Beq
            or ILOp.Bge or ILOp.Bgt or ILOp.Ble or ILOp.Blt or ILOp.Bne_Un or ILOp.Bge_Un
            or ILOp.Bgt_Un or ILOp.Ble_Un or ILOp.Blt_Un or ILOp.Switch or ILOp.Leave or ILOp.Leave_S or
        ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un or
        ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2 or ILOp.Ldind_I4
            or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I or ILOp.Ldind_R4 or ILOp.Ldind_R8
            or ILOp.Ldind_Ref or ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or ILOp.Stind_I2
            or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8 or
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un
            or ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
            or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un
            or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un or ILOp.Neg or ILOp.Not or
        ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8 or ILOp.Conv_R4 or ILOp.Conv_R8
            or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4 or ILOp.Conv_U8 or ILOp.Conv_I or ILOp.Conv_U
            or ILOp.Conv_R_Un or ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un
            or ILOp.Conv_Ovf_I8_Un or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un
            or ILOp.Conv_Ovf_U8_Un or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un or ILOp.Conv_Ovf_I1
            or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or ILOp.Conv_Ovf_U2 or ILOp.Conv_Ovf_I4
            or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I8 or ILOp.Conv_Ovf_U8 or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U
            or ILOp.Ldstr or ILOp.Call or ILOp.Callvirt or ILOp.Newobj or ILOp.Newarr or ILOp.Ldlen
            or ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2 or ILOp.Ldelem_I4
            or ILOp.Ldelem_U4 or ILOp.Ldelem_I8 or ILOp.Ldelem_I or ILOp.Ldelem_R4 or ILOp.Ldelem_R8
            or ILOp.Ldelem_Ref or ILOp.Ldelem or ILOp.Ldelema or ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2
            or ILOp.Stelem_I4 or ILOp.Stelem_I8 or ILOp.Stelem_R4 or ILOp.Stelem_R8 or ILOp.Stelem_Ref
            or ILOp.Stelem or ILOp.Ldfld or ILOp.Ldflda or ILOp.Stfld or ILOp.Ldsfld or ILOp.Ldsflda
            or ILOp.Stsfld or ILOp.Ldobj or ILOp.Stobj or ILOp.Cpobj or ILOp.Initobj or ILOp.Box
            or ILOp.Unbox or ILOp.Unbox_Any or ILOp.Castclass or ILOp.Isinst or ILOp.Throw
            or ILOp.Ckfinite or ILOp.Ldtoken or ILOp.Readonly or ILOp.Constrained or ILOp.Endfinally
            or ILOp.Sizeof or ILOp.Ret => true,
        _ => false,
    };

    private readonly record struct BoxNullBranchInfo(int BranchTarget, int Fallthrough,
        bool BranchIfTrue, int BranchInstructionCost);

    private static bool TryGetBoxNullBranch(DecodedInstruction[] code, int index,
        HashSet<int> branchTargets, IReadOnlyDictionary<int, int> offsets,
        out BoxNullBranchInfo result) {
        result = default;
        if (code[index].Op != ILOp.Box || index + 1 >= code.Length ||
            branchTargets.Contains(index + 1))
            return false;
        var branch = code[index + 1];
        if (branch.Op is not (ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S))
            return false;
        var target = branch.BranchTargetIndex >= 0
            ? branch.BranchTargetIndex : offsets[branch.IntOperand];
        result = new BoxNullBranchInfo(target, index + 2,
            branch.Op is ILOp.BrTrue or ILOp.BrTrue_S, branch.InstructionCost);
        return true;
    }

    private static Expression BuildBoxNullBranch(ParameterExpression frame,
        DecodedInstruction box, BoxNullBranchInfo branch, bool fastExecution) {
        if (fastExecution)
            return Call(frame, nameof(JitFrame.BoxNullBranchFast),
                Constant(branch.BranchTarget), Constant(branch.Fallthrough),
                Constant(branch.BranchIfTrue));

        return Expression.TryFinally(
            Expression.Block(
                Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction)),
                    Constant(box.InstructionCost)),
                Call(frame, nameof(JitFrame.BoxNullBranch),
                    Constant(branch.BranchTarget), Constant(branch.Fallthrough),
                    Constant(branch.BranchIfTrue), Constant(branch.BranchInstructionCost))),
            Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
    }

    private static Expression BuildOperation(ParameterExpression frame, DecodedInstruction instruction,
        int next, IReadOnlyDictionary<int, int> offsets, bool hasReturnValue,
        (ILOp Op, int FieldToken)? fusion = null) {
        var op = instruction.Op;
        if (instruction.Fusion.Kind != IlFusionKind.None)
            return Call(frame, nameof(JitFrame.FusedLocalOperation),
                Expression.Constant(instruction.Fusion), Constant(next));
        if (op is ILOp.Nop or ILOp.Break)
            return Call(frame, nameof(JitFrame.NoOp), Constant(next));
        if (op is ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3)
            return Call(frame, nameof(JitFrame.LoadArgument), Constant((int)(op - ILOp.Ldarg_0)), Constant(next));
        if (op is ILOp.Ldarg_S or ILOp.Ldarg)
            return Call(frame, nameof(JitFrame.LoadArgument), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Ldarga_S or ILOp.Ldarga)
            return Call(frame, nameof(JitFrame.LoadArgumentAddress), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Starg_S or ILOp.Starg)
            return Call(frame, nameof(JitFrame.StoreArgument), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3)
            return Call(frame, nameof(JitFrame.LoadLocal), Constant((int)(op - ILOp.Ldloc_0)), Constant(next));
        if (op is ILOp.Ldloc_S or ILOp.Ldloc)
            return Call(frame, nameof(JitFrame.LoadLocal), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Ldloca_S or ILOp.Ldloca)
            return Call(frame, nameof(JitFrame.LoadLocalAddress), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3)
            return Call(frame, nameof(JitFrame.StoreLocal), Constant((int)(op - ILOp.Stloc_0)), Constant(next));
        if (op is ILOp.Stloc_S or ILOp.Stloc)
            return Call(frame, nameof(JitFrame.StoreLocal), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldnull)
            return Call(frame, nameof(JitFrame.LoadNull), Constant(next));
        if (op == ILOp.Ldc_I4_M1)
            return Call(frame, nameof(JitFrame.LoadInt32), Constant(-1), Constant(next));
        if (op is >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8)
            return Call(frame, nameof(JitFrame.LoadInt32), Constant((int)(op - ILOp.Ldc_I4_0)), Constant(next));
        if (op is ILOp.Ldc_I4_S or ILOp.Ldc_I4)
            return Call(frame, nameof(JitFrame.LoadInt32), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldc_I8)
            return Call(frame, nameof(JitFrame.LoadInt64), Constant(instruction.LongOperand), Constant(next));
        if (op is ILOp.Ldc_R4 or ILOp.Ldc_R8)
            return Call(frame, nameof(JitFrame.LoadFloat), Constant(instruction.DoubleOperand), Constant(next));
        if (op == ILOp.Dup)
            return Call(frame, nameof(JitFrame.Duplicate), Constant(next));
        if (op == ILOp.Pop)
            return Call(frame, nameof(JitFrame.Drop), Constant(next));
        if (op == ILOp.Readonly)
            return Call(frame, nameof(JitFrame.SetReadonly), Constant(next));
        if (op == ILOp.Constrained)
            return Call(frame, nameof(JitFrame.SetConstrained), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Br or ILOp.Br_S or ILOp.BrFalse or ILOp.BrFalse_S or ILOp.BrTrue or ILOp.BrTrue_S
            or ILOp.Beq or ILOp.Beq_S or ILOp.Bge or ILOp.Bge_S or ILOp.Bgt or ILOp.Bgt_S
            or ILOp.Ble or ILOp.Ble_S or ILOp.Blt or ILOp.Blt_S or ILOp.Bne_Un or ILOp.Bne_Un_S
            or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble_Un or ILOp.Ble_Un_S
            or ILOp.Blt_Un or ILOp.Blt_Un_S) {
            var target = instruction.BranchTargetIndex >= 0
                ? instruction.BranchTargetIndex : offsets[instruction.IntOperand];
            if (op is ILOp.Br or ILOp.Br_S)
                return Call(frame, nameof(JitFrame.BranchAlways), Constant(target));
            if (op is ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S)
                return Call(frame, nameof(JitFrame.BranchUnary),
                    Constant(op is ILOp.BrTrue or ILOp.BrTrue_S), Constant(target), Constant(next));
            return Call(frame, nameof(JitFrame.Branch), Constant(op), Constant(target), Constant(next));
        }
        if (op is ILOp.Leave or ILOp.Leave_S) {
            var target = instruction.BranchTargetIndex >= 0
                ? instruction.BranchTargetIndex : offsets[instruction.IntOperand];
            return Call(frame, nameof(JitFrame.BranchAlways), Constant(target));
        }
        if (op == ILOp.Endfinally)
            return Call(frame, nameof(JitFrame.NoOp), Constant(next));
        if (op == ILOp.Switch)
            return Call(frame, nameof(JitFrame.Switch),
                Expression.Constant(instruction.SwitchTargetIndices ??
                    instruction.SwitchTargets!.Select(target => offsets[target]).ToArray()), Constant(next));
        if (op is ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un)
            return Call(frame, nameof(JitFrame.Compare), Constant(op), Constant(next));
        if (IsIndirectLoad(op))
            return Call(frame, nameof(JitFrame.LoadIndirect), Constant(op), Constant(next));
        if (IsIndirectStore(op))
            return Call(frame, nameof(JitFrame.StoreIndirect), Constant(op), Constant(next));
        if (op == ILOp.Call && (TableKind)((uint)instruction.IntOperand >> 24) == TableKind.MethodDef)
            return Call(frame, nameof(JitFrame.CallDirect), Constant(instruction.IntOperand),
                Constant(next));
        if (op is ILOp.Call or ILOp.Callvirt)
            return Call(frame, nameof(JitFrame.Call), Constant(instruction.IntOperand),
                Constant(op == ILOp.Callvirt), Constant(next));
        if (IsBinary(op))
            return Call(frame, nameof(JitFrame.Binary), Constant(op), Constant(next));
        if (op is ILOp.Neg or ILOp.Not)
            return Call(frame, nameof(JitFrame.Unary), Constant(op), Constant(next));
        if (IsConversion(op))
            return Call(frame, nameof(JitFrame.Convert), Constant(op), Constant(next));
        if (op == ILOp.Ldstr)
            return Call(frame, nameof(JitFrame.LoadString), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Newobj)
            return Call(frame, nameof(JitFrame.NewObject), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Newarr)
            return Call(frame, nameof(JitFrame.NewArray), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldlen)
            return Call(frame, nameof(JitFrame.LoadArrayLength), Constant(next));
        if (op == ILOp.Ldelema)
            if (fusion is { Op: ILOp.Ldfld, FieldToken: var fieldToken })
                return Call(frame, nameof(JitFrame.LoadArrayElementField), Constant(fieldToken), Constant(next));
            else if (fusion is { Op: ILOp.Stobj })
                return Call(frame, nameof(JitFrame.StoreArrayElementValue), Constant(next));
        if (op == ILOp.Ldelema)
            return Call(frame, nameof(JitFrame.LoadArrayAddress), Constant(next));
        if (IsArrayLoad(op))
            return op == ILOp.Ldelem
                ? Call(frame, nameof(JitFrame.LoadArrayByType), Constant(instruction.IntOperand), Constant(next))
                : Call(frame, nameof(JitFrame.LoadArray), Constant(ArrayKind(op)), Constant(next));
        if (IsArrayStore(op))
            return op == ILOp.Stelem
                ? Call(frame, nameof(JitFrame.StoreArrayByType), Constant(instruction.IntOperand), Constant(next))
                : Call(frame, nameof(JitFrame.StoreArray), Constant(ArrayKind(op)), Constant(next));
        if (op == ILOp.Ldfld)
            return Call(frame, nameof(JitFrame.LoadField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldflda)
            return Call(frame, nameof(JitFrame.LoadFieldAddress), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Stfld)
            return Call(frame, nameof(JitFrame.StoreField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldsfld)
            return Call(frame, nameof(JitFrame.LoadStaticField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldsflda)
            return Call(frame, nameof(JitFrame.LoadStaticFieldAddress), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Stsfld)
            return Call(frame, nameof(JitFrame.StoreStaticField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Box)
            return Call(frame, nameof(JitFrame.Box), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldobj)
            return Call(frame, nameof(JitFrame.LoadObject), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Stobj)
            return Call(frame, nameof(JitFrame.StoreObject), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Cpobj)
            return Call(frame, nameof(JitFrame.CopyObject), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Initobj)
            return Call(frame, nameof(JitFrame.InitObject), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Unbox)
            return Call(frame, nameof(JitFrame.Unbox), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Unbox_Any)
            return Call(frame, nameof(JitFrame.UnboxAny), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Castclass or ILOp.Isinst)
            return Call(frame, nameof(JitFrame.Cast), Constant(instruction.IntOperand),
                Constant(op == ILOp.Isinst), Constant(next));
        if (op == ILOp.Throw)
            return Call(frame, nameof(JitFrame.Throw));
        if (op == ILOp.Ckfinite)
            return Call(frame, nameof(JitFrame.CheckFinite), Constant(next));
        if (op == ILOp.Sizeof)
            return Call(frame, nameof(JitFrame.SizeOf), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldtoken)
            return Call(frame, nameof(JitFrame.LoadToken), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ret)
            return Call(frame, nameof(JitFrame.Return), Expression.Constant(hasReturnValue));
        throw new InvalidOperationException($"未対応の JIT 命令です: {op}");
    }

    // BuildOperation needs the method return shape only for ret.  It is supplied
    // separately below to keep the operation builder independent of frame state.
    private static MethodInfo Method(string name) => typeof(JitFrame).GetMethod(name,
        BindingFlags.Instance | BindingFlags.Public)!;

    private static MethodCallExpression Call(Expression frame, string name, params Expression[] args) =>
        Expression.Call(frame, Method(name), args);

    private static ConstantExpression Constant(int value) => Expression.Constant(value);
    private static ConstantExpression Constant(long value) => Expression.Constant(value);
    private static ConstantExpression Constant(double value) => Expression.Constant(value);
    private static ConstantExpression Constant(bool value) => Expression.Constant(value);
    private static ConstantExpression Constant(ILOp value) => Expression.Constant(value);
    private static ConstantExpression Constant(MemoryOps.ArrayElementKind value) => Expression.Constant(value);

    private static bool TryGetArrayFusion(DecodedInstruction[] code, int index,
        IReadOnlySet<int> branchTargets, out (ILOp Op, int FieldToken)? fusion) {
        fusion = null;
        if ((uint)index >= (uint)code.Length - 1 || branchTargets.Contains(index + 1) ||
            code[index].Op != ILOp.Ldelema)
            return false;
        var next = code[index + 1].Op;
        if (next == ILOp.Ldfld)
            fusion = (next, code[index + 1].IntOperand);
        else if (next == ILOp.Stobj)
            fusion = (next, 0);
        else
            return false;
        return true;
    }

    private static bool IsIndirectLoad(ILOp op) => op is ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2
        or ILOp.Ldind_U2 or ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I
        or ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref;
    private static bool IsIndirectStore(ILOp op) => op is ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1
        or ILOp.Stind_I2 or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8;
    private static bool IsBinary(ILOp op) => op is ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un
        or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
        or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un;
    private static bool IsConversion(ILOp op) => op is ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8
        or ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4 or ILOp.Conv_U8
        or ILOp.Conv_I or ILOp.Conv_U or ILOp.Conv_R_Un or ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un
        or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un
        or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un
        or ILOp.Conv_Ovf_I1 or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or ILOp.Conv_Ovf_U2 or ILOp.Conv_Ovf_I4
         or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I8 or ILOp.Conv_Ovf_U8 or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U;

    private static bool IsArrayLoad(ILOp op) => op is ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2
        or ILOp.Ldelem_U2 or ILOp.Ldelem_I4 or ILOp.Ldelem_U4 or ILOp.Ldelem_I8 or ILOp.Ldelem_I
        or ILOp.Ldelem_R4 or ILOp.Ldelem_R8 or ILOp.Ldelem_Ref or ILOp.Ldelem;

    private static bool IsArrayStore(ILOp op) => op is ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2
        or ILOp.Stelem_I4 or ILOp.Stelem_I8 or ILOp.Stelem_R4 or ILOp.Stelem_R8 or ILOp.Stelem_Ref
        or ILOp.Stelem;

    private static MemoryOps.ArrayElementKind ArrayKind(ILOp op) => op switch {
        ILOp.Ldelem_I1 or ILOp.Stelem_I1 => MemoryOps.ArrayElementKind.SignedByte,
        ILOp.Ldelem_U1 => MemoryOps.ArrayElementKind.UnsignedByte,
        ILOp.Ldelem_I2 or ILOp.Stelem_I2 => MemoryOps.ArrayElementKind.SignedShort,
        ILOp.Ldelem_U2 => MemoryOps.ArrayElementKind.UnsignedShort,
        ILOp.Ldelem_U4 => MemoryOps.ArrayElementKind.UnsignedInt32,
        ILOp.Ldelem_I8 or ILOp.Stelem_I8 => MemoryOps.ArrayElementKind.Int64,
        ILOp.Ldelem_I => MemoryOps.ArrayElementKind.NativeInt,
        ILOp.Stelem_I => MemoryOps.ArrayElementKind.NativeInt,
        ILOp.Ldelem_R4 or ILOp.Ldelem_R8 or ILOp.Stelem_R4 or ILOp.Stelem_R8 => MemoryOps.ArrayElementKind.Float,
        ILOp.Ldelem_Ref or ILOp.Stelem_Ref => MemoryOps.ArrayElementKind.Object,
        _ => MemoryOps.ArrayElementKind.Int32,
    };
}
