using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// タスク 2 (hardening) の回帰テスト集:
/// - fake System.* 型から特権 binding に到達できない (TrustedCoreLib domain 遮断)
/// - Interop+Kernel32::GetEnvironmentVariable へのゲスト直接呼出が拒否される
/// - LoadAssembly(Stream) が host current directory を探索しない
/// - 非 seekable な巨大 stream は MaxAssemblyBytes 付近で即拒否される
/// </summary>
public class SecurityHardeningTests {
    private static VirtualMachine CreateVm(bool loadCoreLib = true) =>
        new(new VmHostOptions { LoadHostCoreLib = loadCoreLib });

    /// <summary>(fake System.* 型を伴うゲスト) Kernel32 P/Invoke 呼出は
    /// TrustedCoreLib domain 特権面に到達できず OperationNotAllowed になる。</summary>
    [Fact]
    public void Fake_Kernel32PInvoke_Does_Not_Reach_Privileged_Binding() {
        using var vm = CreateVm();
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            """
            namespace Vm.Hardening {
                using System.Runtime.InteropServices;

                public static class FakeHooks {
                    [DllImport("kernel32")]
                    public static extern uint GetEnvironmentVariable(string name, char[] buffer, uint size);
                }

                public static class Ops {
                    public static string Run() {
                        var buffer = new char[64];
                        FakeHooks.GetEnvironmentVariable("SOME_HOST_ENV", buffer, (uint)buffer.Length);
                        return "got";
                    }
                }
            }
            """, "HardFakeKernel32"));
        vm.LoadAssembly(stream);
        // P/Invoke 面は fail-closed 拒否: (Kernel32 PInvokeReplacement binding は
        // trusted CoreLib 呼出専用のためゲスト直接呼出は domain 遮断)
        Assert.ThrowsAny<Exception>(() => vm.Invoke("Vm.Hardening.Ops", "Run"));
    }

    /// <summary>LoadAssembly(Stream) は host current directory を探索しない。</summary>
    [Fact]
    public void StreamLoad_DoesNot_Explore_CurrentDirectory() {
        // 依存 DLL を current directory (作業ディレクトリ) に配置しても
        // main を Stream ロードする際に自動探索が行われないことを検証:
        // main は dep 型を参照するが、dep 型はロード済み画像にのみ解決される
        // (Stream ロードは同一ディレクトリ探索を行わない)。
        var depDir = Directory.GetCurrentDirectory();
        var depPath = Path.Combine(depDir, "Vm.HardeningDep.dll");
        var depWritten = false;
        try {
            var depBytes = TestAssemblyCompiler.CompileToBytes(
                """
                namespace Vm.HardeningDep {
                    public class DepType { }
                }
                """, "Vm.HardeningDep");
            File.WriteAllBytes(depPath, depBytes);
            depWritten = true;

            // main は dep 型を参照する (AssemblyRef を持つため main DLL に依存宣言が入る)。
            // コンパイル時に dep をメタデータ参照として渡す (ゲストの正当な依存と同じ形状)。
            // MetadataReference.CreateFromFile(byte[]) は v4 不対応のため FileStream 経由で渡す
            var depTempPath = Path.Combine(Path.GetTempPath(), "Vm.HardeningDep.compileref.dll");
            File.WriteAllBytes(depTempPath, depBytes);
            using var mainStream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(
                """
                namespace Vm.Hardening {
                    using System;
                    public static class MainOps {
                        public static string TypeRef() => typeof(Vm.HardeningDep.DepType).FullName!;
                    }
                }
                """, "Vm.HardeningMain", extraReferences: [
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(depTempPath),
                ]));
            // Stream ロード (SourcePath = null) → 依存アセンブリの同一ディレクトリ探索なし
            using var vm = new VirtualMachine(new VmHostOptions());
            vm.LoadAssembly(mainStream, sourcePath: null);
            Assert.ThrowsAny<Exception>(() => vm.Invoke("Vm.Hardening.MainOps", "TypeRef"));
        } finally {
            if (depWritten)
                File.Delete(depPath);
        }
    }

    /// <summary>非 seekable な巨大 stream は MaxAssemblyBytes 付近で即拒否される。</summary>
    [Fact]
    public void NonSeekable_HugeStream_Is_Rejected_Promptly() {
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new() { MaxAssemblyBytes = 8 * 1024 },
        });
        var hugeData = new byte[64 * 1024];
        var wrapped = new FakeNonSeekableStream(hugeData);
        // 読み込み途中で拒否される (上限 + 直近 1 チャンクを超えて要求しない)
        var ex = Assert.Throws<OperationNotAllowedException>(() => vm.LoadAssembly(wrapped));
        Assert.Contains("上限", ex.Message);
        Assert.True(wrapped.BytesReadHint <= 8 * 1024 + 81920,
            $"非 seekable 入力が上限付近で打ち切られていません (読込 {wrapped.BytesReadHint:N0} バイト)。" +
            "巨大 stream を丸ごと buffer してから判定していないか。");
    }

    /// <summary>非 seekable 擬態 stream (CanSeek = false、PositionProvider は非対応)。
    /// おける Read 要求のみ対応し、実際のバイトの読み出しも行う。</summary>
    private sealed class FakeNonSeekableStream : Stream {
        private readonly byte[] _data;
        private long _position;

        public FakeNonSeekableStream(byte[] data) => _data = data;

        public long BytesReadHint => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;  // 非 seekable
        public override bool CanWrite => false;
        public override long Length => _data.Length; // 参照は VM からしない
        public override long Position {
            get => _position;
            set => throw new InvalidOperationException("非 seekable stream です。");
        }

        public override int Read(byte[] buffer, int offset, int count) {
            var remaining = _data.Length - _position;
            if (remaining <= 0)
                return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override int ReadByte() {
            if (_position >= _data.Length)
                return -1;
            return _data[_position++];
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("読み取り専用です。");
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new InvalidOperationException("非 seekable stream です。");
        public override void SetLength(long value) =>
            throw new InvalidOperationException("非 seekable stream です。");
    }
}
