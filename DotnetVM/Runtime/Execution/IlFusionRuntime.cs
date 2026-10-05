using DotnetVM.IL;

namespace DotnetVM.Runtime.Execution;

internal static class IlFusionRuntime {
    internal static void Execute(InterpreterFrame frame, IlFusion fusion) {
        var left = SlotOps.PushCopyOfValue(frame.Locals[fusion.LocalA]);
        StackSlot result;
        switch (fusion.Kind) {
            case IlFusionKind.LocalConstantOperationStore:
                result = Apply(fusion.OperationA, left, StackSlot.OfInt32(fusion.ValueA));
                break;
            case IlFusionKind.LocalLocalOperationStore:
                result = Apply(fusion.OperationA, left,
                    SlotOps.PushCopyOfValue(frame.Locals[fusion.LocalB]));
                break;
            case IlFusionKind.LocalTwoConstantsOperationStore:
                result = Apply(fusion.OperationA, left, StackSlot.OfInt32(fusion.ValueA));
                result = Apply(fusion.OperationB, result, StackSlot.OfInt32(fusion.ValueB));
                break;
            case IlFusionKind.LocalLocalConstantOperationStore: {
                var middle = Apply(fusion.OperationA,
                    SlotOps.PushCopyOfValue(frame.Locals[fusion.LocalB]),
                    StackSlot.OfInt32(fusion.ValueA));
                result = Apply(fusion.OperationB, left, middle);
                break;
            }
            default:
                throw new InvalidOperationException($"未知の IL 融合種別です: {fusion.Kind}");
        }
        frame.Locals[fusion.LocalC] = SlotOps.StoreCopyOfValue(result);
    }

    internal static StackSlot Apply(ILOp op, StackSlot left, StackSlot right) {
        if (op is ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un)
            return StackSlot.OfInt32(SlotOps.Compare(op, left, right) ? 1 : 0);
        return MemoryOps.TryPointerArithmetic(op, left, right)
            ?? SlotOps.BinaryArithmetic(op, left, right);
    }
}
