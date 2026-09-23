using System.Collections.Concurrent;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Host worker 上で動く guest Task と async state machine 継続の管理。
/// Task worker は Thread worker と VM-wide budget を共有し、Delay の Timer 数も上限で制限する。
/// </summary>
internal sealed class GuestTaskRuntime(
    int maxWorkers,
    int maxPendingTimers,
    GuestWorkerBudget workerBudget,
    CancellationToken shutdownToken,
    int shutdownTimeoutMilliseconds) : IDisposable {
    private readonly ConcurrentDictionary<VmTaskObject, StackSlot[]> _activeRoots = new();
    private readonly ConcurrentDictionary<VmTaskObject, Timer> _timers = new();
    private readonly ConcurrentDictionary<long, StackSlot[]> _continuationRoots = new();
    private readonly ConcurrentDictionary<int, Thread> _workers = new();
    private readonly object _lifetimeGate = new();
    private readonly int _maxWorkers = maxWorkers;
    private readonly int _maxPendingTimers = maxPendingTimers;
    private readonly GuestWorkerBudget _workerBudget = workerBudget;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private readonly int _shutdownTimeoutMilliseconds = shutdownTimeoutMilliseconds;
    private int _activeWorkers;
    private int _nextWorkerId;
    private long _nextContinuationId;
    private bool _disposed;

    public int ActiveWorkerCount => Volatile.Read(ref _activeWorkers);
    public int PendingTimerCount => _timers.Count;

    public VmTaskObject Create(VmType type, bool completed = false, StackSlot result = default) {
        lock (_lifetimeGate)
            ThrowIfDisposed();
        var task = new VmTaskObject(type);
        if (completed)
            task.SetResult(result);
        else
            _activeRoots[task] = [StackSlot.OfObject(task)];
        return task;
    }

    public IEnumerable<StackSlot[]> EnumerateRoots() {
        foreach (var roots in _activeRoots.Values)
            yield return roots;
        foreach (var roots in _continuationRoots.Values)
            yield return roots;
    }

    public void Complete(VmTaskObject task, StackSlot result = default) {
        task.SetResult(result);
        RemoveTaskRoots(task);
    }

    public void CompleteGuestException(VmTaskObject task, StackSlot exception) {
        task.SetGuestException(exception);
        RemoveTaskRoots(task);
    }

    public void CompleteHostException(VmTaskObject task, Exception exception) {
        task.SetHostException(exception);
        RemoveTaskRoots(task);
    }

    public void Delay(VmTaskObject task, int milliseconds) {
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            if (milliseconds == 0) {
                Complete(task);
                return;
            }
            if (_timers.Count >= _maxPendingTimers) {
                _activeRoots.TryRemove(task, out _);
                throw new GuestConcurrencyLimitExceededException(
                    $"未完了の Task.Delay Timer 上限 ({_maxPendingTimers}) に達しました。");
            }

            _activeRoots[task] = [StackSlot.OfObject(task)];
            var timer = new Timer(_ => Complete(task), null, Timeout.Infinite, Timeout.Infinite);
            if (!_timers.TryAdd(task, timer)) {
                timer.Dispose();
                _activeRoots.TryRemove(task, out _);
                throw new InvalidOperationException("同一 Task に複数の Delay Timer を登録できません。");
            }
            try {
                timer.Change(milliseconds, Timeout.Infinite);
            } catch {
                if (_timers.TryRemove(task, out var failedTimer))
                    failedTimer.Dispose();
                _activeRoots.TryRemove(task, out _);
                throw;
            }
        }
    }

    public void Wait(VmTaskObject task, int millisecondsTimeout) => task.Wait(millisecondsTimeout);

    public void Run(VmTaskObject task, StackSlot[] roots, Func<StackSlot> work) {
        try {
            StartWorker(task, roots, work, result => Complete(task, result),
                ex => CompleteHostException(task, ex));
        } catch (Exception ex) {
            CompleteHostException(task, ex);
            throw;
        }
    }

    public void ScheduleContinuation(VmTaskObject awaited, StackSlot stateMachine, Action<StackSlot> resume) {
        var state = stateMachine.Kind == StackKind.ByRef && stateMachine.ObjectValue is VmByRef byRef
            ? byRef.Read()
            : stateMachine;
        StackSlot detached;
        if (state.ObjectValue is VmStructValue machine)
            detached = StackSlot.OfValueType(machine.Clone());
        else if (state.ObjectValue is VmClassInstance)
            detached = state;
        else
            throw new InvalidOperationException("async state machine は VM オブジェクトである必要があります。");
        var stateContainer = new[] { detached };
        var detachedRef = StackSlot.OfByRef(new VmByRef(stateContainer, 0));
        var id = Interlocked.Increment(ref _nextContinuationId);
        _continuationRoots[id] = [StackSlot.OfObject(awaited), detachedRef];
        try {
            StartWorker(null, [StackSlot.OfObject(awaited), detachedRef], () => {
                awaited.Wait();
                _shutdownToken.ThrowIfCancellationRequested();
                resume(detachedRef);
                return default;
            }, onSuccess: null, onError: _ => { },
                onFinished: () => _continuationRoots.TryRemove(id, out _));
        } catch {
            _continuationRoots.TryRemove(id, out _);
            throw;
        }
    }

    private void StartWorker(VmTaskObject? trackedTask, StackSlot[] roots, Action work, Action<Exception> onError,
        Action? onFinished = null) =>
        StartWorker(trackedTask, roots, () => { work(); return default; }, null, onError, onFinished);

    private void StartWorker(VmTaskObject? trackedTask, StackSlot[] roots, Func<StackSlot> work,
        Action<StackSlot>? onSuccess, Action<Exception> onError, Action? onFinished = null) {
        int workerId;
        Thread thread;
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            if (_activeWorkers >= _maxWorkers)
                throw new GuestConcurrencyLimitExceededException($"guest Task worker 上限 {_maxWorkers} に達しました。");
            if (!_workerBudget.TryAcquire())
                throw new GuestConcurrencyLimitExceededException("VM-wide guest worker 上限に達しました。");

            _activeWorkers++;
            workerId = Interlocked.Increment(ref _nextWorkerId);
            if (trackedTask is not null)
                _activeRoots[trackedTask] = roots;
            thread = new Thread(() => {
                StackSlot result = default;
                Exception? failure = null;
                var cancelled = false;
                try {
                    _shutdownToken.ThrowIfCancellationRequested();
                    result = work();
                } catch (ThreadInterruptedException) when (_shutdownToken.IsCancellationRequested) {
                    // Dispose が blocking wait を解除した。
                    cancelled = true;
                } catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested) {
                    // VM shutdown を worker が観測した。
                    cancelled = true;
                } catch (Exception ex) {
                    failure = ex;
                } finally {
                    _workers.TryRemove(workerId, out _);
                    Interlocked.Decrement(ref _activeWorkers);
                    _workerBudget.Release();
                    onFinished?.Invoke();
                }
                if (cancelled)
                    return;
                if (failure is not null) {
                    if (trackedTask is not null)
                        onError(failure);
                    return;
                }
                onSuccess?.Invoke(result);
            }) { IsBackground = true, Name = "DotnetVM guest Task" };
            _workers[workerId] = thread;
        }

        try {
            thread.Start();
        } catch (Exception ex) {
            _workers.TryRemove(workerId, out _);
            if (trackedTask is not null)
                _activeRoots.TryRemove(trackedTask, out _);
            try {
                onFinished?.Invoke();
            } finally {
                Interlocked.Decrement(ref _activeWorkers);
                _workerBudget.Release();
            }
            if (trackedTask is not null)
                onError(ex);
            else
                throw;
        }
    }

    private void RemoveTaskRoots(VmTaskObject task) {
        _activeRoots.TryRemove(task, out _);
        if (_timers.TryRemove(task, out var timer))
            timer.Dispose();
    }

    public void Dispose() {
        Timer[] timers;
        Thread[] workers;
        VmTaskObject[] tasks;
        lock (_lifetimeGate) {
            if (_disposed)
                return;
            _disposed = true;
            timers = [.. _timers.Values];
            _timers.Clear();
            workers = [.. _workers.Values];
            tasks = [.. _activeRoots.Keys];
        }

        foreach (var timer in timers)
            timer.Dispose();
        var shutdown = new ObjectDisposedException("VirtualMachine");
        foreach (var task in tasks)
            task.SetHostException(shutdown);
        _activeRoots.Clear();
        _continuationRoots.Clear();

        foreach (var worker in workers) {
            if (!worker.IsAlive)
                continue;
            try {
                worker.Interrupt();
            } catch (Exception) when (!worker.IsAlive) {
                // worker が Interrupt と同時に終了した。
            }
        }

        var current = Environment.CurrentManagedThreadId;
        foreach (var worker in workers) {
            if (worker.ManagedThreadId == current)
                continue;
            try {
                worker.Join(_shutdownTimeoutMilliseconds);
            } catch (ThreadStateException) {
                // Start と Dispose の競合時は worker の finally 側に後処理を任せる。
            }
        }
    }

    private void ThrowIfDisposed() {
        if (_disposed)
            throw new ObjectDisposedException("VirtualMachine");
    }
}
