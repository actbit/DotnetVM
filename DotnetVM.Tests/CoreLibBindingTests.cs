using System.Reflection;
using DotnetVM.Host;
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
            }
        }
        """;

    private static readonly Lazy<(Assembly Clr, byte[] Bytes)> Compiled = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "C4Asm");
        return (Assembly.Load(bytes), bytes);
    });

    private static object? RunClr(string method) =>
        Compiled.Value.Clr.GetType("Vm.C4.Entry")!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);

    private static VirtualMachine CreateVm(bool loadCoreLib) {
        var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = loadCoreLib });
        using var stream = new MemoryStream(Compiled.Value.Bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

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
        // P/Invoke 代替バインドは監査表に載った例外面のみ (未登録 P/Invoke は fail-closed のまま、
        // PInvoke_Is_Rejected_As_OperationNotAllowed が検査)。既定登録の PInvokeReplacement は
        // Kernel32::GetEnvironmentVariable (CoreLib culture 不変経路の面再現) と
        // Interop+BCrypt::BCryptGenRandom / Interop+Sys::GetNonCryptographicallySecureRandomBytes
        // (ホスト暗号乱数 API に限定した代替) の 3 面に限る (いずれも
        // CoreLibSurfaceAudit に pinvoke-replacement として監査済み)
        var pinvokeFaces = vm.Bindings
            .Where(b => b.Origin == BindingOrigin.PInvokeReplacement)
            .Select(b => $"{b.Key.TypeFullName}::{b.Key.MethodName}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] {
            "Interop+BCrypt::BCryptGenRandom",
            "Interop+Kernel32::GetEnvironmentVariable",
            "Interop+Sys::GetNonCryptographicallySecureRandomBytes",
        }, pinvokeFaces);
    }
}
