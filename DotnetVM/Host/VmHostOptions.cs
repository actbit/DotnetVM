namespace DotnetVM.Host;

using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;

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

    /// <summary>ネットワークポリシー (既定 = DenyAll)。</summary>
    public NetworkPolicy Network { get; init; } = new();

    /// <summary>ストレージポリシー (既定 = DenyAll)。</summary>
    public StoragePolicy Storage { get; init; } = new();

    /// <summary>ネットワークブリッジ (ホスト実装の通信面。null = 全拒否)。</summary>
    public INetworkBridge? NetworkBridge { get; init; }

    /// <summary>ストレージブリッジ (ホスト実装のファイル I/O 面。null = 全拒否)。</summary>
    public IStorageBridge? StorageBridge { get; init; }
    public bool EnableJit { get; init; } = true;
    public int JitPromotionThreshold { get; init; } = 1000;

    /// <summary>GC 戦略 (既定 = 非世代別マーク &amp; スイープ。世代別戦略に差し替え可能)。</summary>
    public IGcStrategy Gc { get; init; } = new MarkSweepStrategy();

    /// <summary>
    /// ホスト実行環境の本物の System.Private.CoreLib.dll を VM にロードする (既定 = false)。
    /// true にするとゲストの BCL 型参照が実 TypeDef に解決され、CoreLib の managed IL の
    /// 実行が可能になる (段階的に有効化)。false では従来どおり intrinsic ファサード面で動く。
    /// </summary>
    public bool LoadHostCoreLib { get; init; }
}
