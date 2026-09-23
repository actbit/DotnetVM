namespace DotnetVM.Runtime.Execution;

/// <summary>
/// VM 全体で共有する guest worker の同時実行予算。
/// Thread と Task を別々に数えると、各上限を同時に満たしたとき host thread 数が
/// ほぼ 2 倍になるため、worker の種類をまたいで 1 つの予算を消費する。
/// </summary>
internal sealed class GuestWorkerBudget(int limit) {
    private readonly object _gate = new();
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
            if (_disposed || _active >= limit)
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
