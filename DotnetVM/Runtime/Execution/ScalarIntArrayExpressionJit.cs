using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// 配列ローカルを含む整数メソッドを、命令ごとの VM dispatch ではなく式木へ変換する JIT。
/// スタック表現は通常の StackSlot のままにして、配列命令だけをコンテキストの境界へ集約する。
/// そのため整数演算・分岐はホストの式木のループになり、配列の型検査とヒープ課金は維持される。
/// </summary>
internal static class ScalarIntArrayExpressionJit {
    internal static JitScalar? TryCreate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code) {
        var eligible = IsEligible(method, prepared, code);
        if (!eligible)
            return null;
        if (!TryComputeStackHeights(code, prepared.OffsetMap, out var heights, out var maxStack))
            return null;

        var starts = BuildBlockStarts(code, prepared.OffsetMap);
        var costs = new int[code.Length];
        var counts = new int[code.Length];
        var ends = new int[code.Length];
        for (var start = 0; start < code.Length;) {
            if (!starts[start]) {
                start++;
                continue;
            }
            var end = start + 1;
            while (end < code.Length && !starts[end])
                end++;
            var cost = 0;
            for (var i = start; i < end; i++)
                cost = checked(cost + code[i].InstructionCost);
            for (var i = start; i < end; i++) {
                costs[i] = cost;
                counts[i] = end - start;
                ends[i] = end;
            }
            start = end;
        }

        try {
            var context = Expression.Parameter(typeof(ScalarIntJitContext), "context");
            var arguments = Enumerable.Range(0, method.Signature.ParamTypes.Length)
                .Select(i => Expression.Variable(typeof(StackSlot), $"arg{i}")).ToArray();
            var locals = Enumerable.Range(0, prepared.LocalTypes.Length)
                .Select(i => Expression.Variable(typeof(StackSlot), $"local{i}")).ToArray();
            var stack = Enumerable.Range(0, maxStack)
                .Select(i => Expression.Variable(typeof(StackSlot), $"stack{i}")).ToArray();
            var result = Expression.Variable(typeof(StackSlot), "result");
            var labels = Enumerable.Range(0, code.Length)
                .Select(i => Expression.Label($"il{i}")).ToArray();
            var returnLabel = Expression.Label("return");
            var expressions = new List<Expression>(code.Length * 2 + arguments.Length + locals.Length + 2);

            for (var i = 0; i < arguments.Length; i++)
                expressions.Add(Expression.Assign(arguments[i], ContextCall(context, nameof(ScalarIntJitContext.ReadSlotArgument),
                    Expression.Constant(i))));
            for (var i = 0; i < locals.Length; i++)
                expressions.Add(Expression.Assign(locals[i], ContextCall(context, nameof(ScalarIntJitContext.ReadSlotLocal),
                    Expression.Constant(i))));

            for (var start = 0; start < code.Length;) {
                if (!starts[start]) {
                    start++;
                    continue;
                }
                var end = ends[start];
                if (heights[start] < 0) {
                    start = end;
                    continue;
                }
                expressions.Add(Expression.Label(labels[start]));
                expressions.Add(ContextCall(context, nameof(ScalarIntJitContext.Charge),
                    Expression.Constant(costs[start]), Expression.Constant(counts[start])));
                var terminated = false;
                for (var i = start; i < end; i++) {
                    AppendInstruction(expressions, code[i], i, heights[i], stack, locals,
                        arguments, labels, returnLabel, result, context, prepared.OffsetMap, ref terminated);
                    if (terminated)
                        break;
                }
                if (!terminated && end < code.Length)
                    expressions.Add(Expression.Goto(labels[end]));
                start = end;
            }

            expressions.Add(Expression.Label(returnLabel));
            expressions.Add(result);
            var variables = arguments.Concat(locals).Concat(stack).Append(result);
            var body = Expression.Block(variables, expressions);
            var compiled = Expression.Lambda<Func<ScalarIntJitContext, StackSlot>>(body, context).Compile();
            return (interpreter, _, frame) => {
                using var runtime = new ScalarIntJitContext(interpreter, frame);
                return compiled(runtime);
            };
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NotSupportedException or PlatformNotSupportedException) {
            return null;
        }
    }

    private static bool IsEligible(VmMethod method, PreparedMethod prepared, DecodedInstruction[] code) {
        var baseEligible = !method.Signature.HasThis && method.Signature.ReturnType.Kind == SigKind.I4 &&
            method.Signature.ParamTypes.All(IsInt32Like) &&
            prepared.Clauses is not { Length: > 0 } && code.Length != 0 &&
            prepared.LocalTypes.All(IsStackLocal) &&
            prepared.LocalTypes.All(type => type.Kind != SigKind.SzArray ||
                type.Inner is { Kind: SigKind.I4 or SigKind.TypeToken }) &&
            code.Length <= Interpreter.SafepointInterval;
        if (!baseEligible)
            return false;

        foreach (var instruction in code) {
            if (!IsSupported(instruction.Op))
                return false;
            if (instruction.Op == ILOp.Newobj && !IsParameterlessConstructor(method, instruction.IntOperand))
                return false;
            if (instruction.Fusion.Kind != IlFusionKind.None &&
                (!IlFusionValidation.IsValid(instruction.Fusion, prepared.LocalTypes.Length, out _) ||
                 !IsIntLocal(prepared, instruction.Fusion.LocalA) ||
                 !IsIntLocal(prepared, instruction.Fusion.LocalC) ||
                 instruction.Fusion.Kind is IlFusionKind.LocalLocalOperationStore or
                 IlFusionKind.LocalLocalConstantOperationStore && !IsIntLocal(prepared, instruction.Fusion.LocalB)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsIntLocal(PreparedMethod prepared, int index) =>
        (uint)index < (uint)prepared.LocalTypes.Length && IsInt32Like(prepared.LocalTypes[index]);

    private static bool IsParameterlessConstructor(VmMethod method, int token) {
        var loader = method.Loader;
        if (loader is null) return false;
        return (TableKind)(unchecked((uint)token) >> 24) switch {
            TableKind.MethodDef => loader.GetMethodByToken(unchecked((uint)token)) is { Signature.ParamTypes.Length: 0 },
            TableKind.MemberRef => loader.DecodeMemberRefMethodSignature(unchecked((int)token) & 0xFFFFFF).ParamTypes.Length == 0,
            _ => false,
        };
    }

    private static bool IsStackLocal(SigType type) => type.Kind is not
        (SigKind.ByRef or SigKind.Pointer or SigKind.FunctionPointer or SigKind.TypedByRef or SigKind.Array);

    private static bool IsInt32Like(SigType type) => type.Kind is SigKind.Boolean or SigKind.I1 or SigKind.U1 or
        SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4;

    private static bool IsSupported(ILOp op) => op is
        ILOp.Nop or ILOp.Break or ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or
        ILOp.Ldarg_S or ILOp.Ldarg or ILOp.Starg_S or ILOp.Starg or ILOp.Ldloc_0 or ILOp.Ldloc_1 or
        ILOp.Ldloc_2 or ILOp.Ldloc_3 or ILOp.Ldloc_S or ILOp.Ldloc or ILOp.Stloc_0 or ILOp.Stloc_1 or
        ILOp.Stloc_2 or ILOp.Stloc_3 or ILOp.Stloc_S or ILOp.Stloc or ILOp.Ldc_I4_M1 or
        >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Dup or ILOp.Pop or
        ILOp.Newarr or ILOp.Newobj or ILOp.Ldlen or ILOp.Ldelem_I4 or ILOp.Stelem_I4 or ILOp.Ldfld or ILOp.Stfld or ILOp.Conv_I4 or ILOp.Conv_U4 or
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor or
        ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un or
        ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S or
        ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or
        ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble or ILOp.Ble_S or
        ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S or ILOp.Ret;

    private static void AppendInstruction(List<Expression> expressions, DecodedInstruction instruction,
        int index, int height, ParameterExpression[] stack, ParameterExpression[] locals,
        ParameterExpression[] arguments, LabelTarget[] labels, LabelTarget returnLabel,
        ParameterExpression result, ParameterExpression context, IReadOnlyDictionary<int, int> offsets,
        ref bool terminated) {
        if (instruction.Fusion.Kind != IlFusionKind.None) {
            expressions.Add(Expression.Assign(locals[instruction.Fusion.LocalC], FusionExpression(instruction.Fusion, locals)));
            return;
        }

        var op = instruction.Op;
        switch (op) {
            case ILOp.Nop or ILOp.Break:
                return;
            case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                expressions.Add(Expression.Assign(stack[height], arguments[(int)(op - ILOp.Ldarg_0)])); return;
            case ILOp.Ldarg_S or ILOp.Ldarg:
                expressions.Add(Expression.Assign(stack[height], arguments[instruction.IntOperand])); return;
            case ILOp.Starg_S or ILOp.Starg:
                expressions.Add(Expression.Assign(arguments[instruction.IntOperand], stack[height - 1])); return;
            case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                expressions.Add(Expression.Assign(stack[height], locals[(int)(op - ILOp.Ldloc_0)])); return;
            case ILOp.Ldloc_S or ILOp.Ldloc:
                expressions.Add(Expression.Assign(stack[height], locals[instruction.IntOperand])); return;
            case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                expressions.Add(Expression.Assign(locals[(int)(op - ILOp.Stloc_0)], stack[height - 1])); return;
            case ILOp.Stloc_S or ILOp.Stloc:
                expressions.Add(Expression.Assign(locals[instruction.IntOperand], stack[height - 1])); return;
            case ILOp.Ldc_I4_M1:
                expressions.Add(Expression.Assign(stack[height], OfInt(-1))); return;
            case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                expressions.Add(Expression.Assign(stack[height], OfInt((int)(op - ILOp.Ldc_I4_0)))); return;
            case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                expressions.Add(Expression.Assign(stack[height], OfInt(instruction.IntOperand))); return;
            case ILOp.Dup:
                expressions.Add(Expression.Assign(stack[height], stack[height - 1])); return;
            case ILOp.Pop:
                return;
            case ILOp.Newarr:
                expressions.Add(Expression.Assign(stack[height - 1], ContextCall(context,
                    nameof(ScalarIntJitContext.NewArray), stack[height - 1], Expression.Constant(instruction.IntOperand)))); return;
            case ILOp.Newobj:
                expressions.Add(Expression.Assign(stack[height], ContextCall(context,
                    nameof(ScalarIntJitContext.NewObject0), Expression.Constant(instruction.IntOperand)))); return;
            case ILOp.Ldfld:
                expressions.Add(Expression.Assign(stack[height - 1], ContextCall(context,
                    nameof(ScalarIntJitContext.LoadField), stack[height - 1], Expression.Constant(instruction.IntOperand)))); return;
            case ILOp.Stfld:
                expressions.Add(ContextCall(context, nameof(ScalarIntJitContext.StoreField),
                    stack[height - 2], stack[height - 1], Expression.Constant(instruction.IntOperand))); return;
            case ILOp.Ldlen:
                expressions.Add(Expression.Assign(stack[height - 1], ContextCall(context,
                    nameof(ScalarIntJitContext.ArrayLength), stack[height - 1]))); return;
            case ILOp.Ldelem_I4:
                expressions.Add(Expression.Assign(stack[height - 2], ContextCall(context,
                    nameof(ScalarIntJitContext.LoadArrayElementI4), stack[height - 2], stack[height - 1]))); return;
            case ILOp.Stelem_I4:
                expressions.Add(ContextCall(context, nameof(ScalarIntJitContext.StoreArrayElementI4),
                    stack[height - 3], stack[height - 2], stack[height - 1])); return;
            case ILOp.Conv_I4 or ILOp.Conv_U4:
                return;
            case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or
                ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un:
                expressions.Add(Expression.Assign(stack[height - 2], ApplyCall(op, stack[height - 2], stack[height - 1]))); return;
            case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                expressions.Add(Expression.Assign(stack[height - 2], CompareCall(op, stack[height - 2], stack[height - 1]))); return;
            case ILOp.Br or ILOp.Br_S:
                expressions.Add(Expression.Goto(labels[Target(instruction, offsets)])); terminated = true; return;
            case ILOp.BrTrue or ILOp.BrTrue_S:
                expressions.Add(BranchExpression(stack[height - 1], labels[Target(instruction, offsets)], labels[index + 1], true));
                terminated = true; return;
            case ILOp.BrFalse or ILOp.BrFalse_S:
                expressions.Add(BranchExpression(stack[height - 1], labels[Target(instruction, offsets)], labels[index + 1], false));
                terminated = true; return;
            case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
                ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
                ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
                ILOp.Blt_Un or ILOp.Blt_Un_S:
                expressions.Add(Expression.IfThenElse(CompareCall(op, stack[height - 2], stack[height - 1]),
                    Expression.Goto(labels[Target(instruction, offsets)]), Expression.Goto(labels[index + 1])));
                terminated = true; return;
            case ILOp.Ret:
                expressions.Add(Expression.Assign(result, stack[height - 1]));
                expressions.Add(Expression.Goto(returnLabel)); terminated = true; return;
            default:
                throw new InvalidOperationException($"未対応の配列式JIT命令です: {op}");
        }
    }

    private static Expression FusionExpression(IlFusion fusion, ParameterExpression[] locals) => fusion.Kind switch {
        IlFusionKind.LocalConstantOperationStore => ApplyCall(fusion.OperationA, locals[fusion.LocalA], OfInt(fusion.ValueA)),
        IlFusionKind.LocalLocalOperationStore => ApplyCall(fusion.OperationA, locals[fusion.LocalA], locals[fusion.LocalB]),
        IlFusionKind.LocalTwoConstantsOperationStore => ApplyCall(fusion.OperationB,
            ApplyCall(fusion.OperationA, locals[fusion.LocalA], OfInt(fusion.ValueA)), OfInt(fusion.ValueB)),
        IlFusionKind.LocalLocalConstantOperationStore => ApplyCall(fusion.OperationB, locals[fusion.LocalA],
            ApplyCall(fusion.OperationA, locals[fusion.LocalB], OfInt(fusion.ValueA))),
        _ => throw new InvalidOperationException($"未知の融合種別です: {fusion.Kind}"),
    };

    private static Expression BranchExpression(Expression value, LabelTarget taken, LabelTarget next, bool whenTrue) {
        var condition = Expression.Call(typeof(ScalarIntArrayExpressionJit).GetMethod(nameof(IsTrue),
            BindingFlags.Public | BindingFlags.Static)!, value);
        return Expression.IfThenElse(whenTrue ? condition : Expression.Not(condition),
            Expression.Goto(taken), Expression.Goto(next));
    }

    private static MethodCallExpression OfInt(int value) => Expression.Call(typeof(StackSlot).GetMethod(
        nameof(StackSlot.OfInt32), [typeof(int)])!, Expression.Constant(value));
    private static MethodCallExpression ApplyCall(ILOp op, Expression left, Expression right) => Expression.Call(
        typeof(ScalarIntArrayExpressionJit).GetMethod(nameof(ApplySlot), BindingFlags.Public | BindingFlags.Static)!,
        Expression.Constant(op), left, right);
    private static MethodCallExpression CompareCall(ILOp op, Expression left, Expression right) => Expression.Call(
        typeof(ScalarIntArrayExpressionJit).GetMethod(nameof(CompareSlot), BindingFlags.Public | BindingFlags.Static)!,
        Expression.Constant(op), left, right);
    private static MethodCallExpression ContextCall(ParameterExpression context, string name, params Expression[] args) {
        var method = typeof(ScalarIntJitContext).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(typeof(ScalarIntJitContext).FullName, name);
        return Expression.Call(context, method, args);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrue(in StackSlot value) => value.AsInt32 != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot ApplySlot(ILOp op, in StackSlot left, in StackSlot right) =>
        StackSlot.OfInt32(ScalarIntExpressionJit.Apply(op, left.AsInt32, right.AsInt32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot CompareSlot(ILOp op, in StackSlot left, in StackSlot right) {
        var l = left.AsInt32;
        var r = right.AsInt32;
        var result = op switch {
            ILOp.Ceq or ILOp.Beq or ILOp.Beq_S => l == r,
            ILOp.Cgt or ILOp.Bgt or ILOp.Bgt_S => l > r,
            ILOp.Cgt_Un or ILOp.Bgt_Un or ILOp.Bgt_Un_S => (uint)l > (uint)r,
            ILOp.Clt or ILOp.Blt or ILOp.Blt_S => l < r,
            ILOp.Clt_Un or ILOp.Blt_Un or ILOp.Blt_Un_S => (uint)l < (uint)r,
            ILOp.Bne_Un or ILOp.Bne_Un_S => l != r,
            ILOp.Bge or ILOp.Bge_S => l >= r,
            ILOp.Bge_Un or ILOp.Bge_Un_S => (uint)l >= (uint)r,
            ILOp.Ble or ILOp.Ble_S => l <= r,
            ILOp.Ble_Un or ILOp.Ble_Un_S => (uint)l <= (uint)r,
            _ => throw new InvalidOperationException($"配列式JITに適用できない比較です: {op}"),
        };
        return StackSlot.OfInt32(result ? 1 : 0);
    }

    private static bool TryComputeStackHeights(DecodedInstruction[] code,
        IReadOnlyDictionary<int, int> offsets, out int[] heights, out int maxStack) {
        var computedHeights = Enumerable.Repeat(-1, code.Length).ToArray();
        heights = computedHeights;
        maxStack = 0;
        var queue = new Queue<int>();
        computedHeights[0] = 0;
        queue.Enqueue(0);
        while (queue.Count > 0) {
            var index = queue.Dequeue();
            var input = computedHeights[index];
            var delta = StackDelta(code[index].Op);
            var output = input + delta;
            if (output < 0)
                return false;
            if (code[index].Op is ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S)
                output = input - 1;
            else if (IsCompareBranch(code[index].Op))
                output = input - 2;
            if (output < 0) return false;
            maxStack = Math.Max(maxStack, Math.Max(input, output));
            if (code[index].Op == ILOp.Ret) continue;
            if (code[index].Op is ILOp.Br or ILOp.Br_S) {
                if (!Merge(Target(code[index], offsets), output)) return false;
            } else if (IsConditional(code[index].Op)) {
                if (!Merge(Target(code[index], offsets), output) || !Merge(index + 1, output)) return false;
            } else if (index + 1 < code.Length && !Merge(index + 1, output)) return false;
        }
        heights = computedHeights;
        return computedHeights.All(value => value >= 0);

        bool Merge(int target, int value) {
            if ((uint)target >= (uint)code.Length) return false;
            if (computedHeights[target] < 0) { computedHeights[target] = value; queue.Enqueue(target); return true; }
            return computedHeights[target] == value;
        }
    }

    private static int StackDelta(ILOp op) => op switch {
        ILOp.Nop or ILOp.Break or ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or
        ILOp.BrFalse_S or ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
        ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble or
        ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S => 0,
        ILOp.Ret => -1,
        ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg or
        ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or ILOp.Ldloc_S or ILOp.Ldloc or
        ILOp.Ldc_I4_M1 or >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Dup => 1,
        ILOp.Starg_S or ILOp.Starg or ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3 or
        ILOp.Stloc_S or ILOp.Stloc or ILOp.Pop => -1,
        ILOp.Newarr or ILOp.Ldlen or ILOp.Ldfld or ILOp.Conv_I4 or ILOp.Conv_U4 => 0,
        ILOp.Newobj => 1,
        ILOp.Ldelem_I4 => -1,
        ILOp.Stelem_I4 => -3,
        ILOp.Stfld => -2,
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor or
        ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un => -1,
        _ => int.MinValue,
    };

    private static bool IsConditional(ILOp op) => op is ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or
        ILOp.BrFalse_S or ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
        ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble or
        ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S;

    private static bool IsCompareBranch(ILOp op) => op is ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or
        ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or
        ILOp.Bgt_Un_S or ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
        ILOp.Blt_Un or ILOp.Blt_Un_S;

    private static int Target(DecodedInstruction instruction, IReadOnlyDictionary<int, int> offsets) =>
        instruction.BranchTargetIndex >= 0 ? instruction.BranchTargetIndex : offsets[instruction.IntOperand];

    private static bool[] BuildBlockStarts(DecodedInstruction[] code, IReadOnlyDictionary<int, int> offsets) {
        var starts = new bool[code.Length + 1];
        starts[0] = true;
        for (var i = 0; i < code.Length; i++) {
            if (IsConditional(code[i].Op) || code[i].Op is ILOp.Br or ILOp.Br_S or ILOp.Ret)
                starts[i + 1] = true;
            if (IsConditional(code[i].Op) || code[i].Op is ILOp.Br or ILOp.Br_S)
                starts[Target(code[i], offsets)] = true;
        }
        return starts;
    }
}
