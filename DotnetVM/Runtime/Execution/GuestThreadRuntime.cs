using System.Runtime.CompilerServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Host-backed guest thread scheduler. VM objects remain guest-owned; only execution uses CLR threads.
/// Every started thread consumes both the Thread-specific limit and the VM-wide worker budget.
/// </summary>
internal sealed class GuestThreadRuntime(
    int maxThreads,
    GuestWorkerBudget workerBudget,
    CancellationToken shutdownToken,
    int shutdownTimeoutMilliseconds) : IDisposable {
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<VmObject, GuestThread> _threads = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, GuestThread> _active = new();
    private readonly Dictionary<int, GuestThread> _known = [];
    private readonly GuestWorkerBudget _workerBudget = workerBudget;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private readonly int _shutdownTimeoutMilliseconds = shutdownTimeoutMilliseconds;
    private int _nextId;
    private bool _disposed;

    public int ActiveCount => _active.Count;

    public void Configure(VmObject threadObject, VmDelegate startDelegate, bool parameterized) {
        ArgumentNullException.ThrowIfNull(threadObject);
        ArgumentNullException.ThrowIfNull(startDelegate);
        lock (_gate) {
            ThrowIfDisposed();
            if (_threads.TryGetValue(threadObject, out var existing)) {
                existing.SetStartDelegate(startDelegate, parameterized);
                return;
            }
            var thread = new GuestThread(threadObject, Interlocked.Increment(ref _nextId), startDelegate, parameterized);
            _threads.Add(threadObject, thread);
            _known[thread.Id] = thread;
        }
    }

    public bool Start(VmObject threadObject, StackSlot state, Action<VmDelegate, StackSlot, bool> runDelegate, bool passState) {
        var thread = Get(threadObject);
        lock (_gate) {
            ThrowIfDisposed();
            if (thread.Started)
                throw new InvalidOperationException("Thread は既に開始されています。");
            if (passState && !thread.Parameterized)
                throw new InvalidOperationException("ThreadStart で作成されたスレッドに state 引数は渡せません。");
            if (_active.Count >= maxThreads)
                throw new GuestConcurrencyLimitExceededException($"VM の guest Thread 上限 ({maxThreads}) に達しました。");
            if (!_workerBudget.TryAcquire())
                throw new GuestConcurrencyLimitExceededException("VM-wide guest worker 上限に達しました。");

            thread.Started = true;
            thread.BudgetAcquired = true;
            thread.HostThread = new Thread(() => {
                try {
                    _shutdownToken.ThrowIfCancellationRequested();
                    runDelegate(thread.StartDelegate,
                        thread.Parameterized ? (passState ? state : StackSlot.OfObject(null)) : default,
                        thread.Parameterized);
                } catch (ThreadInterruptedException) when (_shutdownToken.IsCancellationRequested) {
                    // Dispose が Sleep/Join/Monitor.Wait を解除した経路。
                } catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested) {
                    // セーフポイントで shutdown を観測した経路。
                } catch (Exception ex) {
                    // CLR の unhandled background-thread exception はプロセスを終了させるため、
                    // ゲストスレッド内の例外は Thread.Join ではなくスレッド状態に保存する。
                    thread.UnhandledException = ex;
                } finally {
                    _active.TryRemove(thread.Id, out _);
                    if (thread.BudgetAcquired) {
                        thread.BudgetAcquired = false;
                        _workerBudget.Release();
                    }
                    // Join が返る時点で active 集計と VM-wide 予算も解放済みにする。
                    thread.Completed.Set();
                }
            }) {
                IsBackground = true,
                Name = $"DotnetVM guest thread {thread.Id}",
            };
            _active[thread.Id] = thread;
            try {
                thread.HostThread.Start();
            } catch {
                _active.TryRemove(thread.Id, out _);
                thread.Started = false;
                thread.HostThread = null;
                if (thread.BudgetAcquired) {
                    thread.BudgetAcquired = false;
                    _workerBudget.Release();
                }
                throw;
            }
        }
        return true;
    }

    public bool Join(VmObject threadObject, int millisecondsTimeout) {
        var thread = Get(threadObject);
        if (!thread.Started)
            throw new InvalidOperationException("開始されていない Thread は Join できません。");
        try {
            return thread.Completed.Wait(millisecondsTimeout, _shutdownToken);
        } catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested) {
            return false;
        }
    }

    public bool IsAlive(VmObject threadObject) {
        var thread = Get(threadObject);
        return thread.Started && !thread.Completed.IsSet;
    }

    public int ManagedThreadId(VmObject threadObject) => Get(threadObject).Id;

    public IEnumerable<VmObject> EnumerateRoots() {
        foreach (var thread in _active.Values) {
            yield return thread.ThreadObject;
            yield return thread.StartDelegate;
        }
    }

    public void Dispose() {
        GuestThread[] threads;
        lock (_gate) {
            if (_disposed)
                return;
            _disposed = true;
            threads = [.. _known.Values];
        }

        foreach (var thread in threads) {
            var hostThread = thread.HostThread;
            if (hostThread is null || !hostThread.IsAlive)
                continue;
            try {
                hostThread.Interrupt();
            } catch (Exception) when (!hostThread.IsAlive) {
                // 既に終了した worker との競合は無視する。
            }
        }

        var current = Environment.CurrentManagedThreadId;
        foreach (var thread in threads) {
            var hostThread = thread.HostThread;
            if (hostThread is null || hostThread.ManagedThreadId == current)
                continue;
            try {
                hostThread.Join(_shutdownTimeoutMilliseconds);
            } catch (ThreadStateException) {
                // Start と Dispose の競合時は worker の finally 側に後処理を任せる。
            }
            if (!hostThread.IsAlive)
                thread.Completed.Dispose();
        }
    }

    private GuestThread Get(VmObject threadObject) {
        lock (_gate)
            return _threads.TryGetValue(threadObject, out var thread)
                ? thread
                : throw new InvalidOperationException("Thread が VM のスレッド管理表に登録されていません。");
    }

    private void ThrowIfDisposed() {
        if (_disposed)
            throw new ObjectDisposedException("VirtualMachine");
    }

    private sealed class GuestThread(VmObject threadObject, int id, VmDelegate startDelegate, bool parameterized) {
        public readonly VmObject ThreadObject = threadObject;
        public readonly int Id = id;
        public readonly ManualResetEventSlim Completed = new(false);
        public VmDelegate StartDelegate = startDelegate;
        public bool Parameterized = parameterized;
        public bool Started;
        public bool BudgetAcquired;
        public Thread? HostThread;
        public Exception? UnhandledException;

        public void SetStartDelegate(VmDelegate value, bool isParameterized) {
            if (Started)
                throw new InvalidOperationException("開始後の Thread の delegate は変更できません。");
            StartDelegate = value;
            Parameterized = isParameterized;
        }
    }
}

/// <summary>Per-guest-object synchronization state. The underlying monitor is never exposed to guest code.</summary>
internal sealed class GuestMonitorTable {
    private readonly ConditionalWeakTable<object, object> _syncRoots = new();
    private readonly object _gate = new();

    public object SyncRoot(object? target) {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        lock (_gate)
            return _syncRoots.GetValue(target, static _ => new object());
    }
}
