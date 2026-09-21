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
