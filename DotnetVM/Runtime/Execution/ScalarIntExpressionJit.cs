using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>整数専用 JIT の式木生成器。IL の制御フローをラベルへ直接変換する。</summary>
internal static class ScalarIntExpressionJit {
    private readonly record struct InlineCall(
        VmMethod Method, DecodedInstruction[] Code, int ArgumentCount, int Cost);

    internal static JitScalar? TryCreate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, bool returnsValue) =>
        TryCreateCore(method, prepared, code, returnsValue, direct: false) as JitScalar;

    internal static JitScalarDirect? TryCreateDirect(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, bool returnsValue) =>
        TryCreateCore(method, prepared, code, returnsValue, direct: true) as JitScalarDirect;

    private static Delegate? TryCreateCore(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, bool returnsValue, bool direct) {
        var inlineCalls = new Dictionary<int, InlineCall>();
        for (var i = 0; i < code.Length; i++) {
            if (code[i].Op != ILOp.Call)
                continue;
            if (!TryCreateInlineCall(method, code[i], out var inlineCall))
                return null;
            inlineCalls[i] = inlineCall;
        }

        if (!TryComputeStackHeights(code, prepared.OffsetMap, returnsValue, inlineCalls,
                out var heights, out var maxStack))
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
            var instructionCount = 0;
            for (var i = start; i < end; i++) {
                cost = checked(cost + code[i].InstructionCost);
                instructionCount++;
                if (inlineCalls.TryGetValue(i, out var inlineCall)) {
                    cost = checked(cost + inlineCall.Cost);
                    instructionCount = checked(instructionCount + inlineCall.Code.Length);
                }
            }
            for (var i = start; i < end; i++) {
                costs[i] = cost;
                counts[i] = instructionCount;
                ends[i] = end;
            }
            start = end;
        }

        try {
            var context = Expression.Parameter(typeof(ScalarIntJitContext), "context");
            var locals = Enumerable.Range(0, prepared.LocalTypes.Length)
                .Select(i => Expression.Variable(typeof(int), $"local{i}")).ToArray();
            var arguments = Enumerable.Range(0, method.Signature.ParamTypes.Length)
                .Select(i => Expression.Variable(typeof(int), $"arg{i}")).ToArray();
            var stack = Enumerable.Range(0, maxStack)
                .Select(i => Expression.Variable(typeof(int), $"stack{i}")).ToArray();
            var result = Expression.Variable(typeof(int), "result");
            var labels = Enumerable.Range(0, code.Length)
                .Select(i => Expression.Label($"il{i}")).ToArray();
            var returnLabel = Expression.Label("return");
            var expressions = new List<Expression>(code.Length * 2 + locals.Length + arguments.Length + 2);

            for (var i = 0; i < arguments.Length; i++)
                expressions.Add(Expression.Assign(arguments[i], Call(context, nameof(ScalarIntJitContext.ReadArgument), i)));
            for (var i = 0; i < locals.Length; i++)
                expressions.Add(Expression.Assign(locals[i], Call(context, nameof(ScalarIntJitContext.ReadLocal), i)));

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
                expressions.Add(Call(context, nameof(ScalarIntJitContext.Charge),
                    costs[start], counts[start]));
                var terminated = false;
                for (var i = start; i < end; i++) {
                    var instruction = code[i];
                    AppendInstruction(expressions, instruction, i, heights[i], stack, locals,
                        arguments, labels, returnLabel, result, returnsValue, context,
                        inlineCalls, ref terminated);
                    if (terminated)
                        break;
                }
                if (!terminated && end < code.Length)
                    expressions.Add(Expression.Goto(labels[end]));
                start = end;
            }

            expressions.Add(Expression.Label(returnLabel));
            expressions.Add(returnsValue
                ? Expression.Call(typeof(StackSlot).GetMethod(nameof(StackSlot.OfInt32), [typeof(int)])!, result)
                : Expression.Default(typeof(StackSlot)));
            var variables = arguments.Concat(locals).Concat(stack).Append(result);
            var body = Expression.Block(variables, expressions);
            var compiled = Expression.Lambda<Func<ScalarIntJitContext, StackSlot>>(body, context).Compile();
            if (direct)
                return (JitScalarDirect)((interpreter, arguments) => {
                    using var runtime = new ScalarIntJitContext(interpreter, arguments);
                    return compiled(runtime);
                });
            return (JitScalar)((interpreter, _, frame) => {
                using var runtime = new ScalarIntJitContext(interpreter, frame);
                return compiled(runtime);
            });
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NotSupportedException or PlatformNotSupportedException) {
            return null;
        }
    }

    private static bool TryCreateInlineCall(VmMethod caller, DecodedInstruction instruction,
        out InlineCall call) {
        call = default;
        if ((TableKind)((uint)instruction.IntOperand >> 24) != TableKind.MethodDef ||
            caller.Loader?.GetMethodByToken((uint)instruction.IntOperand) is not { } target ||
            !target.IsStatic || target.Body is null || target.Signature.GenericParamCount != 0 ||
            target.Body.ExceptionClauses is { Length: > 0 } ||
            target.Body.DynamicLocalTypes is { Length: > 0 } ||
            target.Signature.ReturnType.Kind != SigKind.I4 ||
            target.Signature.ParamTypes.Any(type => type.Kind != SigKind.I4))
            return false;

        DecodedInstruction[] targetCode;
        try {
            targetCode = IlDecoder.Decode(target.Body.IlCode);
        } catch (BadImageFormatException) {
            return false;
        }
        if (targetCode.Length == 0 || targetCode[^1].Op != ILOp.Ret ||
            targetCode.Any(instructionInTarget => !IsInlineInstruction(instructionInTarget.Op)))
            return false;
        call = new InlineCall(target, targetCode, target.Signature.ParamTypes.Length,
            targetCode.Sum(instructionInTarget => instructionInTarget.InstructionCost));
        return true;
    }

    private static bool IsInlineInstruction(ILOp op) => op is
        ILOp.Nop or ILOp.Break or ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or
        ILOp.Ldarg_S or ILOp.Ldarg or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or
        ILOp.Ldc_I4_2 or ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or
        ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Dup or ILOp.Pop or
        ILOp.Neg or ILOp.Not or ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.And or ILOp.Or or
        ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
        ILOp.Clt or ILOp.Clt_Un or ILOp.Conv_I4 or ILOp.Conv_U4 or ILOp.Ret;

    private static Expression InlineExpression(InlineCall call, ParameterExpression context,
        Expression[] arguments) {
        var stack = new List<Expression>();
        foreach (var instruction in call.Code) {
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                    break;
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    stack.Add(arguments[(int)(instruction.Op - ILOp.Ldarg_0)]);
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    stack.Add(arguments[instruction.IntOperand]);
                    break;
                case ILOp.Ldc_I4_M1:
                    stack.Add(Expression.Constant(-1));
                    break;
                case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                    stack.Add(Expression.Constant((int)(instruction.Op - ILOp.Ldc_I4_0)));
                    break;
                case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                    stack.Add(Expression.Constant(instruction.IntOperand));
                    break;
                case ILOp.Dup:
                    stack.Add(stack[^1]);
                    break;
                case ILOp.Pop:
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case ILOp.Neg:
                    stack[^1] = Expression.Negate(stack[^1]);
                    break;
                case ILOp.Not:
                    stack[^1] = Expression.Not(stack[^1]);
                    break;
                case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.And or ILOp.Or or ILOp.Xor or
                    ILOp.Shl or ILOp.Shr or ILOp.Shr_Un:
                    var right = stack[^1];
                    var left = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(BinaryExpression(instruction.Op, left, right));
                    break;
                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                    var compareRight = stack[^1];
                    var compareLeft = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(BoolToInt(CompareExpression(instruction.Op, compareLeft, compareRight)));
                    break;
                case ILOp.Conv_I4 or ILOp.Conv_U4:
                    break;
                case ILOp.Ret:
                    var value = stack[^1];
                    return value;
                default:
                    throw new InvalidOperationException($"インライン化できないIL命令です: {instruction.Op}");
            }
        }
        throw new InvalidOperationException("インライン化対象の ret がありません。");
    }

    public static int Apply(ILOp op, int left, int right) => op switch {
        ILOp.Add => unchecked(left + right),
        ILOp.Sub => unchecked(left - right),
        ILOp.Mul => unchecked(left * right),
        ILOp.Rem => Remainder(left, right),
        ILOp.Rem_Un => UnsignedRemainder(left, right),
        ILOp.And => left & right,
        ILOp.Or => left | right,
        ILOp.Xor => left ^ right,
        ILOp.Shl => left << (right & 31),
        ILOp.Shr => left >> (right & 31),
        ILOp.Shr_Un => (int)((uint)left >> (right & 31)),
        _ => throw new InvalidOperationException($"スカラー式 JIT に適用できない演算です: {op}"),
    };

    private static int Remainder(int left, int right) {
        if (right == 0)
            SlotOps.ThrowDivideByZero();
        if (left == int.MinValue && right == -1)
            SlotOps.ThrowOverflow();
        return left % right;
    }

    private static int UnsignedRemainder(int left, int right) {
        if (right == 0)
            SlotOps.ThrowDivideByZero();
        return (int)((uint)left % (uint)right);
    }

    private static void AppendInstruction(List<Expression> expressions, DecodedInstruction instruction,
        int index, int height, ParameterExpression[] stack, ParameterExpression[] locals,
        ParameterExpression[] arguments, LabelTarget[] labels, LabelTarget returnLabel,
        ParameterExpression result, bool returnsValue, ParameterExpression context,
        IReadOnlyDictionary<int, InlineCall> inlineCalls, ref bool terminated) {
        if (instruction.Fusion.Kind != IlFusionKind.None) {
            expressions.Add(Expression.Assign(locals[instruction.Fusion.LocalC],
                FusionExpression(instruction.Fusion, locals)));
            return;
        }

        var op = instruction.Op;
        switch (op) {
            case ILOp.Nop or ILOp.Break:
                return;
            case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                expressions.Add(Expression.Assign(stack[height],
                    arguments[(int)(op - ILOp.Ldarg_0)]));
                return;
            case ILOp.Ldarg_S or ILOp.Ldarg:
                expressions.Add(Expression.Assign(stack[height], arguments[instruction.IntOperand]));
                return;
            case ILOp.Starg_S or ILOp.Starg:
                expressions.Add(Expression.Assign(arguments[instruction.IntOperand], stack[height - 1]));
                return;
            case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                expressions.Add(Expression.Assign(stack[height], locals[(int)(op - ILOp.Ldloc_0)]));
                return;
            case ILOp.Ldloc_S or ILOp.Ldloc:
                expressions.Add(Expression.Assign(stack[height], locals[instruction.IntOperand]));
                return;
            case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                expressions.Add(Expression.Assign(locals[(int)(op - ILOp.Stloc_0)], stack[height - 1]));
                return;
            case ILOp.Stloc_S or ILOp.Stloc:
                expressions.Add(Expression.Assign(locals[instruction.IntOperand], stack[height - 1]));
                return;
            case ILOp.Ldc_I4_M1:
                expressions.Add(Expression.Assign(stack[height], Expression.Constant(-1))); return;
            case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                expressions.Add(Expression.Assign(stack[height],
                    Expression.Constant((int)(op - ILOp.Ldc_I4_0)))); return;
            case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                expressions.Add(Expression.Assign(stack[height], Expression.Constant(instruction.IntOperand))); return;
            case ILOp.Dup:
                expressions.Add(Expression.Assign(stack[height], stack[height - 1])); return;
            case ILOp.Pop:
                return;
            case ILOp.Neg:
                expressions.Add(Expression.Assign(stack[height - 1], Expression.Negate(stack[height - 1]))); return;
            case ILOp.Not:
                expressions.Add(Expression.Assign(stack[height - 1], Expression.Not(stack[height - 1]))); return;
            case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or
                ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un:
                expressions.Add(Expression.Assign(stack[height - 2],
                    BinaryExpression(op, stack[height - 2], stack[height - 1]))); return;
            case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                expressions.Add(Expression.Assign(stack[height - 2],
                    BoolToInt(CompareExpression(op, stack[height - 2], stack[height - 1])))); return;
            case ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4:
                expressions.Add(Expression.Assign(stack[height - 1], ConvertExpression(op, stack[height - 1]))); return;
            case ILOp.Call: {
                var call = inlineCalls[index];
                var callArguments = Enumerable.Range(0, call.ArgumentCount)
                    .Select(argument => stack[height - call.ArgumentCount + argument])
                    .ToArray();
                var value = InlineExpression(call, context, callArguments);
                expressions.Add(Expression.Assign(stack[height - call.ArgumentCount], value));
                return;
            }
            case ILOp.Br or ILOp.Br_S:
                expressions.Add(Expression.Goto(labels[Target(instruction)])); terminated = true; return;
            case ILOp.BrTrue or ILOp.BrTrue_S:
                expressions.Add(Expression.IfThenElse(
                    Expression.NotEqual(stack[height - 1], Expression.Constant(0)),
                    Expression.Goto(labels[Target(instruction)]), Expression.Goto(labels[index + 1])));
                terminated = true; return;
            case ILOp.BrFalse or ILOp.BrFalse_S:
                expressions.Add(Expression.IfThenElse(
                    Expression.Equal(stack[height - 1], Expression.Constant(0)),
                    Expression.Goto(labels[Target(instruction)]), Expression.Goto(labels[index + 1])));
                terminated = true; return;
            case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
                ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
                ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
                ILOp.Blt_Un or ILOp.Blt_Un_S:
                expressions.Add(Expression.IfThenElse(
                    CompareExpression(op, stack[height - 2], stack[height - 1]),
                    Expression.Goto(labels[Target(instruction)]), Expression.Goto(labels[index + 1])));
                terminated = true; return;
            case ILOp.Ret:
                if (returnsValue)
                    expressions.Add(Expression.Assign(result, stack[height - 1]));
                expressions.Add(Expression.Goto(returnLabel));
                terminated = true; return;
            default:
                throw new InvalidOperationException($"未対応のスカラー式 JIT 命令です: {op}");
        }
    }

    private static Expression FusionExpression(IlFusion fusion, ParameterExpression[] locals) {
        var left = locals[fusion.LocalA];
        return fusion.Kind switch {
            IlFusionKind.LocalConstantOperationStore => BinaryExpression(
                fusion.OperationA, left, Expression.Constant(fusion.ValueA)),
            IlFusionKind.LocalLocalOperationStore => BinaryExpression(
                fusion.OperationA, left, locals[fusion.LocalB]),
            IlFusionKind.LocalTwoConstantsOperationStore => BinaryExpression(
                fusion.OperationB,
                BinaryExpression(fusion.OperationA, left, Expression.Constant(fusion.ValueA)),
                Expression.Constant(fusion.ValueB)),
            IlFusionKind.LocalLocalConstantOperationStore => BinaryExpression(
                fusion.OperationB, left,
                BinaryExpression(fusion.OperationA, locals[fusion.LocalB],
                    Expression.Constant(fusion.ValueA))),
            _ => throw new InvalidOperationException($"未知の IL 融合種別です: {fusion.Kind}"),
        };
    }

    private static Expression BinaryExpression(ILOp op, Expression left, Expression right) => op switch {
        ILOp.Add => Expression.Add(left, right),
        ILOp.Sub => Expression.Subtract(left, right),
        ILOp.Mul => Expression.Multiply(left, right),
        ILOp.And => Expression.And(left, right),
        ILOp.Or => Expression.Or(left, right),
        ILOp.Xor => Expression.ExclusiveOr(left, right),
        ILOp.Shl => Expression.LeftShift(left, Expression.And(right, Expression.Constant(31))),
        ILOp.Shr => Expression.RightShift(left, Expression.And(right, Expression.Constant(31))),
        ILOp.Shr_Un => Expression.Convert(
            Expression.RightShift(Expression.Convert(left, typeof(uint)),
                Expression.And(right, Expression.Constant(31))), typeof(int)),
        ILOp.Rem or ILOp.Rem_Un => Expression.Call(typeof(ScalarIntExpressionJit).GetMethod(
            nameof(Apply), BindingFlags.Public | BindingFlags.Static)!,
            Expression.Constant(op), left, right),
        _ => throw new InvalidOperationException($"スカラー式 JIT に適用できない演算です: {op}"),
    };

    private static Expression ConvertExpression(ILOp op, Expression value) => op switch {
        ILOp.Conv_I1 => Expression.Convert(Expression.Convert(value, typeof(sbyte)), typeof(int)),
        ILOp.Conv_I2 => Expression.Convert(Expression.Convert(value, typeof(short)), typeof(int)),
        ILOp.Conv_I4 or ILOp.Conv_U4 => value,
        ILOp.Conv_U1 => Expression.Convert(Expression.Convert(value, typeof(byte)), typeof(int)),
        ILOp.Conv_U2 => Expression.Convert(Expression.Convert(value, typeof(ushort)), typeof(int)),
        _ => throw new InvalidOperationException($"スカラー式 JIT に適用できない変換です: {op}"),
    };

    private static Expression CompareExpression(ILOp op, Expression left, Expression right) => op switch {
        ILOp.Ceq or ILOp.Beq or ILOp.Beq_S => Expression.Equal(left, right),
        ILOp.Cgt or ILOp.Bgt or ILOp.Bgt_S => Expression.GreaterThan(left, right),
        ILOp.Cgt_Un or ILOp.Bgt_Un or ILOp.Bgt_Un_S => Expression.GreaterThan(
            Expression.Convert(left, typeof(uint)), Expression.Convert(right, typeof(uint))),
        ILOp.Clt or ILOp.Blt or ILOp.Blt_S => Expression.LessThan(left, right),
        ILOp.Clt_Un or ILOp.Blt_Un or ILOp.Blt_Un_S => Expression.LessThan(
            Expression.Convert(left, typeof(uint)), Expression.Convert(right, typeof(uint))),
        ILOp.Bne_Un or ILOp.Bne_Un_S => Expression.NotEqual(left, right),
        ILOp.Bge or ILOp.Bge_S => Expression.GreaterThanOrEqual(left, right),
        ILOp.Bge_Un or ILOp.Bge_Un_S => Expression.GreaterThanOrEqual(
            Expression.Convert(left, typeof(uint)), Expression.Convert(right, typeof(uint))),
        ILOp.Ble or ILOp.Ble_S => Expression.LessThanOrEqual(left, right),
        ILOp.Ble_Un or ILOp.Ble_Un_S => Expression.LessThanOrEqual(
            Expression.Convert(left, typeof(uint)), Expression.Convert(right, typeof(uint))),
        _ => throw new InvalidOperationException($"スカラー式 JIT に適用できない比較です: {op}"),
    };

    private static Expression BoolToInt(Expression value) => Expression.Condition(value,
        Expression.Constant(1), Expression.Constant(0));

    private static int Target(DecodedInstruction instruction) => instruction.BranchTargetIndex;
    private static int Target(DecodedInstruction instruction, IReadOnlyDictionary<int, int> offsets) =>
        instruction.BranchTargetIndex >= 0 ? instruction.BranchTargetIndex : offsets[instruction.IntOperand];

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

    private static bool TryComputeStackHeights(DecodedInstruction[] code,
        IReadOnlyDictionary<int, int> offsets, bool returnsValue,
        IReadOnlyDictionary<int, InlineCall> inlineCalls,
        out int[] heights, out int maxStack) {
        var computedHeights = Enumerable.Repeat(-1, code.Length).ToArray();
        heights = computedHeights;
        maxStack = 0;
        if (code.Length == 0)
            return false;
        var queue = new Queue<int>();
        computedHeights[0] = 0;
        queue.Enqueue(0);
        while (queue.Count > 0) {
            var index = queue.Dequeue();
            var instruction = code[index];
            var input = computedHeights[index];
            if (!TryStackEffect(instruction, index, input, returnsValue, inlineCalls, out var output) || output < 0)
                return false;
            maxStack = Math.Max(maxStack, Math.Max(input, output));
            if (instruction.Op == ILOp.Ret)
                continue;
            if (IsUnconditionalBranch(instruction)) {
                if (!Merge(Target(instruction, offsets), output)) return false;
                continue;
            }
            if (IsConditionalBranch(instruction)) {
                if (!Merge(Target(instruction, offsets), output) || !Merge(index + 1, output)) return false;
                continue;
            }
            if (index + 1 < code.Length && !Merge(index + 1, output))
                return false;
        }
        return computedHeights.All(height => height >= 0);

        bool Merge(int target, int height) {
            if ((uint)target >= (uint)code.Length)
                return false;
            if (computedHeights[target] < 0) {
                computedHeights[target] = height;
                queue.Enqueue(target);
                return true;
            }
            return computedHeights[target] == height;
        }
    }

    private static bool TryStackEffect(DecodedInstruction instruction, int index, int input,
        bool returnsValue, IReadOnlyDictionary<int, InlineCall> inlineCalls, out int output) {
        var op = instruction.Op;
        var delta = op switch {
            ILOp.Nop or ILOp.Break or ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or
            ILOp.BrFalse or ILOp.BrFalse_S or ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or
            ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or
            ILOp.Bgt_Un or ILOp.Bgt_Un_S or ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or
            ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S or ILOp.Ret =>
                op == ILOp.Ret ? (returnsValue ? -1 : 0) : 0,
            ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg or
            ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or ILOp.Ldloc_S or ILOp.Ldloc or
            ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or ILOp.Ldc_I4_3 or
            ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8 or
            ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Dup => 1,
            ILOp.Starg_S or ILOp.Starg or ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3 or
            ILOp.Stloc_S or ILOp.Stloc or ILOp.Pop => -1,
            ILOp.Neg or ILOp.Not or ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_U1 or
            ILOp.Conv_U2 or ILOp.Conv_U4 => 0,
            ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or
            ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
            ILOp.Clt or ILOp.Clt_Un => -1,
            ILOp.Call when inlineCalls.TryGetValue(index, out var inlineCall) =>
                1 - inlineCall.ArgumentCount,
            _ => int.MinValue,
        };
        if (delta == int.MinValue || input + delta < 0) {
            output = -1;
            return false;
        }
        output = input + delta;
        if (op is ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S)
            output = input - 1;
        else if (IsConditionalBranch(op))
            output = input - 2;
        return output >= 0;
    }

    private static bool IsUnconditionalBranch(DecodedInstruction instruction,
        IReadOnlyDictionary<int, int>? offsets = null) => instruction.Op is ILOp.Br or ILOp.Br_S;
    private static bool IsUnconditionalBranch(ILOp op) => op is ILOp.Br or ILOp.Br_S;
    private static bool IsConditionalBranch(DecodedInstruction instruction) => IsConditionalBranch(instruction.Op);
    private static bool IsConditionalBranch(ILOp op) => op is ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or
        ILOp.BrFalse_S or ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
        ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
        ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
        ILOp.Blt_Un or ILOp.Blt_Un_S;
    private static bool IsControlFlow(DecodedInstruction instruction) =>
        IsUnconditionalBranch(instruction) || IsConditionalBranch(instruction);

    private static MethodCallExpression Call(Expression instance, string name, params int[] values) {
        var parameterTypes = Enumerable.Repeat(typeof(int), values.Length).ToArray();
        var method = typeof(ScalarIntJitContext).GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null)
            ?? throw new MissingMethodException(typeof(ScalarIntJitContext).FullName, name);
        return Expression.Call(instance, method, values.Select(value => Expression.Constant(value)));
    }
}

internal sealed class ScalarIntJitContext : IDisposable {
    private const int ReservationSize = 4096;
    private readonly Interpreter _interpreter;
    private readonly Interpreter.ExecutionState _state;
    private readonly StackSlot[] _arguments;
    private readonly InterpreterFrame? _frame;
    private VmExecutionCoordinator.InstructionBatchLease _batch;
    private VmExecutionCoordinator.InstructionLease _instruction;
    private int _batchInstructions;
    private int _remaining;
    private int _pendingInstructionCount;
    private bool _disposed;

    internal ScalarIntJitContext(Interpreter interpreter, InterpreterFrame frame) {
        _interpreter = interpreter;
        _state = interpreter.CurrentExecutionState;
        _frame = frame;
        _arguments = frame.Arguments;
        _interpreter.CheckJitSafepoint();
        _batch = _interpreter.EnterJitInstructionBatch();
        _instruction = _interpreter.EnterJitInstructionInBatch();
    }

    internal ScalarIntJitContext(Interpreter interpreter, StackSlot[] arguments) {
        _interpreter = interpreter;
        _state = interpreter.CurrentExecutionState;
        _arguments = arguments;
        _interpreter.CheckJitSafepoint();
        _batch = _interpreter.EnterJitInstructionBatch();
        _instruction = _interpreter.EnterJitInstructionInBatch();
    }

    public int ReadArgument(int index) => _arguments[index].AsInt32;
    public int ReadLocal(int index) => _frame?.Locals[index].AsInt32 ?? 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Charge(int cost, int instructionCount) {
        if (!_interpreter.InstructionChargingEnabled) {
            _batchInstructions += instructionCount;
            return;
        }
        if (_batchInstructions < Interpreter.SafepointInterval && _remaining >= cost) {
            _remaining -= cost;
            _pendingInstructionCount += cost;
            _batchInstructions += instructionCount;
            return;
        }
        ChargeSlow(cost, instructionCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ChargeSlow(int cost, int instructionCount) {
        if (_batchInstructions >= Interpreter.SafepointInterval) {
            _state.InstructionCount += _pendingInstructionCount;
            _pendingInstructionCount = 0;
            _instruction.Dispose();
            _instruction = default;
            _batch.Dispose();
            _batch = default;
            _interpreter.CheckJitSafepoint();
            _batch = _interpreter.EnterJitInstructionBatch();
            _instruction = _interpreter.EnterJitInstructionInBatch();
            _batchInstructions = 0;
        }
        if (_remaining < cost) {
            try {
                _remaining += _interpreter.ReserveJitInstructionChunk(Math.Max(ReservationSize, cost));
            } catch (InstructionQuotaExceededException) {
                var refund = _remaining;
                _remaining = 0;
                _interpreter.RefundJitInstructionChunk(refund);
                _interpreter.ConsumeJitInstructionForState(_state, cost);
                throw;
            }
        }
        if (_remaining < cost) {
            var refund = _remaining;
            _remaining = 0;
            _interpreter.RefundJitInstructionChunk(refund);
            _interpreter.ConsumeJitInstructionForState(_state, cost);
            throw new InstructionQuotaExceededException("スカラー式 JIT の命令クォータが不足しています。");
        }
        _remaining -= cost;
        _pendingInstructionCount += cost;
        _batchInstructions += instructionCount;
    }

    public void Dispose() {
        if (_disposed)
            return;
        _disposed = true;
        _instruction.Dispose();
        _batch.Dispose();
        _state.InstructionCount += _pendingInstructionCount;
        _pendingInstructionCount = 0;
        _interpreter.RefundJitInstructionChunk(_remaining);
    }

}
