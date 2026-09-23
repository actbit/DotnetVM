using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// VM ごとの共有状態 (インスタンス状態)。
/// static なホストプロセス共有にしない: 仮想環境変数ストアも last system error も
/// VM ごとに分離する (ホスト環境の読み替え遮断と VM 間分離のため)。
/// lastError は CLR の TLS と同じく実行 host thread ごとに分離する。
/// </summary>
public sealed class VmSharedState : IDisposable {
    private readonly System.Threading.ThreadLocal<int> _lastSystemError = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly GuestWorkerBudget _workerBudget;
    private int _disposed;

    public VmSharedState(int maxGuestThreads = 64, int maxTaskWorkers = 64, int maxGuestWorkers = 64,
        int maxPendingTaskTimers = 1024, int shutdownTimeoutMilliseconds = 5_000) {
        if (maxGuestThreads < 1)
            throw new ArgumentOutOfRangeException(nameof(maxGuestThreads));
        if (maxTaskWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTaskWorkers));
        if (maxGuestWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxGuestWorkers));
        if (maxPendingTaskTimers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingTaskTimers));
        if (shutdownTimeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeoutMilliseconds));

        _workerBudget = new GuestWorkerBudget(maxGuestWorkers);
        GuestThreads = new GuestThreadRuntime(maxGuestThreads, _workerBudget, _shutdown.Token,
            shutdownTimeoutMilliseconds);
        GuestTasks = new GuestTaskRuntime(maxTaskWorkers, maxPendingTaskTimers, _workerBudget, _shutdown.Token,
            shutdownTimeoutMilliseconds);
    }

    /// <summary>Guest Thread の実行と join 状態 (VM ごとに分離)。</summary>
    internal GuestThreadRuntime GuestThreads { get; }

    internal GuestTaskRuntime GuestTasks { get; }

    /// <summary>VM 単位の型初期化状態表。</summary>
    internal TypeInitializationTracker TypeInitialization { get; } = new();

    /// <summary>Monitor のオブジェクト別同期ブロック。</summary>
    internal GuestMonitorTable Monitors { get; } = new();

    /// <summary>VM-wide atomic section used by Interlocked bindings over StackSlot references.</summary>
    internal object InterlockedGate { get; } = new();

    internal object TypeFacadeGate { get; } = new();

    /// <summary>VM ごとの仮想環境変数ストア (OrdinalIgnoreCase)。
    /// Kernel32.GetEnvironmentVariable 面は host の Environment を直接呼ばずここだけを読む。</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> VirtualEnvironment =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>VM 代替の last system error (Marshal.SetLastSystemError / GetLastSystemError 面)。
    /// 実 CLR の per-thread TLS スロットの代わりの VM 単位の単一値。</summary>
    public int LastSystemError {
        get => _lastSystemError.Value;
        set => _lastSystemError.Value = value;
    }

    /// <summary>RuntimeType ファサード (typeof / GetType 結果) のインターン表。
    /// 実 CLR の RuntimeType は型ごとに単一実体であり、CoreLib IL は bne.un 等の
    /// 参照同一性で比較する (Convert.DefaultToType の型分岐等)。ラッパ都度の new では
    /// 誤分岐するため、同一 VmType には同一 VmRuntimeObject を返す。
    /// VM 単位 (loader をまたいで共有する。VmType はローダ横断で同一参照のため安全)。</summary>
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<DotnetVM.Runtime.Types.VmType, DotnetVM.Runtime.Objects.VmRuntimeObject> TypeFacades = new();

    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void ThrowIfDisposed() {
        if (IsDisposed)
            throw new ObjectDisposedException("VirtualMachine");
    }

    /// <summary>RuntimeType ファサードを GC の直接ルートとして列挙する。</summary>
    internal IEnumerable<VmObject> EnumerateRoots() => TypeFacades.Values;

    /// <summary>ALC 由来の VM-wide 型初期化状態と RuntimeType ファサードを解放する。</summary>
    internal void RemoveAssemblyContextCaches(VmAssemblyContext context) {
        TypeInitialization.RemoveForContext(context);
        foreach (var type in TypeFacades.Keys)
            if (context.OwnsType(type))
                TypeFacades.TryRemove(type, out _);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _shutdown.Cancel();
        // Task を先に fault させて待機中の continuation / guest Thread を起こし、
        // その後 Thread worker を interrupt する。どちらも同じ cancellation token を見る。
        GuestTasks.Dispose();
        GuestThreads.Dispose();
        _workerBudget.Dispose();
        _lastSystemError.Dispose();
        _shutdown.Dispose();
    }
}
