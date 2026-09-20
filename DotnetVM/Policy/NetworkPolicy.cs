namespace DotnetVM.Policy;

/// <summary>
/// ネットワークアクセスのポリシー (バイトクォータのみ)。
/// 「どこへの通信を許すか」は VM では判断しない — その界面 (System.Net.WebClient ファサード) を
/// 再現するかは VmHostOptions.NetworkBridge の設定有無で決まり、ブリッジ未設定なら
/// ファサード型自体が合成されない。許可の判断はブリッジ (ホスト実装のプロキシ) 側が行う。
/// VM 側は通過したバイトの計上とクォータ強制だけを担う。
/// </summary>
public sealed class NetworkPolicy {
    /// <summary>累計転送バイト上限 (送信 + 受信の合計)。</summary>
    public long TotalTransferByteLimit { get; init; } = long.MaxValue;

    /// <summary>1 要求あたりの転送バイト上限 (送信 + 受信の合計)。</summary>
    public long MaxBytesPerRequest { get; init; } = long.MaxValue;
}

/// <summary>ブリッジに渡される 1 要求。</summary>
public sealed class NetworkRequest {
    public required Uri Url { get; init; }

    /// <summary>送信ボディ (空 = 取得系要求)。</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>ボディ有無の簡易フラグ (取得系なら false)。</summary>
    public bool HasBody => Body.Length > 0;
}

/// <summary>
/// ネットワークブリッジ (プロキシ)。ホストが実装し、実際の通信と「どこへ許すか」の判断を担う。
/// 許可しない要求はホスト実装が例外を投げて拒否する。VM はここを通らないと外部に一切出ない。
/// NetworkBridge が未設定 (null) の場合、System.Net.WebClient のファサード型自体が
/// 合成されないため、ゲストには通信面が存在しない (fail-closed)。
/// </summary>
public interface INetworkBridge {
    /// <summary>要求を送信し、応答ボディを返す。拒否する場合はホスト実装が例外を投げる。</summary>
    byte[] Request(NetworkRequest request);
}

/// <summary>
/// ネットワークゲートウェイ (純粋なプロキシ + クォータ強制点)。名前による許可判断はしない。
/// ゲストからの全通信はここを通り、バイトが計上されてからブリッジに委譲される。
/// </summary>
public sealed class NetworkGateway {
    private readonly NetworkPolicy _policy;
    private readonly INetworkBridge? _bridge;
    private long _totalBytesTransferred;

    public NetworkGateway(NetworkPolicy policy, INetworkBridge? bridge) {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _bridge = bridge;
    }

    /// <summary>ブリッジが設定されているか (未設定なら通信面自体が存在しない)。</summary>
    public bool IsEnabled => _bridge is not null;

    /// <summary>累計転送バイト数 (診断用)。</summary>
    public long TotalBytesTransferred => _totalBytesTransferred;

    /// <summary>要求をブリッジへ委譲し、応答ボディを返す (通過バイトを計上・クォータ強制)。</summary>
    public byte[] Transfer(string url, ReadOnlyMemory<byte> body) {
        if (_bridge is null)
            throw new OperationNotAllowedException(
                "ネットワークブリッジが設定されていないため、通信面は存在しません (VmHostOptions.NetworkBridge = null は全拒否)。");

        Uri uri;
        try {
            uri = new Uri(url ?? throw new NetworkQuotaExceededException("URL が null です。"));
        } catch (UriFormatException) {
            throw new NetworkQuotaExceededException($"URL を解釈できません: {url}");
        }

        var requestBytes = body.Length;
        if (requestBytes > _policy.MaxBytesPerRequest)
            throw new NetworkQuotaExceededException(
                $"送信バイト数 {requestBytes:N0} が 1 要求上限 {_policy.MaxBytesPerRequest:N0} を超過しました。");

        var response = _bridge.Request(new NetworkRequest { Url = uri, Body = body });
        var transferred = (long)requestBytes + response.LongLength;
        if (transferred > _policy.MaxBytesPerRequest)
            throw new NetworkQuotaExceededException(
                $"転送バイト数 {transferred:N0} (送信 {requestBytes:N0} + 受信 {response.LongLength:N0}) が 1 要求上限 {_policy.MaxBytesPerRequest:N0} を超過しました。");
        if (_totalBytesTransferred + transferred > _policy.TotalTransferByteLimit)
            throw new NetworkQuotaExceededException(
                $"累計転送バイト上限 {_policy.TotalTransferByteLimit:N0} を超過しました (これまで {_totalBytesTransferred:N0} + 今回 {transferred:N0})。");
        _totalBytesTransferred += transferred;
        return response;
    }
}
