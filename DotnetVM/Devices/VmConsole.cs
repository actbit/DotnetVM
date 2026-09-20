namespace DotnetVM.Devices;

/// <summary>コンソール出力 1 件。</summary>
public readonly record struct ConsoleOutputEvent(bool IsError, string Text);

/// <summary>
/// 仮想コンソールのバインディング。ホスト実装で全面差し替えできる。
/// 既定の内蔵実装はメモリ上のログ + 入力供給関数。
/// </summary>
public interface IVirtualConsoleBinding {
    void Write(bool isError, string text);
    string? ReadLine();
}

/// <summary>
/// 仮想コンソールデバイス。ゲストの Console 入出力はすべてここに集約され、
/// ホストはバインドして消費する (出力イベント購読 / 入力供給 / 実装差し替え)。
/// デバイス自体はホストの物理 I/O を知らない。
/// </summary>
public sealed class VmConsole {
    private IVirtualConsoleBinding _binding;
    private readonly List<ConsoleOutputEvent> _log = [];
    private readonly object _gate = new();

    public VmConsole() {
        _binding = new InternalBinding(this);
    }

    /// <summary>全出力をイベントとして購読する。</summary>
    public event Action<ConsoleOutputEvent>? OutputWritten;

    /// <summary>蓄積された出力ログ (Clear で消去可)。</summary>
    public IReadOnlyList<ConsoleOutputEvent> OutputLog {
        get { lock (_gate) return [.. _log]; }
    }

    /// <summary>ログを消去する (イベント購読やバインドには影響しない)。</summary>
    public void Clear() {
        lock (_gate)
            _log.Clear();
    }

    /// <summary>ゲストの Console.ReadLine への入力供給源を差し替える。</summary>
    public void BindInput(Func<string?> readLine) {
        ArgumentNullException.ThrowIfNull(readLine);
        var current = _binding;
        _binding = new InternalBinding(this, readLine, current);
    }

    /// <summary>完全カスタム実装へ差し替える (既定の内蔵実装の代わり)。</summary>
    public void BindImplementation(IVirtualConsoleBinding impl) {
        ArgumentNullException.ThrowIfNull(impl);
        _binding = impl;
    }

    /// <summary>蓄積ログを持たない生の現在のバインディングを取得 (デバイス内部用)。</summary>
    internal IVirtualConsoleBinding CurrentBinding => _binding;

    internal void Write(bool isError, string text) {
        lock (_gate)
            _log.Add(new ConsoleOutputEvent(isError, text));
        OutputWritten?.Invoke(new ConsoleOutputEvent(isError, text));
        _binding.Write(isError, text);
    }

    internal string? ReadLine() => _binding.ReadLine();

    /// <summary>内蔵実装: メモリログは Write 側で蓄積済み。入力は BindInput で差し込まれた関数。</summary>
    private sealed class InternalBinding : IVirtualConsoleBinding {
        private readonly VmConsole _owner;
        private readonly Func<string?>? _readLine;
        private readonly IVirtualConsoleBinding? _fallback;

        public InternalBinding(VmConsole owner, Func<string?>? readLine = null, IVirtualConsoleBinding? fallback = null) {
            _owner = owner;
            _readLine = readLine;
            _fallback = fallback;
        }

        public void Write(bool isError, string text) {
            // 内蔵実装は出力をどこにも転送しない (ログは VmConsole.Write で蓄積済み)
        }

        public string? ReadLine() {
            if (_readLine is not null)
                return _readLine();
            if (_fallback is not null)
                return _fallback.ReadLine();
            return null; // 入力未バインド = EOF
        }
    }
}
