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
    int maxTaskCombinatorInputs,
    GuestWorkerBudget workerBudget,
    CancellationToken shutdownToken,
    int shutdownTimeoutMilliseconds) : IDisposable {
    private readonly ConcurrentDictionary<VmTaskObject, StackSlot[]> _activeRoots = new();
    private readonly ConcurrentDictionary<VmTaskObject, Timer> _timers = new();
    private readonly ConcurrentDictionary<VmCancellationState, CancellationTimerRegistration> _cancellationTimers = new();
    private readonly ConcurrentDictionary<VmTaskObject, CancellationTokenRegistration> _cancellationRegistrations = new();
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
    private readonly int _maxTaskCombinatorInputs = maxTaskCombinatorInputs;
    private readonly GuestWorkerBudget _workerBudget = workerBudget;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private readonly int _shutdownTimeoutMilliseconds = shutdownTimeoutMilliseconds;
    private int _activeWorkers;
    private int _pendingTimerSlots;
    private int _nextWorkerId;
    private long _nextContinuationId;
    private bool _disposed;

    public int ActiveWorkerCount => Volatile.Read(ref _activeWorkers);
    public int PendingTimerCount => Volatile.Read(ref _pendingTimerSlots);
    public int CancellationRegistrationCount => _cancellationRegistrations.Count;

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
        VmCancellationState[] cancellationStates;
        lock (_lifetimeGate)
            cancellationStates = [.. _cancellationTimers.Keys];
        foreach (var state in cancellationStates)
            yield return [StackSlot.OfObject(state)];
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

    public void Delay(VmTaskObject task, int milliseconds) => Delay(task, milliseconds, default);

    public void Delay(VmTaskObject task, int milliseconds, CancellationToken cancellationToken) {
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            if (cancellationToken.IsCancellationRequested) {
                task.SetCanceled();
                _activeRoots.TryRemove(task, out _);
                return;
            }
            if (milliseconds == 0) {
                Complete(task);
                return;
            }
            if (!TryReserveTimerSlotUnsafe()) {
                _activeRoots.TryRemove(task, out _);
                throw new GuestConcurrencyLimitExceededException(
                    $"未完了の Task.Delay / CancellationTokenSource Timer 共通上限 ({_maxPendingTimers}) に達しました。");
            }

            _activeRoots[task] = [StackSlot.OfObject(task)];
            var timer = new Timer(_ => Complete(task), null, Timeout.Infinite, Timeout.Infinite);
            if (!_timers.TryAdd(task, timer)) {
                timer.Dispose();
                ReleaseTimerSlot();
                _activeRoots.TryRemove(task, out _);
                throw new InvalidOperationException("同一 Task に複数の Delay Timer を登録できません。");
            }
            try {
                timer.Change(milliseconds, Timeout.Infinite);
                if (cancellationToken.CanBeCanceled) {
                    var registration = cancellationToken.Register(static state => {
                        var target = (GuestTaskRuntime.TaskCancellationState)state!;
                        target.Runtime.Cancel(target.Task);
                    }, new TaskCancellationState(this, task));
                    if (task.IsCompleted)
                        registration.Dispose();
                    else
                        _cancellationRegistrations[task] = registration;
                }
            } catch {
                if (_timers.TryRemove(task, out var failedTimer))
                    ReleaseTimerSlot();
                if (failedTimer is not null)
                    failedTimer.Dispose();
                if (_cancellationRegistrations.TryRemove(task, out var registration))
                    registration.Dispose();
                _activeRoots.TryRemove(task, out _);
                throw;
            }
        }
    }

    /// <summary>
    /// Schedules a VM-owned CTS timer.  Using this runtime rather than the host CTS.CancelAfter
    /// implementation makes timer accounting and VM shutdown deterministic.
    /// </summary>
    public void ScheduleCancellation(VmCancellationState state, int milliseconds) {
        if (milliseconds < Timeout.Infinite)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "millisecondsDelay");

        CancellationTimerRegistration? previous = null;
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            if (state.IsDisposed)
                throw new ObjectDisposedException(nameof(CancellationTokenSource));
            state.DisposeTimer = CancelCancellationTimer;
            state.ScheduleTimer = ScheduleCancellation;
            var current = _cancellationTimers.TryGetValue(state, out var currentRegistration)
                ? currentRegistration : null;
            if (milliseconds == Timeout.Infinite || state.IsCancellationRequested) {
                if (current is not null && RemoveCancellationTimerUnsafe(state, current))
                    previous = current;
            } else {
                var reserved = current is null;
                if (reserved && !TryReserveTimerSlotUnsafe())
                    throw new GuestConcurrencyLimitExceededException(
                        $"未完了の Task.Delay / CancellationTokenSource Timer 共通上限 ({_maxPendingTimers}) に達しました。");

                CancellationTimerRegistration? replacement = null;
                try {
                    replacement = new CancellationTimerRegistration(this, state);
                    replacement.Timer = new Timer(static target => {
                        var registration = (CancellationTimerRegistration)target!;
                        registration.Runtime.FireCancellationTimer(registration);
                    }, replacement, Timeout.Infinite, Timeout.Infinite);
                    // Publish only after Change succeeds.  The timer callback takes the same
                    // lifetime lock before firing, so a zero-delay callback cannot observe a
                    // half-published registration or cancel an older registration during a
                    // replacement.
                    replacement.Timer.Change(milliseconds, Timeout.Infinite);
                    _cancellationTimers[state] = replacement;
                    previous = current;
                } catch {
                    replacement?.Timer?.Dispose();
                    if (reserved)
                        ReleaseTimerSlot();
                    throw;
                }
            }
        }
        previous?.Timer?.Dispose();
    }

    private void CancelCancellationTimer(VmCancellationState state) {
        CancellationTimerRegistration? registration = null;
        lock (_lifetimeGate) {
            if (_cancellationTimers.TryGetValue(state, out var current) &&
                RemoveCancellationTimerUnsafe(state, current))
                registration = current;
        }
        registration?.Timer?.Dispose();
    }

    private void CancelCancellationTimer(CancellationTimerRegistration registration) {
        lock (_lifetimeGate) {
            if (_cancellationTimers.TryGetValue(registration.Source, out var current) &&
                ReferenceEquals(current, registration))
                RemoveCancellationTimerUnsafe(registration.Source, registration);
        }
        registration.Timer?.Dispose();
    }

    private void FireCancellationTimer(CancellationTimerRegistration registration) {
        lock (_lifetimeGate) {
            if (!_cancellationTimers.TryGetValue(registration.Source, out var current) ||
                !ReferenceEquals(current, registration)) {
                registration.Timer?.Dispose();
                return;
            }
            try {
                registration.Source.CancelFromTimer();
            } catch {
                // Cancellation callbacks must not terminate the host timer thread.
            } finally {
                if (_cancellationTimers.TryGetValue(registration.Source, out current) &&
                    ReferenceEquals(current, registration))
                    RemoveCancellationTimerUnsafe(registration.Source, registration);
                registration.Timer?.Dispose();
            }
        }
    }

    private bool RemoveCancellationTimerUnsafe(VmCancellationState state,
        CancellationTimerRegistration registration) {
        if (!_cancellationTimers.TryGetValue(state, out var current) ||
            !ReferenceEquals(current, registration) ||
            !_cancellationTimers.TryRemove(state, out _))
            return false;
        ReleaseTimerSlot();
        return true;
    }

    private bool TryReserveTimerSlotUnsafe() {
        if (_pendingTimerSlots >= _maxPendingTimers)
            return false;
        _pendingTimerSlots++;
        return true;
    }

    private void ReleaseTimerSlot() {
        Interlocked.Decrement(ref _pendingTimerSlots);
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

    public void ScheduleContinuation(VmTaskObject awaited, StackSlot stateMachine, Action<StackSlot> resume,
        StackSlot? contextRoot = null) {
        var state = stateMachine.Kind == StackKind.ByRef && stateMachine.ObjectValue is VmByRef byRef
            ? byRef.Read()
            : stateMachine;
        StackSlot detached;
        if (state.ObjectValue is VmStructValue machine)
            detached = StackSlot.OfValueType(machine.Clone());
        else if (state.ObjectValue is VmObject)
            detached = state;
        else
            throw new InvalidOperationException("async state machine は VM オブジェクトである必要があります。");
        var stateContainer = new[] { detached };
        var detachedRef = StackSlot.OfByRef(new VmByRef(stateContainer, 0));
        var id = Interlocked.Increment(ref _nextContinuationId);
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _continuationRoots[id] = contextRoot is { } root
                ? [StackSlot.OfObject(awaited), detachedRef, root]
                : [StackSlot.OfObject(awaited), detachedRef];
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

    /// <summary>Task.WhenAll / WhenAny が共有する VM task combinator 実行。</summary>
    public void WhenAll(VmTaskObject composite, VmTaskObject[] tasks, Func<StackSlot> result) {
        ValidateTaskCombinatorInputCount(tasks.Length);
        if (tasks.Length == 0) {
            Complete(composite, result());
            return;
        }
        var roots = new StackSlot[tasks.Length + 1];
        roots[0] = StackSlot.OfObject(composite);
        for (var i = 0; i < tasks.Length; i++)
            roots[i + 1] = StackSlot.OfObject(tasks[i]);
        PublishTaskRoots(composite, roots);

        var registrations = new IDisposable[tasks.Length];
        var registrationGate = new object();
        var remaining = tasks.Length;
        void FinalizeWhenAll() {
            if (Interlocked.Decrement(ref remaining) != 0)
                return;
            lock (registrationGate) {
                foreach (var registration in registrations)
                    registration?.Dispose();
            }

            // CLR precedence is fault > cancellation > success.  Inspect every child before
            // materializing a generic result array; reading a failed Task<T>.Result is invalid.
            var firstGuestFault = default(StackSlot);
            Exception? firstHostFault = null;
            var canceled = false;
            foreach (var task in tasks) {
                var snapshot = task.Snapshot();
                if (snapshot.GuestException.Kind != StackKind.Empty) {
                    if (firstGuestFault.Kind == StackKind.Empty)
                        firstGuestFault = snapshot.GuestException;
                } else if (snapshot.HostException is not null) {
                    firstHostFault ??= snapshot.HostException;
                } else if (task.IsCanceled) {
                    canceled = true;
                }
            }

            try {
                if (firstGuestFault.Kind != StackKind.Empty) {
                    CompleteGuestException(composite, firstGuestFault);
                } else if (firstHostFault is not null) {
                    CompleteHostException(composite, firstHostFault);
                } else if (canceled) {
                    composite.SetCanceled();
                    RemoveTaskRoots(composite);
                } else {
                    Complete(composite, result());
                }
            } catch (Exception ex) {
                CompleteHostException(composite, ex);
            }
        }

        for (var i = 0; i < tasks.Length; i++) {
            var registration = tasks[i].RegisterCompletion(FinalizeWhenAll);
            lock (registrationGate) {
                if (Volatile.Read(ref remaining) == 0)
                    registration.Dispose();
                else
                    registrations[i] = registration;
            }
        }
    }

    public void WhenAny(VmTaskObject composite, VmTaskObject[] tasks) {
        try {
            ValidateTaskCombinatorInputCount(tasks.Length);
            if (tasks.Length == 0)
                throw new UnhandledGuestException("System.ArgumentException", "少なくとも 1 つの Task が必要です。");
            if (tasks.Any(static task => task is null))
                throw new UnhandledGuestException("System.ArgumentException", "Task 配列に null 要素があります。");

            var roots = new StackSlot[tasks.Length + 1];
            roots[0] = StackSlot.OfObject(composite);
            for (var i = 0; i < tasks.Length; i++)
                roots[i + 1] = StackSlot.OfObject(tasks[i]);
            PublishTaskRoots(composite, roots);
        } catch {
            // Create() publishes a root before this method can validate quota or arguments.
            // Every exceptional path must undo that publication, including direct callers that
            // bypass the intrinsic overload's pre-validation.
            RemoveTaskRoots(composite);
            throw;
        }

        var registrations = new IDisposable[tasks.Length];
        var registrationGate = new object();
        var winner = -1;
        void CompleteWinner(int index) {
            if (Interlocked.CompareExchange(ref winner, index, -1) != -1)
                return;
            lock (registrationGate) {
                foreach (var registration in registrations)
                    registration?.Dispose();
            }
            Complete(composite, StackSlot.OfObject(tasks[index]));
        }

        for (var i = 0; i < tasks.Length; i++) {
            var index = i;
            var registration = tasks[i].RegisterCompletion(() => CompleteWinner(index));
            lock (registrationGate) {
                if (Volatile.Read(ref winner) != -1)
                    registration.Dispose();
                else
                    registrations[i] = registration;
            }
        }
    }

    private void PublishTaskRoots(VmTaskObject task, StackSlot[] roots) {
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _activeRoots[task] = roots;
        }
    }

    internal void Post(VmObject context, VmDelegate callback, StackSlot state, Action invoke) {
        var roots = new[] {
            StackSlot.OfObject(context), StackSlot.OfObject(callback), state,
        };
        StartWorker(null, roots, () => {
            _shutdownToken.ThrowIfCancellationRequested();
            invoke();
            return default;
        }, onSuccess: null, onError: _ => { });
    }

    public bool WaitAll(VmTaskObject[] tasks, int millisecondsTimeout, CancellationToken cancellationToken) {
        ValidateTaskCombinatorInputCount(tasks.Length);
        if (tasks.Length == 0)
            return true;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var task in tasks) {
            var remaining = millisecondsTimeout == Timeout.Infinite
                ? Timeout.Infinite
                : Math.Max(0, millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds);
            try {
                if (!task.Wait(remaining, cancellationToken))
                    return false;
            } catch (OperationCanceledException) {
                throw new UnhandledGuestException("System.OperationCanceledException", null);
            }
        }
        return true;
    }

    internal void ValidateTaskCombinatorInputCount(int count) {
        if (count > _maxTaskCombinatorInputs)
            throw new GuestConcurrencyLimitExceededException(
                $"Task combinator の入力数上限 ({_maxTaskCombinatorInputs}) を超えました。");
    }

    private void Cancel(VmTaskObject task) {
        task.SetCanceled();
        RemoveTaskRoots(task);
    }

    private sealed record TaskCancellationState(GuestTaskRuntime Runtime, VmTaskObject Task);

    private sealed class CancellationTimerRegistration(GuestTaskRuntime runtime, VmCancellationState source) {
        public GuestTaskRuntime Runtime { get; } = runtime;
        public VmCancellationState Source { get; } = source;
        public Timer? Timer { get; set; }
    }

    /// <summary>
    /// guest awaiter が自前で continuation を保持する場合の登録。Task のイベントを
    /// 待つ代わりに、渡された host callback が呼ばれた時点で guest worker を起動する。
    /// callback・awaiter・state machine は callback が遅延実行されても GC から消えないよう
    /// continuation root として保持する。
    /// </summary>
    internal ExternalContinuation RegisterExternalContinuation(StackSlot stateMachine,
        StackSlot[] extraRoots, VmDelegate callback, Action<StackSlot> resume) {
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
        var roots = new StackSlot[extraRoots.Length + 2];
        Array.Copy(extraRoots, roots, extraRoots.Length);
        roots[^2] = detachedRef;
        roots[^1] = StackSlot.OfObject(callback);
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _continuationRoots[id] = roots;
        }
        return new ExternalContinuation(this, id, roots, detachedRef, resume);
    }

    internal sealed class ExternalContinuation {
        private readonly GuestTaskRuntime _owner;
        private readonly long _id;
        private readonly StackSlot[] _roots;
        private readonly StackSlot _stateMachine;
        private readonly Action<StackSlot> _resume;
        private int _signalled;

        public ExternalContinuation(GuestTaskRuntime owner, long id, StackSlot[] roots,
            StackSlot stateMachine, Action<StackSlot> resume) {
            _owner = owner;
            _id = id;
            _roots = roots;
            _stateMachine = stateMachine;
            _resume = resume;
        }

        public void Signal() {
            if (Interlocked.Exchange(ref _signalled, 1) != 0)
                return;
            try {
                _owner.StartWorker(null, _roots, () => {
                    _owner._shutdownToken.ThrowIfCancellationRequested();
                    _resume(_stateMachine);
                    return default;
                }, onSuccess: null, onError: _ => { },
                    onFinished: () => _owner._continuationRoots.TryRemove(_id, out _));
            } catch {
                _owner._continuationRoots.TryRemove(_id, out _);
                throw;
            }
        }

        public void Cancel() {
            if (Interlocked.Exchange(ref _signalled, 1) == 0)
                _owner._continuationRoots.TryRemove(_id, out _);
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
        try {
            return sentinel.Get();
        } catch {
            // Lazy caches its factory exception. Do not leave that failed entry in the
            // cache: a later call must be able to retry after a transient quota failure.
            lock (_lifetimeGate) {
                if (_completedSentinels.TryGetValue(type, out var current) &&
                    ReferenceEquals(current, sentinel))
                    _completedSentinels.Remove(type);
            }
            throw;
        }
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
    public void ScheduleCallback(VmTaskObject awaited, StackSlot callback, Action<StackSlot> invoke,
        StackSlot? contextRoot = null) {
        var value = callback.Kind == StackKind.ByRef && callback.ObjectValue is VmByRef byRef
            ? byRef.Read()
            : callback;
        if (value.ObjectValue is not VmDelegate)
            throw new InvalidOperationException("awaiter continuation は guest delegate である必要があります。");
        var id = Interlocked.Increment(ref _nextContinuationId);
        lock (_lifetimeGate) {
            ThrowIfDisposed();
            _continuationRoots[id] = contextRoot is { } root
                ? [StackSlot.OfObject(awaited), value, root]
                : [StackSlot.OfObject(awaited), value];
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
        Timer? timer = null;
        lock (_lifetimeGate) {
            if (_timers.TryRemove(task, out timer))
                ReleaseTimerSlot();
        }
        if (timer is not null)
            timer.Dispose();
        if (_cancellationRegistrations.TryRemove(task, out var registration))
            registration.Dispose();
    }

    public void Dispose() {
        Timer[] timers;
        CancellationTimerRegistration[] cancellationTimers;
        CancellationTokenRegistration[] cancellationRegistrations;
        Thread[] workers;
        VmTaskObject[] tasks;
        lock (_lifetimeGate) {
            if (_disposed)
                return;
            _disposed = true;
            _completedSentinels.Clear();
            timers = [.. _timers.Values];
            _timers.Clear();
            cancellationRegistrations = [.. _cancellationRegistrations.Values];
            _cancellationRegistrations.Clear();
            cancellationTimers = [.. _cancellationTimers.Values];
            _cancellationTimers.Clear();
            _pendingTimerSlots = 0;
            workers = [.. _workers.Values];
            tasks = [.. _activeRoots.Keys];
        }

        foreach (var timer in timers)
            timer.Dispose();
        foreach (var registration in cancellationTimers)
            registration.Timer?.Dispose();
        foreach (var registration in cancellationRegistrations)
            registration.Dispose();
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
                // Reserve holds the heap gate while the root is published and the
                // reservation is committed. This makes GC visibility and accounting
                // one transaction, while still allowing the root publication to be
                // rolled back if the allocation fails.
                using var reservation = heap.Reserve(ObjectModel.EstimateSize(task));
                Volatile.Write(ref _root, task);
                try {
                    return reservation.Commit(task);
                } catch {
                    Volatile.Write(ref _root, null);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public VmTaskObject? Root => Volatile.Read(ref _root);

        public VmTaskObject Get() {
            try {
                return _task.Value;
            } catch {
                // The factory normally clears this itself, but keep the cache entry
                // safe if a future factory change fails after publication.
                Volatile.Write(ref _root, null);
                throw;
            }
        }
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
