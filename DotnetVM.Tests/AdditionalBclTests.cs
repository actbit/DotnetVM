using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.IO.Compression;
using DotnetVM.Host;
using DotnetVM.Policy;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

public sealed class AdditionalBclTests {
    private const string Source = """
        using System;
        using System.IO;
        using System.IO.Compression;
        using System.Text;
        using System.Text.RegularExpressions;
        using System.Security.Cryptography;
        using System.Net.Http;
        public static class ExtraChecks {
            private static HttpResponseMessage pendingResponse;
            public static int HttpOpenHeaders(string url) {
                var client = new HttpClient(); pendingResponse = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                int values = 0; foreach (var value in pendingResponse.Headers.GetValues("Set-Cookie")) values++;
                return values;
            }
            public static string HttpReadPending() {
                using (pendingResponse) {
                    var bytes = pendingResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    return bytes.Length + "|" + pendingResponse.TrailingHeaders.Contains("X-End");
                }
            }
            private sealed class CustomContent : HttpContent {
                protected override bool TryComputeLength(out long length) { length = 3; return true; }
                protected override System.Threading.Tasks.Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) => stream.WriteAsync(new byte[] {4,5,6}, 0, 3);
            }
            private sealed class SyncHandler : HttpMessageHandler {
                public bool Disposed;
                protected override HttpResponseMessage Send(HttpRequestMessage request, System.Threading.CancellationToken token) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("sync") };
                protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken token) => System.Threading.Tasks.Task.FromResult(Send(request, token));
                protected override void Dispose(bool disposing) { Disposed = disposing; base.Dispose(disposing); }
            }
            public static string HttpExtended(string url) {
                using var c = new HttpClient(); using var request = new HttpRequestMessage(HttpMethod.Post, url);
                var key = new HttpRequestOptionsKey<int>("number"); request.Options.Set(key, 42);
                bool found = request.Options.TryGetValue(key, out int value);
                using var multipart = new MultipartFormDataContent("boundary");
                multipart.Add(new CustomContent(), "custom", "a.bin"); request.Content = multipart;
                using var response = c.SendAsync(request).GetAwaiter().GetResult();
                using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                var buffer = new byte[12]; int count = stream.ReadAsync(buffer.AsMemory(2, 8)).AsTask().GetAwaiter().GetResult();
                return found + "|" + value + "|" + response.Content.Headers.ContentLength.HasValue + "|" + Encoding.UTF8.GetString(buffer, 2, count);
            }
            public static string HttpSyncHandler() {
                var handler = new SyncHandler(); string text;
                using (var c = new HttpClient(handler)) using (var response = c.Send(new HttpRequestMessage(HttpMethod.Get, "https://example.test/"))) {
                    text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                return text + "|" + handler.Disposed;
            }
            public static bool HttpCancelPending(string url) {
                using var c = new HttpClient(); var first = c.GetAsync(url); var second = c.GetAsync(url); c.CancelPendingRequests();
                try { first.GetAwaiter().GetResult(); return false; } catch (OperationCanceledException) { }
                try { second.GetAwaiter().GetResult(); return false; } catch (OperationCanceledException) { }
                return first.IsCanceled && second.IsCanceled;
            }
            private sealed class DecoratingHandler : DelegatingHandler {
                public DecoratingHandler() { InnerHandler = new HttpClientHandler(); }
                protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken token) {
                    request.Headers.Add("X-Request", "handler");
                    return base.SendAsync(request, token);
                }
            }
            private sealed class MockHandler : HttpMessageHandler {
                protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken token) =>
                    System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Accepted) { Content = new StringContent("mock:" + request.Method.Method) });
            }
            public static string HttpHandler(string url, bool mock) {
                using var client = new HttpClient(mock ? (HttpMessageHandler)new MockHandler() : new DecoratingHandler());
                return client.GetStringAsync(url).GetAwaiter().GetResult();
            }
            public static string RegexCheck() {
                var regex = new Regex(@"(?<word>\p{L}+)-(\d+)", RegexOptions.CultureInvariant);
                var match = regex.Match("日本-123");
                return match.Success + "|" + match.Groups["word"].Value + "|" + match.Groups[2].Value + "|" +
                    regex.Replace("日本-123", "$2:$1") + "|" + Regex.IsMatch("ABC", "abc", RegexOptions.IgnoreCase) + "|" + Regex.Split("a,b;c", "[,;]").Length;
            }
            public static bool RegexTimeout() {
                try { return Regex.IsMatch(new string('a', 2048) + "!", "^(a+)+$"); }
                catch (RegexMatchTimeoutException) { return true; }
            }
            public static int RegexExpansion() => Regex.Replace(new string('a', 2048), "a", "$'").Length;
            public static long Decompress(byte[] bytes) {
                using var input = new MemoryStream(bytes); using var decoder = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(); decoder.CopyTo(output); return output.Length;
            }
            public static string CompressionDispose(bool leaveOpen) {
                var memory = new MemoryStream(); var encoder = new GZipStream(memory, CompressionMode.Compress, leaveOpen);
                encoder.Write(new byte[] { 1, 2, 3 }); encoder.Dispose();
                bool closed = false;
                try { _ = memory.Length; } catch (ObjectDisposedException) { closed = true; }
                return memory.CanRead + "|" + closed + "|" + (memory.ToArray().Length > 0);
            }
            public static string Hashes() {
                var bytes = Encoding.UTF8.GetBytes("日本😀");
                Span<byte> destination = stackalloc byte[32];
                int count = SHA256.HashData(bytes.AsSpan(), destination);
                bool same = CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), destination);
                CryptographicOperations.ZeroMemory(destination);
                return Convert.ToHexString(SHA256.HashData(bytes)) + "|" + SHA384.HashData(bytes).Length + "|" +
                    SHA512.HashData(bytes).Length + "|" + Convert.ToHexString(HMACSHA256.HashData(new byte[] {1,2,3}, bytes)) + "|" + count + "|" + same + "|" + destination[0];
            }
            public static string AesCheck() {
                using var aes = Aes.Create(); aes.Key = new byte[32];
                var iv = new byte[16]; var input = Encoding.UTF8.GetBytes("日本😀 AES");
                var encrypted = aes.EncryptCbc(input, iv, PaddingMode.PKCS7);
                return Convert.ToHexString(encrypted) + "|" + Encoding.UTF8.GetString(aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7));
            }
            public static string AesTransforms() {
                using var aes = Aes.Create(); aes.Key = new byte[16]; aes.IV = new byte[16]; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using var encryptor = aes.CreateEncryptor(); var input = Encoding.UTF8.GetBytes("AES streaming 日本");
                using var memory = new MemoryStream();
                using (var stream = new CryptoStream(memory, encryptor, CryptoStreamMode.Write, true)) { stream.Write(input, 0, input.Length); stream.FlushFinalBlock(); }
                var encrypted = memory.ToArray();
                using var decryptor = aes.CreateDecryptor();
                var plain = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
                return Convert.ToHexString(encrypted) + "|" + Encoding.UTF8.GetString(plain) + "|" + aes.KeySize + "|" + aes.LegalKeySizes[0].MinSize;
            }
            public static string AesGcmCheck() {
                using var aes = new AesGcm(new byte[16], 16); var nonce = new byte[12]; var plain = new byte[16]; var encrypted = new byte[16]; var tag = new byte[16];
                aes.Encrypt(nonce, plain, encrypted, tag); var output = new byte[16]; aes.Decrypt(nonce, encrypted, tag, output);
                tag[0] ^= 1; output[0] = 99; bool rejected = false;
                try { aes.Decrypt(nonce, encrypted, tag, output); } catch (CryptographicException) { rejected = true; }
                return Convert.ToHexString(encrypted) + "|" + Convert.ToHexString(tag) + "|" + rejected + "|" + output[0];
            }
            public static string AesSpan() {
                using var aes = Aes.Create(); aes.Key = new byte[16]; var plain = new byte[] {1,2,3}; var iv = new byte[16];
                Span<byte> cipher = stackalloc byte[32]; Span<byte> output = stackalloc byte[32];
                bool small = aes.TryEncryptCbc(plain.AsSpan(), iv.AsSpan(), cipher.Slice(0, 1), out int none);
                bool success = aes.TryEncryptCbc(plain.AsSpan(), iv.AsSpan(), cipher, out int count);
                int read = aes.DecryptCbc(cipher.Slice(0,count), iv.AsSpan(), output);
                Span<byte> key = stackalloc byte[16]; key.Clear(); using var gcm = new AesGcm(key, 16);
                Span<byte> nonce = stackalloc byte[12]; nonce.Clear(); Span<byte> tag = stackalloc byte[16];
                gcm.Encrypt(nonce, plain.AsSpan(), cipher.Slice(0,3), tag); gcm.Decrypt(nonce, cipher.Slice(0,3), tag, output.Slice(0,3));
                return small + "|" + none + "|" + success + "|" + count + "|" + read + "|" + Convert.ToHexString(output.Slice(0,read));
            }
            public static string Compress(int kind) {
                var input = Encoding.UTF8.GetBytes("hello日本😀hellohello");
                using var compressed = new MemoryStream();
                Stream encoder = kind == 0 ? (Stream)new GZipStream(compressed, CompressionLevel.Optimal, true) : kind == 1 ? (Stream)new DeflateStream(compressed, CompressionLevel.Fastest, true) : new BrotliStream(compressed, CompressionLevel.Optimal, true);
                encoder.Write(input, 0, input.Length); encoder.Dispose();
                compressed.Position = 0;
                Stream decoder = kind == 0 ? (Stream)new GZipStream(compressed, CompressionMode.Decompress, true) : kind == 1 ? (Stream)new DeflateStream(compressed, CompressionMode.Decompress, true) : new BrotliStream(compressed, CompressionMode.Decompress, true);
                using var output = new MemoryStream(); decoder.CopyTo(output); decoder.Dispose();
                return Encoding.UTF8.GetString(output.ToArray());
            }
            public static string HttpGet(string url) { using var c = new HttpClient(); return c.GetStringAsync(url).GetAwaiter().GetResult(); }
            public static int HttpBytes(string url) { using var c = new HttpClient(); return c.GetByteArrayAsync(url).GetAwaiter().GetResult().Length; }
            public static bool HttpStatusFailure(string url) {
                using var c = new HttpClient();
                try { c.GetStringAsync(url).GetAwaiter().GetResult(); return false; } catch (HttpRequestException) { return true; }
            }
            public static bool HttpDisposeCanceled(string url) {
                var c = new HttpClient(); var task = c.GetStringAsync(url); c.Dispose();
                try { task.GetAwaiter().GetResult(); return false; } catch (OperationCanceledException) { return task.IsCanceled; }
            }
            public static bool HttpCanceled(string url) {
                using var c = new HttpClient(); using var stop = new System.Threading.CancellationTokenSource(); stop.Cancel();
                var task = c.GetStringAsync(url, stop.Token);
                try { task.GetAwaiter().GetResult(); return false; } catch (OperationCanceledException) { return task.IsCanceled; }
            }
            public static string HttpPost(string url) {
                using var c = new HttpClient(); using var content = new StringContent("日本");
                using var response = c.PostAsync(url, content).GetAwaiter().GetResult();
                return (int)response.StatusCode + "|" + response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            public static string HttpSend(string url) {
                using var c = new HttpClient(new HttpClientHandler(), true);
                c.BaseAddress = new Uri(url); c.Timeout = TimeSpan.FromMilliseconds(500);
                c.DefaultRequestHeaders.Add("X-Default", "first");
                c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                c.DefaultRequestVersion = new Version(2, 0); c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("edit?q=1", UriKind.Relative));
                request.Headers.Add("X-Request", "second");
                request.Version = new Version(2, 0); request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Content = new StringContent("日本", Encoding.UTF8, "application/json");
                using var response = c.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                response.Headers.TryGetValues("X-Reply", out var values);
                using var body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var memory = new MemoryStream(); body.CopyTo(memory);
                bool reused = false; try { c.SendAsync(request).GetAwaiter().GetResult(); } catch (InvalidOperationException) { reused = true; }
                return (int)response.StatusCode + "|" + Encoding.UTF8.GetString(memory.ToArray()) + "|" + string.Join(",", values) + "|" +
                    response.Content.Headers.ContentType.MediaType + "|" + response.Version + "|" + response.RequestMessage.RequestUri.AbsolutePath + "|" + reused;
            }
            public static string HttpVerbs(string url, int kind) {
                using var c = new HttpClient(); c.BaseAddress = new Uri(url);
                using var bytes = new ByteArrayContent(new byte[] {0, 1, 255}, 1, 2);
                using var response = kind == 0 ? c.PutAsync(new Uri("put", UriKind.Relative), bytes).GetAwaiter().GetResult() :
                    kind == 1 ? c.PatchAsync("patch", bytes).GetAwaiter().GetResult() : kind == 2 ? c.DeleteAsync("delete").GetAwaiter().GetResult() :
                    c.Send(new HttpRequestMessage(HttpMethod.Head, "head"), HttpCompletionOption.ResponseContentRead);
                return response.StatusCode.ToString();
            }
            public static bool HttpTimeout(string url) {
                using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(20) };
                try { c.GetAsync(new Uri(url), HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult(); return false; }
                catch (OperationCanceledException) { return true; }
            }
            public static string HttpForm(string url) {
                using var c = new HttpClient();
                using var content = new FormUrlEncodedContent(new [] { new System.Collections.Generic.KeyValuePair<string,string>("name", "日本 x") });
                using var response = c.PostAsync(new Uri(url), content).GetAwaiter().GetResult();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            public static int Random() { Span<byte> bytes = stackalloc byte[4]; RandomNumberGenerator.Fill(bytes); return bytes[0] + RandomNumberGenerator.GetBytes(2)[1]; }
        }
        """;
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> Guest = new(() => {
        var refs = new[] { typeof(Regex).Assembly, typeof(SHA256).Assembly, typeof(GZipStream).Assembly, typeof(BrotliStream).Assembly, typeof(HttpClient).Assembly, typeof(Uri).Assembly, typeof(System.Net.HttpStatusCode).Assembly }.Distinct().Select(a => MetadataReference.CreateFromFile(a.Location)).ToArray();
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "AdditionalBcl", extraReferences: refs);
        return (bytes, Assembly.Load(bytes));
    });
    private static VirtualMachine Vm(VmHostOptions? options = null) { var vm = new VirtualMachine(options ?? new VmHostOptions { LoadHostCoreLib = true }); vm.LoadAssembly(new MemoryStream(Guest.Value.Bytes)); return vm; }

    [Theory]
    [InlineData("RegexCheck", -1)]
    [InlineData("Hashes", -1)]
    [InlineData("AesCheck", -1)]
    [InlineData("Compress", 0)]
    [InlineData("Compress", 1)]
    [InlineData("Compress", 2)]
    public void GuestMatchesClr(string method, int kind) {
        object?[] args = kind < 0 ? [] : [kind];
        var expected = Guest.Value.Clr.GetType("ExtraChecks")!.GetMethod(method)!.Invoke(null, args);
        using var vm = Vm(); Assert.Equal(expected, vm.Invoke("ExtraChecks", method, args));
    }

    private sealed class Bridge : IHttpNetworkBridge {
        public int Calls;
        public bool WaitForCancellation;
        public int StatusCode = 201;
        public NetworkRequest? Last;
        public byte[] Request(NetworkRequest request) => RequestHttp(request).Body;
        public HttpNetworkResponse RequestHttp(NetworkRequest request) {
            Calls++; Last = request;
            if (WaitForCancellation) { request.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)); request.CancellationToken.ThrowIfCancellationRequested(); }
            return new() { StatusCode = StatusCode, Body = System.Text.Encoding.UTF8.GetBytes("返答"),
                Headers = new Dictionary<string,string> { ["X-Reply"] = "answer", ["Content-Type"] = "application/json; charset=utf-8" }, Version = new Version(2,0) };
        }
    }
    private sealed class ObservedBody(byte[] bytes) : MemoryStream(bytes, false) {
        public int Reads;
        public bool Disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { Reads++; return base.ReadAsync(buffer, token); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class StreamingBridge : IStreamingHttpNetworkBridge {
        public ObservedBody Body = new(new byte[] {1,2,3,4,5,6});
        public byte[] Request(NetworkRequest request) => throw new InvalidOperationException();
        public HttpNetworkResponse RequestHttp(NetworkRequest request) => throw new InvalidOperationException();
        public StreamingHttpResponse OpenHttp(NetworkRequest request) => new() { Body = Body,
            Headers = new Dictionary<string,string[]> { ["Set-Cookie"] = ["a=1", "b=2"] },
            TrailingHeaders = () => new Dictionary<string,string[]> { ["X-End"] = ["done"] } };
    }
    [Fact]
    public void ResponseHeadersReadStreamsAndPreservesRepeatedHeadersAndTrailers() {
        var bridge = new StreamingBridge();
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
            Network = new NetworkPolicy { Http = new HttpPolicy { AllowedOrigins = [new Uri("https://example.test")] } } });
        Assert.Equal(2, vm.Invoke("ExtraChecks", "HttpOpenHeaders", "https://example.test/"));
        Assert.Equal(0, bridge.Body.Reads);
        Assert.Equal("6|True", vm.Invoke("ExtraChecks", "HttpReadPending"));
        Assert.True(bridge.Body.Disposed);
    }
    [Fact]
    public void StreamingBodyLimitsAreEnforcedDuringReads() {
        var bridge = new StreamingBridge();
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
            Network = new NetworkPolicy { Http = new HttpPolicy { AllowedOrigins = [new Uri("https://example.test")], MaxResponseBodyBytes = 4 } } });
        Assert.Equal(2, vm.Invoke("ExtraChecks", "HttpOpenHeaders", "https://example.test/"));
        Assert.Throws<NetworkQuotaExceededException>(() => vm.Invoke("ExtraChecks", "HttpReadPending"));
        Assert.True(bridge.Body.Disposed);
    }

    [Fact]
    public void HttpOptionsCustomMultipartContentAndMemoryReadsWork() {
        var bridge = new Bridge(); using var vm = HttpVm(bridge);
        Assert.Equal("True|42|True|返答", vm.Invoke("ExtraChecks", "HttpExtended", "https://example.test/path"));
        var bytes = bridge.Last!.Body.ToArray();
        Assert.Contains("filename=a.bin", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.AsSpan().IndexOf(new byte[] {4,5,6}) >= 0);
    }
    [Fact]
    public void HttpSynchronousHandlerAndDisposeOverrideWork() {
        using var vm = Vm(); Assert.Equal("sync|True", vm.Invoke("ExtraChecks", "HttpSyncHandler"));
    }
    [Fact]
    public void CancelPendingRequestsCancelsConcurrentRequests() {
        var bridge = new Bridge { WaitForCancellation = true }; using var vm = HttpVm(bridge);
        Assert.Equal(true, vm.Invoke("ExtraChecks", "HttpCancelPending", "https://example.test/path"));
    }

    [Fact]
    public void HttpSendPreservesMessagesHeadersUriAndVersion() {
        var bridge = new Bridge();
        using var vm = HttpVm(bridge);
        Assert.Equal("201|返答|answer|application/json|2.0|/api/edit|True", vm.Invoke("ExtraChecks", "HttpSend", "https://example.test/api/"));
        Assert.Equal("PATCH", bridge.Last!.Method); Assert.Equal("/api/edit?q=1", bridge.Last.Url.PathAndQuery);
        Assert.Equal("first", bridge.Last.Headers["X-Default"]); Assert.Equal("second", bridge.Last.Headers["X-Request"]);
        Assert.Equal("application/json", bridge.Last.Headers["Accept"]); Assert.Equal("application/json; charset=utf-8", bridge.Last.Headers["Content-Type"]);
        Assert.Equal(new Version(2,0), bridge.Last.Version); Assert.Equal(HttpVersionPolicy.RequestVersionExact, bridge.Last.VersionPolicy);
        Assert.Equal(1, bridge.Calls);
    }
    [Fact]
    public void GuestHttpHandlersRunAndForwardThroughPolicy() {
        var bridge = new Bridge(); using var vm = HttpVm(bridge);
        Assert.Equal("mock:GET", vm.Invoke("ExtraChecks", "HttpHandler", "https://example.test/mock", true));
        Assert.Equal(0, bridge.Calls);
        Assert.Equal("返答", vm.Invoke("ExtraChecks", "HttpHandler", "https://example.test/real", false));
        Assert.Equal("handler", bridge.Last!.Headers["X-Request"]);
        Assert.Equal(1, bridge.Calls);
    }
    [Theory]
    [InlineData("AesTransforms")]
    [InlineData("AesGcmCheck")]
    [InlineData("AesSpan")]
    public void AesTransformsAndAuthenticatedEncryptionMatchClr(string method) {
        var expected = Guest.Value.Clr.GetType("ExtraChecks")!.GetMethod(method)!.Invoke(null, null);
        using var vm = Vm(); Assert.Equal(expected, vm.Invoke("ExtraChecks", method));
    }
    private static VirtualMachine HttpVm(Bridge bridge, IReadOnlyCollection<string>? headers = null) => Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
        Network = new NetworkPolicy { Http = new HttpPolicy { AllowedOrigins = [new Uri("https://example.test")], AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"],
            AllowedRequestHeaders = headers ?? ["Content-Type", "Accept", "X-Default", "X-Request"] } } });
    [Theory]
    [InlineData(0, "PUT")]
    [InlineData(1, "PATCH")]
    [InlineData(2, "DELETE")]
    [InlineData(3, "HEAD")]
    public void HttpAllVerbsUseGateway(int kind, string method) {
        var bridge = new Bridge(); using var vm = HttpVm(bridge);
        Assert.Equal("Created", vm.Invoke("ExtraChecks", "HttpVerbs", "https://example.test/api/", kind));
        Assert.Equal(method, bridge.Last!.Method); Assert.Equal("/api/" + method.ToLowerInvariant(), bridge.Last.Url.AbsolutePath);
        if (kind < 2) Assert.Equal(new byte[] {1,255}, bridge.Last.Body.ToArray());
    }
    [Fact]
    public void HttpTimeoutAndFormAreSupported() {
        var bridge = new Bridge { WaitForCancellation = true }; using var vm = HttpVm(bridge);
        Assert.Equal(true, vm.Invoke("ExtraChecks", "HttpTimeout", "https://example.test/path"));
        bridge.WaitForCancellation = false;
        Assert.Equal("返答", vm.Invoke("ExtraChecks", "HttpForm", "https://example.test/path"));
        Assert.Equal("name=%E6%97%A5%E6%9C%AC+x", System.Text.Encoding.UTF8.GetString(bridge.Last!.Body.Span));
    }
    [Fact]
    public void HeadersCannotBypassHttpPolicy() {
        var bridge = new Bridge(); using var vm = HttpVm(bridge, []);
        Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("ExtraChecks", "HttpSend", "https://example.test/api/"));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void HttpClientUsesRestrictedGatewayAndGuestTasks() {
        var bridge = new Bridge();
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
            Network = new NetworkPolicy { MaxBytesPerRequest = 100, Http = new HttpPolicy { AllowedOrigins = new[] { new Uri("https://example.test") }, AllowedMethods = new[] { "GET", "POST" }, AllowedRequestHeaders = ["Content-Type"] } } });
        Assert.Equal("返答", vm.Invoke("ExtraChecks", "HttpGet", "https://example.test/path"));
        Assert.Equal("201|返答", vm.Invoke("ExtraChecks", "HttpPost", "https://example.test/path"));
        Assert.Equal("POST", bridge.Last!.Method); Assert.Equal("日本", System.Text.Encoding.UTF8.GetString(bridge.Last.Body.Span));
        Assert.Equal(2, bridge.Calls);
        Assert.Equal(true, vm.Invoke("ExtraChecks", "HttpCanceled", "https://example.test/path"));
        Assert.Equal(2, bridge.Calls);
        Assert.Equal(6, vm.Invoke("ExtraChecks", "HttpBytes", "https://example.test/path"));
        Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("ExtraChecks", "HttpGet", "https://example.test.evil/path"));
        Assert.Equal(3, bridge.Calls);
    }

    [Fact]
    public void CryptoUsesVmRandomSource() {
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, RandomFill = static bytes => bytes.Fill(7) });
        Assert.Equal(14, vm.Invoke("ExtraChecks", "Random"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressionRespectsLeaveOpen(bool leaveOpen) {
        using var vm = Vm();
        Assert.Equal(Guest.Value.Clr.GetType("ExtraChecks")!.GetMethod("CompressionDispose")!.Invoke(null, [leaveOpen]),
            vm.Invoke("ExtraChecks", "CompressionDispose", leaveOpen));
    }

    [Fact]
    public void HttpClientWithoutCapabilityCannotReachBridge() {
        var bridge = new Bridge();
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge });
        Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("ExtraChecks", "HttpGet", "https://example.test/path"));
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public void HttpClientPropagatesStatusFailureAndDisposeCancellation() {
        var bridge = new Bridge { StatusCode = 404 };
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
            Network = new NetworkPolicy { Http = new HttpPolicy { AllowedOrigins = new[] { new Uri("https://example.test") } } } });
        Assert.Equal(true, vm.Invoke("ExtraChecks", "HttpStatusFailure", "https://example.test/path"));
        bridge.WaitForCancellation = true;
        Assert.Equal(true, vm.Invoke("ExtraChecks", "HttpDisposeCanceled", "https://example.test/path"));
    }

    [Fact]
    public void RegexTimeoutAndDecompressionGrowthAreBounded() {
        using (var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, Memory = new MemoryPolicy { MaxRegexMatchTimeoutMilliseconds = 5 } }))
            Assert.Equal(true, vm.Invoke("ExtraChecks", "RegexTimeout"));
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(new byte[1024 * 1024]);
        using var bounded = Vm(new VmHostOptions { LoadHostCoreLib = true, Memory = new MemoryPolicy { HostTempAllocationByteLimit = 64 * 1024 } });
        Assert.Throws<MemoryQuotaExceededException>(() => bounded.Invoke("ExtraChecks", "Decompress", compressed.ToArray()));
    }

    [Fact]
    public void RegexReplacementTokensCannotBypassHostAllocationBudget() {
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, Memory = new MemoryPolicy { HostTempAllocationByteLimit = 64 * 1024 } });
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("ExtraChecks", "RegexExpansion"));
    }
}
