namespace DotnetVM.Policy;

/// <summary>
/// ストレージアクセスのポリシー (バイトクォータのみ)。
/// 「どのパスを許すか」は VM では判断しない — その界面 (System.IO.File ファサード) を
/// 再現するかは VmHostOptions.StorageBridge の設定有無で決まり、ブリッジ未設定なら
/// ファサード型自体が合成されない。許可の判断はブリッジ (ホスト実装のプロキシ) 側が行う。
/// VM 側は通過したバイトの計上とクォータ強制だけを担う。
/// </summary>
public sealed class StoragePolicy {
    /// <summary>累計転送バイト上限 (読み取り + 書き込みの合計)。</summary>
    public long TotalByteLimit { get; init; } = long.MaxValue;

    /// <summary>1 操作あたりのバイト上限 (読み取り + 書き込みの合計)。</summary>
    public long MaxBytesPerOperation { get; init; } = long.MaxValue;
}

/// <summary>
/// ストレージブリッジ (プロキシ)。ホストが実装し、実際のファイル I/O と
/// 「どのパスを許すか」の判断を担う。許可しない操作はホスト実装が例外を投げて拒否する。
/// VM はここを通らないとファイルシステムに一切触れない。
/// StorageBridge が未設定 (null) の場合、System.IO.File のファサード型自体が
/// 合成されないため、ゲストにはファイル面が存在しない (fail-closed)。
/// </summary>
public interface IStorageBridge {
    bool Exists(string path);
    byte[] Read(string path);
    void Write(string path, ReadOnlyMemory<byte> contents);
    void Delete(string path);
}

/// <summary>
/// ストレージゲートウェイ (純粋なプロキシ + クォータ強制点)。名前による許可判断はしない。
/// ゲストからの全ファイル操作はここを通り、バイトが計上されてからブリッジに委譲される。
/// </summary>
public sealed class StorageGateway {
    private readonly StoragePolicy _policy;
    private readonly IStorageBridge? _bridge;
    private long _totalBytes;

    public StorageGateway(StoragePolicy policy, IStorageBridge? bridge) {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _bridge = bridge;
    }

    /// <summary>ブリッジが設定されているか (未設定ならファイル面自体が存在しない)。</summary>
    public bool IsEnabled => _bridge is not null;

    /// <summary>累計転送バイト数 (診断用)。</summary>
    public long TotalBytes => _totalBytes;

    public bool Exists(string path) {
        RequireBridge();
        return _bridge!.Exists(RequirePath(path));
    }

    public byte[] Read(string path) {
        RequireBridge();
        var contents = _bridge!.Read(RequirePath(path));
        Charge(contents.LongLength, "読み取り");
        return contents;
    }

    public void Write(string path, ReadOnlyMemory<byte> contents) {
        RequireBridge();
        Charge(contents.Length, "書き込み");
        _bridge!.Write(RequirePath(path), contents);
    }

    public void Delete(string path) {
        RequireBridge();
        _bridge!.Delete(RequirePath(path));
    }

    private void RequireBridge() {
        if (_bridge is null)
            throw new OperationNotAllowedException(
                "ストレージブリッジが設定されていないため、ファイル面は存在しません (VmHostOptions.StorageBridge = null は全拒否)。");
    }

    private static string RequirePath(string? path) {
        if (string.IsNullOrWhiteSpace(path))
            throw new StorageQuotaExceededException("パスが空です。");
        return path; // 正規化・許可判断はブリッジ (プロキシ) 側の責務
    }

    private void Charge(long bytes, string operation) {
        if (bytes > _policy.MaxBytesPerOperation)
            throw new StorageQuotaExceededException(
                $"{operation}バイト数 {bytes:N0} が 1 操作上限 {_policy.MaxBytesPerOperation:N0} を超過しました。");
        if (_totalBytes + bytes > _policy.TotalByteLimit)
            throw new StorageQuotaExceededException(
                $"累計ストレージバイト上限 {_policy.TotalByteLimit:N0} を超過しました (これまで {_totalBytes:N0} + 今回 {bytes:N0})。");
        _totalBytes += bytes;
    }
}
