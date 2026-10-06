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
