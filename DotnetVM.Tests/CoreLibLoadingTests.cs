using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C1 多アセンブリ解決のテスト: AssemblyRef による依存アセンブリの同一ディレクトリ自動探索、
/// 欠落時の fail-closed、および本物の System.Private.CoreLib のロード。
/// </summary>
public class CoreLibLoadingTests {
    /// <summary>main + dep の 2 DLL を temp ディレクトリに作成するヘルパ。</summary>
    private static (string Dir, string MainPath, string DepPath) WriteMainAndDependency() {
        var dir = Path.Combine(Path.GetTempPath(), "dotnetvm-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var depBytes = TestAssemblyCompiler.CompileToBytes("""
            namespace Dep {
                public static class Lib {
                    public static int Add(int a, int b) => a + b;
                    public static string Greet(string name) => "hi " + name;
                }
            }
            """, assemblyName: "Dep");

        // main は dep をメタデータ参照としてコンパイルする (Roslyn が AssemblyRef を張る)
        var mainBytes = TestAssemblyCompiler.CompileToBytes("""
            namespace MainNs {
                public static class Entry {
                    public static int CallAdd() => Dep.Lib.Add(2, 3);
                    public static string CallGreet(string name) => Dep.Lib.Greet(name);
                }
            }
            """, assemblyName: "MainAsm",
            extraReferences: [MetadataReference.CreateFromImage(depBytes)]);

        var mainPath = Path.Combine(dir, "MainAsm.dll");
        var depPath = Path.Combine(dir, "Dep.dll");
        File.WriteAllBytes(mainPath, mainBytes);
        File.WriteAllBytes(depPath, depBytes);
        return (dir, mainPath, depPath);
    }

    [Fact]
    public void Dependency_Assembly_Is_Resolved_From_Same_Directory() {
        var (dir, mainPath, _) = WriteMainAndDependency();
        try {
            using var vm = new VirtualMachine();
            vm.LoadAssembly(mainPath);

            // 依存アセンブリは明示ロードしていないが、型解決時に同一ディレクトリから自動ロードされる
            Assert.Equal(5, vm.Invoke("MainNs.Entry", "CallAdd"));
            Assert.Equal("hi vm", vm.Invoke("MainNs.Entry", "CallGreet", "vm"));

            // 依存アセンブリがコンテキストに登録されている
            var dep = vm.Context.FindBySimpleName("Dep");
            Assert.NotNull(dep);
            Assert.NotNull(dep.FindTypeByFullName("Dep.Lib"));
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_Dependency_Fails_Closed_With_Dedicated_Exception() {
        var (dir, mainPath, _) = WriteMainAndDependency();
        try {
            // main のバイト列だけ残して dep を消す (fail-closed: 同一ディレクトリにも見つからない)
            var mainBytes = File.ReadAllBytes(mainPath);
            Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            var mainOnly = Path.Combine(dir, "MainAsm.dll");
            File.WriteAllBytes(mainOnly, mainBytes);

            using var vm = new VirtualMachine();
            vm.LoadAssembly(mainOnly);

            var ex = Assert.Throws<AssemblyDependencyNotFoundException>(
                () => vm.Invoke("MainNs.Entry", "CallAdd"));
            Assert.Equal("Dep", ex.AssemblyName);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadHostCoreLib_Loads_Real_CoreLib_Types_Into_Context() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });

        // CoreLib がコンテキストにロードされ、実 TypeDef (IL ボディ持ち) として参照できる
        var coreLib = vm.Context.FindBySimpleName("System.Private.CoreLib");
        Assert.NotNull(coreLib);

        var stringType = coreLib.FindTypeByFullName("System.String");
        Assert.IsType<VmClassType>(stringType);
        // intrinsic ファサードには IL ボディが無い。実 TypeDef には managed IL がある
        Assert.Contains(stringType.Methods, m => m.Body is not null);

        var objectType = coreLib.FindTypeByFullName("System.Object");
        Assert.NotNull(objectType);
        Assert.Contains(objectType.Methods, m => m.Name == "ToString" && m.Body is not null);
    }

    [Fact]
    public void Default_Options_Keep_Context_Empty() {
        using var vm = new VirtualMachine();
        // LoadHostCoreLib 既定 = false: CoreLib はロードされず、従来どおり intrinsic ファサード面
        Assert.Empty(vm.Context.Loaders);
        Assert.Null(vm.Context.FindBySimpleName("System.Private.CoreLib"));
    }
}
