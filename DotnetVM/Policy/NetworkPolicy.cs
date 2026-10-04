using System.Net;

namespace DotnetVM.Policy;

/// <summary>
/// ネットワークアクセスのポリシー。HTTP は HttpPolicy の origin / method / header と
/// 要求数・転送量・タイムアウトを gateway で検査する。従来の INetworkBridge では
/// ホストのプロキシが宛先の許可を判断し、VM が転送量を計上する。
/// </summary>
public sealed class NetworkPolicy {
    /// <summary>HttpClient capability; null denies HTTP requests.</summary>
    public HttpPolicy? Http { get; init; }
    /// <summary>累計転送バイト上限 (送信 + 受信の合計)。</summary>
    public long TotalTransferByteLimit { get; init; } = long.MaxValue;

    /// <summary>1 要求あたりの転送バイト上限 (送信 + 受信の合計)。</summary>
    public long MaxBytesPerRequest { get; init; } = long.MaxValue;
}

/// <summary>ブリッジに渡される 1 要求。</summary>
public sealed class NetworkRequest {
    public Version Version { get; init; } = HttpVersion.Version11;
    public HttpVersionPolicy VersionPolicy { get; init; } = HttpVersionPolicy.RequestVersionOrLower;
    public bool ContentPresent { get; init; }
    public string Method { get; init; } = "GET";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public CancellationToken CancellationToken { get; init; }
    public int MaxResponseHeaderBytes { get; init; } = 16 * 1024;
    public required Uri Url { get; init; }

    /// <summary>送信ボディ (空 = 取得系要求)。</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>ボディ有無の簡易フラグ (取得系なら false)。</summary>
    public bool HasBody => ContentPresent || Body.Length > 0;

    /// <summary>応答側が許される最大バイト数 (VM が事前に上限を伝える、タスク 2 hardening)。
    /// ブリッジはこの上限HOST側で丸めた応答バッファを作るべきであり、
    /// host scopeの巨大応答を先にホスト RAM に作って VM で拒否、という増幅を防ぐ。</summary>
    public long MaxResponseBytes { get; init; } = long.MaxValue;
}

/// <summary>
/// ネットワークブリッジ (プロキシ)。ホストが実装し、実際の通信と「どこへ許すか」の判断を担う。
/// 許可しない要求はホスト実装が例外を投げて拒否する。VM はここを通らないと外部に一切出ない。
/// NetworkBridge が未設定 (null) の場合、System.Net.WebClient のファサード型自体が
/// 合成されないため、ゲストには通信面が存在しない (fail-closed)。
/// </summary>
public interface INetworkBridge {
    /// <summary>要求を送信し、応答ボディを返す。拒否する場合はホスト実装が例外を投げる。
    /// 応答は MaxResponseBytes を超えないようブリッジ側で丸める (上限超過分は捨てるか
    /// チャンク分割 — ブリッジが保有する上限判断に従う)。</summary>
    byte[] Request(NetworkRequest request);
}

/// <summary>
/// ネットワークゲートウェイ。HTTP の許可条件と転送クォータを検査してから
/// ホストブリッジへ委譲する。ゲストからの全通信はこの境界を通る。
/// </summary>
public sealed class NetworkGateway {
    public int MaxHttpRequestBodyBytes => _policy.Http?.MaxRequestBodyBytes ?? 0;
    private readonly NetworkPolicy _policy;
    private readonly INetworkBridge? _bridge;
    private readonly object _gate = new();
    private long _totalBytesTransferred;
    private long _httpRequests;
    private readonly HashSet<string> _origins;
    private readonly HashSet<string> _methods;
    private readonly HashSet<string> _headers;

    public NetworkGateway(NetworkPolicy policy, INetworkBridge? bridge) {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (_policy.TotalTransferByteLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "TotalTransferByteLimit は 0 以上である必要があります。");
        if (_policy.MaxBytesPerRequest < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxBytesPerRequest は 0 以上である必要があります。");
        _bridge = bridge;
        var http = policy.Http;
        _origins = new((http?.AllowedOrigins ?? Array.Empty<Uri>()).Select(Origin), StringComparer.OrdinalIgnoreCase);
        _methods = new(http?.AllowedMethods ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _headers = new(http?.AllowedRequestHeaders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (http is not null && (http.MaxRequests < 0 || http.RequestTimeout <= TimeSpan.Zero || http.RequestTimeout.TotalMilliseconds > int.MaxValue || http.MaxResponseHeaderBytes < 0 || http.MaxResponseBodyBytes < 0 || http.MaxRequestBodyBytes < 0 || http.MaxRequestHeaderBytes < 0))
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private static string Origin(Uri uri) {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new OperationNotAllowedException("Only absolute HTTP(S) origins without credentials are allowed.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public StreamingHttpResponse OpenHttp(string method, Uri uri, ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default,
        Version? version = null, HttpVersionPolicy versionPolicy = HttpVersionPolicy.RequestVersionOrLower, bool contentPresent = false) {
        cancellationToken.ThrowIfCancellationRequested();
        var http = _policy.Http ?? throw new OperationNotAllowedException("HTTP capability is not configured.");
        if (_bridge is not IHttpNetworkBridge bridge || !_origins.Contains(Origin(uri)) || !_methods.Contains(method) || uri.Fragment.Length != 0)
            throw new OperationNotAllowedException("HTTP destination or method is not permitted.");
        headers ??= new Dictionary<string, string>();
        long headerBytes = 0;
        foreach (var (name, value) in headers) {
            if (!_headers.Contains(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                throw new OperationNotAllowedException("HTTP request header is not permitted.");
            headerBytes += System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value);
        }
        if (headerBytes > http.MaxRequestHeaderBytes) throw new NetworkQuotaExceededException("HTTP request headers exceed their byte budget.");
        long limit;
        lock (_gate) {
            if (_httpRequests >= http.MaxRequests) throw new NetworkQuotaExceededException("HTTP request count exceeded.");
            if (body.Length > http.MaxRequestBodyBytes || body.Length > _policy.MaxBytesPerRequest || body.Length > _policy.TotalTransferByteLimit - _totalBytesTransferred)
                throw new NetworkQuotaExceededException("HTTP request exceeds its byte budget.");
            cancellationToken.ThrowIfCancellationRequested();
            _httpRequests++; _totalBytesTransferred += body.Length;
            limit = Math.Min(http.MaxResponseBodyBytes, Math.Min(_policy.MaxBytesPerRequest - body.Length, _policy.TotalTransferByteLimit - _totalBytesTransferred));
        }
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(http.RequestTimeout);
        try {
            var request = new NetworkRequest { Url = uri, Method = method.ToUpperInvariant(), Body = body, Headers = new Dictionary<string, string>(headers),
                Version = version ?? HttpVersion.Version11, VersionPolicy = versionPolicy, ContentPresent = contentPresent,
                CancellationToken = timeout.Token, MaxResponseBytes = limit, MaxResponseHeaderBytes = http.MaxResponseHeaderBytes };
            StreamingHttpResponse response;
            if (bridge is IStreamingHttpNetworkBridge streaming) response = streaming.OpenHttp(request);
            else {
                var buffered = bridge.RequestHttp(request);
                if (buffered?.Body is null || buffered.Body.LongLength > limit) throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
                response = new StreamingHttpResponse { StatusCode = buffered.StatusCode, Version = buffered.Version, ReasonPhrase = buffered.ReasonPhrase,
                    Headers = buffered.Headers.ToDictionary(h => h.Key, h => new[] { h.Value }),
                    TrailingHeaders = () => buffered.TrailingHeaders.ToDictionary(h => h.Key, h => new[] { h.Value }), Body = new MemoryStream(buffered.Body, false) };
            }
            try {
                timeout.Token.ThrowIfCancellationRequested();
                ValidateStreamingHeaders(response.Headers, http.MaxResponseHeaderBytes);
                var bounded = new GatewayResponseStream(this, response, timeout, limit, http.MaxResponseHeaderBytes);
                var result = new StreamingHttpResponse { StatusCode = response.StatusCode, Version = response.Version, ReasonPhrase = response.ReasonPhrase,
                    Headers = response.Headers, TrailingHeaders = response.TrailingHeaders, Body = bounded };
                bounded.Completed = () => result.BodyCompleted?.Invoke(); return result;
            } catch { response.Dispose(); throw; }
        } catch { timeout.Dispose(); throw; }
    }

    private static void ValidateStreamingHeaders(IEnumerable<KeyValuePair<string, string[]>> headers, int maximum) {
        long size = 0;
        foreach (var (name, values) in headers) foreach (var value in values) {
            size += System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value);
            if (size > maximum) throw new NetworkQuotaExceededException("HTTP response headers exceed their byte budget.");
        }
    }

    private sealed class GatewayResponseStream(NetworkGateway gateway, StreamingHttpResponse response, CancellationTokenSource lifetime, long maximum, int headerMaximum) : Stream {
        private long _read;
        private bool _ended;
        public Action? Completed { get; set; }
        public override bool CanRead => response.Body.CanRead;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => response.Body.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
            try {
            if (buffer.Length == 0) return 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, Math.Min(int.MaxValue, maximum - _read + 1));
            var read = await response.Body.ReadAsync(buffer[..count], linked.Token).ConfigureAwait(false);
            lock (gateway._gate) {
                if (read > maximum - _read || read > gateway._policy.TotalTransferByteLimit - gateway._totalBytesTransferred)
                    throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
                _read += read; gateway._totalBytesTransferred += read;
            }
            if (read == 0 && !_ended) {
                _ended = true;
                ValidateStreamingHeaders(response.Headers.Concat(response.TrailingHeaders()), headerMaximum); Completed?.Invoke();
            }
            return read;
            } catch { Dispose(); throw; }
        }
        protected override void Dispose(bool disposing) { if (disposing) { response.Dispose(); lifetime.Dispose(); } base.Dispose(disposing); }
    }

    public HttpNetworkResponse TransferHttp(string method, Uri uri, ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default,
        Version? version = null, HttpVersionPolicy versionPolicy = HttpVersionPolicy.RequestVersionOrLower, bool contentPresent = false) {
        lock (_gate) {
            var http = _policy.Http ?? throw new OperationNotAllowedException("HTTP capability is not configured.");
            if (_bridge is not IHttpNetworkBridge bridge || !_origins.Contains(Origin(uri)) || !_methods.Contains(method))
                throw new OperationNotAllowedException("HTTP destination or method is not permitted.");
            if (uri.Fragment.Length != 0) throw new OperationNotAllowedException("HTTP URL fragments are not permitted.");
            headers ??= new Dictionary<string, string>();
            long requestHeaderBytes = 0;
            foreach (var (name, value) in headers) {
                if (!_headers.Contains(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                    throw new OperationNotAllowedException("HTTP request header is not permitted.");
                requestHeaderBytes += System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value);
            }
            if (requestHeaderBytes > http.MaxRequestHeaderBytes) throw new NetworkQuotaExceededException("HTTP request headers exceed their byte budget.");
            if (_httpRequests >= http.MaxRequests) throw new NetworkQuotaExceededException("HTTP request count exceeded.");
            if (body.Length > http.MaxRequestBodyBytes || body.Length > _policy.MaxBytesPerRequest || body.Length > _policy.TotalTransferByteLimit - _totalBytesTransferred)
                throw new NetworkQuotaExceededException("HTTP request exceeds its byte budget.");
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(http.RequestTimeout);
            _httpRequests++;
            // Account attempted sends even if the transport subsequently fails.
            _totalBytesTransferred += body.Length;
            var limit = Math.Min(http.MaxResponseBodyBytes, Math.Min(_policy.MaxBytesPerRequest - body.Length, _policy.TotalTransferByteLimit - _totalBytesTransferred));
            var response = bridge.RequestHttp(new NetworkRequest {
                Url = uri, Method = method.ToUpperInvariant(), Body = body,
                Version = version ?? HttpVersion.Version11, VersionPolicy = versionPolicy, ContentPresent = contentPresent,
                Headers = new Dictionary<string, string>(headers), MaxResponseBytes = limit,
                CancellationToken = timeout.Token, MaxResponseHeaderBytes = http.MaxResponseHeaderBytes,
            });
            timeout.Token.ThrowIfCancellationRequested();
            if (response?.Body is null || response.Body.LongLength > limit)
                throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
            long headerBytes = 0;
            foreach (var (name, value) in response.Headers.Concat(response.TrailingHeaders))
                headerBytes += System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value);
            if (headerBytes > http.MaxResponseHeaderBytes) throw new NetworkQuotaExceededException("HTTP response headers exceed their byte budget.");
            _totalBytesTransferred += response.Body.Length;
            return response;
        }
    }

    /// <summary>ブリッジが設定されているか (未設定なら通信面自体が存在しない)。</summary>
    public bool IsEnabled => _bridge is not null;

    /// <summary>累計転送バイト数 (診断用)。</summary>
    public long TotalBytesTransferred { get { lock (_gate) return _totalBytesTransferred; } }

    /// <summary>要求をブリッジへ委譲し、応答ボディを返す (通過バイトを計上・クォータ強制)。</summary>
    public byte[] Transfer(string url, ReadOnlyMemory<byte> body) {
        if (_bridge is IHttpNetworkBridge) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new NetworkQuotaExceededException("Invalid HTTP URL.");
            return TransferHttp(body.IsEmpty ? "GET" : "POST", uri, body).Body;
        }
        lock (_gate) {
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

        // 応答上限を事前にブリッジへ伝える: 今回要求分を差し引いた累計残量と
        // 1 要求上限の min を渡す (タスク 2 hardening)。host 側で巨大バッファを
        // 作ってから VM 側で拒否する増幅を避ける
            var totalRemaining = Math.Max(0, _policy.TotalTransferByteLimit - _totalBytesTransferred - requestBytes);
            var maxResponseBytes = Math.Min(
                _policy.MaxBytesPerRequest > requestBytes ? _policy.MaxBytesPerRequest - requestBytes : 0,
                totalRemaining);
            var response = _bridge.Request(new NetworkRequest {
                Url = uri,
                Body = body,
                MaxResponseBytes = maxResponseBytes,
            }) ?? throw new NetworkQuotaExceededException("ネットワークブリッジが null 応答を返しました。");
            var transferred = (long)requestBytes + response.LongLength;
            if (transferred > _policy.MaxBytesPerRequest)
                throw new NetworkQuotaExceededException(
                    $"転送バイト数 {transferred:N0} (送信 {requestBytes:N0} + 受信 {response.LongLength:N0}) が 1 要求上限 {_policy.MaxBytesPerRequest:N0} を超過しました。");
            if (transferred > _policy.TotalTransferByteLimit - _totalBytesTransferred)
                throw new NetworkQuotaExceededException(
                    $"累計転送バイト上限 {_policy.TotalTransferByteLimit:N0} を超過しました (これまで {_totalBytesTransferred:N0} + 今回 {transferred:N0})。");
            _totalBytesTransferred += transferred;
            return response;
        }
    }
}
