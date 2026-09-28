namespace DotnetVM.Host;

using System.Globalization;
using System.Security.Cryptography;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;

/// <summary>VM の暗号学的乱数を供給するホスト delegate。</summary>
public delegate void VmRandomFill(Span<byte> buffer);

/// <summary>
/// メモリ/実行リソースのポリシー。すべてクォータ+拒否方式の上限。
/// 超過は ResourceExhaustedException 系で拒否され、ゲストの catch には渡らない。
/// </summary>
public sealed class MemoryPolicy {
    /// <summary>ホストスタックを危険な深さまで消費させないための絶対上限。</summary>
    public const int MaxAllowedRecursionDepth = 4096;

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

    /// <summary>ホスト側一時メモリ (ゲスト演算の結果文字列 / 配列等、VM ヒープ外の確保) の
    /// 累計上限。string.Concat(host string[]) / Encoding.GetString 等の intrinsic 内で
    /// 一時的に確保されるバイト量を本クォータに計上する (Reserve → Commit / Rollback の
    /// トランザクションで ChargeHostBuffer / ChargeHostWork と整合)。</summary>
    public long HostTempAllocationByteLimit { get; init; } = long.MaxValue;

    /// <summary>ホスト側 CPU 作業の予算 (culture-aware 比較・formatting・encoding 等の
    /// 重い host 側処理の発生に応じて消费)。「host ⟷ ゲストの CPU 増幅を縛る」独立 quota。
    /// 実装の assignment: 操作種 (文字列比較 = X ワーク、formatting = Y ワーク)
    /// を intrinsic 毎に定数で保持する (全量計上ではなく実コスト近似)。</summary>
    public long HostWorkBudget { get; init; } = 10_000_000;

    /// <summary>診断トレーサが保持する最大イベント数。古いイベントから破棄する。</summary>
    public int MaxTraceEvents { get; init; } = 100_000;

    /// <summary>JIT 式ツリー構築と CLR デリゲート生成専用の VM-wide 作業予算。</summary>
    public long JitCompilationBudget { get; init; } = 1_000_000;

    /// <summary>VM 全体で保持できる JIT 済みメソッド数。0 は JIT コンパイルを拒否する。</summary>
    public int MaxJitCompiledMethods { get; init; } = 256;

    /// <summary>VM 全体の JIT cache entry (昇格拒否済みを含む) の上限。</summary>
    public int MaxJitCacheEntries { get; init; } = 4096;

    /// <summary>
    /// 1 loader が保持する prepared method (デコード済み IL / locals / EH) の上限。
    /// 一度しか実行されないメソッドを大量に受け取る VM で host representation が
    /// 無制限に蓄積しないようにする。0 はキャッシュを無効化する。
    /// </summary>
    public int MaxPreparedMethods { get; init; } = 4096;

    /// <summary>
    /// 1 loader が prepared method cache に保持できる概算 host representation の合計バイト数。
    /// 命令列、offset map、ローカル既定値、EH 解決結果を含む。0 は prepared cache を無効化する。
    /// </summary>
    public long MaxPreparedMethodBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>1 メソッドを式ツリーへ変換する際に許す概算ノード数。</summary>
    public int MaxJitExpressionNodes { get; init; } = 32_768;

    /// <summary>JIT 入力として許すメソッド本体サイズ。通常の loader 上限より厳しい。</summary>
    public int MaxJitMethodBodyBytes { get; init; } = 64 * 1024;

    /// <summary>ロード検証 phase の入力上限 (loader hardening):
    /// Assembly バイト総量 > MaxAssemblyBytes はロード拒否。</summary>
    public long MaxAssemblyBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>VM の生存中にロード済み PE image が保持する host memory の上限。
    /// collectible unload 後も stale handle が画像を保持し得るため、上限の返却は VM Dispose 時に行う。</summary>
    public long LoadedAssemblyHostByteLimit { get; init; } = long.MaxValue;

    /// <summary>メタデータ行 (TypeDef / MethodDef / MemberRef 等) の合計上限。
    /// hostile 画像が ソート不可の数値でメタデータを肥大させるのを防ぐ。</summary>
    public long MaxMetadataRows { get; init; } = 1_000_000;

    /// <summary>メソッド本体の最大バイト数 (method body fat/tiny の上限)。</summary>
    public int MaxMethodBodyBytes { get; init; } = 512 * 1024;

    /// <summary>署名 blob / generic 再帰の深さ上限 (signature 解析が stack overflow するのを防ぐ)。</summary>
    public int MaxSignatureDepth { get; init; } = 64;

    /// <summary>ジェネリック ネスト深度の上限 (generic instability fuzz 対策)。</summary>
    public int MaxGenericNestingDepth { get; init; } = 64;

    /// <summary>ドキュメント読み込みの最大メタデータストリームサイズ (単一 #~ / #Strings 等)。</summary>
    public long MaxMetadataStreamBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>ポリシー値の符号と、実行に必要な最小値を検証する。</summary>
    internal void Validate() {
        if (InstructionQuota < 0) throw new ArgumentOutOfRangeException(nameof(InstructionQuota));
        if (MaxRecursionDepth < 1 || MaxRecursionDepth > MaxAllowedRecursionDepth)
            throw new ArgumentOutOfRangeException(nameof(MaxRecursionDepth),
                $"MaxRecursionDepth は 1..{MaxAllowedRecursionDepth} の範囲で指定してください。");
        if (TotalAllocationByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(TotalAllocationByteLimit));
        if (LiveObjectByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(LiveObjectByteLimit));
        if (GcTriggerAllocationInterval < 1) throw new ArgumentOutOfRangeException(nameof(GcTriggerAllocationInterval));
        if (HostTempAllocationByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(HostTempAllocationByteLimit));
        if (HostWorkBudget < 0) throw new ArgumentOutOfRangeException(nameof(HostWorkBudget));
        if (MaxTraceEvents < 1) throw new ArgumentOutOfRangeException(nameof(MaxTraceEvents));
        if (JitCompilationBudget < 0) throw new ArgumentOutOfRangeException(nameof(JitCompilationBudget));
        if (MaxJitCompiledMethods < 0) throw new ArgumentOutOfRangeException(nameof(MaxJitCompiledMethods));
        if (MaxJitCacheEntries < 0) throw new ArgumentOutOfRangeException(nameof(MaxJitCacheEntries));
        if (MaxPreparedMethods < 0) throw new ArgumentOutOfRangeException(nameof(MaxPreparedMethods));
        if (MaxPreparedMethodBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxPreparedMethodBytes));
        if (MaxJitExpressionNodes < 1) throw new ArgumentOutOfRangeException(nameof(MaxJitExpressionNodes));
        if (MaxJitMethodBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxJitMethodBodyBytes));
        if (MaxAssemblyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxAssemblyBytes));
        if (LoadedAssemblyHostByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(LoadedAssemblyHostByteLimit));
        if (MaxMetadataRows < 0) throw new ArgumentOutOfRangeException(nameof(MaxMetadataRows));
        if (MaxMethodBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxMethodBodyBytes));
        if (MaxSignatureDepth < 0) throw new ArgumentOutOfRangeException(nameof(MaxSignatureDepth));
        if (MaxGenericNestingDepth < 0) throw new ArgumentOutOfRangeException(nameof(MaxGenericNestingDepth));
        if (MaxMetadataStreamBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxMetadataStreamBytes));
    }
}

/// <summary>VM 起動オプション。</summary>
public sealed class VmHostOptions {
    public MemoryPolicy Memory { get; init; } = new();

    /// <summary>ゲスト実行中に使う CurrentCulture / CurrentUICulture。既定は従来互換の不変カルチャ。</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>DateTime.Now / UtcNow / Today が読む VM 単位の時計。既定は host UTC clock。</summary>
    public Func<DateTimeOffset> ClockProvider { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>DateTime.Now / Today の変換先となる VM 単位のタイムゾーン。既定は UTC。</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Guid.NewGuid と trusted CoreLib の乱数 API が使う VM 単位の RNG。既定は host CSPRNG。</summary>
    public VmRandomFill RandomFill { get; init; } = static buffer => RandomNumberGenerator.Fill(buffer);

    /// <summary>ネットワークポリシー (既定 = DenyAll)。</summary>
    public NetworkPolicy Network { get; init; } = new();

    /// <summary>ストレージポリシー (既定 = DenyAll)。</summary>
    public StoragePolicy Storage { get; init; } = new();

    /// <summary>ネットワークブリッジ (ホスト実装の通信面。null = 全拒否)。</summary>
    public INetworkBridge? NetworkBridge { get; init; }

    /// <summary>ストレージブリッジ (ホスト実装のファイル I/O 面。null = 全拒否)。</summary>
    public IStorageBridge? StorageBridge { get; init; }
    /// <summary>簡易 JIT (M8 のスカラー IL ホットパス) を有効化するか。
    /// 既定 = false。未対応の IL は常にインタプリタへフォールバックする。</summary>
    public bool EnableJit { get; init; }
    /// <summary>JIT 昇格までのメソッド呼出回数。1 なら初回呼出でコンパイルする。</summary>
    public int JitPromotionThreshold { get; init; } = 1000;

    /// <summary>GC 戦略 (既定 = 非世代別マーク &amp; スイープ。世代別戦略に差し替え可能)。</summary>
    public IGcStrategy Gc { get; init; } = new MarkSweepStrategy();

    /// <summary>
    /// ホスト実行環境の本物の System.Private.CoreLib.dll を VM にロードする (既定 = false)。
    /// true にするとゲストの BCL 型参照が実 TypeDef に解決され、CoreLib の managed IL の
    /// 実行が可能になる (段階的に有効化)。false では従来どおり intrinsic ファサード面で動く。
    /// </summary>
    public bool LoadHostCoreLib { get; init; }

    /// <summary>
    /// 組み込みの CoreLib ランタイムバインドを登録するか (既定 = true)。false にすると
    /// <see cref="CoreLibBindingProviders"/> で指定したプロバイダーだけが登録される。
    /// DefaultIntrinsics の登録には影響しない。
    /// </summary>
    public bool UseDefaultCoreLibBindings { get; init; } = true;

    /// <summary>
    /// 追加または置換用の CoreLib バインドプロバイダー。既定バインドと併用する場合は
    /// 新しいキーは RegisterBinding、既定の特定キーは ReplaceBinding で登録できる。
    /// 既定バインド全体を置き換える場合は <see cref="UseDefaultCoreLibBindings"/> を false にする。
    /// プロバイダーと登録実装は VM の TCB に加わるホスト権限の特権コードであり、信頼済み実装だけを指定する。
    /// </summary>
    public IReadOnlyList<ICoreLibBindingProvider> CoreLibBindingProviders { get; init; } = [];

    /// <summary>同時に開始できる guest Thread 数 (別途 host 側の OS 制限も適用)。既定 = 64。</summary>
    public int MaxGuestThreads { get; init; } = 64;

    /// <summary>同時に実行できる guest Task worker 数。Thread の上限とは別に明示する。既定 = 64。</summary>
    public int MaxTaskWorkers { get; init; } = 64;

    /// <summary>
    /// Thread と Task worker を合わせた VM-wide host worker 上限。
    /// 個別上限を両方満たしても host thread が二重に増えないよう、共通予算を既定で 64 にする。
    /// </summary>
    public int MaxGuestWorkers { get; init; } = 64;

    /// <summary>未完了の Task.Delay と CancellationTokenSource timer が共有する host Timer 数。
    /// -1 の無限 Delay/CancelAfter も 1 件として数える。</summary>
    public int MaxPendingTaskTimers { get; init; } = 1024;

    /// <summary>
    /// Task.WhenAll / WhenAny / WaitAll が受け付ける入力 Task 数の上限。入力の host 配列、
    /// completion registration、GC root 配列が guest 配列とは別に確保されるため、明示的な
    /// host fan-out quota として検査する。
    /// </summary>
    public int MaxTaskCombinatorInputs { get; init; } = 16_384;

    /// <summary>
    /// VM.Dispose が worker の停止を待つ最大時間 (ミリ秒)。通常は worker が
    /// cancellation/interrupt で速やかに終了する。
    /// </summary>
    public int ShutdownTimeoutMilliseconds { get; init; } = 5_000;

    /// <summary>デバッガが保持できる命令ブレークポイント数。</summary>
    public int MaxBreakpoints { get; init; } = 4096;
}
