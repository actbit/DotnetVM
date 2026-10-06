using DotnetVM.IL;
using Xunit;

namespace DotnetVM.Tests;

public sealed class IlOptimizationTests {
    [Fact]
    public void FoldsIntegerConstants_AndPreservesQuotaCost() {
        var code = new[] {
            Instruction(0, ILOp.Ldc_I4_2),
            Instruction(1, ILOp.Ldc_I4_3),
            Instruction(2, ILOp.Add),
            Instruction(3, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal([(ILOp)((ushort)ILOp.Ldc_I4_0 + 5), ILOp.Ret], optimized.Select(i => i.Op));
        Assert.Equal(3, optimized[0].InstructionCost);
        Assert.Equal(1, optimized[1].InstructionCost);
        Assert.Equal(3, optimized[0].Size);
    }

    [Fact]
    public void DoesNotRemoveAnInstructionUsedAsBranchTarget() {
        var code = new[] {
            Instruction(0, ILOp.Ldc_I4_2),
            Instruction(1, ILOp.Ldc_I4_3),
            Instruction(2, ILOp.Add),
            new DecodedInstruction(3, ILOp.Br_S, IlOperandKind.ShortBrTarget, 2, 1, 0, 0, null),
            Instruction(4, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal(code.Length, optimized.Length);
        Assert.Equal(ILOp.Ldc_I4_3, optimized[1].Op);
    }

    [Fact]
    public void ThreadsUnconditionalBranches() {
        var code = new[] {
            new DecodedInstruction(0, ILOp.Br_S, IlOperandKind.ShortBrTarget, 2, 2, 0, 0, null),
            new DecodedInstruction(2, ILOp.Br_S, IlOperandKind.ShortBrTarget, 2, 4, 0, 0, null),
            Instruction(4, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal(4, optimized[0].IntOperand);
    }

    [Fact]
    public void RemovesNopWithoutRemovingMethodEntryBoundary() {
        var code = new[] {
            Instruction(0, ILOp.Ldarg_0),
            Instruction(1, ILOp.Nop),
            Instruction(2, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal([ILOp.Ldarg_0, ILOp.Ret], optimized.Select(i => i.Op));
        Assert.Equal(2, optimized[1].InstructionCost);
    }

    [Fact]
    public void FusesLocalOperationAndStore() {
        var code = new[] {
            Instruction(0, ILOp.Ldloc_0),
            Instruction(1, ILOp.Ldc_I4_1),
            Instruction(2, ILOp.Add),
            Instruction(3, ILOp.Stloc_0),
            Instruction(4, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal(2, optimized.Length);
        Assert.Equal(IlFusionKind.LocalConstantOperationStore, optimized[0].Fusion.Kind);
        Assert.Equal(4, optimized[0].InstructionCost);
    }

    [Fact]
    public void FusesLocalLocalConstantOperationAndStore() {
        var code = new[] {
            Instruction(0, ILOp.Ldloc_0),
            Instruction(1, ILOp.Ldloc_1),
            Instruction(2, ILOp.Ldc_I4_1),
            Instruction(3, ILOp.Add),
            Instruction(4, ILOp.Xor),
            Instruction(5, ILOp.Stloc_0),
            Instruction(6, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal(2, optimized.Length);
        Assert.Equal(IlFusionKind.LocalLocalConstantOperationStore, optimized[0].Fusion.Kind);
        Assert.Equal(6, optimized[0].InstructionCost);
    }

    [Fact]
    public void DoesNotEraseDupPopThatMayBeInvalidAtTheOriginalStackHeight() {
        var code = new[] {
            Instruction(0, ILOp.Dup),
            Instruction(1, ILOp.Pop),
            Instruction(2, ILOp.Ret),
        };

        var optimized = IlOptimizer.Optimize(code, null);

        Assert.Equal([ILOp.Dup, ILOp.Pop, ILOp.Ret], optimized.Select(i => i.Op));
    }

    [Fact]
    public void RejectsFusionOperandsOutsideThePreparedLocalArray() {
        var fusion = new IlFusion(
            IlFusionKind.LocalConstantOperationStore, 0, 0, 1, 1, 0, ILOp.Add, default);

        Assert.False(IlFusionValidation.IsValid(fusion, localCount: 1, out var reason));
        Assert.Contains("範囲外", reason);
    }

    private static DecodedInstruction Instruction(int offset, ILOp op) =>
        new(offset, op, IlOperandKind.None, 1, 0, 0, 0, null);
}
