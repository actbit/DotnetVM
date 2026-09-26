using System.Reflection;
using DotnetVM.CoreLib;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// VM CoreLib (DotnetVM.CoreLib) の CLR 差分テスト: このライブラリは普通の .NET
/// クラスライブラリなので、ホスト CLR 上で System.Convert / int.Parse / ToString と
/// 直接突合できる (「外でも使える」ことの証明と、VM 実行前の正當性担保)。
/// VM 内実行経路の証明は CoreLibFacesIlTests / CoreLibIlExecutionTests 側で行う。
/// </summary>
public class VmCoreLibClrTests {
    public static IEnumerable<object[]> Int32Values() =>
        new[] {
            0, 1, 9, 10, 99, 100, 999, 1000, 12345, 123456, 1234567, 12345678, 123456789,
            -1, -9, -10, -99, -100, -12345, -123456789,
            int.MaxValue, int.MinValue, int.MaxValue / 2, int.MinValue / 2, -2000000000,
        }.Select(v => new object[] { v });

    public static IEnumerable<object[]> Int64Values() =>
        new[] {
            0L, 1L, 42L, 999999999L, 1000000000000L, long.MaxValue, long.MinValue,
            -4294967296L, 922337203685477580L,
        }.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Int32Values))]
    public void Int32ToString_Matches_Clr(int value) {
        Assert.Equal(value.ToString(), NumberFormatting.Int32ToString(value));
        Assert.Equal(Convert.ToString(value), IntegerConvert.ToString(value));
    }

    [Theory]
    [MemberData(nameof(Int64Values))]
    public void Int64ToString_Matches_Clr(long value) {
        Assert.Equal(value.ToString(), NumberFormatting.Int64ToString(value));
        Assert.Equal(Convert.ToString(value), IntegerConvert.ToString(value));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4294967295u)]
    [InlineData(2147483648u)]
    public void UInt32ToString_Matches_Clr(uint value) =>
        Assert.Equal(value.ToString(), NumberFormatting.UInt32ToString(value));

    [Theory]
    [InlineData(0ul)]
    [InlineData(18446744073709551615ul)]
    [InlineData(9223372036854775808ul)]
    public void UInt64ToString_Matches_Clr(ulong value) =>
        Assert.Equal(value.ToString(), NumberFormatting.UInt64ToString(value));

    // ---- Parse (書式 OK / FormatException / OverflowException の 3 分類突合) ----

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("-42")]
    [InlineData("+42")]
    [InlineData(" 42")]
    [InlineData("42 ")]
    [InlineData(" -2147483648 ")]
    [InlineData("2147483647")]
    [InlineData("-2147483648")]
    public void ParseInt32_Matches_Clr(string s) =>
        Assert.Equal(int.Parse(s), NumberFormatting.ParseInt32(s));

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData(" 1234567890123 ")]
    public void ParseInt64_Matches_Clr(string s) =>
        Assert.Equal(long.Parse(s), NumberFormatting.ParseInt64(s));

    [Theory]
    [InlineData(null)]
    public void ParseInt32_Null_Matches_Clr(string? s) {
        Assert.ThrowsAny<ArgumentNullException>(() => int.Parse(s!));
        Assert.ThrowsAny<ArgumentNullException>(() => NumberFormatting.ParseInt32(s!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("12x")]
    [InlineData("-")]
    [InlineData("+")]
    [InlineData("--5")]
    [InlineData("+ 42")]
    [InlineData("4 2")]
    public void ParseInt32_FormatException_Matches_Clr(string? s) {
        Assert.ThrowsAny<FormatException>(() => int.Parse(s!));
        Assert.ThrowsAny<FormatException>(() => NumberFormatting.ParseInt32(s!));
    }

    [Theory]
    [InlineData("2147483648")]      // int.MaxValue + 1
    [InlineData("-2147483649")]     // int.MinValue - 1
    [InlineData("99999999999999999999")]
    public void ParseInt32_Overflow_Matches_Clr(string s) {
        Assert.Throws<OverflowException>(() => int.Parse(s));
        Assert.Throws<OverflowException>(() => NumberFormatting.ParseInt32(s));
    }

    [Theory]
    [InlineData("9223372036854775808")]     // long.MaxValue + 1
    [InlineData("-9223372036854775809")]    // long.MinValue - 1
    public void ParseInt64_Overflow_Matches_Clr(string s) {
        Assert.Throws<OverflowException>(() => long.Parse(s));
        Assert.Throws<OverflowException>(() => NumberFormatting.ParseInt64(s));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("+42")]
    [InlineData("-0")]                       // CLR は符号付き 0 を許容
    [InlineData(" 18446744073709551615 ")]  // ulong.MaxValue (前後空白)
    [InlineData("4294967295")]
    public void ParseUInt64_Matches_Clr(string s) =>
        Assert.Equal(ulong.Parse(s), NumberFormatting.ParseUInt64(s));

    [Theory]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]    // ulong.MaxValue + 1
    public void ParseUInt64_Overflow_Matches_Clr(string s) {
        Assert.Throws<OverflowException>(() => ulong.Parse(s));
        Assert.Throws<OverflowException>(() => NumberFormatting.ParseUInt64(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1x")]
    public void ParseUInt64_FormatException_Matches_Clr(string s) {
        Assert.ThrowsAny<FormatException>(() => ulong.Parse(s));
        Assert.ThrowsAny<FormatException>(() => NumberFormatting.ParseUInt64(s));
    }

    // ---- Convert 面 (文字列解析: C5.5 Wave 1 の (string, IFormatProvider) 置換面の実装) ----

    // ---- Convert 面 ----

    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    public void Convert_ToInt32_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToInt32(s), IntegerConvert.ToInt32(s));

    [Fact]
    public void Convert_ToInt32_Null_Is_Zero() {
        Assert.Equal(0, Convert.ToInt32((string?)null));
        Assert.Equal(0, IntegerConvert.ToInt32(null));
    }

    [Fact]
    public void Convert_ToInt64_And_Boolean_And_Char_Match_Clr() {
        Assert.Equal(Convert.ToInt64("1234567890123"), IntegerConvert.ToInt64("1234567890123"));
        Assert.Equal(Convert.ToBoolean(1), IntegerConvert.ToBoolean(1));
        Assert.Equal(Convert.ToBoolean(0), IntegerConvert.ToBoolean(0));
        Assert.Equal(Convert.ToChar(97), IntegerConvert.ToChar(97));
        Assert.Throws<OverflowException>(() => IntegerConvert.ToChar(70000));
        Assert.Equal(char.MaxValue, IntegerConvert.ToChar(char.MaxValue));
    }

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData(" True ")]
    [InlineData("true")]       // bool.Parse = OrdinalIgnoreCase (大文字小文字を無視)
    [InlineData("FALSE")]
    public void Convert_ToBoolean_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToBoolean(s), IntegerConvert.ToBoolean(s));

    [Theory]
    [InlineData("YES")]
    [InlineData("")]
    [InlineData("1")]
    public void Convert_ToBoolean_String_FormatException_Matches_Clr(string s) {
        Assert.ThrowsAny<FormatException>(() => Convert.ToBoolean(s));
        Assert.ThrowsAny<FormatException>(() => IntegerConvert.ToBoolean(s));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("255")]
    [InlineData("42")]
    public void Convert_ToByte_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToByte(s), IntegerConvert.ToByte(s));

    [Theory]
    [InlineData("-128")]
    [InlineData("127")]
    [InlineData("-5")]
    public void Convert_ToSByte_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToSByte(s), IntegerConvert.ToSByte(s));

    [Theory]
    [InlineData("-32768")]
    [InlineData("32767")]
    public void Convert_ToInt16_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToInt16(s), IntegerConvert.ToInt16(s));

    [Theory]
    [InlineData("65535")]
    [InlineData("1234")]
    public void Convert_ToUInt16_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToUInt16(s), IntegerConvert.ToUInt16(s));

    [Theory]
    [InlineData("4294967295")]
    [InlineData("1234")]
    public void Convert_ToUInt32_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToUInt32(s), IntegerConvert.ToUInt32(s));

    [Theory]
    [InlineData("18446744073709551615")]
    [InlineData("1234")]
    public void Convert_ToUInt64_String_Matches_Clr(string s) =>
        Assert.Equal(Convert.ToUInt64(s), IntegerConvert.ToUInt64(s));

    [Theory]
    [InlineData("256", "Byte")]
    [InlineData("128", "SByte")]
    [InlineData("32768", "Int16")]
    [InlineData("65536", "UInt16")]
    [InlineData("4294967296", "UInt32")]
    [InlineData("18446744073709551616", "UInt64")]
    [InlineData("-1", "Byte")]
    [InlineData("-129", "SByte")]
    [InlineData("-1", "UInt16")]
    [InlineData("-1", "UInt32")]
    [InlineData("-1", "UInt64")]
    public void Convert_String_Range_Overflow_Matches_Clr(string s, string face) {
        var convert = typeof(Convert).GetMethod("To" + face, [typeof(string)])!;
        var impl = typeof(IntegerConvert).GetMethod("To" + face, [typeof(string)])!;
        // リフレクション呼出は例外を TargetInvocationException で包むため内側を照合する
        AssertInvokesOverflow(() => convert.Invoke(null, [s])!);
        AssertInvokesOverflow(() => impl.Invoke(null, [s])!);
    }

    // ---- String ordinal 面 (C5.5 Wave 5 の (b) 置換面: StringOrdinalOps) ----

    /// <summary>結果が同じなら値、例外なら「!型名」に正規化して突合する。</summary>
    internal static object? Outcome(Func<object?> run) {
        try {
            return run();
        } catch (Exception ex) {
            return "!" + ex.GetType().Name;
        }
    }

    public static IEnumerable<object[]> OrdinalPairs() =>
        new[] {
            ("abc", "abc"), ("abc", "abd"), ("abc", "ab"), ("ab", "abc"),
            ("", ""), ("", "a"), ("a", ""), ("ABC", "abc"), ("abc", "ABC"),
            ("ß", "ss"), ("ss", "ß"), ("Å", "A"), ("é", "e"), ("a", "á"),
            ("￿", "\u0000"), ("Z", "a"), ("abc", "abc\u0001"),
        }.Select(p => new object[] { p.Item1, p.Item2 });

    [Theory]
    [MemberData(nameof(OrdinalPairs))]
    public void CompareOrdinal_Matches_Clr(string? a, string? b) {
        // 本家どおり「最初の差分位置のコード単位差そのもの」/ 共通 prefix は長さ差 /
        // null セーフ (null < 非 null) を値まで含めて突合する
        Assert.Equal(string.CompareOrdinal(a, b), StringOrdinalOps.CompareOrdinal(a, b));
    }

    [Fact]
    public void IndexOfChar_And_LastIndexOfChar_Match_Clr() {
        string[] values = ["", "a", "abc", "aab", "aba", "banana", "éÉ", "a,b"];
        char[] chars = ['a', 'b', 'z', ',', 'é'];
        foreach (var v in values) {
            foreach (var c in chars) {
                Assert.Equal(v.IndexOf(c), StringOrdinalOps.IndexOfChar(v, c));
                Assert.Equal(v.LastIndexOf(c), StringOrdinalOps.LastIndexOfChar(v, c));
            }
        }
    }

    [Theory]
    [InlineData("abcabc", "b")]
    [InlineData("abcabc", "cab")]
    [InlineData("abc", "xyz")]
    [InlineData("abc", "")]
    [InlineData("", "")]
    [InlineData("", "a")]
    [InlineData("Straße", "ß")]
    public void Contains_Matches_Clr(string value, string needle) {
        Assert.Equal(value.Contains(needle), StringOrdinalOps.Contains(value, needle));
        // null needle は本家どおり ArgumentNullException (分類突合)
        Assert.Equal(Outcome(() => value.Contains(null!)),
                     Outcome(() => StringOrdinalOps.Contains(value, null!)));
    }

    [Theory]
    [InlineData("abcabc", "b", "B")]
    [InlineData("abcabc", "bc", "X")]
    [InlineData("aaa", "a", "")]
    [InlineData("aaa", "aa", "ab")]
    [InlineData("abc", "xyz", "Q")]
    [InlineData("", "a", "b")]
    [InlineData("abc", "c", null)]
    public void Replace_Matches_Clr(string value, string oldValue, string? newValue) {
        Assert.Equal(Outcome(() => value.Replace(oldValue, newValue!)),
                     Outcome(() => StringOrdinalOps.Replace(value, oldValue, newValue!)));
    }

    [Fact]
    public void Replace_Exceptions_Match_Clr() {
        // 空 oldValue は本家どおり ArgumentException / null oldValue は ArgumentNullException /
        // null newValue は本家どおり空文字列扱い (例外ではない)
        Assert.Equal(Outcome(() => "abc".Replace("", "x")),
                     Outcome(() => StringOrdinalOps.Replace("abc", "", "x")));
        Assert.Equal(Outcome(() => "abc".Replace(null!, "x")),
                     Outcome(() => StringOrdinalOps.Replace("abc", null!, "x")));
        Assert.Equal(Outcome(() => "abc".Replace("a", null!)),
                     Outcome(() => StringOrdinalOps.Replace("abc", "a", null!)));
    }

    [Fact]
    public void Split_AllOverloads_Match_Clr() {
        string[] values = ["", "a", ",", "a,b", "a,,b", ",a,", "a,b,c", "a;b,c", "a;,;b", " a , b ", "ab cd", "abc"];
        int[] counts = [0, 1, 2, 3, 5];
        var optionsList = new[] { StringSplitOptions.None, StringSplitOptions.RemoveEmptyEntries };
        var charArrs = new char[][] { [','], [',', ';'], [' '], [',', ';', ' '] };
        var strSeps = new string?[] { ",", ";", "a,b", "ab", "", null };
        var strArrs = new string?[][] { ["b", ","], [","], ["ab", "c"], ["", null, ",", "b"], [] };

        foreach (var v in values) {
            // ---- char 系 4 面 (char / char,count / char,options / char,count,options) ----
            Assert.Equal(v.Split(','), StringOrdinalOps.SplitChar(v, ','));
            foreach (var o in optionsList)
                Assert.Equal(v.Split(',', o), StringOrdinalOps.SplitCharOptions(v, ',', o));
            foreach (var count in counts) {
                Assert.Equal(v.Split(',', count), StringOrdinalOps.SplitCharCount(v, ',', count));
                foreach (var o in optionsList)
                    Assert.Equal(v.Split(',', count, o), StringOrdinalOps.SplitCharFull(v, ',', count, o));
            }

            // ---- char[] 系 4 面 (複数 / 空白含む / 1 要素)。null・空配列は Fallback テストで ----
            foreach (var ca in charArrs) {
                Assert.Equal(v.Split(ca), StringOrdinalOps.SplitCharArray(v, ca));
                foreach (var o in optionsList)
                    Assert.Equal(v.Split(ca, o), StringOrdinalOps.SplitCharArrayOptions(v, ca, o));
                foreach (var count in counts) {
                    Assert.Equal(v.Split(ca, count), StringOrdinalOps.SplitCharArrayCount(v, ca, count));
                    foreach (var o in optionsList)
                        Assert.Equal(v.Split(ca, count, o), StringOrdinalOps.SplitCharArrayFull(v, ca, count, o));
                }
            }

            // ---- string 系 2 面 (string,options / string,count,options)。
            //      separator null / 空 = 分割なし 1 要素 (本家どおり、Fallback テストで直接突合) ----
            foreach (var s in strSeps) {
                foreach (var o in optionsList)
                    Assert.Equal(v.Split(s, o), StringOrdinalOps.SplitStringOptions(v, s, o));
                foreach (var count in counts) {
                    foreach (var o in optionsList)
                        Assert.Equal(v.Split(s, count, o), StringOrdinalOps.SplitStringFull(v, s, count, o));
                }
            }

            // ---- string[] 系 2 面 (配列順照合 / null・空要素スキップ / null・空配列は空白)。
            //      ホストに Split(string[]) / Split(string[], int) は存在しないため
            //      options 付き overload 経由のみで突合する ----
            foreach (var sa in strArrs) {
                var hostSeparators = sa.Select(static value => value!).ToArray();
                foreach (var o in optionsList)
                    Assert.Equal(v.Split(hostSeparators, o), StringOrdinalOps.SplitStringsOptions(v, sa, o));
                foreach (var count in counts) {
                    foreach (var o in optionsList)
                        Assert.Equal(v.Split(hostSeparators, count, o), StringOrdinalOps.SplitStringsFull(v, sa, count, o));
                }
            }
        }
    }

    [Fact]
    public void Split_WhitespaceFallback_Matches_Clr() {
        // 本家の未指定意味論は系統ごとに異なる (実測確認済み):
        // - char[] / string[]: null / 空 = 空白 (char.IsWhiteSpace) 区切り
        // - string 単独: null / 空 = 分割なし 1 要素 (空白フォールバックしない)
        string[] values = ["", "a", "a,b,c", " a , b ", "ab cd", "  x  y  "];
        var none = StringSplitOptions.None;
        foreach (var v in values) {
            // char[] 系統 → 空白分割
            Assert.Equal(StringOrdinalOps.SplitCharArray(v, null), v.Split());
            Assert.Equal(StringOrdinalOps.SplitCharArray(v, []), v.Split());
            Assert.Equal(StringOrdinalOps.SplitCharArrayFull(v, null, 3, none), v.Split((char[]?)null, 3, none));
            // string[] 系統 → 空白分割
            Assert.Equal(StringOrdinalOps.SplitStringsOptions(v, null, none), v.Split());
            Assert.Equal(StringOrdinalOps.SplitStringsOptions(v, [], none), v.Split());
            Assert.Equal(StringOrdinalOps.SplitStringsFull(v, null, 3, none), v.Split((string[]?)null, 3, none));
            // string 単独系統 → 分割なし 1 要素 (count / options に関係なく whitespace
            // フォールバックしない。実測: v.Split((string?)null, 3, None) も 1 要素)
            Assert.Equal(StringOrdinalOps.SplitStringOptions(v, null, none), v.Split((string?)null, none));
            Assert.Equal(StringOrdinalOps.SplitStringFull(v, null, 3, none), v.Split((string?)null, 3, none));
        }
    }

    [Fact]
    public void Split_InvalidArguments_Match_Clr() {
        // 負 count → ArgumentOutOfRangeException / 無効 enum → ArgumentException (分類突合)
        Assert.Equal(Outcome(() => "a,b".Split(',', -1)),
                     Outcome(() => StringOrdinalOps.SplitCharCount("a,b", ',', -1)));
        Assert.Equal(Outcome(() => "a,b".Split(',', 3, (StringSplitOptions)(-1))),
                     Outcome(() => StringOrdinalOps.SplitCharFull("a,b", ',', 3, (StringSplitOptions)(-1))));
        Assert.Equal(Outcome(() => "a,b".Split(',', (StringSplitOptions)99)),
                     Outcome(() => StringOrdinalOps.SplitCharOptions("a,b", ',', (StringSplitOptions)99)));
        Assert.Equal(Outcome(() => "a,b".Split(',', 3, (StringSplitOptions)99)),
                     Outcome(() => StringOrdinalOps.SplitCharFull("a,b", ',', 3, (StringSplitOptions)99)));
        Assert.Equal(Outcome(() => "a,b".Split([','], (StringSplitOptions)99)),
                     Outcome(() => StringOrdinalOps.SplitCharArrayOptions("a,b", [','], (StringSplitOptions)99)));
        Assert.Equal(Outcome(() => "a,b".Split(",", (StringSplitOptions)99)),
                     Outcome(() => StringOrdinalOps.SplitStringOptions("a,b", ",", (StringSplitOptions)99)));
        Assert.Equal(Outcome(() => "a,b".Split(",", -1, StringSplitOptions.None)),
                     Outcome(() => StringOrdinalOps.SplitStringFull("a,b", ",", -1, StringSplitOptions.None)));
        Assert.Equal(Outcome(() => "a,b".Split(new[] { "," }, (StringSplitOptions)99)),
                     Outcome(() => StringOrdinalOps.SplitStringsOptions("a,b", [","], (StringSplitOptions)99)));
        Assert.Equal(Outcome(() => "a,b".Split(new[] { "," }, -1, StringSplitOptions.None)),
                     Outcome(() => StringOrdinalOps.SplitStringsFull("a,b", [","], -1, StringSplitOptions.None)));
    }

    // ---- String.Format 複合書式面 (C5.5 Wave 5 の (b) 置換面: StringFormatting) ----

    [Fact]
    public void Format_Grid_Matches_Clr() {
        object?[][] argSets = [
            [42],
            [-42],
            [0],
            [int.MaxValue],
            ["abc"],
            [""],
            [null],
            [42, "abc"],
            ["abc", 42, 3.5],
            [true, 'x', (byte)7],
        ];
        string[] formats = [
            "[{0}]", "[{0,10}]", "[{0,-10}]", "[{0:X4}]", "[{0:x8}]", "[{0:D5}]",
            "[{0:F3}]", "[{0:N2}]", "[{0:E4}]", "[{0:0000.00}]", "[{0}{0}]",
            "[{0,6:F2}]", "[{0, -8}]", "{0}", "pre {0} post", "", "{{{0}}}",
            "[{2}]", "[{5}]", // 引数個数超過 → FormatException (分類突合)
        ];
        foreach (var args in argSets) {
            foreach (var format in formats) {
                Assert.Equal(Outcome(() => string.Format(format, args)),
                             Outcome(() => StringFormatting.FormatArray(format, args)));
            }
        }
    }

    [Fact]
    public void Format_IndividualOverloads_Match_Clr() {
        Assert.Equal(string.Format("{0}", 42), StringFormatting.Format2("{0}", 42));
        Assert.Equal(string.Format("{0}-{1}", 42, "x"), StringFormatting.Format3("{0}-{1}", 42, "x"));
        Assert.Equal(string.Format("{0}-{1}-{2}", 1, 2, 3), StringFormatting.Format4("{0}-{1}-{2}", 1, 2, 3));
        Assert.Equal(string.Format("{0}!{1}!{2}!{3}", 1, 2, 3, 4),
                     StringFormatting.FormatArray("{0}!{1}!{2}!{3}", [1, 2, 3, 4]));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{0")]
    [InlineData("{x}")]
    [InlineData("{-1}")]
    [InlineData("{5}")]
    [InlineData("{}")]
    [InlineData("{0:{1}}")]
    [InlineData("abc{0")]
    [InlineData("{0,")]
    [InlineData("{0,x}")]
    [InlineData("{0,1x}")]
    [InlineData("{0:-1}")]
    [InlineData("}{")]
    [InlineData("{}}")]
    [InlineData("{0:}}")]
    [InlineData("{ {0}")]
    [InlineData("{0,999999999999}")]
    public void Format_Malformed_Matches_Clr(string format) {
        Assert.Equal(Outcome(() => string.Format(format, 42)),
                     Outcome(() => StringFormatting.FormatArray(format, [42])));
    }

    [Fact]
    public void Format_Null_Matches_Clr() {
        // format null → ArgumentNullException("format") / args null も本家どおり ANE
        Assert.Equal(Outcome(() => string.Format(null!, [42])),
                     Outcome(() => StringFormatting.FormatArray(null, [42])));
        Assert.ThrowsAny<ArgumentNullException>(() => string.Format("x", null!));
        Assert.ThrowsAny<ArgumentNullException>(() => StringFormatting.FormatArray("x", (object[]?)null));
        Assert.ThrowsAny<ArgumentNullException>(() => StringFormatting.FormatArray(null, (object[]?)null));
    }

    [Fact]
    public void Format_ProviderOverloads_Match_Clr_With_Invariant() {
        // provider 付き overload も不変カルチャ規約 (provider 無視) — 突合は invariant で行う
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(string.Format(inv, "{0:F2}", 1.5),
                     StringFormatting.FormatProviderArray(inv, "{0:F2}", [1.5]));
        Assert.Equal(string.Format(inv, "{0}-{1}", 42, "x"),
                     StringFormatting.FormatProvider4(inv, "{0}-{1}", 42, "x"));
        Assert.Equal(string.Format(inv, "[{0}]", 42),
                     StringFormatting.FormatProvider3(inv, "[{0}]", 42));
        Assert.Equal(string.Format(inv, "[{0}][{1}][{2}]", 1, 2, 3),
                     StringFormatting.FormatProvider5(inv, "[{0}][{1}][{2}]", 1, 2, 3));
        // provider を無視するため null でも同一結果 (VM 規約: culture スコープ外)
        Assert.Equal(StringFormatting.FormatProviderArray(inv, "{0:F2}", [1.5]),
                     StringFormatting.FormatProviderArray(null, "{0:F2}", [1.5]));
    }

    // ---- culture 正当化 (C5.5 Wave 5): ordinal では不変カルチャ比較を再現できない証明 ----

    [Fact]
    public void Invariant_Compare_Differs_From_Ordinal_Proving_Culture_Out_Of_Scope() {
        // 実測 (net10.0): インバリアントは合成文字を等価とみなす (2 次ウェイト照合 =
        // ignoreNonSpace) / ordinal では非等価。この差分が culture 必須面を
        // (c) culture-out-of-scope (不変カルチャ委譲) に降格する根拠 —
        // ordinal 近似の IL 移植では再現できない。
        // 注: ß/ss はインバリアントでも非等価 (実測 1。de-DE 展開はスコープ外)。
        // StringComparison.InvariantCulture は case-sensitive (実測: "a" vs "A" → 非 0)
        string precomposed = "Å";      // A-ring precomposed
        string combining = "Å";  // A + combining ring above
        Assert.Equal(0, string.Compare(precomposed, combining, StringComparison.InvariantCulture));
        Assert.NotEqual(0, string.CompareOrdinal(precomposed, combining));
        string ePre = "é";             // e-acute precomposed
        string eComb = "é";      // e + combining acute
        Assert.Equal(0, string.Compare(ePre, eComb, StringComparison.InvariantCulture));
        Assert.NotEqual(0, string.CompareOrdinal(ePre, eComb));
    }

    [Fact]
    public void Invariant_Delegation_Matches_Clr_Culture_Faces_Fuzz() {
        // VM ① バインド相当 (不変カルチャ委譲 = StringComparison.InvariantCulture) と
        // CLR の CurrentCulture 依存面 (TestCulture で invariant にピン留め済み) が
        // ASCII + 非 ASCII の全組合わせで一致すること (= 委譲の同一意味論の証明)
        string[] samples = ["a", "A", "b", "abc", "abd", "ß", "ss", "Straße", "strasse",
                            "Å", "Å", "é", "e", "Z", "", "a b"];
        foreach (var a in samples) {
            foreach (var b in samples) {
                Assert.Equal(string.Compare(a, b, StringComparison.CurrentCulture),
                             string.Compare(a, b, StringComparison.InvariantCulture));
                Assert.Equal(a.IndexOf(b, StringComparison.CurrentCulture),
                             a.IndexOf(b, StringComparison.InvariantCulture));
                Assert.Equal(a.StartsWith(b, StringComparison.CurrentCulture),
                             a.StartsWith(b, StringComparison.InvariantCulture));
                Assert.Equal(a.EndsWith(b, StringComparison.CurrentCulture),
                             a.EndsWith(b, StringComparison.InvariantCulture));
            }
        }
    }

    /// <summary>デリゲート実行の例外が TargetInvocationException に包まれる場合に
    /// 内側の例外型が OverflowException であることを検証する。</summary>
    private static void AssertInvokesOverflow(Func<object?> invoke) {
        Exception? thrown = null;
        try {
            invoke();
        } catch (Exception ex) {
            thrown = ex;
        }
        var wrapped = Assert.IsType<TargetInvocationException>(thrown);
        Assert.IsType<OverflowException>(wrapped.InnerException);
    }
}
