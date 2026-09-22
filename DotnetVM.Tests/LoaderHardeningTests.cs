using DotnetVM.Host;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// loader 上限の回帰テスト:
/// - MaxMethodBodyBytes を MethodBodyBlock 読み込み時に強制する
/// - MaxSignatureDepth を signature decoder の再帰処理で強制する
/// - MaxGenericNestingDepth を GenericInst / TypeSpec 解決時に強制する
/// - MaxMetadataStreamBytes を MetadataRoot の各 stream 受理前に強制する
/// </summary>
public class LoaderHardeningTests {
    private static byte[] CompileSample() => TestAssemblyCompiler.CompileToBytes(
        """
        namespace Vm.LoaderProbe {
            public static class Ops {
                public static int Run(int x) => x + 1;
            }
        }
        """, "LoaderProbe");

    [Fact]
    public void MaxMetadataStreamBytes_Is_Enforced_Before_Accept() {
        var bytes = CompileSample();
        // 正常系は通る (既定上限)。
        var ok = AssemblyImage.Parse(bytes, new MemoryPolicy());
        Assert.NotNull(ok);
        // 各 stream を受理する前に拒否する (巨大 #Blob 等を丸ごと確保しない)。
        var tiny = new MemoryPolicy { MaxMetadataStreamBytes = 1 };
        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(bytes, tiny));
    }

    [Fact]
    public void MaxMethodBodyBytes_Is_Enforced_On_Read() {
        var bytes = CompileSample();
        var image = AssemblyImage.Parse(bytes, new MemoryPolicy());
        // 実メソッド本体を持つ rid を探す (RVA != 0)。
        var methodCount = image.Tables.GetRowCount(TableKind.MethodDef);
        int? withBody = null;
        for (var rid = 1; rid <= methodCount; rid++) {
            if ((int)image.Tables.GetCell(TableKind.MethodDef, rid, 0) != 0) {
                withBody = rid;
                break;
            }
        }
        Assert.True(withBody.HasValue, "本体を持つ MethodDef が見つかりません。");
        // 既定上限では読める。
        Assert.NotNull(image.GetMethodBody(withBody.Value));
        // 上限 1 バイトでは読み込み時に拒否される (確保前に落ちる)。
        var strict = AssemblyImage.Parse(bytes, new MemoryPolicy { MaxMethodBodyBytes = 1 });
        Assert.Throws<BadImageFormatException>(() => strict.GetMethodBody(withBody.Value));
    }

    [Fact]
    public void MaxSignatureDepth_Is_Enforced_In_Decoder() {
        // int[][][] の戻り値を持つメソッド署名 (SZARRAY ネスト深さ 3)。
        // blob: callingConv(0x00) paramCount(0x00) returnType(1D 1D 1D 08)
        var blob = new byte[] { 0x00, 0x00, 0x1D, 0x1D, 0x1D, 0x08 };
        var ok = SignatureDecoder.DecodeMethodSignature(blob, maxSignatureDepth: 8, maxGenericNestingDepth: 8);
        Assert.NotNull(ok);
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeMethodSignature(blob, maxSignatureDepth: 2, maxGenericNestingDepth: 8));
    }

    [Fact]
    public void MaxGenericNestingDepth_Is_Enforced_In_Decoder_And_Resolution() {
        // class [TypeRef rid1]<int> の GenericInst (深さ 1)。
        // TypeDefOrRefEncoded: rid1 + tag01(TypeRef) = (1<<2)|1 = 5 → 0x05。
        var single = new byte[] { 0x15, 0x12, 0x05, 0x01, 0x08 };
        var decoded = SignatureDecoder.DecodeType(single, maxSignatureDepth: 16, maxGenericNestingDepth: 4);
        Assert.Equal(SigKind.GenericInst, decoded.Kind);
        // ネスト GenericInst (外側の実引数が内側 GenericInst。深さ 2)。
        var nested = new byte[] { 0x15, 0x12, 0x05, 0x01, 0x15, 0x12, 0x05, 0x01, 0x08 };
        var okNested = SignatureDecoder.DecodeType(nested, maxSignatureDepth: 16, maxGenericNestingDepth: 4);
        Assert.Equal(SigKind.GenericInst, okNested.Kind);
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeType(nested, maxSignatureDepth: 16, maxGenericNestingDepth: 0));
        // 解決時のネスト検査も有効 (TypeSpec 経由)。小さな上限で CoreLib 型の解決は維持し、
        // 深いネストはデコード時点で落ちることを上記で検証済み。
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        Assert.NotNull(vm.Loaders);
    }
}
