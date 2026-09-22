using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// trusted domain 遮断の回帰テスト集:
/// - ゲストから実 CoreLib の特権面 (Marshal) を直接参照しても binding が拒否される
/// - fake 型は trusted 面に到達せず自実装が実行される (厳格な成功条件)
/// - 同一 FullName 型を別 assembly 2 個に定義しても静的ストレージを共有しない
/// - VM ごとの仮想環境ストア分離
/// </summary>
public class TrustedDomainTests {
    /// <summary>ゲスト側 fake Marshal (実在 Marshal とは別 FullName)。
    /// trusted 特権面に差し替えられず自実装 (42) が実行される。</summary>
    private const string FakeMarshalSource = """
        namespace Vm.TrustFake {
            using System;

            public static class FakeMarshal {
                public static int GetLastSystemError() => 42;
            }

            public static class Ops {
                public static int Run() => FakeMarshal.GetLastSystemError();
            }
        }
        """;

    [Fact]
    public void FakeSystemType_DoesNot_Reach_TrustedCoreLib_Binding() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(FakeMarshalSource, "TrustFake"));
        vm.LoadAssembly(stream);
        // fake 型は intrinsic 未登録のためゲスト IL そのままが実行され、自実装の 42 を返す。
        // trusted CoreLib の特権 binding (VM lastError 初期値 0) に差し替えられていたら 0 になる。
        var result = (int)vm.Invoke("Vm.TrustFake.Ops", "Run")!;
        Assert.Equal(42, result);
    }

    /// <summary>ゲストから実在の特権面 (Marshal.GetLastSystemError) を直接参照しても
    /// caller が guest のため TrustedCoreLib binding に到達できず OperationNotAllowed になる。</summary>
    [Fact]
    public void GuestDirect_PrivilegedMarshal_IsRejected() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            """
            namespace Vm.TrustPriv {
                using System.Runtime.InteropServices;

                public static class Ops {
                    public static int RunGet() => Marshal.GetLastSystemError();
                    public static int RunSet() {
                        Marshal.SetLastSystemError(123);
                        return Marshal.GetLastSystemError();
                    }
                }
            }
            """, "TrustPriv"));
        vm.LoadAssembly(stream);
        // 特権面へのゲスト直接呼出は domain 遮断で拒否される (InternalCall 未登録とは区別する)。
        var exGet = Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("Vm.TrustPriv.Ops", "RunGet"));
        Assert.Contains("trusted", exGet.Message, StringComparison.OrdinalIgnoreCase);
        var exSet = Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("Vm.TrustPriv.Ops", "RunSet"));
        Assert.Contains("trusted", exSet.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>同一 FullName 型を別 assembly 2 個に定義しても静的ストレージを共有しない。
    /// 各 loader の型は別 identity であり、Hit() はそれぞれ 1 から始まる。</summary>
    [Fact]
    public void SameFullName_TwoAssemblies_Keep_Separate_Static_Storage() {
        const string sharedSource = """
            namespace Vm.Shared {
                public static class Counter {
                    public static int Hits;
                    public static int Hit() => ++Counter.Hits;
                }
            }
            """;
        using var vm = new VirtualMachine(new VmHostOptions());
        var bytesA = TestAssemblyCompiler.CompileToBytes(sharedSource, "SharedA");
        var bytesB = TestAssemblyCompiler.CompileToBytes(sharedSource, "SharedB");
        using var streamA = new MemoryStream(bytesA);
        using var streamB = new MemoryStream(bytesB);
        vm.LoadAssembly(streamA);
        vm.LoadAssembly(streamB);

        Assert.Equal(2, vm.Loaders.Count);
        var loaderA = vm.Loaders[0];
        var loaderB = vm.Loaders[1];
        var typeA = loaderA.FindTypeByFullName("Vm.Shared.Counter");
        var typeB = loaderB.FindTypeByFullName("Vm.Shared.Counter");
        Assert.NotNull(typeA);
        Assert.NotNull(typeB);
        Assert.False(ReferenceEquals(typeA, typeB));

        var methodA = typeA!.Methods.First(m => m.Name == "Hit");
        var methodB = typeB!.Methods.First(m => m.Name == "Hit");
        // 各 assembly の静的ストレージは独立: どちらも初回は 1 を返す (共有なら 1,2 になる)。
        var rA1 = vm.Execute(methodA);
        var rB1 = vm.Execute(methodB);
        var rA2 = vm.Execute(methodA);
        Assert.Equal(1, rA1.ReturnValue);
        Assert.Equal(1, rB1.ReturnValue);
        Assert.Equal(2, rA2.ReturnValue);
    }

    /// <summary>VM ごとの仮想環境ストアは分離する (static 共有にしない)。</summary>
    [Fact]
    public void VirtualEnvironment_Is_Isolated_Per_Vm() {
        using var vm1 = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var vm2 = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        vm1.SetVirtualEnvironmentVariable("VM_ISOLATION_PROBE", "one");
        Assert.Equal("one", vm1.GetVirtualEnvironmentVariable("VM_ISOLATION_PROBE"));
        Assert.Null(vm2.GetVirtualEnvironmentVariable("VM_ISOLATION_PROBE"));
        vm2.SetVirtualEnvironmentVariable("VM_ISOLATION_PROBE", "two");
        Assert.Equal("one", vm1.GetVirtualEnvironmentVariable("VM_ISOLATION_PROBE"));
        Assert.Equal("two", vm2.GetVirtualEnvironmentVariable("VM_ISOLATION_PROBE"));
    }
}
