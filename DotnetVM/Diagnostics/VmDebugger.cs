using DotnetVM.IL;
using DotnetVM.Policy;

namespace DotnetVM.Diagnostics;

/// <summary>デバッガが停止した理由。</summary>
public enum DebuggerStopReason {
    Breakpoint,
    PauseRequested,
    Step,
}

/// <summary>IL 命令ブレークポイント。</summary>
public sealed record DebuggerBreakpoint(
    int Id,
    string? AssemblyName,
    string TypeFullName,
    string MethodName,
    int IlOffset) {
    internal bool Matches(ExecutionTraceEvent trace) =>
        (AssemblyName is null || AssemblyName == trace.Frame.AssemblyName) &&
        TypeFullName == trace.Frame.TypeFullName &&
        MethodName == trace.Frame.MethodName &&
        IlOffset == trace.IlOffset;
}

/// <summary>デバッガの停止位置。</summary>
public sealed record DebuggerStop(
    ExecutionTraceEvent Instruction,
    DebuggerStopReason Reason,
    int? BreakpointId);

/// <summary>デバッガ停止通知の引数。</summary>
public sealed class DebuggerStoppedEventArgs(DebuggerStop stop) : EventArgs {
    public DebuggerStop Stop { get; } = stop;
}

/// <summary>
/// VM の命令境界デバッガ。
/// ブレークポイント、継続、ステップ実行を提供する。停止中はゲストを実行している
/// ホストスレッドが命令境界で待機し、別スレッドから Continue/Step* を呼び出せる。
/// </summary>
public sealed class VmDebugger : IDisposable {
    private readonly object _gate = new();
    private readonly List<DebuggerBreakpoint> _breakpoints = [];
    private readonly int _maxBreakpoints;
    private int _nextBreakpointId;
    private bool _pauseRequested;
    private bool _paused;
    private bool _disposed;
    private int _pauseThreadId;
    private DebuggerStop? _currentStop;
    private StepPlan _stepPlan;
    private int _active;

    public VmDebugger(int maxBreakpoints = 4096) {
        if (maxBreakpoints < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBreakpoints));
        _maxBreakpoints = maxBreakpoints;
    }

    private enum StepKind {
        None,
        Into,
        Over,
        Out,
    }

    private readonly record struct StepPlan(StepKind Kind, long Sequence, int Depth, int ThreadId);

    /// <summary>命令境界で停止したときに発生する。</summary>
    public event EventHandler<DebuggerStoppedEventArgs>? Stopped;

    /// <summary><see cref="Stopped"/> の別名。デバッガ UI で使いやすい通知名。</summary>
    public event EventHandler<DebuggerStoppedEventArgs>? BreakpointHit;

    /// <summary>現在登録されているブレークポイント。</summary>
    public IReadOnlyList<DebuggerBreakpoint> Breakpoints {
        get { lock (_gate) return _breakpoints.ToArray(); }
    }

    /// <summary>停止中か。</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>命令フックを有効にする必要があるか。</summary>
    internal bool IsActive {
        get => Volatile.Read(ref _active) != 0;
    }

    /// <summary>現在の停止位置。停止していない場合は null。</summary>
    public DebuggerStop? CurrentStop { get { lock (_gate) return _currentStop; } }

    /// <summary>
    /// 指定した型・メソッドの IL オフセットにブレークポイントを設定する。
    /// assemblyName を null にすると、アセンブリ名を限定しない。
    /// </summary>
    public int AddBreakpoint(string typeFullName, string methodName, int ilOffset, string? assemblyName = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        if (ilOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(ilOffset));

        lock (_gate) {
            ThrowIfDisposed();
            if (_breakpoints.Count >= _maxBreakpoints)
                throw new OperationNotAllowedException(
                    $"デバッガのブレークポイント上限 {_maxBreakpoints:N0} を超えました。");
            var id = ++_nextBreakpointId;
            _breakpoints.Add(new DebuggerBreakpoint(id, assemblyName, typeFullName, methodName, ilOffset));
            RefreshActive();
            Monitor.PulseAll(_gate);
            return id;
        }
    }

    /// <summary><see cref="AddBreakpoint"/> の別名。</summary>
    public int SetBreakpoint(string typeFullName, string methodName, int ilOffset, string? assemblyName = null) =>
        AddBreakpoint(typeFullName, methodName, ilOffset, assemblyName);

    /// <summary>ブレークポイントを削除する。</summary>
    public bool RemoveBreakpoint(int id) {
        lock (_gate) {
            ThrowIfDisposed();
            var removed = _breakpoints.RemoveAll(b => b.Id == id) != 0;
            RefreshActive();
            Monitor.PulseAll(_gate);
            return removed;
        }
    }

    /// <summary>すべてのブレークポイントを削除する。</summary>
    public void ClearBreakpoints() {
        lock (_gate) {
            ThrowIfDisposed();
            _breakpoints.Clear();
            RefreshActive();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>次の命令境界で停止するよう要求する。</summary>
    public void Pause() {
        lock (_gate) {
            ThrowIfDisposed();
            _pauseRequested = true;
            RefreshActive();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>停止状態から実行を再開する。</summary>
    public void Continue() {
        lock (_gate) {
            ThrowIfDisposed();
            _pauseRequested = false;
            _stepPlan = default;
            _currentStop = null;
            _paused = false;
            _pauseThreadId = 0;
            RefreshActive();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>次の命令で停止する (ステップイン)。</summary>
    public void StepInto() => ResumeForStep(StepKind.Into);

    /// <summary>現在のフレームから戻るか、同じ深さの次の命令で停止する (ステップオーバー)。</summary>
    public void StepOver() => ResumeForStep(StepKind.Over);

    /// <summary>現在のフレームより浅いフレームに戻った命令で停止する。</summary>
    public void StepOut() => ResumeForStep(StepKind.Out);

    private void ResumeForStep(StepKind kind) {
        lock (_gate) {
            ThrowIfDisposed();
            if (!_paused || _currentStop is null)
                throw new InvalidOperationException("Step* はデバッガが停止中の場合にのみ呼び出せます。");
            _stepPlan = new StepPlan(kind, _currentStop.Instruction.Sequence,
                _currentStop.Instruction.Depth, _currentStop.Instruction.ManagedThreadId);
            _pauseRequested = false;
            _currentStop = null;
            _paused = false;
            _pauseThreadId = 0;
            RefreshActive();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// 停止通知を待つ。同期的な VM.Invoke を別スレッドからデバッグする場合に使う。
    /// </summary>
    public DebuggerStop WaitForBreak(CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(static state => {
            var debugger = (VmDebugger)state!;
            lock (debugger._gate)
                Monitor.PulseAll(debugger._gate);
        }, this);
        lock (_gate) {
            ThrowIfDisposed();
            while (!_paused) {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate);
                ThrowIfDisposed();
            }
            return _currentStop!;
        }
    }

    /// <summary>Interpreter が命令境界で呼び出す内部フック。</summary>
    internal void Observe(ExecutionTraceEvent trace) {
        EventHandler<DebuggerStoppedEventArgs>? stopped;
        EventHandler<DebuggerStoppedEventArgs>? breakpointHit;
        DebuggerStoppedEventArgs? args;
        var threadId = Environment.CurrentManagedThreadId;

        lock (_gate) {
            while (_paused && _pauseThreadId != threadId && !_disposed)
                Monitor.Wait(_gate);
            if (_disposed)
                return;

            var breakpoint = _breakpoints.FirstOrDefault(b => b.Matches(trace));
            var reason = breakpoint is not null
                ? DebuggerStopReason.Breakpoint
                : GetStepReason(trace);
            if (reason is null && !_pauseRequested)
                return;

            reason ??= DebuggerStopReason.PauseRequested;
            _pauseRequested = false;
            _stepPlan = default;
            _paused = true;
            _pauseThreadId = threadId;
            _currentStop = new DebuggerStop(trace, reason.Value, breakpoint?.Id);
            args = new DebuggerStoppedEventArgs(_currentStop);
            stopped = Stopped;
            breakpointHit = breakpoint is not null ? BreakpointHit : null;
        }

        // Do not invoke user code while holding the debugger lock. A handler may
        // inspect CurrentStop and immediately call Continue.
        Exception? callbackFailure = null;
        try {
            stopped?.Invoke(this, args);
            breakpointHit?.Invoke(this, args);
        } catch (Exception exception) {
            callbackFailure = exception;
        }

        if (callbackFailure is not null) {
            // An event handler must not leave the guest permanently suspended.
            lock (_gate) {
                if (_pauseThreadId == threadId) {
                    _paused = false;
                    _pauseThreadId = 0;
                    _currentStop = null;
                }
                RefreshActive();
                Monitor.PulseAll(_gate);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
        }

        lock (_gate) {
            while (_paused && _pauseThreadId == threadId && !_disposed)
                Monitor.Wait(_gate);
        }
    }

    private DebuggerStopReason? GetStepReason(ExecutionTraceEvent trace) {
        if (_stepPlan.Kind == StepKind.None || trace.Sequence <= _stepPlan.Sequence ||
            trace.ManagedThreadId != _stepPlan.ThreadId)
            return null;
        return _stepPlan.Kind switch {
            StepKind.Into => DebuggerStopReason.Step,
            StepKind.Over when trace.Depth <= _stepPlan.Depth => DebuggerStopReason.Step,
            StepKind.Out when trace.Depth < _stepPlan.Depth ||
                trace.Depth == _stepPlan.Depth && trace.Op == ILOp.Ret => DebuggerStopReason.Step,
            _ => null,
        };
    }

    /// <summary>フレーム終了時に、次のフレームへ持ち越せないステップ計画を破棄する。</summary>
    internal void FrameExited(int threadId, int depth) {
        lock (_gate) {
            if (_stepPlan.Kind != StepKind.None && _stepPlan.ThreadId == threadId &&
                _stepPlan.Depth >= depth) {
                _stepPlan = default;
                RefreshActive();
            }
        }
    }

    private void RefreshActive() {
        var active = !_disposed && (_breakpoints.Count != 0 || _pauseRequested || _paused ||
            _stepPlan.Kind != StepKind.None);
        Volatile.Write(ref _active, active ? 1 : 0);
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed)
                return;
            _disposed = true;
            _paused = false;
            _pauseRequested = false;
            _stepPlan = default;
            _currentStop = null;
            Volatile.Write(ref _active, 0);
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowIfDisposed() {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VmDebugger));
    }
}
