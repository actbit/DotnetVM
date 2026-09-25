using System.Linq.Expressions;
using System.Reflection;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// A compiled entry point for the small JIT.  The delegate deliberately receives
/// a VM frame instead of exposing CLR values to generated code; all values still
/// cross the VM boundary as <see cref="StackSlot"/> instances.
/// </summary>
internal sealed class JitCompiledMethod(Func<JitFrame, StackSlot> entry,
    PreparedMethod prepared,
    Func<Interpreter, StackSlot[], StackSlot>? leaf = null) {
    private readonly Func<JitFrame, StackSlot> _entry = entry;
    private readonly Func<Interpreter, StackSlot[], StackSlot>? _leaf = leaf;

    internal PreparedMethod Prepared { get; } = prepared;
    internal bool HasLeaf => _leaf is not null;

    internal bool TryInvokeLeaf(Interpreter interpreter, StackSlot[] arguments, out StackSlot result) {
        if (_leaf is null) {
            result = default;
            return false;
        }
        result = _leaf(interpreter, arguments);
        return true;
    }

    public StackSlot Invoke(Interpreter interpreter, InterpreterServices services, InterpreterFrame frame) =>
        _entry(new JitFrame(interpreter, services, frame));
}

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
    JitResourceBudget resourceBudget) {
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
    private readonly Dictionary<VmMethod, Entry> _entries = [];
    private readonly object _gate = new();

    public JitCompiledMethod? TryGetCompiled(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) {
        if (!_enabled)
            return null;

        Entry entry;
        lock (_gate) {
            if (!_entries.TryGetValue(method, out entry!)) {
                if (!_resourceBudget.TryReserveEntry())
                    return null;
                _entries.Add(method, entry = new Entry());
            }

            if (entry.Compiled is not null || entry.Rejected || entry.Compiling)
                return entry.Compiled;

            if (entry.InvocationCount < int.MaxValue)
                entry.InvocationCount++;
            if (entry.InvocationCount < _promotionThreshold)
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
                    compiled = JitMethodCompiler.TryCompile(method, prepared, code);
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
            entry.Compiled = compiled;
            return compiled;
        }
    }

    internal int InvocationCount(VmMethod method) {
        lock (_gate)
            return _entries.TryGetValue(method, out var entry) ? entry.InvocationCount : 0;
    }

    internal bool IsCompiled(VmMethod method) {
        lock (_gate)
            return _entries.TryGetValue(method, out var entry) && entry.Compiled is not null;
    }

    internal JitCompiledMethod? GetCompiled(VmMethod method) {
        lock (_gate)
            return _entries.TryGetValue(method, out var entry) ? entry.Compiled : null;
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
    private readonly InterpreterServices _services;
    private readonly InterpreterFrame _frame;
    private VmExecutionCoordinator.InstructionBatchLease _instructionBatch;
    private VmExecutionCoordinator.InstructionLease _instructionLease;
    private int _batchInstructions;
    private bool _instructionActive;

    public JitFrame(Interpreter interpreter, InterpreterServices services, InterpreterFrame frame) {
        _interpreter = interpreter;
        _services = services;
        _frame = frame;
    }

    public int InstructionPointer => _frame.Ip;
    public bool Returned { get; private set; }
    public StackSlot ReturnValue { get; private set; }

    public void BeginExecution() {
        _interpreter.CheckJitSafepoint();
        _instructionBatch = _interpreter.EnterJitInstructionBatch();
        _batchInstructions = 0;
    }

    public void EndExecution() {
        _instructionLease.Dispose();
        _instructionLease = default;
        _instructionBatch.Dispose();
        _instructionBatch = default;
    }

    // These two methods bracket every generated instruction.  The try/finally
    // in the generated expression makes the coordinator lease exception-safe.
    public void BeginInstruction() {
        if (_batchInstructions >= Interpreter.SafepointInterval) {
            _instructionBatch.Dispose();
            _instructionBatch = default;
            _interpreter.CheckJitSafepoint();
            _instructionBatch = _interpreter.EnterJitInstructionBatch();
            _batchInstructions = 0;
        }
        _interpreter.CheckJitSafepoint();
        _instructionLease = _interpreter.EnterJitInstructionInBatch();
        _instructionActive = true;
        try {
            _interpreter.ConsumeJitInstruction();
        } catch {
            _instructionLease.Dispose();
            _instructionLease = default;
            _instructionActive = false;
            throw;
        }
    }

    public void EndInstruction() {
        _instructionLease.Dispose();
        _instructionLease = default;
        if (_instructionActive) {
            _instructionActive = false;
            _batchInstructions++;
        }
    }

    public void NoOp(int next) => _frame.Ip = next;

    public void LoadArgument(int index, int next) {
        _frame.Stack.Push(_frame.Arguments[index]);
        _frame.Ip = next;
    }

    public void StoreArgument(int index, int next) {
        _frame.Arguments[index] = _frame.Stack.Pop();
        _frame.Ip = next;
    }

    public void LoadArgumentAddress(int index, int next) {
        _frame.Stack.Push(StackSlot.OfByRef(VmByRef.Frame(_frame.Arguments, index)));
        _frame.Ip = next;
    }

    public void LoadLocal(int index, int next) {
        _frame.Stack.Push(SlotOps.PushCopyOfValue(_frame.Locals[index]));
        _frame.Ip = next;
    }

    public void StoreLocal(int index, int next) {
        _frame.Locals[index] = SlotOps.StoreCopyOfValue(_frame.Stack.Pop());
        _frame.Ip = next;
    }

    public void LoadLocalAddress(int index, int next) {
        _frame.Stack.Push(StackSlot.OfByRef(VmByRef.Frame(_frame.Locals, index)));
        _frame.Ip = next;
    }

    public void LoadNull(int next) {
        _frame.Stack.Push(StackSlot.Null);
        _frame.Ip = next;
    }

    public void LoadInt32(int value, int next) {
        _frame.Stack.Push(StackSlot.OfInt32(value));
        _frame.Ip = next;
    }

    public void LoadInt64(long value, int next) {
        _frame.Stack.Push(StackSlot.OfInt64(value));
        _frame.Ip = next;
    }

    public void LoadFloat(double value, int next) {
        _frame.Stack.Push(StackSlot.OfFloat(value));
        _frame.Ip = next;
    }

    public void Duplicate(int next) {
        _frame.Stack.Push(SlotOps.PushCopyOfValue(_frame.Stack.Peek()));
        _frame.Ip = next;
    }

    public void Drop(int next) {
        _ = _frame.Stack.Pop();
        _frame.Ip = next;
    }

    public void SetReadonly(int next) {
        _frame.PendingReadonly = true;
        _frame.Ip = next;
    }

    public void Branch(ILOp op, int target, int next) {
        var branch = op switch {
            ILOp.Br or ILOp.Br_S => true,
            ILOp.BrTrue or ILOp.BrTrue_S => SlotOps.IsTrue(_frame.Stack.Pop()),
            ILOp.BrFalse or ILOp.BrFalse_S => !SlotOps.IsTrue(_frame.Stack.Pop()),
            _ => CompareBranch(op),
        };
        _frame.Ip = branch ? target : next;
    }

    private bool CompareBranch(ILOp op) {
        var right = _frame.Stack.Pop();
        var left = _frame.Stack.Pop();
        return SlotOps.CompareBranch(op, left, right);
    }

    public void Switch(int[] targets, int next) {
        var index = _frame.Stack.Pop().AsInt32;
        _frame.Ip = (uint)index < (uint)targets.Length ? targets[index] : next;
    }

    public void Compare(ILOp op, int next) {
        var right = _frame.Stack.Pop();
        var left = _frame.Stack.Pop();
        _frame.Stack.Push(StackSlot.OfInt32(SlotOps.Compare(op, left, right) ? 1 : 0));
        _frame.Ip = next;
    }

    public void Binary(ILOp op, int next) {
        var right = _frame.Stack.Pop();
        var left = _frame.Stack.Pop();
        _frame.Stack.Push(MemoryOps.TryPointerArithmetic(op, left, right)
            ?? SlotOps.BinaryArithmetic(op, left, right));
        _frame.Ip = next;
    }

    public void Unary(ILOp op, int next) {
        _frame.Stack.Push(SlotOps.UnaryArithmetic(op, _frame.Stack.Pop()));
        _frame.Ip = next;
    }

    public void Convert(ILOp op, int next) {
        _frame.Stack.Push(SlotOps.ConvertValue(op, _frame.Stack.Pop()));
        _frame.Ip = next;
    }

    public void LoadString(int token, int next) {
        var value = _frame.Method.DynamicStrings is { } dynamicStrings &&
            dynamicStrings.TryGetValue(unchecked((uint)token), out var dynamicString)
                ? dynamicString
                : _services.Loader.Image.GetUserString(token & 0xFFFFFF);
        _frame.Stack.Push(StackSlot.OfObject(_services.Strings.GetOrNew(value)));
        _frame.Ip = next;
    }

    public void Call(int token, bool isCallvirt, int next) {
        var result = _interpreter.JitCallsFor(_frame.Method).Call(token, _frame, isCallvirt,
            constrainedToken: 0, tailCallAllowed: false, out _);
        if (result is { } value)
            _frame.Stack.Push(value);
        _frame.Ip = next;
    }

    /// <summary>
    /// Fast path for a non-virtual MethodDef call from generated code.  The
    /// target is still required to be an already compiled, non-generic guest
    /// method; all other cases return through the regular call gate.
    /// </summary>
    public void CallDirect(int token, int next) {
        if (_frame.Context is not null ||
            _frame.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true) {
            Call(token, isCallvirt: false, next);
            return;
        }

        var target = _services.Loader.GetMethodByToken((uint)token);
        if (target is null || target.Body is null || target.Signature.GenericParamCount != 0) {
            Call(token, isCallvirt: false, next);
            return;
        }

        var arity = target.Signature.ParamTypes.Length + (target.Signature.HasThis ? 1 : 0);
        var arguments = new StackSlot[arity];
        for (var i = arity - 1; i >= 0; i--)
            arguments[i] = _frame.Stack.Pop();
        if (!_interpreter.TryInvokeCompiled(target, arguments, null, out var value)) {
            for (var i = 0; i < arguments.Length; i++)
                _frame.Stack.Push(arguments[i]);
            Call(token, isCallvirt: false, next);
            return;
        }
        if (SlotOps.SignatureReturnsValue(target.Signature))
            _frame.Stack.Push(value);
        _frame.Ip = next;
    }

    public void NewObject(int token, int next) {
        if (_interpreter.JitObjectsFor(_frame.Method).NewObject(token, _frame) is { } value)
            _frame.Stack.Push(value);
        _frame.Ip = next;
    }

    public void NewArray(int token, int next) {
        var count = _frame.Stack.Pop().AsInt32;
        if (count < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var elementType = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        using var reservation = _services.Heap.ReserveArray(count);
        var elements = new StackSlot[count];
        for (var i = 0; i < count; i++)
            elements[i] = _services.Objects.DefaultForType(elementType, _services.Loader);
        _frame.Stack.Push(StackSlot.OfObject(reservation.Commit(
            new VmArray(new VmArrayType { ElementType = elementType }, elements))));
        _frame.Ip = next;
    }

    public void LoadArrayAddress(int next) {
        var index = _frame.Stack.Pop().AsInt32;
        var array = MemoryOps.GetArray(_frame.Stack.Pop());
        MemoryOps.CheckArrayBounds(array, index);
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        _frame.Stack.Push(StackSlot.OfByRef(VmByRef.ArrayElement(array, index, isReadOnly)));
        _frame.Ip = next;
    }

    public void LoadArray(MemoryOps.ArrayElementKind kind, int next) {
        _frame.Stack.Push(MemoryOps.ArrayLoad(_frame, kind));
        _frame.Ip = next;
    }

    public void LoadArrayByType(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        _frame.Stack.Push(MemoryOps.ArrayLoad(_frame,
            MemoryOps.ElementKindFromType(objects.ResolveTypeToken(token, _frame.Context,
                _frame.Method.DynamicTokens))));
        _frame.Ip = next;
    }

    public void StoreArray(MemoryOps.ArrayElementKind kind, int next) {
        MemoryOps.ArrayStore(_frame, kind, _services.StringType);
        _frame.Ip = next;
    }

    public void StoreArrayByType(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        MemoryOps.ArrayStore(_frame,
            MemoryOps.ElementKindFromType(objects.ResolveTypeToken(token, _frame.Context,
                _frame.Method.DynamicTokens)), _services.StringType);
        _frame.Ip = next;
    }

    public void LoadArrayLength(int next) {
        _frame.Stack.Push(StackSlot.OfNativeInt(MemoryOps.GetArray(_frame.Stack.Pop()).Length));
        _frame.Ip = next;
    }

    public void LoadField(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var field = objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        _frame.Stack.Push(SlotOps.PushCopyOfValue(objects.ReadField(_frame.Stack.Pop(), field)));
        _frame.Ip = next;
    }

    public void StoreField(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var field = objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        EnsureFieldWritable(field);
        var value = _frame.Stack.Pop();
        var receiver = _frame.Stack.Pop();
        if (!objects.TryStoreStringField(receiver, field, value))
            objects.WriteField(receiver, field, value);
        _frame.Ip = next;
    }

    public void LoadFieldAddress(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var field = objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        _frame.Stack.Push(objects.FieldAddress(_frame.Stack.Pop(), field, isReadOnly));
        _frame.Ip = next;
    }

    public void LoadStaticField(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        _frame.Stack.Push(SlotOps.PushCopyOfValue(objects.StaticFieldLocation(token, _frame.Context,
            _frame.Method.DynamicTokens).Read()));
        _frame.Ip = next;
    }

    public void StoreStaticField(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var field = objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        EnsureFieldWritable(field);
        objects.StaticFieldLocation(token, _frame.Context, _frame.Method.DynamicTokens)
            .Write(_frame.Stack.Pop());
        _frame.Ip = next;
    }

    public void LoadStaticFieldAddress(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var isReadOnly = _frame.PendingReadonly;
        _frame.PendingReadonly = false;
        _frame.Stack.Push(objects.TryGetStaticFieldRvaAddress(token) ??
            StackSlot.OfByRef(objects.StaticFieldLocation(token, _frame.Context,
                _frame.Method.DynamicTokens, isReadOnly)));
        _frame.Ip = next;
    }

    public void LoadIndirect(ILOp op, int next) {
        _frame.Stack.Push(MemoryOps.LoadIndirect(op, _frame.Stack.Pop()));
        _frame.Ip = next;
    }

    public void StoreIndirect(ILOp op, int next) {
        var value = _frame.Stack.Pop();
        MemoryOps.StoreIndirect(op, _frame.Stack.Pop(), value);
        _frame.Ip = next;
    }

    public void LoadObject(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var address = _frame.Stack.Pop();
        if (address.ObjectValue is VmNativePointer native) {
            var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "ldobj");
            _frame.Stack.Push(MemoryOps.ValueFromBytes(
                native.Bytes.AsSpan(native.ByteOffset, size), type, size));
        } else if (address.ObjectValue is VmByRef byRef) {
            _frame.Stack.Push(SlotOps.PushCopyOfValue(byRef.Slot));
        } else {
            throw InvalidAddress("ldobj", address);
        }
        _frame.Ip = next;
    }

    public void StoreObject(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var value = _frame.Stack.Pop();
        var address = _frame.Stack.Pop();
        if (address.ObjectValue is VmNativePointer native) {
            var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "stobj", writable: true);
            Span<byte> bytes = stackalloc byte[8];
            MemoryOps.BytesOfValue(value, type, size, bytes);
            bytes[..size].CopyTo(native.Bytes.AsSpan(native.ByteOffset, size));
        } else if (address.ObjectValue is VmByRef byRef) {
            byRef.Write(SlotOps.StoreCopyOfValue(value));
        } else {
            throw InvalidAddress("stobj", address);
        }
        _frame.Ip = next;
    }

    public void CopyObject(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var source = _frame.Stack.Pop();
        var destination = _frame.Stack.Pop();
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var size = MemoryOps.SizeOfType(type);
        if (source.ObjectValue is VmNativePointer sourceNative &&
            destination.ObjectValue is VmNativePointer destinationNative) {
            EnsureNativeRange(sourceNative, size, "cpobj");
            EnsureNativeRange(destinationNative, size, "cpobj", writable: true);
            sourceNative.Bytes.AsSpan(sourceNative.ByteOffset, size)
                .CopyTo(destinationNative.Bytes.AsSpan(destinationNative.ByteOffset, size));
        } else if (source.ObjectValue is VmNativePointer sourceOnly) {
            EnsureNativeRange(sourceOnly, size, "cpobj");
            if (destination.ObjectValue is not VmByRef destinationByRef)
                throw InvalidAddress("cpobj", destination);
            destinationByRef.Write(SlotOps.StoreCopyOfValue(MemoryOps.ValueFromBytes(
                sourceOnly.Bytes.AsSpan(sourceOnly.ByteOffset, size), type, size)));
        } else if (destination.ObjectValue is VmNativePointer destinationOnly) {
            EnsureNativeRange(destinationOnly, size, "cpobj", writable: true);
            if (source.ObjectValue is not VmByRef sourceByRef)
                throw InvalidAddress("cpobj", source);
            Span<byte> bytes = stackalloc byte[8];
            MemoryOps.BytesOfValue(sourceByRef.Read(), type, size, bytes);
            bytes[..size].CopyTo(destinationOnly.Bytes.AsSpan(destinationOnly.ByteOffset, size));
        } else if (source.ObjectValue is VmByRef sourceByRef &&
                   destination.ObjectValue is VmByRef destinationByRef) {
            destinationByRef.Write(SlotOps.StoreCopyOfValue(sourceByRef.Read()));
        } else {
            throw InvalidAddress("cpobj", destination);
        }
        _frame.Ip = next;
    }

    public void InitObject(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var address = _frame.Stack.Pop();
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        if (address.ObjectValue is VmNativePointer native) {
            var size = MemoryOps.SizeOfType(type);
            EnsureNativeRange(native, size, "initobj", writable: true);
            Array.Clear(native.Bytes, native.ByteOffset, size);
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
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = _frame.Stack.Pop();
        if (value.ObjectValue is not VmBoxedValue boxed || !TypeChecks.IsExactUnboxType(boxed.Type, target))
            throw new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} を {target.FullName} として unbox できません。");
        _frame.Stack.Push(StackSlot.OfByRef(VmByRef.BoxedValue(boxed)));
        _frame.Ip = next;
    }

    private void EnsureFieldWritable(VmField field) {
        if (!field.IsInitOnly)
            return;
        var allowed = field.IsStatic ? _frame.Method.Name == ".cctor" : _frame.Method.Name == ".ctor";
        if (!allowed)
            throw new UnhandledGuestException("System.FieldAccessException",
                $"readonly フィールド {field} はコンストラクター外から書き込めません。");
    }

    public void Box(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var type = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = _frame.Stack.Pop();
        var fields = value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue sv
            ? sv.Clone().Fields
            : [value];
        _frame.Stack.Push(StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields))));
        _frame.Ip = next;
    }

    public void UnboxAny(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = _frame.Stack.Pop();
        if (target.IsValueType) {
            if (value.ObjectValue is not VmBoxedValue boxed || !TypeChecks.IsExactUnboxType(boxed.Type, target))
                throw new UnhandledGuestException("System.InvalidCastException",
                    $"{SlotOps.Describe(value)} を {target.FullName} に unbox.any できません。");
            if (VmPrimitiveTypes.IsSlotPrimitive(target.FullName))
                _frame.Stack.Push(boxed.Fields[0]);
            else if (target is VmClassType or VmConstructedType) {
                var args = boxed.Type is VmConstructedType constructed ? constructed.TypeArguments : null;
                _frame.Stack.Push(StackSlot.OfValueType(new VmStructValue(target,
                    (StackSlot[])boxed.Fields.Clone(), args)));
            } else
                _frame.Stack.Push(boxed.Fields[0]);
        } else {
            var ok = value.ObjectValue is null ||
                TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
            if (!ok)
                throw new UnhandledGuestException("System.InvalidCastException",
                    $"{SlotOps.Describe(value)} を {target.FullName} に変換できません。");
            _frame.Stack.Push(value);
        }
        _frame.Ip = next;
    }

    public void LoadToken(int token, int next) {
        if (_frame.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true) {
            VmObject handle = dynamicReference switch {
                VmType dynamicType => _services.Heap.Allocate(new VmTypeHandle { Target = dynamicType }),
                VmMethod dynamicMethod => _services.Heap.Allocate(new VmMethodHandle { Target = dynamicMethod }),
                VmField dynamicField => _services.Heap.Allocate(new VmFieldHandle { Target = dynamicField }),
                _ => throw new NotSupportedException("動的 ldtoken の参照種別は未対応です."),
            };
            _frame.Stack.Push(StackSlot.OfObject(handle));
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
                var handle = _services.Heap.Allocate(new VmFieldRvaData {
                    Data = loader.Image.GetRvaDataToEnd(rva),
                });
                _frame.Stack.Push(StackSlot.OfObject(handle));
                break;
            }
            case TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec: {
                var type = _interpreter.JitObjectsFor(_frame.Method).ResolveTypeToken(
                    token, _frame.Context, _frame.Method.DynamicTokens);
                _frame.Stack.Push(StackSlot.OfObject(
                    _services.Heap.Allocate(new VmTypeHandle { Target = type })));
                break;
            }
            case TableKind.MethodDef: {
                var method = loader.GetMethodByToken((uint)token)
                    ?? throw new BadImageFormatException($"MethodDef rid {tokenRid} を解決できません。");
                _frame.Stack.Push(StackSlot.OfObject(
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
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var target = objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = _frame.Stack.Pop();
        var ok = value.ObjectValue is null ||
            TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
        if (!ok && !isInst)
            throw new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} を {target.FullName} にキャストできません。");
        _frame.Stack.Push(ok ? value : StackSlot.Null);
        _frame.Ip = next;
    }

    public void Throw() => throw _interpreter.JitExceptionsFor(_frame.Method)
        .MakeGuestThrow(_frame.Stack.Pop());

    public void CheckFinite(int next) {
        var value = _frame.Stack.Pop();
        if (value.Kind == StackKind.Float &&
            (double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue)))
            throw new UnhandledGuestException("System.ArithmeticException", null);
        _frame.Stack.Push(value);
        _frame.Ip = next;
    }

    public void SizeOf(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        _frame.Stack.Push(StackSlot.OfInt32(MemoryOps.SizeOfType(
            objects.ResolveTypeToken(token, _frame.Context, _frame.Method.DynamicTokens))));
        _frame.Ip = next;
    }

    public void Return(bool hasValue) {
        ReturnValue = hasValue ? _frame.Stack.Pop() : default;
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
            !CanCompile(method, prepared, code))
            return null;

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
        if (IsLeafCandidate(method, prepared, code) || IsConstructorLeafCandidate(method, prepared, code))
            expressionNodes = checked(expressionNodes + 8L + code.Length * 8L);
        if (expressionNodes > memory.MaxJitExpressionNodes)
            return null;
        var workUnits = checked(expressionNodes + method.Body.IlCode.Length + switchTargets);
        var hostMemoryBytes = checked(4096L + expressionNodes * 64L + switchTargets * 8L);
        return new CompilationCost(workUnits, hostMemoryBytes, expressionNodes, switchTargets);
    }

    public static JitCompiledMethod? TryCompile(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) {
        if (!CanCompile(method, prepared, code))
            return null;

        try {
            var frame = Expression.Parameter(typeof(JitFrame), "frame");
            var cases = new SwitchCase[code.Length];
            var offsets = new Dictionary<int, int>(code.Length * 2);
            for (var i = 0; i < code.Length; i++)
                offsets[code[i].Offset] = i;

            for (var i = 0; i < code.Length; i++) {
                var instruction = code[i];
                var next = i + 1;
                var operation = BuildOperation(frame, instruction, next, offsets,
                    SlotOps.SignatureReturnsValue(method.Signature));
                var instructionBody = Expression.TryFinally(
                    Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction))), operation),
                    Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
                cases[i] = Expression.SwitchCase(instructionBody, Expression.Constant(i));
            }

            Expression execution;
            if (IsStraightLine(code)) {
                var operations = new List<Expression>(code.Length * 2 + 2) {
                    Expression.Call(frame, Method(nameof(JitFrame.BeginExecution))),
                };
                for (var i = 0; i < code.Length; i++) {
                    var instructionBody = Expression.TryFinally(
                        Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginInstruction))),
                            BuildOperation(frame, code[i], i + 1, offsets,
                                SlotOps.SignatureReturnsValue(method.Signature))),
                        Expression.Call(frame, Method(nameof(JitFrame.EndInstruction))));
                    operations.Add(instructionBody);
                }
                operations.Add(Expression.Property(frame, nameof(JitFrame.ReturnValue)));
                execution = Expression.Block(operations);
            } else {
                var returnLabel = Expression.Label(typeof(StackSlot), "jitReturn");
                var invalidIp = Expression.Throw(Expression.New(
                    typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                    Expression.Constant($"JIT フレームの命令位置が不正です: {method}")));
                var loopBody = Expression.Block(
                    Expression.Switch(Expression.Property(frame, nameof(JitFrame.InstructionPointer)),
                        invalidIp, null, cases),
                    Expression.IfThen(Expression.Property(frame, nameof(JitFrame.Returned)),
                        Expression.Break(returnLabel, Expression.Property(frame, nameof(JitFrame.ReturnValue)))));
                var loop = Expression.Loop(loopBody, returnLabel);
                execution = Expression.Block(Expression.Call(frame, Method(nameof(JitFrame.BeginExecution))), loop);
            }
            var body = Expression.TryFinally(
                execution,
                Expression.Call(frame, Method(nameof(JitFrame.EndExecution))));
            var lambda = Expression.Lambda<Func<JitFrame, StackSlot>>(body, frame).Compile();
            Func<Interpreter, StackSlot[], StackSlot>? leaf = null;
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
            prepared.Clauses is { Length: > 0 })
            return false;
        if (ContainsUnsupportedType(method.Signature.ReturnType) ||
            method.Signature.ParamTypes.Any(ContainsUnsupportedType) ||
            prepared.LocalTypes.Any(ContainsUnsupportedType))
            return false;

        var offsets = new HashSet<int>(code.Select(instruction => instruction.Offset));
        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
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
        SigKind.GenericVar or SigKind.GenericMethodVar or SigKind.GenericInst
            or SigKind.Pointer or SigKind.TypedByRef => true,
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

    private static Func<Interpreter, StackSlot[], StackSlot>? TryCompileLeaf(VmMethod method,
        PreparedMethod prepared, DecodedInstruction[] code) {
        if (IsConstructorLeafCandidate(method, prepared, code))
            return TryCompileConstructorLeaf(method, code);
        if (!IsLeafCandidate(method, prepared, code))
            return null;

        var interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
        var arguments = Expression.Parameter(typeof(StackSlot[]), "arguments");
        var statements = new List<Expression>(code.Length + 1);
        var stack = new List<Expression>();
        var consume = typeof(Interpreter).GetMethod(nameof(Interpreter.ConsumeJitInstruction),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var binary = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafBinaryArithmetic))!;
        var unary = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafUnaryArithmetic))!;
        var convert = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafConvertValue))!;
        var compare = typeof(SlotOps).GetMethod(nameof(SlotOps.LeafCompare))!;
        var ofInt32 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt32), [typeof(int)])!;
        var ofInt64 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt64), [typeof(long)])!;
        var ofFloat = typeof(StackSlot).GetMethod(nameof(StackSlot.OfFloat), [typeof(double)])!;
        var nullProperty = typeof(StackSlot).GetProperty(nameof(StackSlot.Null))!;

        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            statements.Add(Expression.Call(interpreter, consume));
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                    break;
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    stack.Add(Expression.ArrayIndex(arguments,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldarg_0))));
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    stack.Add(Expression.ArrayIndex(arguments, Expression.Constant(instruction.IntOperand)));
                    break;
                case ILOp.Ldnull:
                    stack.Add(Expression.Property(null, nullProperty));
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
                    return Expression.Lambda<Func<Interpreter, StackSlot[], StackSlot>>(
                        Expression.Block(statements), interpreter, arguments).Compile();
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool IsLeafType(SigType type) => type.Kind is
        SigKind.Void or SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1 or
        SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4 or SigKind.I8 or SigKind.U8 or
        SigKind.I or SigKind.U or SigKind.R4 or SigKind.R8;

    private static bool IsLeafCandidate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) =>
        !method.Signature.HasThis && IsStraightLine(code) && prepared.LocalTypes.Length == 0 &&
        IsLeafType(method.Signature.ReturnType) &&
        method.Signature.ParamTypes.All(IsLeafType);

    private static bool IsConstructorLeafCandidate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) =>
        method.Name == ".ctor" && method.Signature.HasThis &&
        method.Signature.ReturnType.Kind == SigKind.Void && prepared.LocalTypes.Length == 0 &&
        method.Signature.ParamTypes.All(IsLeafType) && IsStraightLine(code) &&
        code.Any(instruction => instruction.Op == ILOp.Stfld);

    private static Func<Interpreter, StackSlot[], StackSlot>? TryCompileConstructorLeaf(
        VmMethod method, DecodedInstruction[] code) {
        var interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
        var arguments = Expression.Parameter(typeof(StackSlot[]), "arguments");
        var statements = new List<Expression>(code.Length + 1);
        var stack = new List<Expression>();
        var consume = typeof(Interpreter).GetMethod(nameof(Interpreter.ConsumeJitInstruction),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storeField = typeof(Interpreter).GetMethod(nameof(Interpreter.StoreLeafField),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ofInt32 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt32), [typeof(int)])!;
        var ofInt64 = typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt64), [typeof(long)])!;
        var ofFloat = typeof(StackSlot).GetMethod(nameof(StackSlot.OfFloat), [typeof(double)])!;

        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            statements.Add(Expression.Call(interpreter, consume));
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                    break;
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    stack.Add(Expression.ArrayIndex(arguments,
                        Expression.Constant((int)(instruction.Op - ILOp.Ldarg_0))));
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    stack.Add(Expression.ArrayIndex(arguments, Expression.Constant(instruction.IntOperand)));
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
                    return Expression.Lambda<Func<Interpreter, StackSlot[], StackSlot>>(
                        Expression.Block(statements), interpreter, arguments).Compile();
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
            or ILOp.Bgt_Un or ILOp.Ble_Un or ILOp.Blt_Un or ILOp.Switch or
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
            or ILOp.Ckfinite or ILOp.Ldtoken or ILOp.Readonly or ILOp.Sizeof or ILOp.Ret => true,
        _ => false,
    };

    private static Expression BuildOperation(ParameterExpression frame, DecodedInstruction instruction,
        int next, IReadOnlyDictionary<int, int> offsets, bool hasReturnValue) {
        var op = instruction.Op;
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
        if (op is ILOp.Br or ILOp.Br_S or ILOp.BrFalse or ILOp.BrFalse_S or ILOp.BrTrue or ILOp.BrTrue_S
            or ILOp.Beq or ILOp.Beq_S or ILOp.Bge or ILOp.Bge_S or ILOp.Bgt or ILOp.Bgt_S
            or ILOp.Ble or ILOp.Ble_S or ILOp.Blt or ILOp.Blt_S or ILOp.Bne_Un or ILOp.Bne_Un_S
            or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble_Un or ILOp.Ble_Un_S
            or ILOp.Blt_Un or ILOp.Blt_Un_S)
            return Call(frame, nameof(JitFrame.Branch), Constant(op), Constant(offsets[instruction.IntOperand]), Constant(next));
        if (op == ILOp.Switch)
            return Call(frame, nameof(JitFrame.Switch),
                Expression.Constant(instruction.SwitchTargets!.Select(target => offsets[target]).ToArray()), Constant(next));
        if (op is ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un)
            return Call(frame, nameof(JitFrame.Compare), Constant(op), Constant(next));
        if (IsIndirectLoad(op))
            return Call(frame, nameof(JitFrame.LoadIndirect), Constant(op), Constant(next));
        if (IsIndirectStore(op))
            return Call(frame, nameof(JitFrame.StoreIndirect), Constant(op), Constant(next));
        if (op == ILOp.Call && (TableKind)((uint)instruction.IntOperand >> 24) == TableKind.MethodDef)
            return Call(frame, nameof(JitFrame.CallDirect), Constant(instruction.IntOperand), Constant(next));
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
