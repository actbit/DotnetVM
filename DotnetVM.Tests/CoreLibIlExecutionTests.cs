using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C5 CoreLib IL 実行の全面化: System.String の面を実型 (CoreLib TypeDef) の managed IL で
/// 実行する (優先順位 ① ランタイムバインド → ② IL 本体実行 → ③ legacy intrinsic)。
/// - String の演算結果を CLR 突合 (CoreLib あり / なしの両 VM)
/// - ExecutionTracer で System.Private.CoreLib の IL フレームが実行されたことを証明し、
///   CoreLib 未ロード時 (legacy intrinsic 委譲) は CoreLib フレームが現れないことで
///   実行経路を区別する
/// - String IL はランタイム内部面 (FastAllocateString / Buffer.Memmove / Unsafe.* /
///   ldflda _firstChar の unmanaged ポインタ) がバインドされていなければ成立しないため、
///   IL フレームの存在自体が内部面バインドの証明になる
/// </summary>
public class CoreLibIlExecutionTests {
    private const string Source = """
        namespace Vm.C5 {
            using System;

            public static class StringOps {
                // Substring は CoreLib では FastAllocateString + Buffer.Memmove の IL に落ちる
                public static string Sub(string s) => s.Substring(2, 3) + "|" + s.Substring(5);

                // Trim は CoreLib IL が _firstChar / _stringLength フィールドを直接読む
                public static string Trimmed(string s) => "[" + s.Trim() + "]";

                // get_Chars ([Intrinsic] 面) は Unsafe.Add(ref _firstChar, i) + ldind の IL
                public static string FirstChar(string s) => s[0].ToString() + s[s.Length - 1];

                public static string Indexes(string s) =>
                    s.IndexOf('c').ToString() + ":" + s.IndexOf("cd").ToString() + ":" +
                    s.LastIndexOf('a').ToString();

                public static string ReplaceText(string s) => s.Replace("ab", "XY");

                // Length (get_Length) は ldfld _stringLength の IL
                public static string Halves(string s) {
                    var half = s.Length / 2;
                    return s.Substring(0, half) + "/" + s.Substring(half);
                }

                // culture 依存面 (Format) はランタイムバインド (Managed) で優先提供
                public static string Formatted(string s) =>
                    string.Format("{0}-{1}", s, 42) + "|" + string.Format("[{0}]", (object)s);

                // 表現境界面 (Split) はランタイムバインド (Managed) で優先提供
                public static string SplitText(string s) {
                    var parts = s.Split(',');
                    var joined = "";
                    foreach (var p in parts)
                        joined = joined + p + ";";
                    return joined + parts.Length;
                }

                // Insert は CoreLib IL が FastAllocateString + Memmove x3 + Unsafe.Add を辿る
                public static string Inserted(string s) => s.Insert(2, "--");

                public static string Mixed(string s) {
                    var trimmed = ("  " + s + "  ").Trim();
                    var mid = trimmed.Substring(2, 3);
                    var contains = trimmed.Contains("cde") ? "y" : "n";
                    return trimmed.Length + ":" + mid + ":" + trimmed[4].ToString() + ":" + contains;
                }
            }
        }
        """;

    private const string Input = "abcdefgh";

    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new CompiledTestAssembly(Source, "C5Asm"));

    private static object? RunClr(string method) =>
        Compiled.Value.InvokeClr("Vm.C5.StringOps", method, Input);

    private static DotnetVM.Host.VirtualMachine CreateVm(bool loadCoreLib) =>
        Compiled.Value.CreateVm(loadCoreLib);

    /// <summary>CoreLib あり/なしの両 VM と CLR の 3 方突合 (IL 実行と legacy 委譲の結果不変性)。</summary>
    private static void AssertSameEverywhere(string method) {
        var expected = RunClr(method);
        using (var withCoreLib = CreateVm(loadCoreLib: true))
            Assert.Equal(expected, withCoreLib.Invoke("Vm.C5.StringOps", method, Input));
        using (var withoutCoreLib = CreateVm(loadCoreLib: false))
            Assert.Equal(expected, withoutCoreLib.Invoke("Vm.C5.StringOps", method, Input));
    }

    /// <summary>CoreLib ありのみ CLR 突合。ゲスト IL が System.ReadOnlySpan`1 を要求する面
    /// (Roslyn が char→string の暗黙変換を new ReadOnlySpan&lt;char&gt;(in c) + String.Concat(span, span)
    /// に下げる等) は、CoreLib 未ロードの legacy VM では fail-closed が正の挙動
    /// (表現境界: span の実型は CoreLib にしか存在しないため legacy intrinsic では再現しない)。</summary>
    private static void AssertSameWithCoreLib(string method) {
        var expected = RunClr(method);
        using var vm = CreateVm(loadCoreLib: true);
        Assert.Equal(expected, vm.Invoke("Vm.C5.StringOps", method, Input));
    }

    /// <summary>CoreLib IL 実行の証明: 当該メソッドの IL フレームが System.Private.CoreLib
    /// として記録されること (CoreLib なしでは legacy intrinsic 委譲なので記録されない)。</summary>
    private static void AssertRunsCoreLibIl(bool loadCoreLib, string method,
        params (string type, string name)[] frames) {
        using var vm = CreateVm(loadCoreLib);
        var tracer = vm.Tracer;
        tracer.Start();
        try {
            vm.Invoke("Vm.C5.StringOps", method, Input);
        } finally {
            tracer.Stop();
        }
        if (!loadCoreLib) {
            // 委譲経路との区別: CoreLib 未ロードでは IL フレームは一切現れない
            Assert.DoesNotContain(tracer.Frames, f => f.AssemblyName == "System.Private.CoreLib");
            return;
        }
        foreach (var (type, name) in frames)
            Assert.True(tracer.ContainsFrame("System.Private.CoreLib", type, name),
                $"System.Private.CoreLib の {type}::{name} の IL フレームが記録されていません。" +
                "ランタイムバインド面の欠落で legacy 委譲や失敗に落ちていないか確認してください。");
    }

    [Fact]
    public void String_Substring_Runs_CoreLib_Il_And_Matches_Clr() {
        AssertSameEverywhere("Sub");
        AssertRunsCoreLibIl(loadCoreLib: true, "Sub",
            ("System.String", "Substring"), ("System.String", "InternalSubString"));
        AssertRunsCoreLibIl(loadCoreLib: false, "Sub");
    }

    [Fact]
    public void String_Trim_Reads_Char_Buffer_Fields_In_CoreLib_Il() {
        AssertSameEverywhere("Trimmed");
        // .NET 10 の Trim IL は TrimWhiteSpaceHelper ではなく Latin1 早回り
        // (Char.IsLatin1 / get_Latin1CharInfo / new ReadOnlySpan<char>(ref _firstChar, len)) を辿る
        AssertRunsCoreLibIl(loadCoreLib: true, "Trimmed",
            ("System.String", "Trim"), ("System.Char", "IsWhiteSpace"),
            ("System.ReadOnlySpan`1", ".ctor"));
    }

    [Fact]
    public void String_Indexer_Runs_CoreLib_Il_With_Unsafe_Add() {
        // ゲスト IL が char→string 変換で ReadOnlySpan を要求するため CoreLib ありのみ
        AssertSameWithCoreLib("FirstChar");
        // get_Chars は [Intrinsic] 面: CoreLib の IL (Unsafe.Add(ref _firstChar, i)) がそのまま走る
        AssertRunsCoreLibIl(loadCoreLib: true, "FirstChar",
            ("System.String", "get_Chars"), ("System.String", "get_Length"));
    }

    [Fact]
    public void String_Index_Faces_Match_Clr() => AssertSameEverywhere("Indexes");

    [Fact]
    public void String_Replace_Matches_Clr() => AssertSameEverywhere("ReplaceText");

    [Fact]
    public void String_Length_Field_Il_Matches_Clr() => AssertSameEverywhere("Halves");

    [Fact]
    public void String_Insert_Builds_Char_Buffer_In_CoreLib_Il() {
        // CoreLib ありのみ (legacy intrinsic には Insert 面がなく fail-closed になるため)
        var expected = RunClr("Inserted");
        using var vm = CreateVm(loadCoreLib: true);
        Assert.Equal(expected, vm.Invoke("Vm.C5.StringOps", "Inserted", Input));
        var tracer = vm.Tracer;
        tracer.Start();
        try {
            vm.Invoke("Vm.C5.StringOps", "Inserted", Input);
        } finally {
            tracer.Stop();
        }
        Assert.True(tracer.ContainsFrame("System.Private.CoreLib", "System.String", "Insert"),
            "System.String::Insert の IL フレームが記録されていません。");
    }

    [Fact]
    public void String_Format_Binding_Matches_Clr() => AssertSameEverywhere("Formatted");

    [Fact]
    public void String_Split_Binding_Matches_Clr() => AssertSameEverywhere("SplitText");

    [Fact]
    public void String_Faces_Combined_Match_Clr() {
        // Mixed は trimmed[4].ToString() の char→string 変換で ReadOnlySpan を要求するため CoreLib ありのみ
        AssertSameWithCoreLib("Mixed");
    }
}
