namespace DotnetVM.Runtime.Execution;

/// <summary>
/// VM 全体で共有する guest worker の同時実行予算。
/// Thread と Task を別々に数えると、個別の上限を同時に満たしたとき host thread 数が
/// 予算を越えるため、worker の種類をまたいで 1 つの予算を消費する。
/// </summary>
internal sealed class GuestWorkerBudget(int limit) : IDisposable {
    private readonly object _gate = new();
    private readonly int _limit = limit;
    private int _active;
    private bool _disposed;

    public int ActiveCount {
        get {
            lock (_gate)
                return _active;
        }
    }

    public bool TryAcquire() {
        lock (_gate) {
            if (_disposed || _active >= _limit)
                return false;
            _active++;
            return true;
        }
    }

    public void Release() {
        lock (_gate) {
            if (_active > 0)
                _active--;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose() {
        lock (_gate)
            _disposed = true;
    }
}
