namespace DotnetVM.IL;

/// <summary>準備済み IL で複数命令を1回のローカル更新へ融合する種別。</summary>
public enum IlFusionKind : byte {
    None,
    LocalConstantOperationStore,
    LocalLocalOperationStore,
    LocalTwoConstantsOperationStore,
    LocalLocalConstantOperationStore,
}

/// <summary>ローカル更新融合命令の不変オペランド。</summary>
public readonly record struct IlFusion(
    IlFusionKind Kind,
    int LocalA,
    int LocalB,
    int LocalC,
    int ValueA,
    int ValueB,
    ILOp OperationA,
    ILOp OperationB);

internal static class IlFusionValidation {
    internal static bool IsValid(IlFusion fusion, int localCount, out string reason) {
        if (localCount < 0) {
            reason = "ローカル数が負です。";
            return false;
        }

        if (!IsLocalInRange(fusion.LocalA, localCount) ||
            !IsLocalInRange(fusion.LocalC, localCount)) {
            reason = $"融合命令のローカル番号が範囲外です (A={fusion.LocalA}, C={fusion.LocalC}, ローカル数={localCount})。";
            return false;
        }

        switch (fusion.Kind) {
            case IlFusionKind.LocalConstantOperationStore:
                if (!IsFusableOperation(fusion.OperationA)) {
                    reason = $"融合命令の演算 A が不正です: {fusion.OperationA}";
                    return false;
                }
                break;
            case IlFusionKind.LocalLocalOperationStore:
                if (!IsLocalInRange(fusion.LocalB, localCount) ||
                    !IsFusableOperation(fusion.OperationA)) {
                    reason = "融合命令のローカル B または演算 A が不正です。";
                    return false;
                }
                break;
            case IlFusionKind.LocalTwoConstantsOperationStore:
                if (!IsFusableOperation(fusion.OperationA) ||
                    !IsFusableOperation(fusion.OperationB)) {
                    reason = "融合命令の演算 A/B が不正です。";
                    return false;
                }
                break;
            case IlFusionKind.LocalLocalConstantOperationStore:
                if (!IsLocalInRange(fusion.LocalB, localCount) ||
                    !IsFusableOperation(fusion.OperationA) ||
                    !IsFusableOperation(fusion.OperationB)) {
                    reason = "融合命令のローカル B または演算 A/B が不正です。";
                    return false;
                }
                break;
            default:
                reason = $"未知の融合種別です: {fusion.Kind}";
                return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsLocalInRange(int index, int localCount) =>
        (uint)index < (uint)localCount;

    private static bool IsFusableOperation(ILOp op) => op is
        ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un or
        ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or
        ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un or
        ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un or ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or
        ILOp.Clt or ILOp.Clt_Un;
}
