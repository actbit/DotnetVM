using System.Reflection;
using System.Text;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// CLR 互換性修正の突合テスト。互換性監査で発見・修正した項目を検証する:
/// - ldtoken Type/Method (typeof / GetType / MethodBase.GetCurrentMethod)
/// - 構築ジェネリック型の静的フィールドは実引数ごとに別ストレージ (CLR 規約)
/// - VM インスタンス間で静的ストレージを共有しない (分離)
/// - 文字列比較は CurrentCulture (CLR 既定。旧実装は Ordinal で差異があった)
/// - int.MinValue / -1 は OverflowException (ECMA/CLR 挙動)
/// - Console の char / bool / object / 複合書式 (旧実装は char/bool が数字で出ていた)
/// - String.Format / Split / Join / 文字列補間
/// </summary>
public class CompatTests {
    private const string Source = """
        using System;
        namespace Vm {
            public static class Compat {
                public sealed class Box {
                    public int V;
                    public Box(int v) { V = v; }
                    public override string ToString() => $"Box({V})";
                }

                public static string TypeName() => typeof(int).Name;
                public static string TypeFullName() => typeof(string).FullName;
                public static string GuestTypeFullName() => new Box(3).GetType().FullName;

                public static string OverrideToString() => new Box(7).ToString();
                public static string WriteObject() { Console.Write(new Box(5)); return "done"; }
                public static string WriteCharBool() { Console.Write('A'); Console.Write(','); Console.Write(true); return ""; }

                public sealed class Ctx<T> {
                    public static int Count;
                }
                public static long GenericStatics(bool which) {
                    if (which) { Ctx<int>.Count++; return Ctx<int>.Count; }
                    Ctx<long>.Count += 10;
                    return Ctx<long>.Count;
                }

                public static int CompareStrings(string a, string b) => a.CompareTo(b);
                public static bool StartsWith(string a, string b) => a.StartsWith(b);
                public static int IndexOf(string a, string b) => a.IndexOf(b);
                public static bool Contains(string a, string b) => a.Contains(b);

                // 定数式だとコンパイル時に CS0220 になるため実行時値を使う
                public static string DivMin() {
                    int n = int.MinValue; int d = -1;
                    try { var x = n / d; return "no-throw"; }
                    catch (OverflowException) { return "overflow"; }
                }
                public static string RemMin() {
                    int n = int.MinValue; int d = -1;
                    try { return n % d == 0 ? "zero" : "nonzero"; }
                    catch (OverflowException) { return "overflow"; }
                }

                public static string FormatTest(int n, double d, string s) =>
                    string.Format("[{0}] [{1,6}] [{2,-6}] [0x{0:X}] [d={1:F2}]", n, d, s);
                public static string Interp(int n) => $"v={n}, hex={n:X2}";
                // VM 側の戻り値は文字列化して比較する (配列の VM/CLR 突合のため)
                public static string SplitTest(string s) => string.Join("|", s.Split(','));
                public static string JoinTest(string[] parts) => string.Join("|", parts);
                public static string CurrentMethodName() =>
                    System.Reflection.MethodBase.GetCurrentMethod()!.Name;
            }
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "CompatAsm");
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm() {
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(Compiled.Bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(string method, params object?[] args) {
        var (clr, _) = Compiled;
        return clr.GetType("Vm.Compat")!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
    }

    private static void AssertMatchesClr(string method, params object?[] args) {
        var expected = RunClr(method, args);
        using var vm = CreateVm();
        Assert.Equal(expected, vm.Invoke("Vm.Compat", method, args));
    }

    // ---- typeof / GetType (ldtoken Type + System.Type 面) ----

    [Fact]
    public void TypeOf_MatchesClr() {
        AssertMatchesClr("TypeName");
        AssertMatchesClr("TypeFullName");
        AssertMatchesClr("GuestTypeFullName");
    }

    // ---- ToString / Console の object 面 (ゲスト override の仮想ディスパッチ) ----

    [Fact]
    public void OverrideToString_MatchesClr() => AssertMatchesClr("OverrideToString");

    [Fact]
    public void ConsoleWriteObject_InvokesGuestToString() {
        using var vm = CreateVm();
        vm.Invoke("Vm.Compat", "WriteObject");
        Assert.Contains(vm.Console.OutputLog, ev => ev.Text == "Box(5)");
    }

    [Fact]
    public void ConsoleWriteCharAndBool_MatchesClr() {
        // 旧実装は char/bool が i4 スロット統合のため「65」「1」と出力していた
        using var vm = CreateVm();
        vm.Invoke("Vm.Compat", "WriteCharBool");
        var text = string.Concat(vm.Console.OutputLog.Select(ev => ev.Text));
        Assert.Equal("A,True", text);
    }

    // ---- 構築ジェネリック型の静的フィールド (実引数ごとに別ストレージ) ----

    [Fact]
    public void GenericStaticFields_AreIsolatedPerInstantiation() {
        // VM と CLR で同じ呼出列を同じ順に実行し、各ステップの値を突合する
        using var vm = CreateVm();
        for (var i = 0; i < 3; i++) {
            Assert.Equal(RunClr("GenericStatics", true), vm.Invoke("Vm.Compat", "GenericStatics", true));
            Assert.Equal(RunClr("GenericStatics", false), vm.Invoke("Vm.Compat", "GenericStatics", false));
        }
    }

    // ---- VM インスタンス間の静的分離 (旧実装は ObjectModel が static で共有されていた) ----

    [Fact]
    public void StaticStorage_IsNotSharedAcrossVmInstances() {
        using var vm1 = CreateVm();
        Assert.Equal(1L, vm1.Invoke("Vm.Compat", "GenericStatics", true));
        using var vm2 = CreateVm();
        // 別 VM の静的は初期状態 (vm1 の加算が漏れない)
        Assert.Equal(1L, vm2.Invoke("Vm.Compat", "GenericStatics", true));
        // vm1 は自身の状態を保持し、vm2 の実行にも影響されない (継続値 = 2)
        Assert.Equal(2L, vm1.Invoke("Vm.Compat", "GenericStatics", true));
    }

    // ---- 文字列比較 (CLR 既定 = CurrentCulture。旧実装は Ordinal で差異) ----

    [Fact]
    public void StringComparison_MatchesClr() {
        // "a"/"A" は Ordinal と CurrentCulture で符号が逆になるペア (VM と CLR の同一プロセス文化で突合)
        AssertMatchesClr("CompareStrings", "a", "A");
        AssertMatchesClr("CompareStrings", "A", "a");
        AssertMatchesClr("StartsWith", "apple", "APPLE");
        AssertMatchesClr("IndexOf", "hello", "ELL");
        AssertMatchesClr("Contains", "strasse", "ss");
    }

    // ---- 除算のエッジ (int.MinValue / -1 は OverflowException) ----

    [Fact]
    public void DivisionEdgeCases_MatchClr() {
        AssertMatchesClr("DivMin");
        AssertMatchesClr("RemMin");
    }

    // ---- 複合書式 / 文字列補間 / Split / Join ----

    [Fact]
    public void CompositeFormatting_MatchesClr() {
        AssertMatchesClr("FormatTest", 255, 3.14159, "ab");
        AssertMatchesClr("Interp", 202);
    }

    [Fact]
    public void SplitAndJoin_MatchClr() {
        AssertMatchesClr("SplitTest", "a,b,c");
        // string[] は params 展開されないよう object に包む
        AssertMatchesClr("JoinTest", (object?)new[] { "x", "y", "z" });
    }

    // ---- MethodBase.GetCurrentMethod (ldtoken Method + MethodBase 面) ----

    [Fact]
    public void CurrentMethodName_MatchesClr() => AssertMatchesClr("CurrentMethodName");
}
