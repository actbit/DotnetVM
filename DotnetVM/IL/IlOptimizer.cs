using DotnetVM.Metadata;

namespace DotnetVM.IL;

/// <summary>
/// デコード済み IL に対する、意味を変えない準備段階の最適化。
/// 元の IL オフセットを保持し、分岐先や EH 境界に入れる命令は削除しない。
/// </summary>
internal static class IlOptimizer {
    internal static DecodedInstruction[] Optimize(
        DecodedInstruction[] code, ExceptionClause[]? clauses) {
        if (code.Length < 2)
            return code;

        var branchTargets = CollectBranchTargets(code);
        var boundaries = CollectExceptionBoundaries(clauses);
        var optimized = code.ToList();

        // EH がないメソッドだけで branch threading を行う。分岐命令を
        // 飛び越すこと自体は観測できないが、EH 境界を跨ぐと finally の
        // 選択が変わり得るため、EH 付きメソッドは保守的に扱う。
        if (clauses is null or { Length: 0 })
            ThreadUnconditionalBranches(optimized);

        var changed = true;
        while (changed) {
            changed = false;
            for (var i = 0; i < optimized.Count; i++) {
                if (TryFuseLocalOperation(optimized, i, branchTargets, boundaries) ||
                    TryFoldIntegerTriple(optimized, i, branchTargets, boundaries) ||
                    TryRemovePurePushPop(optimized, i, branchTargets, boundaries) ||
                    TryRemoveNop(optimized, i, branchTargets, boundaries)) {
                    changed = true;
                    break;
                }
            }
        }

        return optimized.Count == code.Length && optimized.SequenceEqual(code)
            ? code
            : optimized.ToArray();
    }

    private static HashSet<int> CollectBranchTargets(DecodedInstruction[] code) {
        var targets = new HashSet<int>();
        foreach (var instruction in code) {
            if (instruction.SwitchTargets is { } switchTargets) {
                foreach (var target in switchTargets)
                    targets.Add(target);
            } else if (instruction.OperandKind is IlOperandKind.ShortBrTarget or IlOperandKind.BrTarget) {
                targets.Add(instruction.IntOperand);
            }
        }
        return targets;
    }

    private static HashSet<int> CollectExceptionBoundaries(ExceptionClause[]? clauses) {
        var boundaries = new HashSet<int>();
        if (clauses is null)
            return boundaries;

        foreach (var clause in clauses) {
            boundaries.Add(clause.TryOffset);
            AddBoundary(boundaries, clause.TryOffset, clause.TryLength);
            boundaries.Add(clause.HandlerOffset);
            AddBoundary(boundaries, clause.HandlerOffset, clause.HandlerLength);
            if (clause.Kind == ExceptionClauseKind.Filter)
                boundaries.Add(clause.ClassTokenOrFilterOffset);
        }
        return boundaries;

        static void AddBoundary(HashSet<int> set, int offset, int length) {
            var end = (long)offset + length;
            if (end is >= int.MinValue and <= int.MaxValue)
                set.Add((int)end);
        }
    }

    private static void ThreadUnconditionalBranches(List<DecodedInstruction> code) {
        for (var i = 0; i < code.Count; i++) {
            if (!IsUnconditionalBranch(code[i].Op))
                continue;

            var target = code[i].IntOperand;
            var visited = new HashSet<int>();
            while (visited.Add(target)) {
                var targetIndex = IndexOfOffset(code, target);
                if (targetIndex < 0 || !IsUnconditionalBranch(code[targetIndex].Op))
                    break;
                var next = code[targetIndex].IntOperand;
                if (next == target)
                    break;
                target = next;
            }

            if (target != code[i].IntOperand)
                code[i] = code[i] with { IntOperand = target };
        }
    }

    private static bool TryFoldIntegerTriple(
        List<DecodedInstruction> code, int index,
        HashSet<int> branchTargets, HashSet<int> boundaries) {
        if (index + 2 >= code.Count ||
            !TryGetInt32Constant(code[index], out var left) ||
            !TryGetInt32Constant(code[index + 1], out var right) ||
            !TryEvaluate(code[index + 2].Op, left, right, out var result) ||
            !CanRemove(code, index + 1, index + 2, branchTargets, boundaries))
            return false;

        var first = code[index];
        var folded = new DecodedInstruction(
            first.Offset,
            SelectIntConstantOp(result),
            ConstantOperandKind(result),
            checked(first.Size + code[index + 1].Size + code[index + 2].Size),
            result, 0, 0, null) {
            InstructionCost = checked(first.InstructionCost + code[index + 1].InstructionCost +
                code[index + 2].InstructionCost),
        };
        code[index] = folded;
        code.RemoveRange(index + 1, 2);
        return true;
    }

    private static bool TryFuseLocalOperation(
        List<DecodedInstruction> code, int index,
        HashSet<int> branchTargets, HashSet<int> boundaries) {
        if (index + 5 < code.Count &&
            TryGetLocalLoad(code[index], out var localLocalSourceA) &&
            TryGetLocalLoad(code[index + 1], out var localLocalSourceB) &&
            TryGetInt32Constant(code[index + 2], out var localLocalConstant) &&
            IsFusableOperation(code[index + 3].Op) &&
            IsFusableOperation(code[index + 4].Op) &&
            TryGetLocalStore(code[index + 5], out var localLocalDestination) &&
            CanRemove(code, index + 1, index + 5, branchTargets, boundaries)) {
            ReplaceWithFusion(code, index, 6, new IlFusion(
                IlFusionKind.LocalLocalConstantOperationStore,
                localLocalSourceA, localLocalSourceB, localLocalDestination,
                localLocalConstant, 0, code[index + 3].Op, code[index + 4].Op));
            return true;
        }

        if (index + 5 < code.Count &&
            TryGetLocalLoad(code[index], out var localA) &&
            TryGetInt32Constant(code[index + 1], out var valueA) &&
            IsFusableOperation(code[index + 2].Op) &&
            TryGetInt32Constant(code[index + 3], out var valueB) &&
            IsFusableOperation(code[index + 4].Op) &&
            TryGetLocalStore(code[index + 5], out var localC) &&
            CanRemove(code, index + 1, index + 5, branchTargets, boundaries)) {
            ReplaceWithFusion(code, index, 6, new IlFusion(
                IlFusionKind.LocalTwoConstantsOperationStore,
                localA, 0, localC, valueA, valueB,
                code[index + 2].Op, code[index + 4].Op));
            return true;
        }

        if (index + 3 >= code.Count ||
            !TryGetLocalLoad(code[index], out var source) ||
            !TryGetLocalStore(code[index + 3], out var destination) ||
            !CanRemove(code, index + 1, index + 3, branchTargets, boundaries))
            return false;

        if (TryGetInt32Constant(code[index + 1], out var constant) &&
            IsFusableOperation(code[index + 2].Op)) {
            ReplaceWithFusion(code, index, 4, new IlFusion(
                IlFusionKind.LocalConstantOperationStore,
                source, 0, destination, constant, 0, code[index + 2].Op, default));
            return true;
        }

        if (TryGetLocalLoad(code[index + 1], out var secondSource) &&
            IsFusableOperation(code[index + 2].Op)) {
            ReplaceWithFusion(code, index, 4, new IlFusion(
                IlFusionKind.LocalLocalOperationStore,
                source, secondSource, destination, 0, 0, code[index + 2].Op, default));
            return true;
        }

        return false;
    }

    private static void ReplaceWithFusion(
        List<DecodedInstruction> code, int index, int count, IlFusion fusion) {
        var first = code[index];
        var cost = 0;
        var size = 0;
        for (var i = index; i < index + count; i++) {
            cost = checked(cost + code[i].InstructionCost);
            size = checked(size + code[i].Size);
        }
        code[index] = new DecodedInstruction(
            first.Offset, ILOp.Nop, IlOperandKind.None, size, 0, 0, 0, null) {
            InstructionCost = cost,
            Fusion = fusion,
        };
        code.RemoveRange(index + 1, count - 1);
    }

    private static bool TryRemovePurePushPop(
        List<DecodedInstruction> code, int index,
        HashSet<int> branchTargets, HashSet<int> boundaries) {
        if (index + 1 >= code.Count || code[index + 1].Op != ILOp.Pop ||
            !IsPurePush(code[index].Op) ||
            !CanRemove(code, index + 1, index + 1, branchTargets, boundaries))
            return false;

        var first = code[index];
        code[index] = new DecodedInstruction(
            first.Offset, ILOp.Nop, IlOperandKind.None,
            checked(first.Size + code[index + 1].Size), 0, 0, 0, null) {
            InstructionCost = checked(first.InstructionCost + code[index + 1].InstructionCost),
        };
        code.RemoveAt(index + 1);
        return true;
    }

    private static bool TryRemoveNop(
        List<DecodedInstruction> code, int index,
        HashSet<int> branchTargets, HashSet<int> boundaries) {
        if (code[index].Op != ILOp.Nop || code[index].Fusion.Kind != IlFusionKind.None ||
            index + 1 >= code.Count ||
            code[index].Offset == 0 ||
            branchTargets.Contains(code[index].Offset) || boundaries.Contains(code[index].Offset))
            return false;

        var nop = code[index];
        var next = code[index + 1];
        code[index + 1] = next with {
            InstructionCost = checked(next.InstructionCost + nop.InstructionCost),
        };
        code.RemoveAt(index);
        return true;
    }

    private static bool CanRemove(
        List<DecodedInstruction> code, int first, int last,
        HashSet<int> branchTargets, HashSet<int> boundaries) {
        for (var i = first; i <= last; i++) {
            var offset = code[i].Offset;
            if (branchTargets.Contains(offset) || boundaries.Contains(offset))
                return false;
        }
        return true;
    }

    private static int IndexOfOffset(List<DecodedInstruction> code, int offset) {
        for (var i = 0; i < code.Count; i++)
            if (code[i].Offset == offset)
                return i;
        return -1;
    }

    private static bool IsUnconditionalBranch(ILOp op) => op is ILOp.Br or ILOp.Br_S;

    private static bool IsPurePush(ILOp op) => op is
        ILOp.Ldnull or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or
        ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or
        ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4 or ILOp.Ldc_I8 or ILOp.Ldc_R4 or
        ILOp.Ldc_R8 or ILOp.Dup;

    private static bool IsFusableOperation(ILOp op) => op is
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un or
        ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or
        ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un or
        ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
        ILOp.Clt or ILOp.Clt_Un;

    private static bool TryGetLocalLoad(DecodedInstruction instruction, out int index) {
        switch (instruction.Op) {
            case ILOp.Ldloc_0: index = 0; return true;
            case ILOp.Ldloc_1: index = 1; return true;
            case ILOp.Ldloc_2: index = 2; return true;
            case ILOp.Ldloc_3: index = 3; return true;
            case ILOp.Ldloc_S or ILOp.Ldloc: index = instruction.IntOperand; return true;
            default: index = 0; return false;
        }
    }

    private static bool TryGetLocalStore(DecodedInstruction instruction, out int index) {
        switch (instruction.Op) {
            case ILOp.Stloc_0: index = 0; return true;
            case ILOp.Stloc_1: index = 1; return true;
            case ILOp.Stloc_2: index = 2; return true;
            case ILOp.Stloc_3: index = 3; return true;
            case ILOp.Stloc_S or ILOp.Stloc: index = instruction.IntOperand; return true;
            default: index = 0; return false;
        }
    }

    private static bool TryGetInt32Constant(DecodedInstruction instruction, out int value) {
        switch (instruction.Op) {
            case ILOp.Ldc_I4_M1: value = -1; return true;
            case >= ILOp.Ldc_I4_0 and <= ILOp.Ldc_I4_8:
                value = (int)(instruction.Op - ILOp.Ldc_I4_0); return true;
            case ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                value = instruction.IntOperand; return true;
            default:
                value = 0; return false;
        }
    }

    private static bool TryEvaluate(ILOp op, int left, int right, out int result) {
        result = op switch {
            ILOp.Add => unchecked(left + right),
            ILOp.Sub => unchecked(left - right),
            ILOp.Mul => unchecked(left * right),
            ILOp.And => left & right,
            ILOp.Or => left | right,
            ILOp.Xor => left ^ right,
            ILOp.Shl => left << (right & 31),
            ILOp.Shr => left >> (right & 31),
            ILOp.Shr_Un => (int)((uint)left >> (right & 31)),
            ILOp.Ceq => left == right ? 1 : 0,
            ILOp.Cgt => left > right ? 1 : 0,
            ILOp.Cgt_Un => (uint)left > (uint)right ? 1 : 0,
            ILOp.Clt => left < right ? 1 : 0,
            ILOp.Clt_Un => (uint)left < (uint)right ? 1 : 0,
            _ => 0,
        };
        return op is ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.And or ILOp.Or or ILOp.Xor or
            ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
            ILOp.Clt or ILOp.Clt_Un;
    }

    private static ILOp SelectIntConstantOp(int value) => value switch {
        -1 => ILOp.Ldc_I4_M1,
        >= 0 and <= 8 => (ILOp)((ushort)ILOp.Ldc_I4_0 + (ushort)value),
        _ => ILOp.Ldc_I4,
    };

    private static IlOperandKind ConstantOperandKind(int value) => value is >= -1 and <= 8
        ? IlOperandKind.None : IlOperandKind.I4;
}
