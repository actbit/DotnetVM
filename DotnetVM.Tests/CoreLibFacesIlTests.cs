using System.Reflection;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C5 CoreLib IL 実行の全面化 (String 以外の面): Convert / Math / Enum / Type / 例外生成の
/// managed IL が VM で走ることを CLR 突合で確認する (面の欠落は fail-closed で顕在化)。
///
/// 実行経路の内実 (ExecutionTracer 証明つき):
/// - Math (int / double 全面): CoreLib 実型 IL で実行 (IlPreferred 面)
/// - String 構築 (Concat 等): CoreLib 実型 IL で実行 (IlPreferred 面)
/// - 整数 ToString / Parse / Convert (文字列⇔整数): VM CoreLib (DotnetVM.CoreLib) の
///   managed IL に置換 (VmCoreLibSurfaces 面)。実在 CoreLib の該当面は culture 機構 +
///   生ポインタ演算で構成され VM 表現に落ちないため。DotnetVM.CoreLib は普通の .NET
///   ライブラリでもあり、CLR 差分テスト (VmCoreLibClrTests) で正当性を担保してから配線する。
///   String.Concat IL 内の boxed int の callvirt ToString も Invoke の choke point で
///   同一の面に置換される
/// - Enum.HasFlag / Object.GetType: 実 CLR も JIT が IL を丸ごと置き換える [Intrinsic] 面。
///   VM は同一意味論のバインドで提供 (CoreLibBindings の RegisterEnum / RegisterObject)
/// - Convert.ToInt32(object) 等のボックス化経由面 (C5.5 Wave 1): 実 CoreLib IL の
///   IConvertible ディスパッチで実行 (面単位 IL 優先 IlPreferredFaces)。
///   Enum の IConvertible EII が依存する GetValue / InternalGetCorElementType は
///   internal-call リーフバインド (CoreLibBindings.RegisterEnum)、例外文言面は
///   System.SR バインド (culture スコープ外)
/// - Type (RuntimeType) / 例外既定文言 面: ランタイム型 / culture インフラの委譲継続面。
///   バインド (legacy intrinsic) で CLR 突合を保つ
/// </summary>
public class CoreLibFacesIlTests {
    private const string Source = """
        namespace Vm.C5 {
            using System;

            [Flags]
            public enum Color { Red = 1, Green = 2, Blue = 4, All = 7 }

            public static class FaceOps {
                // Math 内部: Abs/Max/Min/Sign は CoreLib の managed IL (MinMax binop / cgt 系)
                public static string MathFaces(int a, int b) =>
                    Math.Abs(a) + ":" + Math.Max(a, b) + ":" + Math.Min(a, b) + ":" + Math.Sign(b);

                public static int MathClamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);

                public static double DoubleFaces(double d) => Math.Abs(d) * Math.Floor(d) == 0 ? 1 : 2;

                // Convert: 文字列/数値/真偽の変換 (int.Parse 相当の IL を含む)
                public static string ConvertFaces(string s) =>
                    Convert.ToInt32(s) + ":" + Convert.ToBoolean(1) + ":" + Convert.ToString(42) +
                    ":" + Convert.ToChar(97) + ":" + Convert.ToInt64(s);

                // int.Parse 面: 成功 / FormatException / OverflowException の 3 分類
                public static string ParseFaces(string s) {
                    try {
                        return int.Parse(s).ToString();
                    } catch (FormatException) {
                        return "format";
                    } catch (OverflowException) {
                        return "overflow";
                    }
                }

                // Enum: HasFlag は CoreLib の managed IL (span 比較に展開される)。
                // 引数はホスト Invoke 経由の int で受け、ゲスト内で enum にキャストする
                public static bool EnumHasFlag(int c, int flag) => ((Color)c).HasFlag((Color)flag);

                public static string EnumValues(int c) =>
                    ((int)(Color)c).ToString() + ":" + Convert.ToInt32((Color)c);

                // object 経由面 (C5.5 Wave 1): Convert.ToXxx(object) は実 CoreLib IL の
                // ((IConvertible)value).ToXxx(null) ディスパッチで実行される。
                // 変換ごとに独立メソッドにして例外分類も含めて CLR と突合する。
                // 戻り値はホスト境界の型精度のため具象型で宣言する (box は VM オブジェクトのまま)
                public static int ConvertToInt32(object v) => Convert.ToInt32(v);
                public static long ConvertToInt64(object v) => Convert.ToInt64(v);
                public static byte ConvertToByte(object v) => Convert.ToByte(v);
                public static sbyte ConvertToSByte(object v) => Convert.ToSByte(v);
                public static short ConvertToInt16(object v) => Convert.ToInt16(v);
                public static ushort ConvertToUInt16(object v) => Convert.ToUInt16(v);
                public static uint ConvertToUInt32(object v) => Convert.ToUInt32(v);
                public static ulong ConvertToUInt64(object v) => Convert.ToUInt64(v);
                public static bool ConvertToBoolean(object v) => Convert.ToBoolean(v);
                public static char ConvertToChar(object v) => Convert.ToChar(v);
                public static string ConvertToString(object v) => Convert.ToString(v) ?? "null";

                // 例外分類 (OverflowException / FormatException / InvalidCastException) も CLR 突合
                public static string ConvertClassify(object v, int which) {
                    try {
                        return (which switch {
                            0 => (object)Convert.ToInt32(v),
                            1 => Convert.ToInt64(v),
                            2 => Convert.ToByte(v),
                            3 => Convert.ToSByte(v),
                            4 => Convert.ToBoolean(v),
                            5 => Convert.ToChar(v),
                            6 => Convert.ToString(v) ?? "null",
                            7 => Convert.ToInt16(v),
                            8 => Convert.ToUInt16(v),
                            9 => Convert.ToUInt32(v),
                            10 => Convert.ToUInt64(v),
                            _ => "none",
                        })!.ToString()!;
                    } catch (OverflowException) {
                        return "overflow";
                    } catch (FormatException) {
                        return "format";
                    } catch (InvalidCastException) {
                        return "invalid-cast";
                    }
                }

                // enum box の基底型幅 (value__ 型) ごとの IConvertible 変換
                // (Convert.ToByte(enum 値) は ToByte(object) への box を伴う呼出にコンパイルされる)。
                // 戻り値はホスト境界での型精度のため object でなく具象型で宣言する
                [Flags]
                public enum Tiny : byte { A = 1, B = 2, All = 3 }

                public static byte ConvertEnumBox(int v) => Convert.ToByte((Tiny)v);
                public static ushort ConvertEnumBox16(int v) => Convert.ToUInt16((Tiny)v);
                public static ulong ConvertEnumBox64(int v) => Convert.ToUInt64((Tiny)v);

                // Type 面: typeof は ldtoken Type → RuntimeTypeHandle → Type 面経由
                public static string TypeNameOfInt() => typeof(int).Name;

                public static string TypeFullNameOfString() => typeof(string).FullName ?? "";

                // 例外生成: CoreLib の Exception::.ctor / get_Message の IL 経路
                public static string ExceptionMessage() {
                    try {
                        throw new InvalidOperationException("boom");
                    } catch (InvalidOperationException ex) {
                        return ex.Message;
                    }
                }

                // 例外の型面 (GetType().Name / 例外メッセージの既定文面)
                public static string ExceptionDefaultMessage(int v) {
                    try {
                        _ = v / (v - v);
                        return "no-throw";
                    } catch (Exception ex) {
                        return ex.GetType().Name + ":" + ex.Message.Length;
                    }
                }

                // ---- C5.5 Wave 2: 書式付き ToString 面 (Faces 置換 → FormatSpecifiers IL) ----
                // ゲスト差分で使う書式は culture 非依存のものに限る (N/C/P 等の culture 面の
                // 全面グリッドは FormatSpecifiersClrTests で不変カルチャ固定して突合済み)
                public static string IntFormatFaces(int v, string fmt) => v.ToString(fmt);

                public static string LongFormatFaces(long v, string fmt) => v.ToString(fmt);

                public static string UnsignedFormatFaces(ulong v, string fmt) => v.ToString(fmt);

                public static string SmallTypeFormatFaces(int v, string fmt) =>
                    ((byte)v).ToString(fmt) + ":" + ((sbyte)v).ToString(fmt) + ":" +
                    ((short)v).ToString(fmt) + ":" + ((ushort)v).ToString(fmt);

                public static string BoolCharToStringFaces(bool b, char c) => b.ToString() + ":" + c.ToString();

                // 連結文脈でない素の呼出 (Roslyn は連結内の char.ToString() を
                // String::op_Implicit(char) へ最適化するため、Char ToString 面の直接証明用)
                public static string BoolToStringFace(bool b) => b.ToString();

                public static string CharToStringFace(char c) => c.ToString();

                // provider のみの面 (ToString(IFormatProvider)) と format=null (G 既定) 面
                public static string ProviderOnlyFaces(short v, byte b) =>
                    v.ToString((IFormatProvider)null!) + ":" + b.ToString((string?)null);

                // FormatException 分類 (無効な標準書式文字 "Q5" 等 / letter+ガベージ "D2x")
                public static string FormatClassify(int v, string fmt) {
                    try {
                        return v.ToString(fmt);
                    } catch (FormatException) {
                        return "format";
                    }
                }
            }
        }
        """;

    private static readonly Lazy<(Assembly Clr, byte[] Bytes)> Compiled = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "C5FacesAsm");
        return (Assembly.Load(bytes), bytes);
    });

    private static object? InvokeClr(string method, params object?[] args) =>
        Compiled.Value.Clr.GetType("Vm.C5.FaceOps")!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);

    private static object? InvokeVm(string method, params object?[] args) {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(Compiled.Value.Bytes);
        vm.LoadAssembly(stream);
        return vm.Invoke("Vm.C5.FaceOps", method, args);
    }

    private static void AssertSame(string method, params object?[] args) =>
        Assert.Equal(InvokeClr(method, args), InvokeVm(method, args));

    /// <summary>CoreLib IL 実行の証明: 当該メソッドの IL フレームが System.Private.CoreLib
    /// として記録されること。Math 系は実型 IL で走る (IlPreferred 面)。</summary>
    private static void AssertRunsCoreLibIl(string method, object?[] args,
        params (string type, string name)[] frames) =>
        AssertRunFrames("System.Private.CoreLib", method, args, frames,
            "ランタイムバインド面の欠落で legacy 委譲や失敗に落ちていないか確認してください。");

    /// <summary>VM CoreLib (DotnetVM.CoreLib) 置換面の証明: 置換先の managed IL フレームが
    /// アセンブリ名 "DotnetVM.CoreLib" として記録されること (legacy intrinsic 委譲との区別)。</summary>
    private static void AssertRunsVmCoreLibIl(string method, object?[] args,
        params (string type, string name)[] frames) =>
        AssertRunFrames("DotnetVM.CoreLib", method, args, frames,
            "VmCoreLibSurfaces の対応表か IlPreferred 面の設定を確認してください。");

    private static void AssertRunFrames(string assemblyName, string method, object?[] args,
        (string type, string name)[] frames, string hint) {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(Compiled.Value.Bytes);
        vm.LoadAssembly(stream);
        var tracer = vm.Tracer;
        tracer.Start();
        try {
            vm.Invoke("Vm.C5.FaceOps", method, args);
        } finally {
            tracer.Stop();
        }
        foreach (var (type, name) in frames)
            Assert.True(tracer.ContainsFrame(assemblyName, type, name),
                $"{assemblyName} の {type}::{name} の IL フレームが記録されていません。{hint}");
    }

    [Fact]
    public void Math_Integer_Faces_Match_Clr() {
        AssertSame("MathFaces", -7, 12);
        AssertSame("MathFaces", 0, -3);
        // Math は IlPreferred 面: legacy intrinsic でなく CoreLib の実型 IL で走る
        AssertRunsCoreLibIl("MathFaces", [-7, 12],
            ("System.Math", "Abs"), ("System.Math", "Max"),
            ("System.Math", "Min"), ("System.Math", "Sign"));
        // 結果の文字列化は VM CoreLib (DotnetVM.CoreLib) の managed IL に置換される
        // (String.Concat IL 内の boxed int の callvirt ToString も同一の面に置換)
        AssertRunsVmCoreLibIl("MathFaces", [-7, 12],
            ("DotnetVM.CoreLib.NumberFormatting", "Int32ToString"),
            ("DotnetVM.CoreLib.NumberFormatting", "UInt32ToString"));
    }

    [Fact]
    public void Math_Clamp_Matches_Clr() {
        AssertSame("MathClamp", 5, 1, 10);
        AssertSame("MathClamp", -2, 1, 10);
        AssertSame("MathClamp", 99, 1, 10);
        AssertRunsCoreLibIl("MathClamp", [5, 1, 10], ("System.Math", "Clamp"));
    }

    [Fact]
    public void Math_Double_Faces_Match_Clr() {
        AssertSame("DoubleFaces", -3.5);
        AssertSame("DoubleFaces", 0.25);
        // Abs(double) の IL は BitConverter (Unsafe.BitCast バインド面) を辿る
        AssertRunsCoreLibIl("DoubleFaces", [-3.5],
            ("System.Math", "Abs"),
            ("System.BitConverter", "DoubleToUInt64Bits"),
            ("System.BitConverter", "UInt64BitsToDouble"));
    }

    [Fact]
    public void Convert_Faces_Match_Clr() {
        AssertSame("ConvertFaces", "42");
        AssertSame("ConvertFaces", "7");
        // Convert 面の文字列⇔整数変換は VM CoreLib (DotnetVM.CoreLib) の managed IL に置換
        // (ToInt32/ToInt64 は NumberFormatting の解析 IL も辿る)
        AssertRunsVmCoreLibIl("ConvertFaces", ["42"],
            ("DotnetVM.CoreLib.IntegerConvert", "ToInt32"),
            ("DotnetVM.CoreLib.IntegerConvert", "ToInt64"),
            ("DotnetVM.CoreLib.IntegerConvert", "ToString"),
            ("DotnetVM.CoreLib.IntegerConvert", "ToBoolean"),
            ("DotnetVM.CoreLib.IntegerConvert", "ToChar"),
            ("DotnetVM.CoreLib.NumberFormatting", "ParseInt32"),
            ("DotnetVM.CoreLib.NumberFormatting", "ParseInt64"));
    }

    [Fact]
    public void Parse_Faces_Match_Clr() {
        // 書式 OK / FormatException / OverflowException の 3 分類を CLR と突合
        // (ParseInt32 は uint 空間での絶対値処理のため int.MinValue も正しく扱う)
        AssertSame("ParseFaces", "-2147483648");
        AssertSame("ParseFaces", "2147483649");   // int.MaxValue + 1 → overflow
        AssertSame("ParseFaces", "abc");           // format
        AssertSame("ParseFaces", " 42 ");
        AssertRunsVmCoreLibIl("ParseFaces", ["42"],
            ("DotnetVM.CoreLib.NumberFormatting", "ParseInt32"),
            ("DotnetVM.CoreLib.NumberFormatting", "ParseMagnitude"),
            ("DotnetVM.CoreLib.NumberFormatting", "Int32ToString"));
    }

    [Fact]
    public void Enum_HasFlag_Matches_Clr() {
        AssertSame("EnumHasFlag", (int)7, (int)4);  // All ⊃ Blue
        AssertSame("EnumHasFlag", (int)2, (int)4);  // Green ⊅ Blue
        AssertSame("EnumHasFlag", (int)3, (int)1);  // Red|Green ⊃ Red
    }

    [Fact]
    public void Enum_Values_Match_Clr() {
        AssertSame("EnumValues", (int)4);
        // enum→int キャスト後の ToString() は VM CoreLib の managed IL に置換される。
        // Convert.ToInt32((Color)c) は object 経由面の実 IL (Enum EII → GetValue リーフ) で実行
        AssertRunsVmCoreLibIl("EnumValues", [4],
            ("DotnetVM.CoreLib.NumberFormatting", "Int32ToString"));
    }

    [Fact]
    public void Convert_Object_Faces_Match_Clr() {
        // C5.5 Wave 1: object 経由面は実 CoreLib IL の IConvertible ディスパッチで実行。
        // ToString 面は全入力で成功する。数値/真偽面は CLR の変換可否規約 (bool→整数は
        // InvalidCastException 等) に従うため、成功ペアのみここで値を突合し、
        // 可否・例外分類の全体マトリクスは Convert_Object_Error_Classification_Matches_Clr
        // (成功値 / overflow / format / invalid-cast のゲスト側分類) で全面的に突合する
        foreach (var v in new object?[] { 42, -7, (long)-3, true, 'A', "True", "42" })
            AssertSame("ConvertToString", v);
        AssertSame("ConvertToInt32", 42);
        AssertSame("ConvertToInt64", -7);
        AssertSame("ConvertToByte", (byte)7);
        AssertSame("ConvertToSByte", -7);
        AssertSame("ConvertToInt16", 42);
        AssertSame("ConvertToUInt16", 42);
        AssertSame("ConvertToUInt32", 42);
        AssertSame("ConvertToUInt64", 42);
        AssertSame("ConvertToBoolean", true);
        AssertSame("ConvertToChar", 'A');
        // 実 IL の証明: Convert 本体の IL フレームが記録される (legacy 委譲ではなく)
        AssertRunsCoreLibIl("ConvertToInt32", [42], ("System.Convert", "ToInt32"));
        AssertRunsCoreLibIl("ConvertToByte", [(byte)7], ("System.Convert", "ToByte"));
    }

    [Fact]
    public void Convert_Object_Error_Classification_Matches_Clr() {
        // 範囲外 (OverflowException) / 不正書式 (FormatException) / 変換不可 (InvalidCastException)
        // の分類を面ごとに突合。OverflowException は実 IL の Throw*OverflowException →
        // SR リソース文言面 (バインド) を辿る
        foreach (var v in new object?[] { 300, -300, 100, "xyz", "300", "", "True", 'A', (long)99999 }) {
            for (var which = 0; which <= 10; which++)
                AssertSame("ConvertClassify", v, which);
        }
    }

    [Fact]
    public void Convert_Enum_Box_Matches_Clr() {
        // byte 基底 enum の box は Enum.GetValue (internal-call リーフ) で基底型に再 box され、
        // 基底型幅ごとの IConvertible EII → Convert.ToXxx (実 IL) で変換される
        foreach (var v in new object?[] { 1, 2, 3 }) {
            AssertSame("ConvertEnumBox", v);
            AssertSame("ConvertEnumBox16", v);
            AssertSame("ConvertEnumBox64", v);
        }
        AssertRunsCoreLibIl("ConvertEnumBox", [2], ("System.Convert", "ToByte"));
    }

    [Fact]
    public void Type_Faces_Match_Clr() {
        AssertSame("TypeNameOfInt");
        AssertSame("TypeFullNameOfString");
    }

    [Fact]
    public void Exception_Message_Matches_Clr() {
        AssertSame("ExceptionMessage");
    }

    [Fact]
    public void Exception_Default_Message_Matches_Clr() {
        AssertSame("ExceptionDefaultMessage", 5);
    }

    // ---- C5.5 Wave 2: 整数書式 overload (Faces 置換面 → FormatSpecifiers IL) ----

    [Fact]
    public void Integer_Format_Overloads_Match_Clr() {
        foreach (var fmt in new[] { "G", "D", "D2", "D10", "x", "X4", "X8", "b", "B16", "0.00", "#,##0" })
            foreach (var v in new[] { 0, 5, -7, 255, 12345, int.MaxValue, int.MinValue })
                AssertSame("IntFormatFaces", v, fmt);
        foreach (var fmt in new[] { "G", "D20", "x16", "X8", "B64", "0.000" })
            foreach (var v in new long[] { 0, 9, -999, long.MaxValue, long.MinValue })
                AssertSame("LongFormatFaces", v, fmt);
        foreach (var fmt in new[] { "G", "X16", "b64", "N0" })
            AssertSame("UnsignedFormatFaces", (ulong)ulong.MaxValue, fmt);
        foreach (var fmt in new[] { "x2", "X4", "D6", "0.0" }) {
            AssertSame("SmallTypeFormatFaces", 200, fmt);
            AssertSame("SmallTypeFormatFaces", 7, fmt);
        }
    }

    [Fact]
    public void Bool_Char_And_Provider_ToString_Match_Clr() {
        AssertSame("BoolCharToStringFaces", true, 'A');
        AssertSame("BoolCharToStringFaces", false, 'z');
        AssertSame("BoolToStringFace", true);
        AssertSame("BoolToStringFace", false);
        AssertSame("CharToStringFace", 'A');
        AssertSame("CharToStringFace", '‰');
        AssertSame("ProviderOnlyFaces", (short)42, (byte)7);
        AssertSame("ProviderOnlyFaces", (short)-1, (byte)200);
    }

    [Fact]
    public void Integer_Format_Exception_Classification_Matches_Clr() {
        foreach (var fmt in new[] { "G", "D2", "x", "X8", "B", "Q5", "q3", "z0", "D2x", "X2j", "E+2", "" })
            AssertSame("FormatClassify", 12345, fmt);
    }

    [Fact]
    public void Integer_Format_Overloads_Run_VmCoreLib_Il() {
        // Faces 置換面の証明: 書式付き ToString の IL フレームが
        // アセンブリ名 "DotnetVM.CoreLib" の FormatSpecifiers として記録される
        AssertRunsVmCoreLibIl("IntFormatFaces", [255, "x"],
            ("DotnetVM.CoreLib.FormatSpecifiers", "Int32ToString"));
        AssertRunsVmCoreLibIl("IntFormatFaces", [-7, "D10"],
            ("DotnetVM.CoreLib.FormatSpecifiers", "Int32ToString"));
        AssertRunsVmCoreLibIl("LongFormatFaces", [-1, "X16"],
            ("DotnetVM.CoreLib.FormatSpecifiers", "Int64ToString"));
        AssertRunsVmCoreLibIl("UnsignedFormatFaces", [ulong.MaxValue, "b64"],
            ("DotnetVM.CoreLib.FormatSpecifiers", "UInt64ToString"));
        AssertRunsVmCoreLibIl("SmallTypeFormatFaces", [200, "x2"],
            ("DotnetVM.CoreLib.FormatSpecifiers", "ByteToString"),
            ("DotnetVM.CoreLib.FormatSpecifiers", "SByteToString"),
            ("DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString"),
            ("DotnetVM.CoreLib.FormatSpecifiers", "UInt16ToString"));
        AssertRunsVmCoreLibIl("BoolToStringFace", [true],
            ("DotnetVM.CoreLib.FormatSpecifiers", "BooleanToString"));
        AssertRunsVmCoreLibIl("CharToStringFace", ['A'],
            ("DotnetVM.CoreLib.FormatSpecifiers", "CharToString"));
        AssertRunsVmCoreLibIl("ProviderOnlyFaces", [42, 7],
            ("DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString"),
            ("DotnetVM.CoreLib.FormatSpecifiers", "ByteToString"));
    }
}
