using System.Globalization;
using DotnetVM.CoreLib;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C5.5 Wave 2: DotnetVM.CoreLib.FormatSpecifiers (整数書式 overload 置換面) の CLR 差分テスト。
/// このライブラリは普通の .NET クラスライブラリなので、ホスト CLR 上で
/// <c>value.ToString(format, CultureInfo.InvariantCulture)</c> と直接突合できる
/// (VM の culture 規約 = 不変カルチャ固定)。全標準書式 × 境界値のグリッドと、
/// カスタム書式・FormatException 分類を突合してから VmCoreLibSurfaces に配線する。
/// VM 内実行経路の証明 (AssertRunsVmCoreLibIl) は CoreLibFacesIlTests 側で行う。
/// </summary>
public class FormatSpecifiersClrTests {
    // ---- 突合ケースの構築: (値, FormatSpecifiers の 2 引数 impl, ホスト CLR の不変書式) ----

    private static readonly string[] StandardFormats = [
        "G", "g", "d", "D", "D2", "d5", "D10", "D11",
        "x", "X", "x2", "X4", "x8", "X16", "b", "B", "B8", "b16", "B64",
        "f", "F", "F0", "F1", "f2", "F3",
        "n", "N", "N0", "n1", "N2", "n3",
        "e", "E", "E1", "e2", "E3", "E0",
        "c", "C", "C0", "c1", "p", "P", "P0", "p1",
        "r", "R", "g1", "G1", "g2", "G3", "G4", "G5", "g10", "G20",
    ];

    private static readonly string[] CustomFormats = [
        "00000", "#####", "#0", "00",
        "0.00", "#.##", "0.###", "#.#", "00.00", "##0.0#",
        "#,##0", "#,#", "#,##,##0", "0,.0", "0,,.00", "0,,,.0",
        "0%", "0.0%", "#0.00%", "#‰", "0.00‰",
        "0.0e0", "0.00E+00", "0E-0", "00.0e+000", "0.###e-0",
        "0.0;(0.0)", "#.00;(#.00);zero", "pos;neg;zero",
        "'x'0'x'", "\"y\"0\"y\"", "\\#0", "a 0 b", "0\\%",
        "E+00", "E00", "e0", "0.0E-00",
    ];

    private static List<(object Value, Func<string?, string> Impl, Func<string, string> Clr)> BuildCases() {
        var cases = new List<(object, Func<string?, string>, Func<string, string>)>();
        void Add<T>(T value, Func<T, string?, string> impl) where T : notnull, IFormattable {
            Func<string?, string> implCall = f => impl(value, f);
            Func<string, string> clrCall = f => value.ToString(f, CultureInfo.InvariantCulture)!;
            cases.Add(((object)value, implCall, clrCall));
        }
        foreach (var v in new sbyte[] { 0, 1, 5, 9, 12, 99, 100, 127, -1, -5, -9, -12, -99, -100, -128 })
            Add(v, FormatSpecifiers.SByteToString);
        foreach (var v in new byte[] { 0, 1, 9, 200, 255 })
            Add(v, FormatSpecifiers.ByteToString);
        foreach (var v in new short[] { 0, 1, 9, 10, 999, 1000, 32767, -1, -999, -32768 })
            Add(v, FormatSpecifiers.Int16ToString);
        foreach (var v in new ushort[] { 0, 1, 65535 })
            Add(v, FormatSpecifiers.UInt16ToString);
        foreach (var v in new[] {
            0, 1, 5, 9, 10, 15, 16, 95, 99, 100, 123, 995, 999, 1000, 4095, 4999, 5000, 10000,
            49999, 50000, 123456789, 949999999, 950000000,
            int.MaxValue, int.MinValue, -2147483, 2147483,
        })
            Add(v, FormatSpecifiers.Int32ToString);
        foreach (var v in new uint[] { 0, 1, 2147483648u, uint.MaxValue })
            Add(v, FormatSpecifiers.UInt32ToString);
        foreach (var v in new long[] {
            0, 1, 5, 9, 10, 99, 1000, 994999999999, 995000000000, 1234567890123, long.MaxValue, long.MinValue,
            8995000000000000000L, -999999999999L, 922337203685477580L,
        })
            Add(v, FormatSpecifiers.Int64ToString);
        foreach (var v in new ulong[] { 0, 1, 9223372036854775808ul, ulong.MaxValue })
            Add(v, FormatSpecifiers.UInt64ToString);
        return cases;
    }

    [Fact]
    public void Standard_Formats_Match_Clr_Invariant() {
        foreach (var (value, impl, clr) in BuildCases())
            foreach (var format in StandardFormats) {
                var expected = clr(format);
                var actual = impl(format);
                Assert.True(expected == actual,
                    $"{value}.ToString(\"{format}\", Invariant): CLR=\"{expected}\" FormatSpecifiers=\"{actual}\"");
            }
    }

    [Fact]
    public void Custom_Formats_Match_Clr_Invariant() {
        foreach (var (value, impl, clr) in BuildCases())
            foreach (var format in CustomFormats) {
                var expected = clr(format);
                var actual = impl(format);
                Assert.True(expected == actual,
                    $"{value}.ToString(\"{format}\", Invariant): CLR=\"{expected}\" FormatSpecifiers=\"{actual}\"");
            }
    }

    [Fact]
    public void Format_Exceptions_Match_Clr() {
        foreach (var (value, impl, clr) in BuildCases().Take(12)) {
            // 無効な標準書式文字 (letter + 数字列) は FormatException
            foreach (var format in new[] { "Q5", "q3", "z0", "K10", "D2x", "X2j" })
                Assert.Equal(
                    Record.Exception(() => clr(format)) is FormatException,
                    Record.Exception(() => impl(format)) is FormatException);
            // 精度 1e8 以上は書式指定子が長すぎて FormatException
            Assert.Equal(
                Record.Exception(() => clr("D100000000")) is FormatException,
                Record.Exception(() => impl("D100000000")) is FormatException);
        }
    }

    [Fact]
    public void Null_Empty_And_Single_Char_Formats_Match_Clr() {
        foreach (var (value, impl, clr) in BuildCases()) {
            Assert.Equal(clr(null!), impl(null));
            Assert.Equal(clr(""), impl(""));
            // letter 単独は標準書式 (既定精度)、非 letter 単独はカスタム
            Assert.Equal(clr("D"), impl("D"));
            Assert.Equal(clr("x"), impl("x"));
            Assert.Equal(clr("0"), impl("0"));
            Assert.Equal(clr("#"), impl("#"));
        }
    }

    // ---- provider / IFormatProvider 面 (provider は不変カルチャ固定で無視) ----

    [Fact]
    public void Provider_Overloads_Match_Clr_Invariant() {
        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal(42.ToString("X8", invariant), FormatSpecifiers.Int32ToString(42, "X8", null));
        Assert.Equal(42.ToString("X8", invariant), FormatSpecifiers.Int32ToString(42, "X8", invariant));
        Assert.Equal((-7).ToString("N2", invariant), FormatSpecifiers.Int32ToString(-7, "N2", invariant));
        Assert.Equal(long.MaxValue.ToString("E10", invariant), FormatSpecifiers.Int64ToString(long.MaxValue, "E10", invariant));
        Assert.Equal(255.ToString("b8", invariant), FormatSpecifiers.ByteToString(255, "b8", null));
        Assert.Equal(ulong.MaxValue.ToString("N0", invariant), FormatSpecifiers.UInt64ToString(ulong.MaxValue, "N0", invariant));
        // (IFormatProvider) のみの面は Faces の余剰スロット無視で 1 引数 impl に配線される
        Assert.Equal(((short)42).ToString(invariant), FormatSpecifiers.Int16ToString(42));
        Assert.Equal(((sbyte)-5).ToString(invariant), FormatSpecifiers.SByteToString(-5));
        Assert.Equal(true.ToString(invariant), FormatSpecifiers.BooleanToString(true, invariant));
        Assert.Equal('A'.ToString(invariant), FormatSpecifiers.CharToString('A', invariant));
    }

    [Fact]
    public void Small_Integer_And_Bool_Char_ToString_Match_Clr() {
        var invariant = CultureInfo.InvariantCulture;
        foreach (var v in new sbyte[] { 0, 1, -1, sbyte.MaxValue, sbyte.MinValue })
            Assert.Equal(v.ToString(invariant), FormatSpecifiers.SByteToString(v));
        foreach (var v in new byte[] { 0, 1, byte.MaxValue })
            Assert.Equal(v.ToString(invariant), FormatSpecifiers.ByteToString(v));
        foreach (var v in new short[] { 0, 1, -1, short.MaxValue, short.MinValue })
            Assert.Equal(v.ToString(invariant), FormatSpecifiers.Int16ToString(v));
        foreach (var v in new ushort[] { 0, 1, ushort.MaxValue })
            Assert.Equal(v.ToString(invariant), FormatSpecifiers.UInt16ToString(v));
        foreach (var v in new[] { true, false }) {
            Assert.Equal(v.ToString(), FormatSpecifiers.BooleanToString(v));
            Assert.Equal(v.ToString(invariant), FormatSpecifiers.BooleanToString(v, invariant));
        }
        foreach (var c in new[] { 'A', 'z', '0', ' ', '¤', '‰', '\n' }) {
            Assert.Equal(c.ToString(), FormatSpecifiers.CharToString(c));
            Assert.Equal(c.ToString(invariant), FormatSpecifiers.CharToString(c, invariant));
        }
    }
}
