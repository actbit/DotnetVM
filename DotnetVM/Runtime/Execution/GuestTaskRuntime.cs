using System.Collections.Concurrent;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
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
    // ValueTask の default 値は backing Task を持たない completed state として扱う。
    // AsTask() が guest-visible Task を要求した場合に限り、通常の heap allocation を通した
    // Task を型ごとに再利用する。Lazy は heap lock と lifetime lock の逆順取得を防ぐ。
    private readonly Dictionary<VmType, CompletedTaskCacheEntry> _completedSentinels =
        new(VmTypeIdentityComparer.Instance);
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
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            var task = new VmTaskObject(type);
            if (completed)
                task.SetResult(result);
            else
                _activeRoots[task] = [StackSlot.OfObject(task)];
            return task;
        }
    }

    public IEnumerable<StackSlot[]> EnumerateRoots() {
        foreach (var roots in _activeRoots.Values)
            yield return roots;
        foreach (var roots in _continuationRoots.Values)
            yield return roots;
        CompletedTaskCacheEntry[] sentinels;
        lock (_lifetimeGate)
            sentinels = [.. _completedSentinels.Values];
        foreach (var sentinel in sentinels)
            if (sentinel.Root is { } root)
                yield return [StackSlot.OfObject(root)];
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
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _continuationRoots[id] = [StackSlot.OfObject(awaited), detachedRef];
        }
        try {
            StartWorker(null, [StackSlot.OfObject(awaited), detachedRef], () => {
                awaited.Wait(_shutdownToken);
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

    /// <summary>
    /// 既定値の ValueTask が AsTask で guest-visible Task を必要とするときの完了済み Task。
    /// default(ValueTask) 自体はこのオブジェクトを必要とせず、null backing task を completed
    /// state として使う。AsTask が初めて呼ばれた時だけ heap に割り当て、その後は型ごとに再利用する。
    /// </summary>
    public VmTaskObject CompletedSentinel(VmType type, StackSlot result, VmHeap heap) {
        CompletedTaskCacheEntry sentinel;
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            if (!_completedSentinels.TryGetValue(type, out sentinel!)) {
                sentinel = new CompletedTaskCacheEntry(type, result, heap);
                _completedSentinels.Add(type, sentinel);
            }
        }
        return sentinel.Get();
    }

    /// <summary>collectible ALC の型を参照する completed Task cache entry を解放する。</summary>
    public void RemoveAssemblyContextCaches(VmAssemblyContext context) {
        lock (_lifetimeGate) {
            foreach (var type in _completedSentinels.Keys.ToArray())
                if (context.OwnsType(type))
                    _completedSentinels.Remove(type);
        }
    }

    /// <summary>TaskAwaiter/ValueTaskAwaiter.OnCompleted から渡される guest delegate を
    /// worker 上で一度だけ実行する。awaiter の直接利用でも no-op にせず、state machine
    /// 継続と同じ worker quota・shutdown 規約を通す。</summary>
    public void ScheduleCallback(VmTaskObject awaited, StackSlot callback, Action<StackSlot> invoke) {
        var value = callback.Kind == StackKind.ByRef && callback.ObjectValue is VmByRef byRef
            ? byRef.Read()
            : callback;
        if (value.ObjectValue is not VmDelegate)
            throw new InvalidOperationException("awaiter continuation は guest delegate である必要があります。");
        var id = Interlocked.Increment(ref _nextContinuationId);
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _continuationRoots[id] = [StackSlot.OfObject(awaited), value];
        }
        try {
            StartWorker(null, [StackSlot.OfObject(awaited), value], () => {
                awaited.Wait(_shutdownToken);
                _shutdownToken.ThrowIfCancellationRequested();
                invoke(value);
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
                    try {
                        onFinished?.Invoke();
                    } finally {
                        _workers.TryRemove(workerId, out _);
                        Interlocked.Decrement(ref _activeWorkers);
                        _workerBudget.Release();
                    }
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
            _completedSentinels.Clear();
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

    /// <summary>
    /// Publishes the task as a GC root before heap registration. That closes the window where a
    /// concurrent collection could sweep the object after Allocate returns but before Lazy stores it.
    /// </summary>
    private sealed class CompletedTaskCacheEntry {
        private readonly Lazy<VmTaskObject> _task;
        private VmTaskObject? _root;

        public CompletedTaskCacheEntry(VmType type, StackSlot result, VmHeap heap) {
            _task = new Lazy<VmTaskObject>(() => {
                var task = new VmTaskObject(type);
                task.SetResult(result);
                Volatile.Write(ref _root, task);
                return heap.Allocate(task);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public VmTaskObject? Root => Volatile.Read(ref _root);
        public VmTaskObject Get() => _task.Value;
    }

    /// <summary>型名ではなく VM 型定義と型引数の identity による cache key 比較。</summary>
    private sealed class VmTypeIdentityComparer : IEqualityComparer<VmType> {
        public static VmTypeIdentityComparer Instance { get; } = new();

        public bool Equals(VmType? x, VmType? y) {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return (x, y) switch {
                (VmConstructedType left, VmConstructedType right) =>
                    Equals(left.Definition, right.Definition) && SequenceEqual(left.TypeArguments, right.TypeArguments),
                (VmArrayType left, VmArrayType right) => Equals(left.ElementType, right.ElementType),
                (VmMultiDimArrayType left, VmMultiDimArrayType right) =>
                    left.Rank == right.Rank && Equals(left.ElementType, right.ElementType),
                (VmByRefType left, VmByRefType right) => Equals(left.ElementType, right.ElementType),
                _ => false,
            };
        }

        public int GetHashCode(VmType type) => type switch {
            VmConstructedType constructed => CombineHash(
                GetHashCode(constructed.Definition), constructed.TypeArguments),
            VmArrayType array => HashCode.Combine(typeof(VmArrayType), GetHashCode(array.ElementType)),
            VmMultiDimArrayType array => HashCode.Combine(typeof(VmMultiDimArrayType), array.Rank, GetHashCode(array.ElementType)),
            VmByRefType byRef => HashCode.Combine(typeof(VmByRefType), GetHashCode(byRef.ElementType)),
            _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(type),
        };

        private bool SequenceEqual(VmType[] left, VmType[] right) {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!Equals(left[index], right[index]))
                    return false;
            return true;
        }

        private int CombineHash(int definitionHash, VmType[] arguments) {
            var hash = new HashCode();
            hash.Add(typeof(VmConstructedType));
            hash.Add(definitionHash);
            foreach (var argument in arguments)
                hash.Add(GetHashCode(argument));
            return hash.ToHashCode();
        }
    }
}
