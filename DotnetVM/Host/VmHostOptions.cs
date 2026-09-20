namespace DotnetVM.Host;

/// <summary>
/// メモリ/実行リソースのポリシー。すべてクォータ+拒否方式の上限。
/// 超過は ResourceExhaustedException 系で拒否され、ゲストの catch には渡らない。
/// </summary>
public sealed class MemoryPolicy {
    /// <summary>命令数クォータ (停止性保証)。既定 = 実用上ほぼ無制限。</summary>
    public long InstructionQuota { get; init; } = 1_000_000_000;

    /// <summary>ゲスト呼出の最大深さ (深い再帰によるホスト StackOverflow の事前拒否)。</summary>
    public int MaxRecursionDepth { get; init; } = 512;

    /// <summary>累計アロケーション上限 (M6 の VmHeap 計上で強制)。</summary>
    public long TotalAllocationByteLimit { get; init; } = long.MaxValue;

    /// <summary>生存オブジェクト合計の上限 (M6 の GC で強制)。</summary>
    public long LiveObjectByteLimit { get; init; } = long.MaxValue;

    /// <summary>このバイト数のアロケーションごとに GC 起動を検討する間隔 (M6)。</summary>
    public long GcTriggerAllocationInterval { get; init; } = 1 << 20;
}

/// <summary>VM 起動オプション。</summary>
public sealed class VmHostOptions {
    public MemoryPolicy Memory { get; init; } = new();

    // Network/Storage ポリシーとブリッジ (既定 = DenyAll) は M7 で接続
    public bool EnableJit { get; init; } = true;
    public int JitPromotionThreshold { get; init; } = 1000;
}
