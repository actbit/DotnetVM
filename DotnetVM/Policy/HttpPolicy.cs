using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace DotnetVM.Policy;

/// <summary>Explicit HTTP capability. An empty origin list denies every request.</summary>
public sealed class HttpPolicy {
    public IReadOnlyCollection<Uri> AllowedOrigins { get; init; } = Array.Empty<Uri>();
    public IReadOnlyCollection<string> AllowedMethods { get; init; } = new[] { "GET", "HEAD" };
    public IReadOnlyCollection<string> AllowedRequestHeaders { get; init; } = Array.Empty<string>();
    public long MaxRequests { get; init; } = 100;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxResponseHeaderBytes { get; init; } = 16 * 1024;
    public int MaxResponseBodyBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxRequestBodyBytes { get; init; } = 1024 * 1024;
    public int MaxRequestHeaderBytes { get; init; } = 16 * 1024;
}

public sealed class HttpNetworkResponse {
    public Version Version { get; init; } = HttpVersion.Version11;
    public string? ReasonPhrase { get; init; }
    public IReadOnlyDictionary<string, string> TrailingHeaders { get; init; } = new Dictionary<string, string>();
    public int StatusCode { get; init; } = 200;
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public required byte[] Body { get; init; }
}

public interface IHttpNetworkBridge : INetworkBridge {
    HttpNetworkResponse RequestHttp(NetworkRequest request);
}

/// <summary>A transport response whose body is read on demand and owned by the caller.</summary>
public sealed class StreamingHttpResponse : IDisposable {
    public int StatusCode { get; init; } = 200;
    public Version Version { get; init; } = HttpVersion.Version11;
    public string? ReasonPhrase { get; init; }
    public IReadOnlyDictionary<string, string[]> Headers { get; init; } = new Dictionary<string, string[]>();
    public Func<IReadOnlyDictionary<string, string[]>> TrailingHeaders { get; init; } = static () => new Dictionary<string, string[]>();
    public required Stream Body { get; init; }
    internal Action? BodyCompleted { get; set; }
    public void Dispose() => Body.Dispose();
}

public interface IStreamingHttpNetworkBridge : IHttpNetworkBridge {
    StreamingHttpResponse OpenHttp(NetworkRequest request);
}

/// <summary>Host HTTP transport with bounded response reads. Gateway policy is mandatory.</summary>
public sealed class HttpNetworkBridge : IStreamingHttpNetworkBridge, IDisposable {
    private readonly HttpClient _client;
    public HttpNetworkBridge() : this(CreateDefaultHandler()) { }

    /// <summary>
    /// The host-supplied handler is part of the VM TCB. It must disable
    /// automatic redirects, cookies, and decompression, honor cancellation,
    /// and enforce any DNS/IP policy required by the host.
    /// </summary>
    public HttpNetworkBridge(HttpMessageHandler handler) => _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static SocketsHttpHandler CreateDefaultHandler() => new() {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        ConnectCallback = ConnectOnlyToPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectOnlyToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken) {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        foreach (var address in addresses) {
            if (IsPrivateOrLocal(address))
                continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            } catch (SocketException) {
                socket.Dispose();
            } catch {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            $"HTTP 接続先 {context.DnsEndPoint.Host} が private/loopback/link-local アドレスに解決されました。");
    }

    /// <summary>
    /// Checks the address actually returned by DNS immediately before the
    /// socket is connected. The origin allowlist alone is not sufficient when
    /// an allowed hostname can be controlled by an attacker.
    /// </summary>
    internal static bool IsPrivateOrLocal(IPAddress address) {
        if (address.IsIPv4MappedToIPv6)
            return IsPrivateOrLocal(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) {
            var first = bytes[0];
            var second = bytes[1];
            return first == 0 || first == 10 || first == 127 ||
                first == 169 && second == 254 ||
                first == 172 && second is >= 16 and <= 31 ||
                first == 192 && second == 168 ||
                first == 100 && second is >= 64 and <= 127 ||
                first == 192 && second == 0 ||
                first == 198 && second is 18 or 19 ||
                first >= 224;
        }

        // IPv6 unique-local (fc00::/7), documentation (2001:db8::/32),
        // and unspecified addresses are not valid public transport targets.
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            ((bytes[0] & 0xfe) == 0xfc ||
             bytes[0] == 0x20 && bytes[1] == 0x01 &&
             bytes[2] == 0x0d && bytes[3] == 0xb8 ||
             bytes.All(static value => value == 0));
    }

    public byte[] Request(NetworkRequest request) => RequestHttp(request).Body;

    public StreamingHttpResponse OpenHttp(NetworkRequest request) {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        if (request.HasBody) message.Content = new ByteArrayContent(request.Body.ToArray());
        foreach (var (name, value) in request.Headers) {
            if (!message.Headers.TryAddWithoutValidation(name, value)) message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        var response = _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.CancellationToken).GetAwaiter().GetResult();
        try {
            if (response.Content.Headers.ContentLength is long size && size > request.MaxResponseBytes)
                throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
            var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            CheckHeaders(headers, request.MaxResponseHeaderBytes);
            var body = response.Content.ReadAsStreamAsync(request.CancellationToken).GetAwaiter().GetResult();
            return new StreamingHttpResponse {
                StatusCode = (int)response.StatusCode, Version = response.Version, ReasonPhrase = response.ReasonPhrase, Headers = headers,
                Body = new OwnedResponseStream(body, response),
                TrailingHeaders = () => {
                    var trailers = response.TrailingHeaders.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                    CheckHeaders(headers.Concat(trailers), request.MaxResponseHeaderBytes); return trailers;
                },
            };
        } catch { response.Dispose(); throw; }
    }

    private static void CheckHeaders(IEnumerable<KeyValuePair<string, string[]>> headers, int maximum) {
        long bytes = 0;
        foreach (var (name, values) in headers) foreach (var value in values) {
            bytes += System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value);
            if (bytes > maximum) throw new NetworkQuotaExceededException("HTTP response headers exceed their byte budget.");
        }
    }

    private sealed class OwnedResponseStream(Stream stream, HttpResponseMessage response) : Stream {
        public override bool CanRead => stream.CanRead;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => stream.ReadAsync(buffer, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => stream.ReadAsync(buffer, token);
        protected override void Dispose(bool disposing) { if (disposing) response.Dispose(); base.Dispose(disposing); }
    }

    public HttpNetworkResponse RequestHttp(NetworkRequest request) {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        if (request.HasBody) message.Content = new ByteArrayContent(request.Body.ToArray());
        foreach (var (name, value) in request.Headers) {
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.CancellationToken).GetAwaiter().GetResult();
        if (response.Content.Headers.ContentLength is long size && size > request.MaxResponseBytes)
            throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long headerBytes = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers)) {
            var value = string.Join(",", header.Value);
            headerBytes += System.Text.Encoding.UTF8.GetByteCount(header.Key) + System.Text.Encoding.UTF8.GetByteCount(value);
            if (headerBytes > request.MaxResponseHeaderBytes)
                throw new NetworkQuotaExceededException("HTTP response headers exceed their byte budget.");
            headers[header.Key] = value;
        }
        using var stream = response.Content.ReadAsStreamAsync(request.CancellationToken).GetAwaiter().GetResult();
        using var body = new MemoryStream();
        var buffer = new byte[(int)Math.Min(8192, Math.Max(1, request.MaxResponseBytes + (request.MaxResponseBytes < long.MaxValue ? 1 : 0)))];
        while (true) {
            int wanted = (int)Math.Min(buffer.Length, Math.Max(1, request.MaxResponseBytes - body.Length + (request.MaxResponseBytes < long.MaxValue ? 1 : 0)));
            var read = stream.ReadAsync(buffer.AsMemory(0, wanted), request.CancellationToken).GetAwaiter().GetResult();
            if (read == 0) break;
            if (read > request.MaxResponseBytes - body.Length)
                throw new NetworkQuotaExceededException("HTTP response exceeds its byte budget.");
            body.Write(buffer, 0, read);
        }
        var trailing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.TrailingHeaders) {
            var value = string.Join(",", header.Value);
            headerBytes += System.Text.Encoding.UTF8.GetByteCount(header.Key) + System.Text.Encoding.UTF8.GetByteCount(value);
            if (headerBytes > request.MaxResponseHeaderBytes) throw new NetworkQuotaExceededException("HTTP response headers exceed their byte budget.");
            trailing[header.Key] = value;
        }
        return new HttpNetworkResponse { StatusCode = (int)response.StatusCode, Headers = headers, TrailingHeaders = trailing,
            Version = response.Version, ReasonPhrase = response.ReasonPhrase, Body = body.ToArray() };
    }

    public void Dispose() => _client.Dispose();
}
