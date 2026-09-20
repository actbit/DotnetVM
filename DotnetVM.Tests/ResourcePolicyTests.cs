using System.Reflection;
using DotnetVM.Devices;
using DotnetVM.Host;
using DotnetVM.Policy;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M7 リソース拒否の突合テスト。設計方針:
/// - **界面の再現制御**: ブリッジ未設定なら System.IO.File / System.Net.WebClient の
///   ファサード型自体を合成しない (ゲストにその面が存在しない = fail-closed)
/// - **プロキシ**: ゲートウェイは名前で許可判断しない。バイト計上とクォータのみ担い、
///   「どこを許すか」はブリッジ (ホスト実装のプロキシ) が判断する
/// - **intrinsic 呼出ゲート**: intrinsic 実行でも命令クォータを消費し、アロケーションを計上する
/// </summary>
public class ResourcePolicyTests {
    private const string Source = """
        using System;
        using System.IO;
        namespace Vm {
            public static class Policy {
                public static string ReadFile(string path) => File.ReadAllText(path);
                public static void WriteFile(string path, string text) => File.WriteAllText(path, text);

                public static string Download(string url) {
                    using var wc = new System.Net.WebClient();
                    return wc.DownloadString(url);
                }

                // intrinsic が新規文字列を確保する面 (アロケーション計上の検証用)
                public static string IntToString(int x) => x.ToString();

                // intrinsic 呼出ゲートのクォータ消費比較用
                public static int LoopOnly(int n) { var s = 0; for (var i = 0; i < n; i++) s++; return s; }
                public static int LoopWithWrite(int n) {
                    var s = 0;
                    for (var i = 0; i < n; i++) { Console.Write("."); s++; }
                    return s;
                }

                // Convert 拡充 (ToByte / オーバーフローはゲストで捕捉可能)
                public static int DoubleByte(byte b) => Convert.ToInt32(Convert.ToByte(b)) * 2;
                public static string CatchOverflow(long v) {
                    try { Convert.ToByte(v); return "ok"; }
                    catch (OverflowException) { return "overflow"; }
                }

                // 仮想コンソール (入力バインド)
                public static string EchoLine() => Console.ReadLine() ?? "";
                public static void Hello() => Console.WriteLine("hello");
            }
        }
        """;

    // System.Net.WebClient は現行 .NET の既定参照に無いため、契約アセンブリ (ファサードの
    // C# 側シグネチャ) をテスト側で用意して参照に渡す。ホストが自前の intrinsic 契約を
    // 持ち込むという intrinsic 機構の実用形と同じ構図
    private static readonly MetadataReference WebClientContractRef = MakeWebClientContract();

    private static MetadataReference MakeWebClientContract() {
        const string contract = """
            namespace System.Net {
                public class WebClient : System.IDisposable {
                    public byte[] DownloadData(string url) => [];
                    public string DownloadString(string url) => "";
                    public byte[] UploadData(string url, byte[] data) => [];
                    public void Dispose() { }
                }
            }
            """;
        var bytes = TestAssemblyCompiler.CompileToBytes(contract, "WebClientContract");
        return MetadataReference.CreateFromStream(new MemoryStream(bytes)); // 参照はプロセス存続中使う
    }

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, extraReferences: [WebClientContractRef]);
        return (Assembly.Load(bytes), bytes);
    }

    private static (Assembly Clr, byte[] Bytes) Compile() => Compiled;

    private static VirtualMachine CreateVm(INetworkBridge? network = null, IStorageBridge? storage = null,
        MemoryPolicy? memory = null) {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = memory ?? new MemoryPolicy { InstructionQuota = 100_000_000 },
            NetworkBridge = network,
            StorageBridge = storage,
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(string method, params object?[] args) {
        var (clr, _) = Compile();
        return clr.GetType("Vm.Policy")!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
    }

    // ---- フェイクブリッジ (ホスト実装のプロキシの代役) ----

    private sealed class FakeNetworkBridge : INetworkBridge {
        public Func<NetworkRequest, byte[]> Handler { get; set; } = _ => "ok"u8.ToArray();
        public List<NetworkRequest> Requests { get; } = [];

        public byte[] Request(NetworkRequest request) {
            Requests.Add(request);
            return Handler(request);
        }
    }

    private sealed class FakeStorageBridge : IStorageBridge {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Func<string, byte[]>? OnRead { get; set; } // 設定されていればプロキシ側の判断として優先

        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] Read(string path) =>
            OnRead is not null ? OnRead(path)
            : Files.TryGetValue(path, out var v) ? v
            : throw new StorageQuotaExceededException($"ファイルが見つかりません: {path}");
        public void Write(string path, ReadOnlyMemory<byte> contents) => Files[path] = contents.ToArray();
        public void Delete(string path) => Files.Remove(path);
    }

    // ---- 界面の再現制御 (ブリッジ未設定 = 面が存在しない) ----

    [Fact]
    public void FileSurface_ExistsOnlyWhenStorageBridgeConfigured() {
        using var vm = CreateVm();
        // ブリッジ未設定 → ファサード型が合成されず、呼出は「未登録 intrinsic」として拒否される
        Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("Vm.Policy", "ReadFile", "/data/a.txt"));

        using var vmWithBridge = CreateVm(storage: new FakeStorageBridge {
            Files = { ["/data/b.txt"] = "ok"u8.ToArray() },
        });
        // 同じゲストコードでもブリッジがあれば面が存在し、呼出が通る
        Assert.Equal("ok", vmWithBridge.Invoke("Vm.Policy", "ReadFile", "/data/b.txt"));
    }

    [Fact]
    public void WebClientSurface_ExistsOnlyWhenNetworkBridgeConfigured() {
        using var vm = CreateVm();
        // newobj の解決自体が失敗する (型が合成されていない)
        Assert.Throws<NotSupportedException>(() => vm.Invoke("Vm.Policy", "Download", "https://example.com/"));

        var bridge = new FakeNetworkBridge { Handler = _ => "body"u8.ToArray() };
        using var vmWithBridge = CreateVm(network: bridge);
        Assert.Equal("body", vmWithBridge.Invoke("Vm.Policy", "Download", "https://example.com/"));
        // ブリッジ (プロキシ) が要求を受け取っている
        var request = Assert.Single(bridge.Requests);
        Assert.Equal("example.com", request.Url.Host);
    }

    // ---- プロキシ経由のゲスト I/O (File) ----

    [Fact]
    public void FileReadWrite_GoesThroughProxy() {
        var bridge = new FakeStorageBridge();
        using var vm = CreateVm(storage: bridge);

        vm.Invoke("Vm.Policy", "WriteFile", "/data/a.txt", "hello gc");
        // ブリッジに届いている (VM 自身はファイルシステムに触れない)
        Assert.Equal("hello gc", System.Text.Encoding.UTF8.GetString(bridge.Files["/data/a.txt"]));
        Assert.Equal("hello gc", vm.Invoke("Vm.Policy", "ReadFile", "/data/a.txt"));
    }

    [Fact]
    public void ProxyHostDecision_PropagatesAsManagementException() {
        var bridge = new FakeStorageBridge {
            // ホスト側プロキシの許可判断: このパスは拒否する
            OnRead = _ => throw new StorageQuotaExceededException("許可されていないパスです。"),
        };
        using var vm = CreateVm(storage: bridge);
        // 管理例外としてゲストの catch を迂回しホストまで伝播する
        Assert.Throws<StorageQuotaExceededException>(() => vm.Invoke("Vm.Policy", "ReadFile", "/etc/passwd"));
    }

    // ---- ゲートウェイのバイトクォータ (単体) ----

    [Fact]
    public void NetworkGateway_CountsBytesAndEnforcesTotalQuota() {
        var bridge = new FakeNetworkBridge { Handler = _ => new byte[100] };
        var gateway = new NetworkGateway(new NetworkPolicy { TotalTransferByteLimit = 150 }, bridge);

        Assert.Equal(100, gateway.Transfer("https://a.example/x", ReadOnlyMemory<byte>.Empty).Length);
        Assert.Equal(100, gateway.TotalBytesTransferred);
        // 累計 100 + 100 = 200 > 150 → 拒否
        Assert.Throws<NetworkQuotaExceededException>(
            () => gateway.Transfer("https://a.example/x", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void NetworkGateway_EnforcesPerRequestQuota() {
        var gateway = new NetworkGateway(
            new NetworkPolicy { MaxBytesPerRequest = 50 }, new FakeNetworkBridge { Handler = _ => new byte[100] });
        Assert.Throws<NetworkQuotaExceededException>(
            () => gateway.Transfer("https://a.example/x", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void NetworkGateway_WithoutBridge_Rejects() {
        var gateway = new NetworkGateway(new NetworkPolicy(), bridge: null);
        Assert.Throws<OperationNotAllowedException>(
            () => gateway.Transfer("https://a.example/x", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void StorageGateway_CountsBytesAndEnforcesQuota() {
        var bridge = new FakeStorageBridge { Files = { ["/data/big.bin"] = new byte[100] } };
        var gateway = new StorageGateway(new StoragePolicy { TotalByteLimit = 150 }, bridge);

        Assert.Equal(100, gateway.Read("/data/big.bin").Length);
        Assert.Equal(100, gateway.TotalBytes);
        // 累計 100 + 書き込み 100 = 200 > 150 → 拒否
        Assert.Throws<StorageQuotaExceededException>(
            () => gateway.Write("/data/out.bin", new byte[100]));
    }

    [Fact]
    public void StorageGateway_WithoutBridge_Rejects() {
        var gateway = new StorageGateway(new StoragePolicy(), bridge: null);
        Assert.Throws<OperationNotAllowedException>(() => gateway.Read("/data/a.txt"));
    }

    // ---- intrinsic 呼出ゲート (IL 実行と等価な制約適用) ----

    [Fact]
    public void IntrinsicCall_ConsumesInstructionQuota() {
        using var vm = CreateVm();
        vm.Invoke("Vm.Policy", "LoopOnly", 100);
        var afterPlain = vm.InstructionCount;
        vm.Invoke("Vm.Policy", "LoopWithWrite", 100);
        var afterCalls = vm.InstructionCount;
        vm.Invoke("Vm.Policy", "LoopOnly", 100);
        var afterPlainAgain = vm.InstructionCount;

        var withCalls = afterCalls - afterPlain;
        var plainAgain = afterPlainAgain - afterCalls;
        // Console.Write 1 回あたり intrinsic ゲートの追加消費があるため、同ループより確実に多い
        Assert.True(withCalls > plainAgain + 100,
            $"intrinsic 呼出が命令クォータを消費していません (withCalls={withCalls}, plain={plainAgain})。");
    }

    [Fact]
    public void IntrinsicCall_ChargesAllocation() {
        // x.ToString() が確保する VmString (24 + 2*3 = 30 バイト) が計上される構え
        using var vm = CreateVm(memory: new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 20,
        });
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("Vm.Policy", "IntToString", 123));

        using var normal = CreateVm();
        Assert.Equal("123", normal.Invoke("Vm.Policy", "IntToString", 123));
    }

    // ---- Convert 拡充 ----

    [Fact]
    public void ConvertExtensions_MatchClr() {
        using var vm = CreateVm();
        Assert.Equal(200, vm.Invoke("Vm.Policy", "DoubleByte", (byte)100));
        Assert.Equal(200, RunClr("DoubleByte", (byte)100));
        // オーバーフローはゲスト例外 (OverflowException) としてゲストで捕捉可能
        Assert.Equal("overflow", vm.Invoke("Vm.Policy", "CatchOverflow", 300L));
        Assert.Equal("overflow", RunClr("CatchOverflow", 300L));
        Assert.Equal("ok", vm.Invoke("Vm.Policy", "CatchOverflow", 255L));
    }

    // ---- 仮想コンソールデバイスのバインド ----

    [Fact]
    public void ConsoleOutputWritten_EventObservesGuestOutput() {
        using var vm = CreateVm();
        var events = new List<ConsoleOutputEvent>();
        vm.Console.OutputWritten += ev => events.Add(ev);
        vm.Invoke("Vm.Policy", "Hello");
        Assert.Contains(vm.Console.OutputLog, ev => ev.Text == "hello\n");
        Assert.Contains(events, ev => ev.Text == "hello\n");
    }

    [Fact]
    public void ConsoleBindInput_SuppliesDeterministicReadLine() {
        using var vm = CreateVm();
        var queue = new Queue<string>(["first", "second"]);
        vm.Console.BindInput(() => queue.Dequeue());
        Assert.Equal("first", vm.Invoke("Vm.Policy", "EchoLine"));
        Assert.Equal("second", vm.Invoke("Vm.Policy", "EchoLine"));
    }

    [Fact]
    public void ConsoleBindImplementation_ReplacesWholeDevice() {
        using var vm = CreateVm();
        var written = new List<string>();
        var inputs = new Queue<string>(["custom"]);
        vm.Console.BindImplementation(new TestBinding(written, inputs));

        Assert.Equal("custom", vm.Invoke("Vm.Policy", "EchoLine"));
        vm.Invoke("Vm.Policy", "Hello");
        Assert.Equal(["hello\n"], written);
    }

    private sealed class TestBinding(List<string> written, Queue<string> inputs) : IVirtualConsoleBinding {
        public void Write(bool isError, string text) => written.Add(text);
        public string? ReadLine() => inputs.Count > 0 ? inputs.Dequeue() : null;
    }
}
