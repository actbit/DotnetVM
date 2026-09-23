namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Coordinates concurrent guest execution with stop-the-world collection. Guest instructions hold a
/// shared lease, so independent VM threads can execute at the same time. Collection takes an exclusive
/// lease and therefore only observes frames between instructions.
/// </summary>
internal sealed class VmExecutionCoordinator {
    private readonly ReaderWriterLockSlim _world = new(LockRecursionPolicy.SupportsRecursion);
    [ThreadStatic] private static Dictionary<VmExecutionCoordinator, int>? s_instructionDepth;

    public bool IsExecutingOnCurrentThread => _world.IsReadLockHeld;
    public bool IsInsideGuestInstruction => s_instructionDepth?.GetValueOrDefault(this) > 0;

    public IDisposable EnterRead() {
        _world.EnterReadLock();
        return new ReadLease(_world, null);
    }

    public IDisposable EnterInstruction() {
        _world.EnterReadLock();
        var depths = s_instructionDepth ??= new Dictionary<VmExecutionCoordinator, int>();
        depths[this] = depths.GetValueOrDefault(this) + 1;
        return new ReadLease(_world, () => {
            var remaining = depths[this] - 1;
            if (remaining == 0)
                depths.Remove(this);
            else
                depths[this] = remaining;
        });
    }

    public IDisposable StopTheWorld() {
        _world.EnterWriteLock();
        return new WriteLease(_world);
    }

    /// <summary>Safepoint-only collection. Releases any enclosing host-operation read leases while
    /// holding no guest instruction, then reacquires them after collection.</summary>
    public IDisposable StopTheWorldAtBoundary() {
        if (IsInsideGuestInstruction)
            return EmptyLease.Instance;
        var readDepth = _world.RecursiveReadCount;
        for (var i = 0; i < readDepth; i++)
            _world.ExitReadLock();
        _world.EnterWriteLock();
        return new BoundaryLease(_world, readDepth);
    }

    /// <summary>Temporarily yields all execution leases around a blocking host wait.</summary>
    public IDisposable SuspendExecution() {
        var readDepth = _world.RecursiveReadCount;
        var instructionDepth = s_instructionDepth?.GetValueOrDefault(this) ?? 0;
        for (var i = 0; i < readDepth; i++)
            _world.ExitReadLock();
        if (instructionDepth > 0)
            s_instructionDepth!.Remove(this);
        return new ResumeLease(_world, readDepth, instructionDepth, this);
    }

    public void Dispose() => _world.Dispose();

    private sealed class ReadLease(ReaderWriterLockSlim world, Action? onExit) : IDisposable {
        private ReaderWriterLockSlim? _world = world;

        public void Dispose() {
            var current = Interlocked.Exchange(ref _world, null);
            if (current is null)
                return;
            onExit?.Invoke();
            current.ExitReadLock();
        }
    }

    private sealed class WriteLease(ReaderWriterLockSlim world) : IDisposable {
        private ReaderWriterLockSlim? _world = world;
        public void Dispose() => Interlocked.Exchange(ref _world, null)?.ExitWriteLock();
    }

    private sealed class BoundaryLease(ReaderWriterLockSlim world, int readDepth) : IDisposable {
        private ReaderWriterLockSlim? _world = world;
        public void Dispose() {
            var current = Interlocked.Exchange(ref _world, null);
            if (current is null)
                return;
            current.ExitWriteLock();
            for (var i = 0; i < readDepth; i++)
                current.EnterReadLock();
        }
    }

    private sealed class ResumeLease(ReaderWriterLockSlim world, int readDepth, int instructionDepth,
        VmExecutionCoordinator owner) : IDisposable {
        private ReaderWriterLockSlim? _world = world;
        public void Dispose() {
            var current = Interlocked.Exchange(ref _world, null);
            if (current is null)
                return;
            for (var i = 0; i < readDepth; i++)
                current.EnterReadLock();
            if (instructionDepth > 0) {
                var depths = s_instructionDepth ??= new Dictionary<VmExecutionCoordinator, int>();
                depths[owner] = depths.GetValueOrDefault(owner) + instructionDepth;
            }
        }
    }

    private sealed class EmptyLease : IDisposable {
        public static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }
}
