namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Coordinates concurrent guest execution with stop-the-world collection. Guest instructions hold a
/// shared lease, so independent VM threads can execute at the same time. Collection takes an exclusive
/// lease and therefore only observes frames between instructions.
/// </summary>
internal sealed class VmExecutionCoordinator {
    private readonly ReaderWriterLockSlim _world = new(LockRecursionPolicy.SupportsRecursion);
    [ThreadStatic] private static Dictionary<VmExecutionCoordinator, int>? s_instructionDepth;
    [ThreadStatic] private static VmExecutionCoordinator? s_fastInstructionOwner;
    [ThreadStatic] private static int s_fastInstructionDepth;

    public bool IsExecutingOnCurrentThread => _world.IsReadLockHeld;
    public bool IsInsideGuestInstruction => ReferenceEquals(s_fastInstructionOwner, this)
        ? s_fastInstructionDepth > 0
        : s_instructionDepth?.GetValueOrDefault(this) > 0;

    public IDisposable EnterRead() {
        _world.EnterReadLock();
        return new ReadLease(_world, null);
    }

    /// <summary>
    /// Hold one read lease for a bounded run of guest instructions.  A frame
    /// releases this lease at each safepoint interval, so stop-the-world GC and
    /// host disposal still get a bounded wait while the common instruction path
    /// avoids a ReaderWriterLockSlim operation per instruction.
    /// </summary>
    public InstructionBatchLease EnterInstructionBatch() {
        _world.EnterReadLock();
        return new InstructionBatchLease(_world);
    }

    // This is deliberately a value lease.  Guest instructions are the hottest
    // synchronization path; returning IDisposable here used to allocate a
    // ReadLease and a closure for every instruction.
    public InstructionLease EnterInstruction() {
        _world.EnterReadLock();
        try {
            EnterInstructionDepth();
            return new InstructionLease(this, ownsReadLock: true);
        } catch {
            _world.ExitReadLock();
            throw;
        }
    }

    /// <summary>Enter an instruction while the caller owns an instruction batch read lease.</summary>
    public InstructionLease EnterInstructionInBatch() {
        EnterInstructionDepth();
        return new InstructionLease(this, ownsReadLock: false);
    }

    private void ExitInstruction(bool ownsReadLock) {
        if (ReferenceEquals(s_fastInstructionOwner, this)) {
            if (--s_fastInstructionDepth == 0)
                s_fastInstructionOwner = null;
        } else {
            var depths = s_instructionDepth
                ?? throw new InvalidOperationException("ゲスト命令の実行深度がありません。");
            var remaining = depths[this] - 1;
            if (remaining == 0)
                depths.Remove(this);
            else
                depths[this] = remaining;
        }
        if (ownsReadLock)
            _world.ExitReadLock();
    }

    private void EnterInstructionDepth() {
        if (ReferenceEquals(s_fastInstructionOwner, this)) {
            s_fastInstructionDepth++;
            return;
        }
        // The common case is one interpreter executing on a host thread.  Keep
        // that depth in ThreadStatic fields; the dictionary remains the safe
        // fallback when multiple VMs are nested on the same thread.
        if (s_fastInstructionOwner is null &&
            (s_instructionDepth is null || !s_instructionDepth.ContainsKey(this))) {
            s_fastInstructionOwner = this;
            s_fastInstructionDepth = 1;
            return;
        }
        var depths = s_instructionDepth ??= new Dictionary<VmExecutionCoordinator, int>();
        depths[this] = depths.GetValueOrDefault(this) + 1;
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
        var instructionDepth = ReferenceEquals(s_fastInstructionOwner, this)
            ? s_fastInstructionDepth
            : s_instructionDepth?.GetValueOrDefault(this) ?? 0;
        for (var i = 0; i < readDepth; i++)
            _world.ExitReadLock();
        if (instructionDepth > 0) {
            if (ReferenceEquals(s_fastInstructionOwner, this)) {
                s_fastInstructionOwner = null;
                s_fastInstructionDepth = 0;
            } else
                s_instructionDepth!.Remove(this);
        }
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

    internal struct InstructionBatchLease : IDisposable {
        private ReaderWriterLockSlim? _world;

        internal InstructionBatchLease(ReaderWriterLockSlim world) => _world = world;

        public void Dispose() => Interlocked.Exchange(ref _world, null)?.ExitReadLock();
    }

    internal struct InstructionLease : IDisposable {
        private VmExecutionCoordinator? _owner;
        private readonly bool _ownsReadLock;

        internal InstructionLease(VmExecutionCoordinator owner, bool ownsReadLock) {
            _owner = owner;
            _ownsReadLock = ownsReadLock;
        }

        public void Dispose() {
            var owner = _owner;
            _owner = null;
            owner?.ExitInstruction(_ownsReadLock);
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
                if (s_fastInstructionOwner is null) {
                    s_fastInstructionOwner = owner;
                    s_fastInstructionDepth = instructionDepth;
                } else {
                    var depths = s_instructionDepth ??= new Dictionary<VmExecutionCoordinator, int>();
                    depths[owner] = depths.GetValueOrDefault(owner) + instructionDepth;
                }
            }
        }
    }

    private sealed class EmptyLease : IDisposable {
        public static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }
}
