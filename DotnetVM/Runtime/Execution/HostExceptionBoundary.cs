using DotnetVM.Policy;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Host implementation failures must not become an accidental second VM
/// exception channel. Resource and policy exceptions remain management
/// exceptions; ordinary failures are converted to InvalidProgramException at
/// the guest boundary.
/// </summary>
internal static class HostExceptionBoundary {
    internal static bool IsNormalizable(Exception exception) =>
        exception is not VmExecutionException and
        not VmGuestThrow and
        not TailCallTransfer and
        not OperationCanceledException and
        not ThreadInterruptedException and
        not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    internal static UnhandledGuestException InvalidProgram(Exception exception) =>
        new("System.InvalidProgramException", $"ゲスト実行中にホスト実装例外が発生しました: {exception.Message}");
}
