namespace DotnetVM.Diagnostics;

/// <summary>実行されたフレーム 1 件分の記録。</summary>
public readonly record struct ExecutionFrame(string AssemblyName, string TypeFullName, string MethodName) {
    public override string ToString() => $"{AssemblyName}!{TypeFullName}::{MethodName}";
}

/// <summary>
/// 実行トレース: インタプリタが実行したフレーム (アセンブリ / 型 / メソッド) を記録する。
/// CoreLib の managed IL が実際に VM で走ったことの証明 (Legacy intrinsic 委譲との区別)、
/// ホットメソッド検出 (M8 JIT) やデバッグ用トレース (M9) の基盤。
/// 記録は <see cref="Enabled"/> が true の間のみ行われる (常時記録はゲストの長時間実行で
/// 無制限に増えるため、ホストが明示的に有効化する)。
/// </summary>
public sealed class ExecutionTracer {
    private readonly List<ExecutionFrame> _frames = [];
    private readonly object _gate = new();

    public ExecutionTracer(int maxEvents = 100_000) {
        if (maxEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        MaxEvents = maxEvents;
    }

    /// <summary>保持する最大イベント数。古いフレームから破棄して bounded に保つ。</summary>
    public int MaxEvents { get; }

    /// <summary>記録済みフレーム (記録順)。読み取り専用ビュー。</summary>
    public IReadOnlyList<ExecutionFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    /// <summary>記録の有効化状態。Start() で true になり Stop() で false になる。</summary>
    public bool Enabled { get { lock (_gate) return _enabled; } }
    private bool _enabled;

    /// <summary>記録を開始する (既存の記録はクリア)。</summary>
    public void Start() {
        lock (_gate) {
            _frames.Clear();
            _enabled = true;
        }
    }

    /// <summary>記録を停止する (既存の記録は保持)。</summary>
    public void Stop() { lock (_gate) _enabled = false; }

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
            if (_frames.Count > MaxEvents)
                _frames.RemoveRange(0, _frames.Count - MaxEvents);
        }
    }
}
