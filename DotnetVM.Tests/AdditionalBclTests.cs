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
            return new() { StatusCode = StatusCode, Body = System.Text.Encoding.UTF8.GetBytes("返答") };
        }
    }

    [Fact]
    public void HttpClientUsesRestrictedGatewayAndGuestTasks() {
        var bridge = new Bridge();
        using var vm = Vm(new VmHostOptions { LoadHostCoreLib = true, NetworkBridge = bridge,
            Network = new NetworkPolicy { MaxBytesPerRequest = 100, Http = new HttpPolicy { AllowedOrigins = new[] { new Uri("https://example.test") }, AllowedMethods = new[] { "GET", "POST" } } } });
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
