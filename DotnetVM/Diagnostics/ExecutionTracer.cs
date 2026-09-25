namespace DotnetVM.Diagnostics;

/// <summary>実行されたフレーム 1 件分の記録。</summary>
public readonly record struct ExecutionFrame(string AssemblyName, string TypeFullName, string MethodName) {
    public override string ToString() => $"{AssemblyName}!{TypeFullName}::{MethodName}";
}

/// <summary>
/// 実行トレース: インタプリタが実行したフレームと IL 命令を記録する。
/// CoreLib の managed IL が実際に VM で走ったことの証明 (Legacy intrinsic 委譲との区別)、
/// ホットメソッド検出 (M8 JIT) やデバッグ用トレース (M9) の基盤。
/// 記録は <see cref="Enabled"/> が true の間のみ行われる (常時記録はゲストの長時間実行で
/// 無制限に増えるため、ホストが明示的に有効化する)。
/// </summary>
public sealed class ExecutionTracer {
    private readonly List<ExecutionFrame> _frames = [];
    private readonly List<ExecutionTraceEvent> _events = [];
    private readonly object _gate = new();
    private ExecutionTraceOptions _options = new();
    private long _droppedEventCount;
    private long _sequence;
    private int _capturesInstructions;

    /// <summary>記録済みフレーム (記録順)。読み取り専用ビュー。</summary>
    public IReadOnlyList<ExecutionFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    /// <summary>記録済みの命令イベント (記録順)。最大数は Start の設定に従う。</summary>
    public IReadOnlyList<ExecutionTraceEvent> Events { get { lock (_gate) return _events.ToArray(); } }

    /// <summary>Events の上限超過により保存されなかったイベント数。</summary>
    public long DroppedEventCount { get { lock (_gate) return _droppedEventCount; } }

    /// <summary>命令イベントを記録中か。JIT は命令の可観測性を保つため自動的に迂回される。</summary>
    internal bool CapturesInstructions {
        get => Volatile.Read(ref _capturesInstructions) != 0;
    }

    /// <summary>記録の有効化状態。Start() で true になり Stop() で false になる。</summary>
    public bool Enabled { get { lock (_gate) return _enabled; } }
    private bool _enabled;

    /// <summary>記録を開始する (既存の記録はクリア)。</summary>
    public void Start() => Start(null);

    /// <summary>記録を開始する (既存の記録はクリア)。</summary>
    public void Start(ExecutionTraceOptions? options) {
        options ??= new ExecutionTraceOptions();
        options.Validate();
        lock (_gate) {
            _frames.Clear();
            _events.Clear();
            _droppedEventCount = 0;
            _sequence = 0;
            _options = options;
            _enabled = true;
            Volatile.Write(ref _capturesInstructions, options.CaptureInstructions ? 1 : 0);
        }
    }

    /// <summary>記録を停止する (既存の記録は保持)。</summary>
    public void Stop() {
        lock (_gate) {
            _enabled = false;
            Volatile.Write(ref _capturesInstructions, 0);
        }
    }

    /// <summary>指定アセンブリの指定メソッドが実行されたか (IL 実行の証明に使う)。</summary>
    public bool ContainsFrame(string assemblyName, string typeFullName, string methodName) {
        lock (_gate)
            return _frames.Any(f => f.AssemblyName == assemblyName &&
                                    f.TypeFullName == typeFullName && f.MethodName == methodName);
    }

    /// <summary>Interpreter がフレーム開始時に呼ぶ (internal: VM 本体からの記録のみ)。</summary>
    internal void Record(string assemblyName, string typeFullName, string methodName) {
        lock (_gate) {
            if (_enabled)
                _frames.Add(new ExecutionFrame(assemblyName, typeFullName, methodName));
        }
    }

    /// <summary>Interpreter が命令境界で呼ぶ (internal: VM 本体からの記録のみ)。</summary>
    internal ExecutionTraceEvent RecordInstruction(ExecutionTraceEvent trace) {
        lock (_gate) {
            if (!_enabled || !_options.CaptureInstructions)
                return trace;
            // Sequence assignment and append are intentionally one critical
            // section. This keeps Events ordered even when guest workers run
            // concurrently and one thread is preempted after creating an event.
            trace = trace with { Sequence = ++_sequence };
            if (_events.Count >= _options.MaxEvents) {
                _droppedEventCount++;
                return trace;
            }
            _events.Add(trace);
            return trace;
        }
    }
}
