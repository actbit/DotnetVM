using System.Linq.Expressions;
using System.Reflection;
using DotnetVM.Host;
using DotnetVM.IL;
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
internal sealed class JitCompiledMethod(Func<JitFrame, StackSlot> entry) {
    private readonly Func<JitFrame, StackSlot> _entry = entry;

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
internal sealed class JitFrame {
    private readonly Interpreter _interpreter;
    private readonly InterpreterServices _services;
    private readonly InterpreterFrame _frame;
    private IDisposable? _instructionLease;

    public JitFrame(Interpreter interpreter, InterpreterServices services, InterpreterFrame frame) {
        _interpreter = interpreter;
        _services = services;
        _frame = frame;
    }

    public int InstructionPointer => _frame.Ip;
    public bool Returned { get; private set; }
    public StackSlot ReturnValue { get; private set; }

    // These two methods bracket every generated instruction.  The try/finally
    // in the generated expression makes the coordinator lease exception-safe.
    public void BeginInstruction() {
        _interpreter.CheckJitSafepoint();
        _instructionLease = _interpreter.EnterJitInstruction();
        try {
            _interpreter.ConsumeJitInstruction();
        } catch {
            _instructionLease.Dispose();
            _instructionLease = null;
            throw;
        }
    }

    public void EndInstruction() => Interlocked.Exchange(ref _instructionLease, null)?.Dispose();

    public void NoOp(int next) => _frame.Ip = next;

    public void LoadArgument(int index, int next) {
        _frame.Stack.Push(_frame.Arguments[index]);
        _frame.Ip = next;
    }

    public void StoreArgument(int index, int next) {
        _frame.Arguments[index] = _frame.Stack.Pop();
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
        _frame.Stack.Push(_frame.Stack.Peek());
        _frame.Ip = next;
    }

    public void Drop(int next) {
        _ = _frame.Stack.Pop();
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
        var location = objects.FieldLocation(_frame.Stack.Pop(), field);
        _frame.Stack.Push(SlotOps.PushCopyOfValue(location.Read()));
        _frame.Ip = next;
    }

    public void StoreField(int token, int next) {
        var objects = _interpreter.JitObjectsFor(_frame.Method);
        var field = objects.ResolveFieldToken(token, _frame.Context, _frame.Method.DynamicTokens);
        var value = _frame.Stack.Pop();
        var receiver = _frame.Stack.Pop();
        if (!objects.TryStoreStringField(receiver, field, value))
            objects.FieldLocation(receiver, field).Write(SlotOps.StoreCopyOfValue(value));
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
        objects.StaticFieldLocation(token, _frame.Context, _frame.Method.DynamicTokens)
            .Write(_frame.Stack.Pop());
        _frame.Ip = next;
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
            if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
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
/// Expression-tree compiler for the first JIT tier.  Calls, EH, byrefs,
/// pointers and runtime-dependent instructions deliberately fall back to the
/// interpreter. The supported tier also reuses VM call/object/array helpers so
/// ordinary non-EH methods can be promoted without exposing CLR objects.
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

            var returnLabel = Expression.Label(typeof(StackSlot), "jitReturn");
            var invalidIp = Expression.Throw(Expression.New(
                typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                Expression.Constant($"JIT フレームの命令位置が不正です: {method}")));
            var loopBody = Expression.Block(
                Expression.Switch(Expression.Property(frame, nameof(JitFrame.InstructionPointer)),
                    invalidIp, null, cases),
                Expression.IfThen(Expression.Property(frame, nameof(JitFrame.Returned)),
                    Expression.Break(returnLabel, Expression.Property(frame, nameof(JitFrame.ReturnValue)))));
            var body = Expression.Loop(loopBody, returnLabel);
            var lambda = Expression.Lambda<Func<JitFrame, StackSlot>>(body, frame).Compile();
            return new JitCompiledMethod(lambda);
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
        foreach (var instruction in code) {
            if (!IsSupported(instruction.Op))
                return false;
            if (instruction.Op is ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3
                && (int)(instruction.Op - ILOp.Ldarg_0) >= method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0))
                return false;
            if (instruction.Op is ILOp.Ldarg or ILOp.Ldarg_S or ILOp.Starg or ILOp.Starg_S)
                if ((uint)instruction.IntOperand >= (uint)(method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0)))
                    return false;
            if (instruction.Op is ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3
                && (int)(instruction.Op - ILOp.Ldloc_0) >= prepared.LocalTypes.Length)
                return false;
            if (instruction.Op is ILOp.Ldloc or ILOp.Ldloc_S or ILOp.Stloc or ILOp.Stloc_S)
                if ((uint)instruction.IntOperand >= (uint)prepared.LocalTypes.Length)
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
            or SigKind.ByRef or SigKind.Pointer or SigKind.TypedByRef => true,
        SigKind.SzArray or SigKind.Array => type.Inner is not null && ContainsUnsupportedType(type.Inner),
        _ => false,
    };

    private static bool IsSupported(ILOp op) => op switch {
        ILOp.Nop or ILOp.Break or
        ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg or
        ILOp.Starg_S or ILOp.Starg or
        ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or ILOp.Ldloc_S or ILOp.Ldloc or
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
            or ILOp.Ldelem_Ref or ILOp.Ldelem or ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2
            or ILOp.Stelem_I4 or ILOp.Stelem_I8 or ILOp.Stelem_R4 or ILOp.Stelem_R8 or ILOp.Stelem_Ref
            or ILOp.Stelem or ILOp.Ldfld or ILOp.Stfld or ILOp.Ldsfld or ILOp.Stsfld or ILOp.Box
            or ILOp.Unbox_Any or ILOp.Castclass or ILOp.Isinst or ILOp.Throw or ILOp.Ckfinite
            or ILOp.Sizeof or ILOp.Ret => true,
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
        if (op is ILOp.Starg_S or ILOp.Starg)
            return Call(frame, nameof(JitFrame.StoreArgument), Constant(instruction.IntOperand), Constant(next));
        if (op is ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3)
            return Call(frame, nameof(JitFrame.LoadLocal), Constant((int)(op - ILOp.Ldloc_0)), Constant(next));
        if (op is ILOp.Ldloc_S or ILOp.Ldloc)
            return Call(frame, nameof(JitFrame.LoadLocal), Constant(instruction.IntOperand), Constant(next));
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
        if (op == ILOp.Stfld)
            return Call(frame, nameof(JitFrame.StoreField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Ldsfld)
            return Call(frame, nameof(JitFrame.LoadStaticField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Stsfld)
            return Call(frame, nameof(JitFrame.StoreStaticField), Constant(instruction.IntOperand), Constant(next));
        if (op == ILOp.Box)
            return Call(frame, nameof(JitFrame.Box), Constant(instruction.IntOperand), Constant(next));
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
        ILOp.Ldelem_I8 or ILOp.Ldelem_I or ILOp.Stelem_I8 => MemoryOps.ArrayElementKind.Int64,
        ILOp.Ldelem_R4 or ILOp.Ldelem_R8 or ILOp.Stelem_R4 or ILOp.Stelem_R8 => MemoryOps.ArrayElementKind.Float,
        ILOp.Ldelem_Ref or ILOp.Stelem_Ref => MemoryOps.ArrayElementKind.Object,
        _ => MemoryOps.ArrayElementKind.Int32,
    };
}
