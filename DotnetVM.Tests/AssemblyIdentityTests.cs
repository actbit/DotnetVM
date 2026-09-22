using DotnetVM.Host;
using DotnetVM.Metadata;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// タスク 2 hardening (trusted identity): AssemblyIdentity (Name / Version / Culture /
/// PublicKeyToken) の解析と照合規則の回帰。単純名一致だけでなく公開鍵トークンの一致を
/// 要求することで、同名別 identity のアセンブリへ誤結合しないことを検証する。
/// </summary>
public class AssemblyIdentityTests {
    /// <summary>ホスト System.Private.CoreLib の identity が期待どおり (strong-named)。</summary>
    [Fact]
    public void HostCoreLib_Has_Expected_Identity() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        var coreLib = vm.Loaders
            .Select(l => l.Image)
            .First(i => i.Identity.Name == "System.Private.CoreLib");
        Assert.Equal("System.Private.CoreLib", coreLib.Identity.Name);
        Assert.True(coreLib.Identity.IsStrongNamed,
            $"CoreLib が strong-named として解析されていません: {coreLib.Identity}");
        Assert.True(coreLib.Identity.PublicKeyToken.Length == 16,
            $"公開鍵トークンが 16 進 16 文字ではありません: '{coreLib.Identity.PublicKeyToken}'");
        Assert.True(coreLib.Identity.Version.Major > 0,
            $"CoreLib の Version が解析されていません: {coreLib.Identity.Version}");
    }

    /// <summary>照合規則: 名前一致 + (参照側が strong-named なら) トークン一致を要求する。</summary>
    [Fact]
    public void Matches_Requires_Name_And_Token() {
        var token = "7cec85d7bea7798e";
        var reference = new AssemblyIdentity("System.Runtime", new Version(10, 0, 0, 0), "neutral", token);
        // 名前 + トークン一致 → 一致 (Version 差は soft)
        Assert.True(reference.Matches(new AssemblyIdentity("System.Runtime", new Version(9, 0, 0, 0), "neutral", token)));
        // 名前は同じだがトークンが異なる → 不一致 (同名別 identity への誤結合防止)
        Assert.False(reference.Matches(new AssemblyIdentity("System.Runtime", new Version(10, 0, 0, 0), "neutral", "0000000000000000")));
        // 名前が異なる → 不一致
        Assert.False(reference.Matches(new AssemblyIdentity("System.Collections", new Version(10, 0, 0, 0), "neutral", token)));
    }

    /// <summary>トークン無し (非 strong-named) の参照は名前一致のみで照合する。</summary>
    [Fact]
    public void Matches_NonStrongNamed_Is_NameOnly() {
        var reference = new AssemblyIdentity("MyLib", new Version(1, 0, 0, 0), "", "");
        Assert.True(reference.Matches(new AssemblyIdentity("mylib", new Version(2, 0, 0, 0), "", "")));
        Assert.False(reference.Matches(new AssemblyIdentity("OtherLib", new Version(1, 0, 0, 0), "", "")));
    }

    /// <summary>ゲストが別アセンブリを参照する場合、AssemblyRef の identity が解析できる
    /// (Version / Culture / トークン)。参照先はコンパイラ構成に依存するため、
    /// 解析結果が identity として整合していることを検証する。</summary>
    [Fact]
    public void GuestAssemblyRef_Identity_IsParsed() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes("""
            namespace Vm.IdTest {
                public static class Ops {
                    public static int Run() => System.Array.Empty<int>().Length;
                }
            }
            """, "IdTestAsm"));
        var image = vm.LoadAssembly(stream);
        var tables = image.Tables;
        var refCount = tables.GetRowCount(TableKind.AssemblyRef);
        Assert.True(refCount > 0, "AssemblyRef が 1 件もありません。");
        var strongNamed = 0;
        for (var rid = 1; rid <= refCount; rid++) {
            var id = image.GetAssemblyRefIdentity(rid);
            Assert.False(string.IsNullOrEmpty(id.Name), $"AssemblyRef rid={rid} の名前が空です。");
            // BCL 参照 (System.Runtime / System.Private.CoreLib 等) は strong-named
            if (id.IsStrongNamed) {
                strongNamed++;
                Assert.Equal(16, id.PublicKeyToken.Length);
            }
        }
        Assert.True(strongNamed > 0,
            "strong-named な AssemblyRef が 1 件も解析されませんでした (公開鍵トークン解析の失敗)。");
    }
}
