using System.Runtime.CompilerServices;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Execution;

/// <summary>Host-backed guest thread scheduler. VM objects remain guest-owned; only execution uses CLR threads.</summary>
internal sealed class GuestThreadRuntime(int maxThreads) {
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<VmObject, GuestThread> _threads = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, GuestThread> _active = new();
    private int _nextId;

    public void Configure(VmObject threadObject, VmDelegate startDelegate, bool parameterized) {
        ArgumentNullException.ThrowIfNull(threadObject);
        ArgumentNullException.ThrowIfNull(startDelegate);
        lock (_gate) {
            if (_threads.TryGetValue(threadObject, out var existing)) {
                existing.SetStartDelegate(startDelegate, parameterized);
                return;
            }
            var thread = new GuestThread(threadObject, Interlocked.Increment(ref _nextId), startDelegate, parameterized);
            _threads.Add(threadObject, thread);
        }
    }

    public bool Start(VmObject threadObject, StackSlot state, Action<VmDelegate, StackSlot, bool> runDelegate, bool passState) {
        var thread = Get(threadObject);
        lock (_gate) {
            if (thread.Started)
                throw new InvalidOperationException("Thread は既に開始されています。");
            if (passState && !thread.Parameterized)
                throw new InvalidOperationException("ThreadStart で作成されたスレッドに state 引数は渡せません。");
            if (_active.Count >= maxThreads)
                throw new InvalidOperationException($"VM のゲストスレッド上限 ({maxThreads}) に達しました。");
            thread.Started = true;
            thread.HostThread = new Thread(() => {
                try {
                    runDelegate(thread.StartDelegate,
                        thread.Parameterized ? (passState ? state : StackSlot.OfObject(null)) : default,
                        thread.Parameterized);
                } catch (Exception ex) {
                    // CLR の unhandled background-thread exception はプロセスを終了させるため、
                    // ゲストスレッド内の例外は Thread.Join ではなくスレッド状態に保存する。
                    thread.UnhandledException = ex;
                } finally {
                    thread.Completed.Set();
                    _active.TryRemove(thread.Id, out _);
                }
            }) {
                IsBackground = true,
                Name = $"DotnetVM guest thread {thread.Id}",
            };
            _active[thread.Id] = thread;
            thread.HostThread.Start();
        }
        return true;
    }

    public bool Join(VmObject threadObject, int millisecondsTimeout) {
        var thread = Get(threadObject);
        if (!thread.Started)
            throw new InvalidOperationException("開始されていない Thread は Join できません。");
        return thread.Completed.Wait(millisecondsTimeout);
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

    private GuestThread Get(VmObject threadObject) {
        lock (_gate)
            return _threads.TryGetValue(threadObject, out var thread)
                ? thread
                : throw new InvalidOperationException("Thread が VM のスレッド管理表に登録されていません。");
    }

    private sealed class GuestThread(VmObject threadObject, int id, VmDelegate startDelegate, bool parameterized) {
        public readonly VmObject ThreadObject = threadObject;
        public readonly int Id = id;
        public readonly ManualResetEventSlim Completed = new(false);
        public VmDelegate StartDelegate = startDelegate;
        public bool Parameterized = parameterized;
        public bool Started;
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
