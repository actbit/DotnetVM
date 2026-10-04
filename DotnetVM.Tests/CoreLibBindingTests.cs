using DotnetVM.Policy;
using DotnetVM.Runtime.Intrinsics;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C4 ランタイムバインド層のテスト: メソッド解決の優先順位 (① ランタイムバインド →
/// ② IL 本体実行 → ③ legacy intrinsic → ④ fail-closed) とバインド面の意味論を
/// CoreLib あり/なしの両 VM で CLR 突合する。あわせて P/Invoke の構造的拒否
/// (代替バインド無しは OperationNotAllowedException) とバインド由来 (Origin) の
/// 監査面を検証する。
/// </summary>
public class CoreLibBindingTests {
    private const string Source = """
        namespace Vm.C4 {
            using System;
            using System.Runtime.InteropServices;

            [Flags]
            public enum Color {
                Red = 1,
                Green = 2,
                Blue = 4,
            }

            public static class Entry {
                // 4 項以上の文字列連結は Roslyn が String.Concat(params string[]) にコンパイルする
                public static string ConcatFive() {
                    var a = "a";
                    var b = "b";
                    var c = "c";
                    var d = "d";
                    var e = "e";
                    var r = a + b + c + d + e;
                    r = r + "|";
                    r = r + a + b + c + d;
                    return r;
                }

                // 混在連結は String.Concat(params object[]) 経由 (null は空文字列化)
                public static string ConcatMixed() {
                    var a = "a";
                    var one = 1;
                    var b = "b";
                    var two = 2;
                    var r = a + one + b + two;
                    r = r + "|";
                    var nullString = (string?)null;
                    r = r + nullString + "end";
                    return r;
                }

                // CoreLib の Enum.HasFlag (managed IL) を通す (内部で GetType / GetValue 等を呼ぶ)
                public static string CheckFlags() {
                    var c = Color.Green | Color.Blue;
                    var r = c.HasFlag(Color.Green) ? "G" : "-";
                    r = r + (c.HasFlag(Color.Red) ? "R" : "-");
                    r = r + (c.HasFlag(Color.Blue) ? "B" : "-");
                    return r;
                }

                public static string ConcatOrdinalCompare() {
                    var left = "apple";
                    var right = "banana";
                    return (string.Compare(left, right) < 0 ? "lt" : "ge") + ":" +
                           (string.CompareOrdinal(left, right) < 0 ? "lt" : "ge");
                }

                // P/Invoke 面は代替バインドが無い限りネイティブ実行せず拒否される
                [DllImport("vm_native_stub")]
                private static extern int NativeAdd(int x, int y);

                public static int CallNative() => NativeAdd(1, 2);
                public static string? ReadVirtualEnvironment() => Environment.GetEnvironmentVariable("VM_ENV_PROBE");
            }
        }
        """;

    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new CompiledTestAssembly(Source, "C4Asm"));

    private static object? RunClr(string method) =>
        Compiled.Value.InvokeClr("Vm.C4.Entry", method);

    private static DotnetVM.Host.VirtualMachine CreateVm(bool loadCoreLib) =>
        Compiled.Value.CreateVm(loadCoreLib);

    /// <summary>CoreLib あり/なしの両 VM と CLR の 3 方突合 (解決経路が変わっても結果が不変なこと)。</summary>
    private static void AssertSameEverywhere(string method) {
        var expected = RunClr(method);
        using (var withCoreLib = CreateVm(loadCoreLib: true))
            Assert.Equal(expected, withCoreLib.Invoke("Vm.C4.Entry", method));
        using (var withoutCoreLib = CreateVm(loadCoreLib: false))
            Assert.Equal(expected, withoutCoreLib.Invoke("Vm.C4.Entry", method));
    }

    [Fact]
    public void Four_Item_String_Concat_Uses_Array_Binding() =>
        AssertSameEverywhere("ConcatFive");

    [Fact]
    public void Mixed_Object_Concat_Uses_Array_Binding() =>
        AssertSameEverywhere("ConcatMixed");

    [Fact]
    public void Enum_HasFlag_Runs_CoreLib_Il() =>
        AssertSameEverywhere("CheckFlags");

    [Fact]
    public void String_Compare_Faces_Match_Clr() =>
        AssertSameEverywhere("ConcatOrdinalCompare");

    [Fact]
    public void PInvoke_Is_Rejected_As_OperationNotAllowed() {
        foreach (var loadCoreLib in new[] { true, false }) {
            using var vm = CreateVm(loadCoreLib);
            var ex = Assert.Throws<OperationNotAllowedException>(
                () => vm.Invoke("Vm.C4.Entry", "CallNative"));
            // 監査性: ネイティブ実行の拒否であることがメッセージから分かること
            Assert.Contains("P/Invoke", ex.Message);
        }
    }

    [Fact]
    public void Console_Bindings_Are_Audited_As_Device_Origin() {
        using var vm = CreateVm(loadCoreLib: false);
        var deviceBindings = vm.Bindings.Where(b => b.Origin == BindingOrigin.Device).ToList();
        Assert.Contains(deviceBindings, b => b.Key == BindingKey.StaticAnyParams("System.Console", "Write"));
        Assert.Contains(deviceBindings, b => b.Key == BindingKey.StaticAnyParams("System.Console", "WriteLine"));
        Assert.Contains(deviceBindings, b => b.Key == BindingKey.StaticAnyParams("System.Console", "ReadLine"));
        // Pin the registered native boundaries. The separate metadata test
        // verifies that each import present in the host CoreLib is a DllImport.
        var pinvokeFaces = vm.Bindings
            .Where(b => b.Origin == BindingOrigin.PInvokeReplacement)
            .Select(b => $"{b.Key.TypeFullName}::{b.Key.MethodName}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var expected = new List<string> {
            "Interop+BCrypt::BCryptGenRandom",
            "Interop+Kernel32::<GetEnvironmentVariable>g____PInvoke|296_0",
            "Interop+Sys::GetNonCryptographicallySecureRandomBytes",
        };
        if (OperatingSystem.IsWindows()) {
            expected.Add("System.Runtime.InteropServices.Marshal::<IsBuiltInComSupportedInternal>g____PInvoke|30_0");
            expected.Add("Interop+Kernel32::GetCPInfo");
            expected.Add("Interop+Kernel32::GetLastError");
            expected.Add("Interop+Kernel32::SetLastError");
        }
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), pinvokeFaces);
    }

    [Fact]
    public void PInvokeBindingsTargetRealDllImportsOnHost() {
        using var vm = CreateVm(loadCoreLib: true);
        foreach (var binding in vm.Bindings.Where(binding => binding.Origin == BindingOrigin.PInvokeReplacement)) {
            var type = typeof(object).Assembly.GetType(binding.Key.TypeFullName);
            if (type is null) continue; // OS-specific imports absent from this CoreLib.
            var method = Assert.Single(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
                method => method.Name == binding.Key.MethodName);
            Assert.Null(method.GetMethodBody());
            Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>(method));
        }
    }

    [Fact]
    public void WindowsEnvironmentMarshallingExecutesOriginalIl() {
        if (!OperatingSystem.IsWindows()) return;
        using var vm = CreateVm(loadCoreLib: true);
        vm.SetVirtualEnvironmentVariable("VM_ENV_PROBE", "value-日本語");
        vm.Tracer.Start();
        Assert.Equal("value-日本語", vm.Invoke("Vm.C4.Entry", "ReadVirtualEnvironment"));
        var longValue = new string('日', 180);
        vm.SetVirtualEnvironmentVariable("VM_ENV_PROBE", longValue);
        Assert.Equal(longValue, vm.Invoke("Vm.C4.Entry", "ReadVirtualEnvironment"));
        vm.SetVirtualEnvironmentVariable("VM_ENV_PROBE", null);
        Assert.Null(vm.Invoke("Vm.C4.Entry", "ReadVirtualEnvironment"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "Interop+Kernel32", "GetEnvironmentVariable"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.Runtime.InteropServices.Marshal", "GetLastSystemError"));
        Assert.DoesNotContain(vm.Bindings, binding => binding.Key.TypeFullName == "Interop+Kernel32" && binding.Key.MethodName == "GetEnvironmentVariable");
    }
}
