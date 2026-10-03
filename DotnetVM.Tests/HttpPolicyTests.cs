using System.Net;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

public sealed class HttpPolicyTests {
    private sealed class Bridge : IHttpNetworkBridge {
        public int Calls;
        public Func<NetworkRequest, HttpNetworkResponse> Handler = _ => new() { Body = new byte[4] };
        public byte[] Request(NetworkRequest request) => RequestHttp(request).Body;
        public HttpNetworkResponse RequestHttp(NetworkRequest request) { Calls++; return Handler(request); }
    }
    private static NetworkGateway Gateway(Bridge bridge, HttpPolicy? http = null, long total = 100) => new(new NetworkPolicy {
        TotalTransferByteLimit = total, MaxBytesPerRequest = 20,
        Http = http ?? new HttpPolicy { AllowedOrigins = new[] { new Uri("https://example.test") }, MaxRequests = 2 },
    }, bridge);

    [Theory]
    [InlineData("https://example.test.evil/path")]
    [InlineData("http://example.test/path")]
    [InlineData("https://example.test:8443/path")]
    [InlineData("https://user@example.test/path")]
    [InlineData("file:///tmp/test")]
    public void DestinationChecksRunBeforeBridge(string url) {
        var bridge = new Bridge(); var gateway = Gateway(bridge);
        Assert.Throws<OperationNotAllowedException>(() => gateway.TransferHttp("GET", new Uri(url), default));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void DefaultHttpCapabilityDeniesRequests() {
        var bridge = new Bridge();
        Assert.Throws<OperationNotAllowedException>(() => new NetworkGateway(new(), bridge).TransferHttp("GET", new("https://example.test"), default));
        Assert.Throws<OperationNotAllowedException>(() => Gateway(bridge, new HttpPolicy()).TransferHttp("GET", new("https://example.test"), default));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void LegacyWebClientCannotBypassHttpPolicy() {
        var bridge = new Bridge();
        Assert.Throws<OperationNotAllowedException>(() => Gateway(bridge).Transfer("https://evil.test", default));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void MethodsAndHeadersCannotBypassPolicy() {
        var bridge = new Bridge(); var gateway = Gateway(bridge, new HttpPolicy {
            AllowedOrigins = new[] { new Uri("https://example.test") }, AllowedRequestHeaders = new[] { "Host", "X-Test" },
        });
        Assert.Throws<OperationNotAllowedException>(() => gateway.TransferHttp("POST", new("https://example.test"), default));
        foreach (var headers in new[] { new Dictionary<string, string> { ["Authorization"] = "secret" }, new Dictionary<string, string> { ["Host"] = "evil.test" }, new Dictionary<string, string> { ["X-Test"] = "a\r\nb" } })
            Assert.Throws<OperationNotAllowedException>(() => gateway.TransferHttp("GET", new("https://example.test"), default, headers));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void PolicyListsAreSnapshotsAndRequestLimitsPrecedeTransport() {
        var bridge = new Bridge(); var origins = new[] { new Uri("https://example.test") };
        var methods = new[] { "GET" }; var headers = new[] { "X-Test" };
        var gateway = Gateway(bridge, new HttpPolicy { AllowedOrigins = origins, AllowedMethods = methods,
            AllowedRequestHeaders = headers, MaxRequestBodyBytes = 1, MaxRequestHeaderBytes = 7 });
        origins[0] = new Uri("https://evil.test"); methods[0] = "POST"; headers[0] = "Authorization";
        Assert.Throws<OperationNotAllowedException>(() => gateway.TransferHttp("GET", origins[0], default));
        Assert.Throws<OperationNotAllowedException>(() => gateway.TransferHttp("POST", new("https://example.test"), default));
        Assert.Throws<NetworkQuotaExceededException>(() => gateway.TransferHttp("GET", new("https://example.test"), new byte[2]));
        Assert.Throws<NetworkQuotaExceededException>(() => gateway.TransferHttp("GET", new("https://example.test"), default, new Dictionary<string, string> { ["X-Test"] = "ab" }));
        Assert.Equal(0, bridge.Calls);
        gateway.TransferHttp("GET", new("https://example.test"), default, new Dictionary<string, string> { ["X-Test"] = "a" });
        Assert.Equal(1, bridge.Calls);
    }

    [Fact]
    public void CountAndCumulativeTransferAreEnforced() {
        var bridge = new Bridge(); var gateway = Gateway(bridge, total: 8);
        gateway.TransferHttp("GET", new("https://example.test"), default);
        gateway.TransferHttp("GET", new("https://example.test"), default);
        Assert.Equal(8, gateway.TotalBytesTransferred);
        Assert.Throws<NetworkQuotaExceededException>(() => gateway.TransferHttp("GET", new("https://example.test"), default));
        Assert.Equal(2, bridge.Calls);
    }

    [Fact]
    public void ResponseAndHeaderLimitsArePassedAndChecked() {
        var bridge = new Bridge(); var gateway = Gateway(bridge, new HttpPolicy {
            AllowedOrigins = new[] { new Uri("https://example.test") }, MaxResponseBodyBytes = 3, MaxResponseHeaderBytes = 2,
        });
        bridge.Handler = request => { Assert.Equal(3, request.MaxResponseBytes); Assert.Equal(2, request.MaxResponseHeaderBytes); return new() { Body = new byte[4] }; };
        Assert.Throws<NetworkQuotaExceededException>(() => gateway.TransferHttp("GET", new("https://example.test"), default));
        bridge.Handler = _ => new() { Body = [], Headers = new Dictionary<string, string> { ["X"] = "abc" } };
        Assert.Throws<NetworkQuotaExceededException>(() => gateway.TransferHttp("GET", new("https://example.test"), default));
    }

    [Fact]
    public void TimeoutAndCancellationReachTransport() {
        var bridge = new Bridge(); var gateway = Gateway(bridge, new HttpPolicy { AllowedOrigins = new[] { new Uri("https://example.test") }, RequestTimeout = TimeSpan.FromMilliseconds(20) });
        bridge.Handler = request => { Assert.True(request.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))); request.CancellationToken.ThrowIfCancellationRequested(); return new() { Body = [] }; };
        Assert.ThrowsAny<OperationCanceledException>(() => gateway.TransferHttp("GET", new("https://example.test"), default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => gateway.TransferHttp("GET", new("https://example.test"), default, cancellationToken: cancel.Token));
        Assert.Equal(1, bridge.Calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    [Fact]
    public void StandardTransportBoundsUnknownLengthResponsesAndReturnsRedirects() {
        using var bridge = new HttpNetworkBridge(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) {
            Content = new StreamContent(new NonSeekableStream(new byte[5])), Headers = { Location = new Uri("https://evil.test") },
        }));
        Assert.Throws<NetworkQuotaExceededException>(() => bridge.RequestHttp(new NetworkRequest { Url = new("https://example.test"), MaxResponseBytes = 4 }));
        var response = bridge.RequestHttp(new NetworkRequest { Url = new("https://example.test"), MaxResponseBytes = 5 });
        Assert.Equal(302, response.StatusCode); Assert.Equal(5, response.Body.Length);
    }
}
