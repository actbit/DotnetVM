namespace DotnetVM.CoreLib;

/// <summary>
/// 整数の 10 進書式 / 解析 (実在 CoreLib の System.Number 面の再構築)。
///
/// CLR の不変カルチャ書式と同一の結果を返す (符号は "-"、桁区切りなし):
/// <c>NumberFormatting.Int32ToString(-123) == ((object)-123).ToString()</c>。
/// 実装は VM で IL 実行できる形に限定する (配列 / ループ / 基本演算のみ。
/// 実在 CoreLib の Number.Formatting が使う byte* ポインタ演算 / NumberBuffer /
/// stackalloc は VM の表現モデルに落ちないため使わない)。
/// </summary>
public static class NumberFormatting {
    /// <summary>uint.MaxValue の 10 進桁数 (負の int の絶対値もこれに収まる)。</summary>
    private const int MaxUInt32Digits = 10;
    /// <summary>ulong.MaxValue の 10 進桁数 (負の long の絶対値もこれに収まる)。</summary>
    private const int MaxUInt64Digits = 20;

    // ---- 書式 (ToString) ----

    /// <summary>CLR の int.ToString() (不変カルチャ) と同一。int.MinValue も
    /// 絶対値を uint 空間 (|int.MinValue| = 2147483648) で処理するため neg であふれない。</summary>
    public static string Int32ToString(int value) {
        if (value >= 0)
            return UInt32ToString((uint)value);
        return "-" + UInt32ToString(0u - (uint)value);
    }

    /// <summary>CLR の uint.ToString() (不変カルチャ) と同一。</summary>
    public static string UInt32ToString(uint value) {
        if (value == 0)
            return "0";
        var buffer = new char[MaxUInt32Digits];
        var index = buffer.Length;
        while (value != 0) {
            buffer[--index] = (char)('0' + value % 10);
            value /= 10;
        }
        return new string(buffer, index, buffer.Length - index);
    }

    /// <summary>CLR の long.ToString() (不変カルチャ) と同一。long.MinValue も
    /// 絶対値を ulong 空間 (|long.MinValue| = 9223372036854775808) で処理する。</summary>
    public static string Int64ToString(long value) {
        if (value >= 0)
            return UInt64ToString((ulong)value);
        return "-" + UInt64ToString(0ul - (ulong)value);
    }

    /// <summary>CLR の ulong.ToString() (不変カルチャ) と同一。</summary>
    public static string UInt64ToString(ulong value) {
        if (value == 0)
            return "0";
        var buffer = new char[MaxUInt64Digits];
        var index = buffer.Length;
        while (value != 0) {
            buffer[--index] = (char)('0' + value % 10);
            value /= 10;
        }
        return new string(buffer, index, buffer.Length - index);
    }

    // ---- 解析 (Parse)。CLR の int.Parse / long.Parse (NumberStyles.Integer / 不変カルチャ) と同一:
    //      前後の ASCII 空白と任意の符号、10 進数字列のみを受け付ける。それ以外は FormatException、
    //      表現範囲外は OverflowException (空文字列 / null / 符号のみも FormatException) ----

    public static int ParseInt32(string s) {
        var (negative, magnitude) = ParseMagnitude(s, (uint)int.MaxValue + 1ul);
        if (!negative)
            return (int)magnitude; // 上限検査済み (magnitude <= int.MaxValue)
        return magnitude == (uint)int.MaxValue + 1ul ? int.MinValue : -(int)magnitude;
    }

    public static long ParseInt64(string s) {
        var (negative, magnitude) = ParseMagnitude(s, (ulong)long.MaxValue + 1ul);
        if (!negative)
            return (long)magnitude; // 上限検査済み (magnitude <= long.MaxValue)
        return magnitude == (ulong)long.MaxValue + 1ul ? long.MinValue : -(long)magnitude;
    }

    /// <summary>CLR の uint.Parse / ulong.Parse (NumberStyles.Integer / 不変カルチャ) と同一。
    /// ulong.MaxValue (20 桁) も符号なし空間で処理するため ParseMagnitude (ulong 絶対値) では
    /// 表現しきれない。符号は '+' / '-' を許容するが '-' 付きの非零は OverflowException
    /// ("-0" は CLR 同様 0)。</summary>
    public static ulong ParseUInt64(string s) {
        if (s is null)
            throw new ArgumentNullException(nameof(s));
        var index = 0;
        var length = s.Length;
        while (index < length && IsWhiteSpace(s[index]))
            index++;
        var negative = false;
        if (index < length && (s[index] == '+' || s[index] == '-')) {
            negative = s[index] == '-';
            index++;
        }
        if (index >= length)
            throw new FormatException($"入力文字列 '{s}' は有効な整数ではありません。");
        var digits = 0ul;
        var any = false;
        while (index < length && s[index] >= '0' && s[index] <= '9') {
            var digit = (ulong)(s[index] - '0');
            if (digits > (ulong.MaxValue - digit) / 10)
                throw new OverflowException($"値 {s} は対象の整数の範囲外です。");
            digits = digits * 10 + digit;
            index++;
            any = true;
        }
        while (index < length && IsWhiteSpace(s[index]))
            index++;
        if (index != length || !any)
            throw new FormatException($"入力文字列 '{s}' は有効な整数ではありません。");
        if (negative && digits != 0)
            throw new OverflowException($"値 {s} は対象の整数の範囲外です。");
        return digits;
    }

    /// <summary>符号付き 10 進整数の共通解析。limitAbs は表現できる絶対値の上限
    /// (最大値、または最小値の絶対値 = |min|)。これを超えたら OverflowException。
    /// 戻り値は符号と絶対値 (CLR int.Parse(null) と同様、null は ArgumentNullException)。</summary>
    private static (bool Negative, ulong Magnitude) ParseMagnitude(string s, ulong limitAbs) {
        if (s is null)
            throw new ArgumentNullException(nameof(s));
        var index = 0;
        var length = s.Length;
        while (index < length && IsWhiteSpace(s[index]))
            index++;
        var negative = false;
        if (index < length) {
            var c = s[index];
            if (c == '-' || c == '+') {
                negative = c == '-';
                index++;
            }
        }
        if (index >= length)
            throw new FormatException($"入力文字列 '{s}' は有効な整数ではありません。");
        var digits = 0ul;
        var any = false;
        while (index < length && s[index] >= '0' && s[index] <= '9') {
            var digit = (ulong)(s[index] - '0');
            if (digits > (ulong.MaxValue - digit) / 10)
                throw new OverflowException($"値 {s} は対象の整数の範囲外です。");
            digits = digits * 10 + digit;
            index++;
            any = true;
        }
        while (index < length && IsWhiteSpace(s[index]))
            index++;
        if (index != length || !any)
            throw new FormatException($"入力文字列 '{s}' は有効な整数ではありません。");
        var limit = negative ? limitAbs : limitAbs - 1; // 正数の上限は limitAbs - 1 (|min| = max + 1)
        if (digits > limit)
            throw new OverflowException($"値 {s} は対象の整数の範囲外です。");
        return (negative, digits);
    }

    /// <summary>CLR char.IsWhiteSpace の ASCII 部分集合 (不変カルチャ解析が許容する空白)。
    /// VM の決定論のため Unicode 面には依存しない。</summary>
    private static bool IsWhiteSpace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
}
