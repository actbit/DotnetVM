namespace DotnetVM.Policy;

/// <summary>VM 実行に関する例外の基底。ホスト側で捕捉して扱う。</summary>
public abstract class VmExecutionException : Exception {
    protected VmExecutionException(string message) : base(message) { }
    protected VmExecutionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// リソース上限超過 (メモリ/ネットワーク/ストレージ/命令クォータ) の基底。
/// この系統の例外はゲストの catch には渡らず VM の最上位まで伝播する (管理例外)。
/// </summary>
public abstract class ResourceExhaustedException : VmExecutionException {
    protected ResourceExhaustedException(string message) : base(message) { }
}

/// <summary>累計/生存アロケーションのメモリ上限を超過した。</summary>
public sealed class MemoryQuotaExceededException : ResourceExhaustedException {
    public MemoryQuotaExceededException(string message) : base(message) { }
}

/// <summary>ネットワークの許可リスト/バイトクォータに違反した。</summary>
public sealed class NetworkQuotaExceededException : ResourceExhaustedException {
    public NetworkQuotaExceededException(string message) : base(message) { }
}

/// <summary>ストレージの許可パス/バイトクォータに違反した。</summary>
public sealed class StorageQuotaExceededException : ResourceExhaustedException {
    public StorageQuotaExceededException(string message) : base(message) { }
}

/// <summary>命令数クォータ (停止性保証) を超過した。</summary>
public sealed class InstructionQuotaExceededException : ResourceExhaustedException {
    public InstructionQuotaExceededException(string message) : base(message) { }
}

/// <summary>未許可の操作 (P/Invoke、マルチモジュール、未登録 intrinsic、未設定ブリッジ等)。</summary>
public sealed class OperationNotAllowedException : VmExecutionException {
    public OperationNotAllowedException(string message) : base(message) { }
}

/// <summary>ゲスト側で処理されずに VM 外へ出てきた例外 (ゲストオブジェクトの型名を保持)。</summary>
public sealed class UnhandledGuestException : VmExecutionException {
    /// <summary>ゲスト例外オブジェクトの型フルネーム (例: System.DivideByZeroException 相当のファサード型)。</summary>
    public string ExceptionTypeName { get; }

    /// <summary>ゲスト例外の Message スロットの値 (取れる場合)。</summary>
    public string? GuestMessage { get; }

    public UnhandledGuestException(string exceptionTypeName, string? guestMessage)
        : base($"ゲストで未処理の例外: {exceptionTypeName}" + (guestMessage is null ? "" : $": {guestMessage}")) {
        ExceptionTypeName = exceptionTypeName;
        GuestMessage = guestMessage;
    }
}
