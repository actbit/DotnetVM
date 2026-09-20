using DotnetVM.Devices;

namespace DotnetVM.Host;

/// <summary>メソッド実行の結果。戻り値と仮想コンソール出力のスナップショットを含む。</summary>
public sealed record ExecutionResult(
    /// <summary>戻り値 (void/null は null)。VM オブジェクトはホスト型に変換済み。</summary>
    object? ReturnValue,
    /// <summary>実行中の仮想コンソール出力。</summary>
    IReadOnlyList<ConsoleOutputEvent> ConsoleOutput,
    /// <summary>消費した命令数。</summary>
    long InstructionCount);
