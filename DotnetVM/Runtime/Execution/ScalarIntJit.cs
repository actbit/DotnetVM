using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// 検証済みの「整数スロットだけで閉じる」IL メソッド用の JIT 実行器。
/// 通常の JIT が命令ごとに StackSlot 操作とフレームメソッド呼出を行うのに対し、
/// この経路は同じ準備済み IL を int32 値として直接実行する。
/// オブジェクト、配列、EH は対象外。呼出しは検証済みの純粋な int32 static メソッドだけを
/// 式木へインライン化し、意味を変えるライブラリ置換は行わない。
/// </summary>
internal static class ScalarIntJit {
    internal static JitScalarDirect? TryCreateDirect(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, MethodPreparer preparer) {
        if (!IsScalarMethodEligible(method, prepared, code, out _))
            return null;
        var returnsValue = method.Signature.ReturnType.Kind != SigKind.Void;
        var result = ScalarIntExpressionJit.TryCreateDirect(method, prepared, code, returnsValue, preparer);
        return result;
    }

    internal static JitScalar? TryCreate(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, MethodPreparer preparer) {
        var scalarEligible = IsScalarMethodEligible(method, prepared, code, out var hasCalls);
        if (!scalarEligible) {
            if (ScalarIntArrayExpressionJit.TryCreate(method, prepared, code) is { } arrayExpression)
                return arrayExpression;
            if (TryCreateArray(method, prepared, code, out var arrayScalar))
                return arrayScalar;
            return null;
        }

        foreach (var instruction in code) {
            if (instruction.Op == ILOp.Call) {
                hasCalls = true;
                continue;
            }
            if (!IsSupported(instruction) || instruction.Fusion.Kind != IlFusionKind.None &&
                (!IsScalarOperation(instruction.Fusion.OperationA) ||
                 instruction.Fusion.Kind is IlFusionKind.LocalTwoConstantsOperationStore or
                 IlFusionKind.LocalLocalConstantOperationStore &&
                 !IsScalarOperation(instruction.Fusion.OperationB))) {
                return null;
            }
        }

        if (code.Length > Interpreter.SafepointInterval)
            return null;

        var (blockCosts, blockEnds) = BuildBlocks(code, prepared.OffsetMap);
        var returnsValue = method.Signature.ReturnType.Kind != SigKind.Void;
        if (ScalarIntExpressionJit.TryCreate(method, prepared, code, returnsValue, preparer) is { } expression)
            return expression;
        if (hasCalls)
            return null;
        return (interpreter, _, frame) => Execute(interpreter, frame, code, returnsValue,
            blockCosts, blockEnds);
    }

    private static bool TryCreateArray(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, out JitScalar? scalar) {
        scalar = null;
        if (method.Signature.HasThis || method.Signature.ReturnType.Kind != SigKind.I4 ||
            method.Signature.ParamTypes.Any(type => !IsInt32Like(type)) ||
            prepared.Clauses is { Length: > 0 } || code.Length == 0 ||
            prepared.LocalTypes.Any(type => type.Kind is not (SigKind.Boolean or SigKind.I1 or SigKind.U1 or
                SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4 or SigKind.SzArray)) ||
            prepared.LocalTypes.Any(type => type.Kind == SigKind.SzArray &&
                (type.Inner is null || type.Inner.Kind is not (SigKind.I4 or SigKind.TypeToken))))
            return false;

        foreach (var instruction in code) {
            if (instruction.Op == ILOp.Call || instruction.Fusion.Kind != IlFusionKind.None &&
                (!IsScalarOperation(instruction.Fusion.OperationA) ||
                 instruction.Fusion.Kind is IlFusionKind.LocalTwoConstantsOperationStore or
                 IlFusionKind.LocalLocalConstantOperationStore &&
                 !IsScalarOperation(instruction.Fusion.OperationB)))
                return false;
            if (!IsArraySupported(instruction.Op) ||
                instruction.Fusion.Kind != IlFusionKind.None &&
                !IlFusionValidation.IsValid(instruction.Fusion, prepared.LocalTypes.Length, out _)) {
                return false;
            }
        }
        if (code.Length > Interpreter.SafepointInterval)
            return false;

        var (blockCosts, blockEnds) = BuildBlocks(code, prepared.OffsetMap);
        scalar = (interpreter, services, frame) => ExecuteArray(interpreter, services, frame,
            code, blockCosts, blockEnds);
        return true;
    }

    private static StackSlot ExecuteArray(Interpreter interpreter, InterpreterServices services,
        InterpreterFrame frame, DecodedInstruction[] code, int[] blockCosts, int[] blockEnds) {
        var stack = frame.Stack.RootSlots;
        var sp = 0;
        var batchInstructions = 0;
        var state = interpreter.CurrentExecutionState;
        var budget = new ScalarInstructionBudget(interpreter, state);
        var batch = default(VmExecutionCoordinator.InstructionBatchLease);
        var instruction = default(VmExecutionCoordinator.InstructionLease);
        try {
            interpreter.CheckJitSafepoint();
            batch = interpreter.EnterJitInstructionBatch();
            instruction = interpreter.EnterJitInstructionInBatch();
            while (true) {
                if (batchInstructions >= Interpreter.SafepointInterval) {
                    instruction.Dispose();
                    instruction = default;
                    batch.Dispose();
                    batch = default;
                    interpreter.CheckJitSafepoint();
                    batch = interpreter.EnterJitInstructionBatch();
                    instruction = interpreter.EnterJitInstructionInBatch();
                    batchInstructions = 0;
                }

                var blockEnd = blockEnds[frame.Ip];
                budget.Take(blockCosts[frame.Ip]);
                while (frame.Ip < blockEnd) {
                    var current = code[frame.Ip];
                    var ip = frame.Ip;
                    frame.Ip = ip;
                    batchInstructions++;
                    if (current.Fusion.Kind != IlFusionKind.None) {
                        ExecuteFusion(frame.Locals, current.Fusion);
                        frame.Ip = ip + 1;
                        continue;
                    }

                    switch (current.Op) {
                        case ILOp.Nop or ILOp.Break:
                            frame.Ip = ip + 1; break;
                        case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                            PushSlot(ref sp, stack, frame.ArgumentAt((int)(current.Op - ILOp.Ldarg_0))); frame.Ip = ip + 1; break;
                        case ILOp.Ldarg_S or ILOp.Ldarg:
                            PushSlot(ref sp, stack, frame.ArgumentAt(current.IntOperand)); frame.Ip = ip + 1; break;
                        case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                            PushSlot(ref sp, stack, frame.Locals[(int)(current.Op - ILOp.Ldloc_0)]); frame.Ip = ip + 1; break;
                        case ILOp.Ldloc_S or ILOp.Ldloc:
                            PushSlot(ref sp, stack, frame.Locals[current.IntOperand]); frame.Ip = ip + 1; break;
                        case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                            frame.Locals[(int)(current.Op - ILOp.Stloc_0)] = PopSlot(ref sp, stack); frame.Ip = ip + 1; break;
                        case ILOp.Stloc_S or ILOp.Stloc:
                            frame.Locals[current.IntOperand] = PopSlot(ref sp, stack); frame.Ip = ip + 1; break;
                        case ILOp.Ldc_I4_M1:
                            PushSlot(ref sp, stack, StackSlot.OfInt32(-1)); frame.Ip = ip + 1; break;
                        case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                            PushSlot(ref sp, stack, StackSlot.OfInt32((int)(current.Op - ILOp.Ldc_I4_0))); frame.Ip = ip + 1; break;
                        case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                            PushSlot(ref sp, stack, StackSlot.OfInt32(current.IntOperand)); frame.Ip = ip + 1; break;
                        case ILOp.Dup:
                            PushSlot(ref sp, stack, stack[sp - 1]); frame.Ip = ip + 1; break;
                        case ILOp.Pop:
                            _ = PopSlot(ref sp, stack); frame.Ip = ip + 1; break;
                        case ILOp.Newarr: {
                            var count = PopSlot(ref sp, stack).AsInt32;
                            if (count < 0) throw new UnhandledGuestException("System.OverflowException", null);
                            var elementType = services.Loader.ResolveToken(new SigType(SigKind.TypeToken,
                                Token: unchecked((uint)current.IntOperand)));
                            var elements = new StackSlot[count];
                            for (var i = 0; i < count; i++)
                                elements[i] = services.Objects.DefaultForType(elementType, services.Loader);
                            using var reservation = services.Heap.ReserveArray(count);
                            PushSlot(ref sp, stack, StackSlot.OfObject(reservation.Commit(new VmArray(
                                new VmArrayType { ElementType = elementType }, elements))));
                            frame.Ip = ip + 1;
                            break;
                        }
                        case ILOp.Ldlen: {
                            var array = MemoryOps.GetArray(PopSlot(ref sp, stack));
                            PushSlot(ref sp, stack, StackSlot.OfNativeInt(array.Length));
                            frame.Ip = ip + 1;
                            break;
                        }
                        case ILOp.Ldelem_I4: {
                            var index = PopSlot(ref sp, stack).AsInt32;
                            var array = MemoryOps.GetArray(PopSlot(ref sp, stack));
                            MemoryOps.CheckArrayBounds(array, index);
                            PushSlot(ref sp, stack, StackSlot.OfInt32(array.Elements[index].AsInt32));
                            frame.Ip = ip + 1;
                            break;
                        }
                        case ILOp.Stelem_I4: {
                            var value = PopSlot(ref sp, stack);
                            var index = PopSlot(ref sp, stack).AsInt32;
                            var array = MemoryOps.GetArray(PopSlot(ref sp, stack));
                            MemoryOps.CheckArrayBounds(array, index);
                            array.Elements[index] = StackSlot.OfInt32(value.AsInt32);
                            frame.Ip = ip + 1;
                            break;
                        }
                        case ILOp.Conv_I4:
                            stack[sp - 1] = StackSlot.OfInt32(stack[sp - 1].AsInt32); frame.Ip = ip + 1; break;
                        case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or
                            ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un: {
                            var right = PopSlot(ref sp, stack).AsInt32;
                            var left = PopSlot(ref sp, stack).AsInt32;
                            PushSlot(ref sp, stack, StackSlot.OfInt32(Apply(current.Op, left, right)));
                            frame.Ip = ip + 1; break;
                        }
                        case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un: {
                            var right = PopSlot(ref sp, stack).AsInt32;
                            var left = PopSlot(ref sp, stack).AsInt32;
                            PushSlot(ref sp, stack, StackSlot.OfInt32(Compare(current.Op, left, right) ? 1 : 0));
                            frame.Ip = ip + 1; break;
                        }
                        case ILOp.Br or ILOp.Br_S:
                            frame.Ip = Target(current, frame); break;
                        case ILOp.BrTrue or ILOp.BrTrue_S:
                            frame.Ip = PopSlot(ref sp, stack).AsInt32 != 0 ? Target(current, frame) : ip + 1; break;
                        case ILOp.BrFalse or ILOp.BrFalse_S:
                            frame.Ip = PopSlot(ref sp, stack).AsInt32 == 0 ? Target(current, frame) : ip + 1; break;
                        case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or
                            ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or ILOp.Bge_Un_S or
                            ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
                            ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or
                            ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S: {
                            var right = PopSlot(ref sp, stack).AsInt32;
                            var left = PopSlot(ref sp, stack).AsInt32;
                            frame.Ip = CompareBranch(current.Op, left, right) ? Target(current, frame) : ip + 1;
                            break;
                        }
                        case ILOp.Ret:
                            return PopSlot(ref sp, stack);
                        default:
                            throw new InvalidOperationException($"未対応の配列スカラー JIT 命令です: {current.Op}");
                    }
                    if (IsControlFlow(current)) break;
                }
            }
        } finally {
            instruction.Dispose();
            batch.Dispose();
            budget.Dispose();
            frame.Stack.Clear();
        }
    }

    private static bool IsArraySupported(ILOp op) => IsSupported(new DecodedInstruction(
        0, op, default, 1, 0, 0, 0, null)) || op is ILOp.Newarr or ILOp.Ldlen or
        ILOp.Ldelem_I4 or ILOp.Stelem_I4 or ILOp.Conv_I4;

    private static void PushSlot(ref int sp, StackSlot[] stack, in StackSlot value) => stack[sp++] = value;
    private static StackSlot PopSlot(ref int sp, StackSlot[] stack) {
        var index = --sp;
        var value = stack[index];
        stack[index] = default;
        return value;
    }

    private static bool IsScalarMethodEligible(VmMethod method, PreparedMethod prepared,
        DecodedInstruction[] code, out bool hasCalls) {
        hasCalls = false;
        if (method.Signature.HasThis || prepared.Clauses is { Length: > 0 } ||
            !IsInt32Like(method.Signature.ReturnType) ||
            method.Signature.ParamTypes.Any(type => !IsInt32Like(type)) ||
            prepared.LocalTypes.Any(type => !IsInt32Like(type)) || code.Length == 0 ||
            prepared.LocalTypes.Length == 0 && !code.Any(IsControlFlow) ||
            code.Any(instruction => instruction.Fusion.Kind != IlFusionKind.None &&
                !IlFusionValidation.IsValid(instruction.Fusion, prepared.LocalTypes.Length, out _)))
            return false;
        return code.Length <= Interpreter.SafepointInterval;
    }

    private static StackSlot Execute(Interpreter interpreter, InterpreterFrame frame,
        DecodedInstruction[] code, bool returnsValue, int[] blockCosts, int[] blockEnds) {
        var stack = frame.Stack.RootSlots;
        var sp = 0;
        var ip = 0;
        var batchInstructions = 0;
        var state = interpreter.CurrentExecutionState;
        var budget = new ScalarInstructionBudget(interpreter, state);
        var batch = default(VmExecutionCoordinator.InstructionBatchLease);
        var instruction = default(VmExecutionCoordinator.InstructionLease);

        try {
            interpreter.CheckJitSafepoint();
            batch = interpreter.EnterJitInstructionBatch();
            instruction = interpreter.EnterJitInstructionInBatch();
            while (true) {
                if (batchInstructions >= Interpreter.SafepointInterval) {
                    instruction.Dispose();
                    instruction = default;
                    batch.Dispose();
                    batch = default;
                    interpreter.CheckJitSafepoint();
                    batch = interpreter.EnterJitInstructionBatch();
                    instruction = interpreter.EnterJitInstructionInBatch();
                    batchInstructions = 0;
                }

                var blockEnd = blockEnds[ip];
                budget.Take(blockCosts[ip]);
                while (ip < blockEnd) {
                    var current = code[ip];
                    frame.Ip = ip;
                    batchInstructions++;

                    if (current.Fusion.Kind != IlFusionKind.None) {
                        ExecuteFusion(frame.Locals, current.Fusion);
                        ip++;
                        continue;
                    }

                    switch (current.Op) {
                    case ILOp.Nop or ILOp.Break:
                        ip++;
                        break;
                    case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                        Push(ref sp, stack, frame.ArgumentAt((int)(current.Op - ILOp.Ldarg_0)).AsInt32);
                        ip++;
                        break;
                    case ILOp.Ldarg_S or ILOp.Ldarg:
                        Push(ref sp, stack, frame.ArgumentAt(current.IntOperand).AsInt32);
                        ip++;
                        break;
                    case ILOp.Starg_S or ILOp.Starg:
                        frame.SetArgument(current.IntOperand, StackSlot.OfInt32(Pop(ref sp, stack)));
                        ip++;
                        break;
                    case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                        Push(ref sp, stack, frame.Locals[(int)(current.Op - ILOp.Ldloc_0)].AsInt32);
                        ip++;
                        break;
                    case ILOp.Ldloc_S or ILOp.Ldloc:
                        Push(ref sp, stack, frame.Locals[current.IntOperand].AsInt32);
                        ip++;
                        break;
                    case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                        frame.Locals[(int)(current.Op - ILOp.Stloc_0)] = StackSlot.OfInt32(Pop(ref sp, stack));
                        ip++;
                        break;
                    case ILOp.Stloc_S or ILOp.Stloc:
                        frame.Locals[current.IntOperand] = StackSlot.OfInt32(Pop(ref sp, stack));
                        ip++;
                        break;
                    case ILOp.Ldc_I4_M1:
                        Push(ref sp, stack, -1); ip++; break;
                    case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                        Push(ref sp, stack, (int)(current.Op - ILOp.Ldc_I4_0)); ip++; break;
                    case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                        Push(ref sp, stack, current.IntOperand); ip++; break;
                    case ILOp.Dup:
                        Push(ref sp, stack, stack[sp - 1].AsInt32); ip++; break;
                    case ILOp.Pop:
                        _ = Pop(ref sp, stack); ip++; break;
                    case ILOp.Neg:
                        Push(ref sp, stack, unchecked(-Pop(ref sp, stack))); ip++; break;
                    case ILOp.Not:
                        Push(ref sp, stack, ~Pop(ref sp, stack)); ip++; break;
                    case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or
                        ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un:
                        var right = Pop(ref sp, stack);
                        var left = Pop(ref sp, stack);
                        Push(ref sp, stack, Apply(current.Op, left, right));
                        ip++;
                        break;
                    case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                        var compareRight = Pop(ref sp, stack);
                        var compareLeft = Pop(ref sp, stack);
                        Push(ref sp, stack, Compare(current.Op, compareLeft, compareRight) ? 1 : 0);
                        ip++;
                        break;
                    case ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_U1 or
                        ILOp.Conv_U2 or ILOp.Conv_U4:
                        Push(ref sp, stack, Convert(current.Op, Pop(ref sp, stack)));
                        ip++;
                        break;
                    case ILOp.Br or ILOp.Br_S:
                        ip = Target(current, frame);
                        break;
                    case ILOp.BrTrue or ILOp.BrTrue_S:
                        ip = Pop(ref sp, stack) != 0 ? Target(current, frame) : ip + 1;
                        break;
                    case ILOp.BrFalse or ILOp.BrFalse_S:
                        ip = Pop(ref sp, stack) == 0 ? Target(current, frame) : ip + 1;
                        break;
                    case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or
                        ILOp.Bge or ILOp.Bge_S or ILOp.Bge_Un or ILOp.Bge_Un_S or
                        ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
                        ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or
                        ILOp.Blt or ILOp.Blt_S or ILOp.Blt_Un or ILOp.Blt_Un_S:
                        var branchRight = Pop(ref sp, stack);
                        var branchLeft = Pop(ref sp, stack);
                        ip = CompareBranch(current.Op, branchLeft, branchRight)
                            ? Target(current, frame) : ip + 1;
                        break;
                    case ILOp.Ret:
                        return returnsValue ? StackSlot.OfInt32(Pop(ref sp, stack)) : default;
                    default:
                        throw new InvalidOperationException($"未対応のスカラー JIT 命令です: {current.Op}");
                    }
                    if (IsControlFlow(current))
                        break;
                }
            }
        } finally {
            instruction.Dispose();
            batch.Dispose();
            budget.Dispose();
            frame.Stack.Clear();
        }
    }

    private ref struct ScalarInstructionBudget(Interpreter interpreter, Interpreter.ExecutionState state) {
        private const int ReservationSize = 4096;
        private int _remaining;

        internal void Take(int cost) {
            if (cost < 1)
                throw new ArgumentOutOfRangeException(nameof(cost));
            if (!interpreter.InstructionChargingEnabled)
                return;
            if (_remaining < cost) {
                try {
                    _remaining += interpreter.ReserveJitInstructionChunk(Math.Max(ReservationSize, cost));
                } catch (InstructionQuotaExceededException) {
                    var refund = _remaining;
                    _remaining = 0;
                    interpreter.RefundJitInstructionChunk(refund);
                    interpreter.ConsumeJitInstructionForState(state, cost);
                    throw;
                }
            }
            if (_remaining < cost) {
                var refund = _remaining;
                _remaining = 0;
                interpreter.RefundJitInstructionChunk(refund);
                interpreter.ConsumeJitInstructionForState(state, cost);
                throw new InstructionQuotaExceededException("スカラー JIT の命令クォータが不足しています。");
            }
            _remaining -= cost;
            state.InstructionCount += cost;
        }

        internal void Dispose() => interpreter.RefundJitInstructionChunk(_remaining);
    }

    private static void ExecuteFusion(StackSlot[] locals, IlFusion fusion) {
        if (!IlFusionValidation.IsValid(fusion, locals.Length, out var reason))
            throw new InvalidOperationException($"不正な IL 融合命令です: {reason}");
        var left = locals[fusion.LocalA].AsInt32;
        var result = fusion.Kind switch {
            IlFusionKind.LocalConstantOperationStore =>
                ApplyScalarOperation(fusion.OperationA, left, fusion.ValueA),
            IlFusionKind.LocalLocalOperationStore =>
                ApplyScalarOperation(fusion.OperationA, left, locals[fusion.LocalB].AsInt32),
            IlFusionKind.LocalTwoConstantsOperationStore =>
                ApplyScalarOperation(fusion.OperationB, ApplyScalarOperation(fusion.OperationA, left, fusion.ValueA), fusion.ValueB),
            IlFusionKind.LocalLocalConstantOperationStore =>
                ApplyScalarOperation(fusion.OperationB, left,
                    ApplyScalarOperation(fusion.OperationA, locals[fusion.LocalB].AsInt32, fusion.ValueA)),
            _ => throw new InvalidOperationException($"未知の IL 融合種別です: {fusion.Kind}"),
        };
        locals[fusion.LocalC] = StackSlot.OfInt32(result);
    }

    private static int Apply(ILOp op, int left, int right) => op switch {
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
        _ => throw new InvalidOperationException($"スカラー JIT に適用できない演算です: {op}"),
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

    private static int Convert(ILOp op, int value) => op switch {
        ILOp.Conv_I1 => (sbyte)value,
        ILOp.Conv_I2 => (short)value,
        ILOp.Conv_I4 => value,
        ILOp.Conv_U1 => (byte)value,
        ILOp.Conv_U2 => (ushort)value,
        ILOp.Conv_U4 => value,
        _ => throw new InvalidOperationException($"スカラー JIT に適用できない変換です: {op}"),
    };

    private static bool Compare(ILOp op, int left, int right) => op switch {
        ILOp.Ceq => left == right,
        ILOp.Cgt => left > right,
        ILOp.Cgt_Un => (uint)left > (uint)right,
        ILOp.Clt => left < right,
        ILOp.Clt_Un => (uint)left < (uint)right,
        _ => throw new InvalidOperationException($"スカラー JIT に適用できない比較です: {op}"),
    };

    private static bool CompareBranch(ILOp op, int left, int right) => op switch {
        ILOp.Beq or ILOp.Beq_S => left == right,
        ILOp.Bne_Un or ILOp.Bne_Un_S => left != right,
        ILOp.Bge or ILOp.Bge_S => left >= right,
        ILOp.Bge_Un or ILOp.Bge_Un_S => (uint)left >= (uint)right,
        ILOp.Bgt or ILOp.Bgt_S => left > right,
        ILOp.Bgt_Un or ILOp.Bgt_Un_S => (uint)left > (uint)right,
        ILOp.Ble or ILOp.Ble_S => left <= right,
        ILOp.Ble_Un or ILOp.Ble_Un_S => (uint)left <= (uint)right,
        ILOp.Blt or ILOp.Blt_S => left < right,
        ILOp.Blt_Un or ILOp.Blt_Un_S => (uint)left < (uint)right,
        _ => throw new InvalidOperationException($"スカラー JIT に適用できない分岐です: {op}"),
    };

    private static int Target(DecodedInstruction instruction, InterpreterFrame frame) =>
        instruction.BranchTargetIndex >= 0
            ? instruction.BranchTargetIndex : frame.OffsetMap[instruction.IntOperand];

    private static (int[] Costs, int[] Ends) BuildBlocks(DecodedInstruction[] code,
        IReadOnlyDictionary<int, int> offsets) {
        var starts = new bool[code.Length + 1];
        starts[0] = true;
        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            if (IsControlFlow(instruction) || instruction.Op == ILOp.Ret)
                starts[i + 1] = true;
            if (IsBranch(instruction)) {
                var target = instruction.BranchTargetIndex >= 0
                    ? instruction.BranchTargetIndex : offsets[instruction.IntOperand];
                starts[target] = true;
            }
        }

        var costs = new int[code.Length];
        var ends = new int[code.Length];
        for (var start = 0; start < code.Length;) {
            var end = start + 1;
            while (end < code.Length && !starts[end])
                end++;
            var cost = 0;
            for (var i = start; i < end; i++)
                cost = checked(cost + code[i].InstructionCost);
            for (var i = start; i < end; i++) {
                costs[i] = cost;
                ends[i] = end;
            }
            start = end;
        }
        return (costs, ends);
    }

    private static bool IsBranch(DecodedInstruction instruction) => IsControlFlow(instruction);

    private static void Push(ref int sp, StackSlot[] stack, int value) {
        stack[sp++] = StackSlot.OfInt32(value);
    }

    private static int Pop(ref int sp, StackSlot[] stack) {
        var index = --sp;
        var value = stack[index].AsInt32;
        stack[index] = default;
        return value;
    }

    private static bool IsSupported(DecodedInstruction instruction) {
        if (instruction.Fusion.Kind != IlFusionKind.None)
            return true;

        return instruction.Op is ILOp.Nop or ILOp.Break or ILOp.Ret or
            ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or ILOp.Ldarg_S or ILOp.Ldarg or
            ILOp.Starg_S or ILOp.Starg or ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or
            ILOp.Ldloc_S or ILOp.Ldloc or ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3 or
            ILOp.Stloc_S or ILOp.Stloc or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or
            ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8 or
            ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Dup or ILOp.Pop or ILOp.Neg or ILOp.Not or
            ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor or
            ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un or
            ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_U1 or ILOp.Conv_U2 or ILOp.Conv_U4 or
            ILOp.Call or
            ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S or
            ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
            ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
            ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
            ILOp.Blt_Un or ILOp.Blt_Un_S;
    }

    private static bool IsScalarOperation(ILOp op) => op is
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or
        ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
        ILOp.Clt or ILOp.Clt_Un;

    private static int ApplyScalarOperation(ILOp op, int left, int right) =>
        op is ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un
            ? Compare(op, left, right) ? 1 : 0
            : Apply(op, left, right);

    private static bool IsControlFlow(DecodedInstruction instruction) => instruction.Op is
        ILOp.Br or ILOp.Br_S or ILOp.BrTrue or ILOp.BrTrue_S or ILOp.BrFalse or ILOp.BrFalse_S or
        ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
        ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
        ILOp.Ble or ILOp.Ble_S or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt or ILOp.Blt_S or
        ILOp.Blt_Un or ILOp.Blt_Un_S;

    private static bool IsInt32Like(SigType type) => type.Kind is
        SigKind.Void or SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1 or
        SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4;
}
