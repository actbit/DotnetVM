using DotnetVM.IL;

namespace DotnetVM.Diagnostics;

/// <summary>命令単位の実行トレース設定。</summary>
public sealed class ExecutionTraceOptions {
    /// <summary>IL 命令イベントを保存するか。既定では保存する。</summary>
    public bool CaptureInstructions { get; init; } = true;

    /// <summary>保存する命令イベントの最大数。超過分は破棄される。</summary>
    public int MaxEvents { get; init; } = 100_000;

    /// <summary>保存するフレーム開始記録の最大数。超過分は破棄される。</summary>
    public int MaxFrames { get; init; } = 100_000;

    internal void Validate() {
        if (MaxEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxEvents), "MaxEvents は 1 以上である必要があります。");
        if (MaxFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxFrames), "MaxFrames は 1 以上である必要があります。");
    }
}

/// <summary>1 命令分の実行記録。</summary>
public readonly record struct ExecutionTraceEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    int ManagedThreadId,
    int Depth,
    ExecutionFrame Frame,
    int IlOffset,
    ILOp Op,
    string OpName) {
    public override string ToString() =>
        $"#{Sequence} {Frame} IL_{IlOffset:X4}: {OpName} (depth={Depth}, thread={ManagedThreadId})";
}
