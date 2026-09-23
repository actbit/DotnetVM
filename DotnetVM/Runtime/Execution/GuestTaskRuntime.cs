using System.Collections.Concurrent;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>ホスト worker 上で動く guest Task と async state machine 継続の管理。</summary>
internal sealed class GuestTaskRuntime(int maxWorkers) {
    private readonly ConcurrentDictionary<VmTaskObject, StackSlot[]> _activeRoots = new();
    private readonly ConcurrentDictionary<VmTaskObject, Timer> _timers = new();
    private readonly ConcurrentDictionary<long, StackSlot[]> _continuationRoots = new();
    private int _activeWorkers;
    private long _nextContinuationId;

    public VmTaskObject Create(VmType type, bool completed = false, StackSlot result = default) {
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
        _activeRoots.TryRemove(task, out _);
        if (_timers.TryRemove(task, out var timer))
            timer.Dispose();
    }

    public void CompleteGuestException(VmTaskObject task, StackSlot exception) {
        task.SetGuestException(exception);
        _activeRoots.TryRemove(task, out _);
        if (_timers.TryRemove(task, out var timer))
            timer.Dispose();
    }

    public void CompleteHostException(VmTaskObject task, Exception exception) {
        task.SetHostException(exception);
        _activeRoots.TryRemove(task, out _);
        if (_timers.TryRemove(task, out var timer))
            timer.Dispose();
    }

    public void Delay(VmTaskObject task, int milliseconds) {
        if (milliseconds == 0) {
            Complete(task);
            return;
        }
        _activeRoots[task] = [StackSlot.OfObject(task)];
        var timer = new Timer(_ => Complete(task), null, Timeout.Infinite, Timeout.Infinite);
        _timers[task] = timer;
        timer.Change(milliseconds, Timeout.Infinite);
    }

    public void Wait(VmTaskObject task, int millisecondsTimeout) => task.Wait(millisecondsTimeout);

    public void Run(VmTaskObject task, StackSlot[] roots, Func<StackSlot> work) {
        StartWorker(task, roots, () => Complete(task, work()), ex => CompleteHostException(task, ex));
    }

    public void ScheduleContinuation(VmTaskObject awaited, StackSlot stateMachine, Action<StackSlot> resume) {
        var state = stateMachine.Kind == StackKind.ByRef && stateMachine.ObjectValue is VmByRef byRef
            ? byRef.Read()
            : stateMachine;
        if (state.Kind != StackKind.ValueType || state.ObjectValue is not VmStructValue machine)
            throw new InvalidOperationException("async state machine は値型の参照である必要があります。");
        var detached = StackSlot.OfValueType(machine.Clone());
        var stateContainer = new[] { detached };
        var detachedRef = StackSlot.OfByRef(new VmByRef(stateContainer, 0));
        var id = Interlocked.Increment(ref _nextContinuationId);
        _continuationRoots[id] = [StackSlot.OfObject(awaited), detachedRef];
        StartWorker(null, [StackSlot.OfObject(awaited), detachedRef], () => {
            awaited.Wait();
            resume(detachedRef);
            return default;
        }, _ => { }, () => _continuationRoots.TryRemove(id, out _));
    }

    private void StartWorker(VmTaskObject? trackedTask, StackSlot[] roots, Action work, Action<Exception> onError,
        Action? onFinished = null) =>
        StartWorker(trackedTask, roots, () => { work(); return default; }, onError, onFinished);

    private void StartWorker(VmTaskObject? trackedTask, StackSlot[] roots, Func<StackSlot> work, Action<Exception> onError,
        Action? onFinished = null) {
        if (Interlocked.Increment(ref _activeWorkers) > maxWorkers) {
            Interlocked.Decrement(ref _activeWorkers);
            onFinished?.Invoke();
            var error = new InvalidOperationException($"guest Task worker 上限 {maxWorkers} に達しました。");
            if (trackedTask is not null)
                onError(error);
            else
                throw error;
            return;
        }
        if (trackedTask is not null)
            _activeRoots[trackedTask] = roots;
        var thread = new Thread(() => {
            try {
                work();
            } catch (Exception ex) {
                if (trackedTask is not null)
                    onError(ex);
            } finally {
                onFinished?.Invoke();
                Interlocked.Decrement(ref _activeWorkers);
            }
        }) { IsBackground = true, Name = "DotnetVM guest Task" };
        try {
            thread.Start();
        } catch (Exception ex) {
            onFinished?.Invoke();
            Interlocked.Decrement(ref _activeWorkers);
            if (trackedTask is not null)
                onError(ex);
            else
                throw;
        }
    }
}
