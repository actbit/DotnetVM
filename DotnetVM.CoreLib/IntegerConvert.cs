namespace DotnetVM.CoreLib;

/// <summary>
/// 整数系の型変換面 (実在 CoreLib の System.Convert 面の再構築)。
/// CLR の System.Convert の整数/文字列間変換と同一の結果を返す:
/// - ToInt32(string) / ToInt64(string): null は 0 (CLR 規約)、それ以外は Parse と同一
/// - ToString(int) / ToString(long): CLR 単体では不変カルチャ、VM では設定カルチャ書式
/// - ToBoolean(int) / ToChar(int): CLR の 0 以外 true / 下位 16bit 文字化
/// </summary>
public static class IntegerConvert {
    /// <summary>CLR の Convert.ToInt32(string) と同一 (null → 0、不正書式は FormatException)。</summary>
    public static int ToInt32(string? value) =>
        value is null ? 0 : NumberFormatting.ParseInt32(value);

    /// <summary>CLR の Convert.ToInt64(string) と同一 (null → 0、不正書式は FormatException)。</summary>
    public static long ToInt64(string? value) =>
        value is null ? 0 : NumberFormatting.ParseInt64(value);

    /// <summary>CLR の Convert.ToString(int) と同一 (不変カルチャ書式)。</summary>
    public static string ToString(int value) => NumberFormatting.Int32ToString(value);

    /// <summary>CLR の Convert.ToString(long) と同一 (不変カルチャ書式)。</summary>
    public static string ToString(long value) => NumberFormatting.Int64ToString(value);

    /// <summary>CLR の Convert.ToBoolean(int) と同一 (0 以外は true)。</summary>
    public static bool ToBoolean(int value) => value != 0;

    /// <summary>CLR の Convert.ToChar(int) と同一 (下位 16bit を文字として解釈。範囲外は OverflowException)。</summary>
    public static char ToChar(int value) {
        if (value is < char.MinValue or > char.MaxValue)
            throw new OverflowException($"値 {value} は Char の範囲外です。");
        return (char)value;
    }

    // ---- 文字列解析面 (Convert.ToXxx(string[, provider]) の置換先)。
    //      実在 CoreLib は X.Parse → Number.Formatting (char* / NumberBuffer) を辿るため
    //      VM では設定カルチャ解析 (NumberFormatting) + 範囲検査に置換する。provider は無視
    //      (VM の設定カルチャを使う)。null は 0 / false (CLR 規約) ----

    /// <summary>CLR の Convert.ToBoolean(string) と同一 ("True" / "False" の大文字小文字を
    /// 無視した一致 (bool.Parse = OrdinalIgnoreCase)、前後空白可。null は false)。</summary>
    public static bool ToBoolean(string? value) {
        if (value is null)
            return false;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "True", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(trimmed, "False", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new FormatException($"文字列 '{value}' は有効な Boolean ではありません。");
    }

    /// <summary>CLR の Convert.ToByte(string) と同一 (0..255。null は 0)。</summary>
    public static byte ToByte(string? value) {
        if (value is null)
            return 0;
        var parsed = NumberFormatting.ParseInt64(value);
        if (parsed is < byte.MinValue or > byte.MaxValue)
            throw new OverflowException($"値 {value} は Byte の範囲外です。");
        return (byte)parsed;
    }

    /// <summary>CLR の Convert.ToSByte(string) と同一 (-128..127。null は 0)。</summary>
    public static sbyte ToSByte(string? value) {
        if (value is null)
            return 0;
        var parsed = NumberFormatting.ParseInt64(value);
        if (parsed is < sbyte.MinValue or > sbyte.MaxValue)
            throw new OverflowException($"値 {value} は SByte の範囲外です。");
        return (sbyte)parsed;
    }

    /// <summary>CLR の Convert.ToInt16(string) と同一 (-32768..32767。null は 0)。</summary>
    public static short ToInt16(string? value) {
        if (value is null)
            return 0;
        var parsed = NumberFormatting.ParseInt64(value);
        if (parsed is < short.MinValue or > short.MaxValue)
            throw new OverflowException($"値 {value} は Int16 の範囲外です。");
        return (short)parsed;
    }

    /// <summary>CLR の Convert.ToUInt16(string) と同一 (0..65535。null は 0)。</summary>
    public static ushort ToUInt16(string? value) {
        if (value is null)
            return 0;
        var parsed = NumberFormatting.ParseInt64(value);
        if (parsed is < ushort.MinValue or > ushort.MaxValue)
            throw new OverflowException($"値 {value} は UInt16 の範囲外です。");
        return (ushort)parsed;
    }

    /// <summary>CLR の Convert.ToUInt32(string) と同一 (0..4294967295。null は 0)。</summary>
    public static uint ToUInt32(string? value) {
        if (value is null)
            return 0;
        var parsed = NumberFormatting.ParseUInt64(value);
        if (parsed > uint.MaxValue)
            throw new OverflowException($"値 {value} は UInt32 の範囲外です。");
        return (uint)parsed;
    }

    /// <summary>CLR の Convert.ToUInt64(string) と同一 (0..18446744073709551615。null は 0)。</summary>
    public static ulong ToUInt64(string? value) =>
        value is null ? 0 : NumberFormatting.ParseUInt64(value);
}
