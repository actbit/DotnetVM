namespace DotnetVM.CoreLib;

/// <summary>
/// 整数の標準書式 + カスタム書式エンジン (実在 CoreLib の System.Number 書式面の再構築)。
///
/// dotnet/runtime (MIT) の System.Private.CoreLib System.Number
/// (Number.Formatting.cs: ParseFormatSpecifier / NumberToString / NumberToStringFormat /
/// RoundNumber / FormatFixed / FormatGeneral / FormatScientific / FormatExponent /
/// FindSection / 通貨・パーセント・数値パターン表) の意味論を、VM で IL 実行できる形に
/// 限定して移植したもの (byte* ポインタ / stackalloc / ValueListBuilder / Span を
/// char[] + インデックス + 手書きバッファに置換。Array.Copy 等の依存面も使わない)。
///
/// culture は不変カルチャ固定 (NumberFormatInfo.Invariant の固定値を定数として持つ:
/// 負号 "-"、小数点 "."、桁区切り ","、通貨記号 "¤"、パーセント "%"、既定小数桁数 2、
/// 区切り桁数 [3]、負数パターン -n / (¤n) / -n %)。
/// CLR の不変カルチャ書式と同一の結果を返す (VmCoreLibClrTests の全面グリッドで突合)。
/// </summary>
public static class FormatSpecifiers {
    // ---- 不変カルチャ (NumberFormatInfo.InvariantInfo) の固定値 ----
    private const string NegativeSign = "-";
    private const string DecimalSeparator = ".";
    private const string GroupSeparator = ",";
    private const string CurrencySymbol = "¤"; // U+00A4
    private const string PercentSymbol = "%";
    private const string PerMilleSymbol = "‰"; // U+2030
    private const int DecimalDigits = 2;       // Number/Currency/PercentDecimalDigits (不変カルチャは全部 2)
    private static readonly int[] NumberGroupSizes = [3]; // 不変カルチャの桁区切り (3 桁ごと)

    // パターン表 (invariant のインデックス固定: NumberNegativePattern=1, CurrencyPos=0,
    // CurrencyNeg=0, PercentPos=0, PercentNeg=0)。'_' = 数値部, 'n' = 負号, 'c' = 通貨記号,
    // 'p' = パーセント記号 として展開する (本家は "#" が数値部)
    private const string NumberNegativePattern = "n_";
    private const string CurrencyPositivePattern = "c_";
    private const string CurrencyNegativePattern = "(c_)";
    private const string PercentPositivePattern = "_ p";
    private const string PercentNegativePattern = "n_ p";

    /// <summary>数値バッファのサイズ (ulong 20 桁 + 丸め余裕)。</summary>
    private const int DigitBufferSize = 24;

    // ---- 公開エントリ (面ごと)。provider は不変カルチャ固定で無視 ----

    public static string SByteToString(sbyte value) => FormatSigned(value, 0xFF, null);
    public static string SByteToString(sbyte value, string? format) => FormatSigned(value, 0xFF, format);
    public static string SByteToString(sbyte value, string? format, object? provider) => FormatSigned(value, 0xFF, format);
    public static string ByteToString(byte value) => FormatUnsigned(value, null);
    public static string ByteToString(byte value, string? format) => FormatUnsigned(value, format);
    public static string ByteToString(byte value, string? format, object? provider) => FormatUnsigned(value, format);
    public static string Int16ToString(short value) => FormatSigned(value, 0xFFFF, null);
    public static string Int16ToString(short value, string? format) => FormatSigned(value, 0xFFFF, format);
    public static string Int16ToString(short value, string? format, object? provider) => FormatSigned(value, 0xFFFF, format);
    public static string UInt16ToString(ushort value) => FormatUnsigned(value, null);
    public static string UInt16ToString(ushort value, string? format) => FormatUnsigned(value, format);
    public static string UInt16ToString(ushort value, string? format, object? provider) => FormatUnsigned(value, format);
    public static string Int32ToString(int value, string? format) => FormatSigned(value, 0xFFFFFFFF, format);
    public static string Int32ToString(int value, string? format, object? provider) => FormatSigned(value, 0xFFFFFFFF, format);
    public static string UInt32ToString(uint value, string? format) => FormatUnsigned(value, format);
    public static string UInt32ToString(uint value, string? format, object? provider) => FormatUnsigned(value, format);
    public static string Int64ToString(long value, string? format) => FormatSignedLong(value, 0xFFFFFFFFFFFFFFFF, format);
    public static string Int64ToString(long value, string? format, object? provider) => FormatSignedLong(value, 0xFFFFFFFFFFFFFFFF, format);
    public static string UInt64ToString(ulong value, string? format) => FormatUnsigned(value, format);
    public static string UInt64ToString(ulong value, string? format, object? provider) => FormatUnsigned(value, format);

    public static string BooleanToString(bool value) => value ? "True" : "False";
    public static string BooleanToString(bool value, object? provider) => value ? "True" : "False";
    public static string CharToString(char value) => new string(value, 1);
    public static string CharToString(char value, object? provider) => new string(value, 1);

    // ---- 共通核 ----

    /// <summary>符号付き整数の書式。magnitude は絶対値、raw は型幅マスク済みの生ビット
    /// (X/B 書式が負数の 2 の補数表現をそのまま 16/2 進化する CLR 規約のため)。</summary>
    private static string FormatSigned(long value, ulong mask, string? format) {
        var negative = value < 0;
        // long 空間で反転してから ulong 化 (sbyte/short/int の絶対値は long に収まるためあふれない)
        var magnitude = negative ? (ulong)(-value) : (ulong)value;
        var raw = (ulong)value & mask;
        return FormatCore(negative, magnitude, raw, format);
    }

    private static string FormatSignedLong(long value, ulong mask, string? format) {
        var negative = value < 0;
        // long.MinValue も uint/ulong 空間で絶対値を取る (unchecked 反転 = bit パターン)
        var magnitude = negative ? 0ul - (ulong)value : (ulong)value;
        var raw = (ulong)value & mask;
        return FormatCore(negative, magnitude, raw, format);
    }

    private static string FormatUnsigned(ulong value, string? format) =>
        FormatCore(false, value, value, format);

    private static string FormatCore(bool negative, ulong magnitude, ulong raw, string? format) {
        var (fmt, digits) = ParseFormatSpecifier(format);
        var upper = (char)(fmt & '￟'); // 大文字化 (本家 c & 0xFFDF)
        // G (精度なし) / D: 10 進 + 最小桁数の高速経路
        if ((upper == 'G' && digits < 1) || upper == 'D')
            return DecimalStr(negative, magnitude, digits);
        if (upper == 'X')
            return HexStr(raw, (char)(fmt - 33), digits); // GetHexBase: 'X'→'A'-10, 'x'→'a'-10
        if (upper == 'B')
            return BinaryStr(raw, digits);
        var number = ToNumber(negative, magnitude);
        var output = new Out();
        if (fmt != '\0')
            NumberToString(output, number, fmt, digits);
        else
            NumberToStringFormat(output, number, format!);
        return output.Build();
    }

    // ---- 書式指定子の解析 (本家 ParseFormatSpecifier の移植) ----

    /// <summary>標準書式文字 (小文字のまま。大文字化は呼出側) と精度を返す。
    /// カスタム書式なら Fmt = '\0'。null / 空 / '\0' 開始は 'G'。
    /// (out は VM IL 実績がないため NumberFormatting.ParseMagnitude と同じタプル戻り値)</summary>
    private static (char Fmt, int Digits) ParseFormatSpecifier(string? format) {
        char c = '\0';
        if (format is not null && format.Length > 0) {
            c = format[0];
            if (IsAsciiLetter(c)) {
                if (format.Length == 1)
                    return (c, -1);
                if (format.Length == 2) {
                    var d = format[1] - '0';
                    if ((uint)d < 10u)
                        return (c, d);
                } else if (format.Length == 3) {
                    var d1 = format[1] - '0';
                    var d2 = format[2] - '0';
                    if ((uint)d1 < 10u && (uint)d2 < 10u)
                        return (c, d1 * 10 + d2);
                }
                // 3 桁以上の精度 (D100 等)。数字列の後ろに無関係な文字が来たらカスタム書式
                var value = 0;
                var i = 1;
                while (i < format.Length && IsAsciiDigit(format[i])) {
                    if (value >= 100000000)
                        throw new FormatException($"書式指定子 '{format}' は有効ではありません。");
                    value = value * 10 + format[i++] - '0';
                }
                if (i >= format.Length || format[i] == '\0')
                    return (c, value);
            }
        }
        if (format is not null && format.Length != 0 && c != '\0')
            return ('\0', -1); // カスタム書式
        return ('G', -1);
    }

    private static bool IsAsciiLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    // ---- 標準書式 (本家 NumberToString の移植。整数のみのサブセット) ----

    private static void NumberToString(Out output, NumberBuffer number, char fmt, int digits) {
        switch (fmt) {
            case 'C':
            case 'c':
                if (digits < 0) digits = DecimalDigits;
                RoundNumber(number, number.Scale + digits);
                FormatByPattern(output, number, digits, number.Negative ? CurrencyNegativePattern : CurrencyPositivePattern,
                    NumberGroupSizes, CurrencySymbol, 'c');
                break;
            case 'F':
            case 'f':
                if (digits < 0) digits = DecimalDigits;
                RoundNumber(number, number.Scale + digits);
                if (number.Negative)
                    output.Append(NegativeSign);
                FormatFixed(output, number, digits, null);
                break;
            case 'N':
            case 'n':
                if (digits < 0) digits = DecimalDigits;
                RoundNumber(number, number.Scale + digits);
                FormatByPattern(output, number, digits, number.Negative ? NumberNegativePattern : "_",
                    NumberGroupSizes, null, '\0');
                break;
            case 'E':
            case 'e':
                if (digits < 0) digits = 6;
                digits++;
                RoundNumber(number, digits);
                if (number.Negative)
                    output.Append(NegativeSign);
                FormatScientific(output, number, digits, fmt);
                break;
            case 'G':
            case 'g': {
                // 整数の G は精度があれば有効数字 (桁あふれは指数表記)。無ければ全桁 10 進
                var maxDigits = digits < 1 ? number.Count : digits;
                RoundNumber(number, maxDigits);
                if (number.Negative)
                    output.Append(NegativeSign);
                FormatGeneral(output, number, maxDigits, (char)(fmt - 2)); // 'G'→'E', 'g'→'e'
                break;
            }
            case 'R':
            case 'r':
                // R は整数では G と同一 (本家: format - 11)
                NumberToString(output, number, (char)(fmt - 11), digits);
                break;
            case 'P':
            case 'p':
                if (digits < 0) digits = DecimalDigits;
                number.Scale += 2; // パーセントは桁位置を 2 桁右へ (×100 をスケールで表現 — あふれない)
                RoundNumber(number, number.Scale + digits);
                FormatByPattern(output, number, digits, number.Negative ? PercentNegativePattern : PercentPositivePattern,
                    NumberGroupSizes, PercentSymbol, 'p');
                break;
            default:
                // 有効な標準書式文字以外は FormatException ("Q5" 等の letter+数字列)
                throw new FormatException($"書式指定子 '{fmt}' は有効ではありません。");
        }
    }

    /// <summary>パターン文字列に沿って数値部を展開する (通貨 / パーセント / 数値負数パターン)。
    /// 本家の s_*Formats 表 + FormatCurrency/FormatPercent/FormatNumber の walks。</summary>
    private static void FormatByPattern(Out output, NumberBuffer number, int digits, string pattern,
        int[] groupSizes, string? symbol, char symbolKey) {
        for (var i = 0; i < pattern.Length; i++) {
            var c = pattern[i];
            if (c == '_') {
                FormatFixed(output, number, digits, groupSizes);
            } else if (c == 'n') {
                output.Append(NegativeSign);
            } else if (c == symbolKey) {
                output.Append(symbol!);
            } else {
                output.Append(c);
            }
        }
    }

    // ---- 数値バッファ (本家 NumberBuffer の配列表現) ----

    private sealed class NumberBuffer {
        public readonly char[] Digits = new char[DigitBufferSize];
        /// <summary>有効数字の個数 (0 = 値 0)。</summary>
        public int Count;
        /// <summary>小数点位置 (値 = 0.数字列 × 10^Scale)。</summary>
        public int Scale;
        public bool Negative;
    }

    private static NumberBuffer ToNumber(bool negative, ulong magnitude) {
        var number = new NumberBuffer { Negative = negative };
        if (magnitude == 0)
            return number; // 値 0 は空の数字列 + Scale 0 (本家 Int32ToNumber と同一)
        var index = DigitBufferSize;
        var digits = number.Digits;
        while (magnitude != 0) {
            digits[--index] = (char)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        number.Count = DigitBufferSize - index;
        number.Scale = number.Count;
        // 前詰めする
        for (var i = 0; i < number.Count; i++)
            digits[i] = digits[index + i];
        return number;
    }

    /// <summary>小数点位置 pos (有効数字 pos 桁) で丸める (half-away-from-zero。
    /// 本家 RoundNumber の移植。丸めで桁上がりしたら Scale を進める)。</summary>
    private static void RoundNumber(NumberBuffer number, int pos) {
        var digits = number.Digits;
        var i = 0;
        while (i < pos && i < number.Count)
            i++;
        if (i == pos && pos < number.Count && digits[pos] >= '5') {
            // 丸め上がり: 後続の '9' を巻き戻して桁上がり
            while (i > 0 && digits[i - 1] == '9')
                i--;
            if (i > 0) {
                digits[i - 1]++;
            } else {
                number.Scale++;
                digits[0] = '1';
                i = 1;
            }
        } else {
            // 丸めなし: 末尾の '0' を詰める
            while (i > 0 && digits[i - 1] == '0')
                i--;
        }
        if (i == 0) {
            number.Negative = false; // 整数 (非浮動小数点) は 0 に丸れたら負を外す
            number.Scale = 0;
        }
        number.Count = i;
    }

    // ---- 出力バッファ ----

    private sealed class Out {
        private char[] _buffer = new char[64];
        private int _length;

        public int Length => _length;

        public void Append(char c) {
            if (_length == _buffer.Length) {
                var bigger = new char[_buffer.Length * 2];
                for (var i = 0; i < _buffer.Length; i++)
                    bigger[i] = _buffer[i];
                _buffer = bigger;
            }
            _buffer[_length++] = c;
        }

        public void Append(string s) {
            for (var i = 0; i < s.Length; i++)
                Append(s[i]);
        }

        public void Append(char[] buffer, int start, int count) {
            for (var i = 0; i < count; i++)
                Append(buffer[start + i]);
        }

        /// <summary>先頭に 1 文字挿入 (カスタム書式の負号挿入。稀経路)。</summary>
        public void InsertAtFront(char c) {
            Append('\0');
            for (var i = _length - 1; i > 0; i--)
                _buffer[i] = _buffer[i - 1];
            _buffer[0] = c;
        }

        public string Build() => new string(_buffer, 0, _length);
    }

    // ---- 固定小数点出力 (本家 FormatFixed の移植) ----

    /// <summary>整数部 Scale 桁 + 小数部 maxDigits 桁を出力する。groupSizes は null で
    /// 区切りなし (不変カルチャは単一区切り文字 ",")。</summary>
    private static void FormatFixed(Out output, NumberBuffer number, int maxDigits, int[]? groupSizes) {
        var scale = number.Scale;
        var digits = number.Digits;
        // 整数部で消費した桁数 (小数部はこの続きから出力する — 本家の ptr 進行と同一)
        var digitIndex = 0;
        if (scale > 0) {
            if (groupSizes is not null) {
                // 右から左へ groupSizes をサイクル適用して区切り位置を決め、
                // 一時バッファに右詰めで構築してから出力する
                var useSizes = groupSizes.Length != 0 && groupSizes[0] > 0;
                var temp = new char[scale + (scale / 3) + 2];
                var t = temp.Length;
                var sizeIndex = 0;
                var currentSize = useSizes ? groupSizes[0] : 0;
                var sinceSeparator = 0;
                for (var i = scale - 1; i >= 0; i--) {
                    temp[--t] = i < number.Count ? digits[i] : '0';
                    if (currentSize > 0) {
                        sinceSeparator++;
                        if (sinceSeparator == currentSize && i != 0) {
                            temp[--t] = GroupSeparator[0];
                            if (sizeIndex < groupSizes.Length - 1)
                                sizeIndex++;
                            currentSize = groupSizes[sizeIndex];
                            sinceSeparator = 0;
                        }
                    }
                }
                output.Append(temp, t, temp.Length - t);
                digitIndex = number.Count < scale ? number.Count : scale;
            } else {
                while (scale > 0) {
                    output.Append(digitIndex < number.Count ? digits[digitIndex++] : '0');
                    scale--;
                }
            }
        } else {
            output.Append('0');
        }
        if (maxDigits <= 0)
            return;
        output.Append(DecimalSeparator);
        if (scale < 0) {
            // 整数部より小さい値 (パーセント等のスケール崩れは整数では起きないが本家どおり)
            var leadingZeros = -scale < maxDigits ? -scale : maxDigits;
            for (var i = 0; i < leadingZeros; i++)
                output.Append('0');
            scale += leadingZeros;
            maxDigits -= leadingZeros;
        }
        while (maxDigits > 0) {
            output.Append(digitIndex < number.Count ? digits[digitIndex++] : '0');
            maxDigits--;
        }
    }

    // ---- 指数・一般出力 (本家 FormatScientific / FormatGeneral / FormatExponent の移植) ----

    private static void FormatScientific(Out output, NumberBuffer number, int maxDigits, char expChar) {
        var digits = number.Digits;
        var index = 0;
        output.Append(index < number.Count ? digits[index++] : '0');
        if (maxDigits != 1)
            output.Append(DecimalSeparator);
        while (--maxDigits > 0)
            output.Append(index < number.Count ? digits[index++] : '0');
        // 指数は Scale - 1 (最初の数字の桁位置)。値 0 は指数 0
        FormatExponent(output, number.Count != 0 ? number.Scale - 1 : 0, expChar, 3, true);
    }

    private static void FormatGeneral(Out output, NumberBuffer number, int maxDigits, char expChar) {
        var i = number.Scale;
        var scientific = false;
        if (i > maxDigits || i < -3) {
            i = 1;
            scientific = true;
        }
        var digits = number.Digits;
        var index = 0;
        if (i > 0) {
            do {
                output.Append(index < number.Count ? digits[index++] : '0');
            } while (--i > 0);
        } else {
            output.Append('0');
        }
        if (index < number.Count || i < 0) {
            output.Append(DecimalSeparator);
            for (; i < 0; i++)
                output.Append('0');
            while (index < number.Count)
                output.Append(digits[index++]);
        }
        if (scientific)
            FormatExponent(output, number.Scale - 1, expChar, 2, true);
    }

    private static void FormatExponent(Out output, int value, char expChar, int minDigits, bool positiveSign) {
        output.Append(expChar);
        if (value < 0) {
            output.Append(NegativeSign);
            value = -value;
        } else if (positiveSign) {
            output.Append('+');
        }
        var buffer = new char[10];
        var t = 10;
        do {
            buffer[--t] = (char)('0' + value % 10);
            value /= 10;
        } while (value != 0);
        while (10 - t < minDigits)
            buffer[--t] = '0';
        output.Append(buffer, t, 10 - t);
    }

    // ---- カスタム書式 (本家 NumberToStringFormat / FindSection の移植) ----

    private static void NumberToStringFormat(Out output, NumberBuffer number, string format) {
        var sectionStart = FindSection(format, number.Count == 0 ? 2 : number.Negative ? 1 : 0);

        // 第 1 パスの走査結果 (ゼロセクション再走査で書き直るためループ外で宣言)
        var scaleDelta = 0;        // 末尾コンマによるスケール (,1 個 = ÷1000)
        var digitCount = 0;        // 走査中のプレースホルダ数
        var decimalPos = -1;       // '.' の位置 (プレースホルダ数で表す。複数の '.' は最初のみ有効)
        var firstZero = int.MaxValue; // 最初の '0' プレースホルダ位置 (整数部の 0 埋め下限)
        var lastZero = 0;          // 最後の '0' プレースホルダ位置
        var groupPos = -1;         // 最後の ',' の位置 (スケール vs 区切りの判定用)
        var useGrouping = false;
        var percentDelta = 0;      // '%' / '‰' によるスケール (+2 / +3)
        var exponent = false;      // 'E+'/'E-'/'e+'/'e-'/'E0' 形式の指数部がある

        while (true) {
            // ---- 第 1 パス: セクションの走査 (プレースホルダ配置の収集) ----
            scaleDelta = 0;
            digitCount = 0;
            decimalPos = -1;
            firstZero = int.MaxValue;
            lastZero = 0;
            groupPos = -1;
            useGrouping = false;
            percentDelta = 0;
            exponent = false;
            var i = sectionStart;
            while (i < format.Length) {
                var c = format[i++];
                if (c == '\0')
                    break;
                if (c == ';')
                    break;
                if (c == '#') {
                    digitCount++;
                } else if (c == '0') {
                    if (firstZero == int.MaxValue)
                        firstZero = digitCount;
                    digitCount++;
                    lastZero = digitCount;
                } else if (c == '.') {
                    if (decimalPos < 0)
                        decimalPos = digitCount;
                } else if (c == ',') {
                    if (digitCount > 0 && decimalPos < 0) {
                        if (groupPos >= 0) {
                            if (groupPos == digitCount) {
                                scaleDelta++; // 直前の ',' と同位置 = スケール用コンマの連続
                                continue;
                            }
                            useGrouping = true; // 位置が違うコンマ = 桁区切り
                        }
                        groupPos = digitCount;
                        scaleDelta = 1;
                    }
                } else if (c == '%') {
                    percentDelta += 2;
                } else if (c == '‰') {
                    percentDelta += 3;
                } else if (c == '"' || c == '\'') {
                    while (i < format.Length && format[i] != c)
                        i++;
                    if (i < format.Length)
                        i++;
                } else if (c == '\\') {
                    if (i < format.Length)
                        i++;
                } else if (c == 'E' || c == 'e') {
                    if ((i < format.Length && format[i] == '0') ||
                        (i + 1 < format.Length && (format[i] == '+' || format[i] == '-') && format[i + 1] == '0')) {
                        while (++i < format.Length && format[i] == '0') { }
                        exponent = true;
                    }
                }
            }

            if (decimalPos < 0)
                decimalPos = digitCount;
            // 小数点直前 (または整数プレースホルダ直後) の ',' は区切りでなくスケール
            if (groupPos >= 0) {
                if (groupPos == decimalPos)
                    percentDelta -= scaleDelta * 3;
                else
                    useGrouping = true;
            }

            // ---- スケール適用 + 丸め (0 に丸れたらゼロセクションへ切替) ----
            if (number.Count != 0) {
                number.Scale += percentDelta;
                var roundPos = exponent ? digitCount : number.Scale + digitCount - decimalPos;
                RoundNumber(number, roundPos);
                if (number.Count != 0)
                    break;
                var zeroSection = FindSection(format, 2);
                if (zeroSection == sectionStart)
                    break; // ゼロセクションが無い → このまま空出力
                sectionStart = zeroSection;
                continue; // 同じセクションをゼロ値として再走査
            }
            // 値 0: 整数 (非浮動小数点) は負を外し Scale を 0 に
            number.Negative = false;
            number.Scale = 0;
            break;
        }

        // ---- 出力パラメータの確定 ----
        // 整数部の 0 埋め下限 ('0' プレースホルダから決まる) と小数部の 0 埋め下限
        var minIntegerDigits = firstZero < decimalPos ? decimalPos - firstZero : 0;
        var decimalPad = lastZero > decimalPos ? decimalPos - lastZero : 0; // 負値 = 必要小数桁数
        int intDigits;
        var num11 = 0;
        if (exponent) {
            intDigits = decimalPos;
        } else {
            intDigits = number.Scale > decimalPos ? number.Scale : decimalPos;
            num11 = number.Scale - decimalPos;
        }

        // 桁区切り位置 (左から数えた桁数。本家 span と同じく昇順に格納し後ろから消費)
        var separatorCount = 0;
        var separators = new int[intDigits + 1];
        if (useGrouping) {
            var sizes = NumberGroupSizes;
            var sizeIndex = 0;
            var position = sizes[0];
            var threshold = intDigits + (num11 < 0 ? num11 : 0);
            var start = minIntegerDigits > threshold ? minIntegerDigits : threshold;
            while (position > 0 && start > position) {
                separators[separatorCount++] = position;
                if (sizeIndex < sizes.Length - 1)
                    sizeIndex++;
                if (sizes[sizeIndex] == 0)
                    break;
                position += sizes[sizeIndex];
            }
        }

        if (number.Negative && sectionStart == 0 && number.Scale != 0)
            output.Append(NegativeSign);

        // ---- 第 2 パス: 出力 ----
        var digitIndex = 0;
        var separatorIndex = separatorCount - 1;
        var decimalWritten = false;
        var i2 = sectionStart;
        while (i2 < format.Length) {
            var c = format[i2++];
            if (c == '\0' || c == ';')
                break;
            // 整数部の先行桁 (値の桁が書式のプレースホルダより多い分) を先に出す
            if (num11 > 0 && (c == '#' || c == '.' || c == '0')) {
                while (num11 > 0) {
                    output.Append(digitIndex < number.Count ? number.Digits[digitIndex++] : '0');
                    if (useGrouping && intDigits > 1 && separatorIndex >= 0 && intDigits == separators[separatorIndex] + 1) {
                        output.Append(GroupSeparator);
                        separatorIndex--;
                    }
                    intDigits--;
                    num11--;
                }
            }
            if (c == '#' || c == '0') {
                char emit;
                if (num11 < 0) {
                    // 小数部プレースホルダの中で整数桁の 0 埋めを要求された場合 (本家どおり)
                    num11++;
                    emit = intDigits <= minIntegerDigits ? '0' : '\0';
                } else {
                    emit = digitIndex < number.Count ? number.Digits[digitIndex++]
                        : (intDigits > decimalPad ? '0' : '\0');
                }
                if (emit != '\0') {
                    output.Append(emit);
                    if (useGrouping && intDigits > 1 && separatorIndex >= 0 && intDigits == separators[separatorIndex] + 1) {
                        output.Append(GroupSeparator);
                        separatorIndex--;
                    }
                }
                intDigits--;
            } else if (c == '.') {
                if (!(intDigits != 0 || decimalWritten) &&
                    (decimalPad < 0 || (decimalPos < digitCount && digitIndex < number.Count))) {
                    output.Append(DecimalSeparator);
                    decimalWritten = true;
                }
            } else if (c == '‰') {
                output.Append(PerMilleSymbol);
            } else if (c == '%') {
                output.Append(PercentSymbol);
            } else if (c == '"' || c == '\'') {
                while (i2 < format.Length && format[i2] != c)
                    output.Append(format[i2++]);
                if (i2 < format.Length)
                    i2++;
            } else if (c == '\\') {
                if (i2 < format.Length)
                    output.Append(format[i2++]);
            } else if (c == 'E' || c == 'e') {
                if (exponent) {
                    var positiveSign = false;
                    var minDigits = 0;
                    if (i2 < format.Length && format[i2] == '0') {
                        minDigits = 1;
                    } else if (i2 + 1 < format.Length && format[i2] == '+' && format[i2 + 1] == '0') {
                        positiveSign = true;
                    } else if (i2 + 1 >= format.Length || format[i2] != '-' || format[i2 + 1] != '0') {
                        output.Append(c); // 指数部でないただの E/e はリテラル
                        continue;
                    }
                    while (++i2 < format.Length && format[i2] == '0')
                        minDigits++;
                    if (minDigits > 10)
                        minDigits = 10;
                    FormatExponent(output, number.Count != 0 ? number.Scale - decimalPos : 0, c, minDigits, positiveSign);
                    exponent = false;
                } else {
                    output.Append(c);
                    if (i2 < format.Length) {
                        if (format[i2] == '+' || format[i2] == '-')
                            output.Append(format[i2++]);
                        while (i2 < format.Length && format[i2] == '0')
                            output.Append(format[i2++]);
                    }
                }
            } else if (c == ',') {
                // スキップ (区切りはプレースホルダ出力時に挿入)
            } else {
                output.Append(c);
            }
        }

        if (number.Negative && sectionStart == 0 && number.Scale == 0 && output.Length > 0)
            output.InsertAtFront(NegativeSign[0]);
    }

    /// <summary>';' 区切りのセクション (正 / 負 / ゼロ) の開始位置を返す
    /// (引用符とエスケープをまたぐ。本家 FindSection の移植)。</summary>
    private static int FindSection(string format, int section) {
        if (section == 0)
            return 0;
        var i = 0;
        while (true) {
            if (i >= format.Length)
                return 0;
            var c = format[i++];
            if (c == '"') {
                while (i < format.Length && format[i] != '"')
                    i++;
                if (i < format.Length)
                    i++;
            } else if (c == '\'') {
                while (i < format.Length && format[i] != '\'')
                    i++;
                if (i < format.Length)
                    i++;
            } else if (c == '\\') {
                if (i < format.Length)
                    i++;
            } else if (c == ';') {
                if (--section == 0) {
                    if (i >= format.Length || format[i] == '\0' || format[i] == ';')
                        return 0; // 空セクションは親セクション扱い
                    return i;
                }
            }
        }
    }

    // ---- 高速経路 (10 進 / 16 進 / 2 進。本家の *ToDecStr / *ToHexStr / *ToBinaryStr) ----

    private static string DecimalStr(bool negative, ulong magnitude, int digits) {
        if (digits < 1)
            digits = 1;
        var count = 0;
        var v = magnitude;
        while (v != 0) {
            count++;
            v /= 10;
        }
        var length = digits > count ? digits : count;
        if (negative)
            length++;
        var buffer = new char[length];
        var index = length;
        while (--digits >= 0 || magnitude != 0) {
            buffer[--index] = (char)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        if (negative)
            buffer[--index] = '-';
        return new string(buffer, 0, buffer.Length);
    }

    private static string HexStr(ulong value, char hexBase, int digits) {
        if (digits < 1)
            digits = 1;
        var count = 0;
        var v = value;
        while (v != 0) {
            count++;
            v >>= 4;
        }
        var length = digits > count ? digits : count;
        var buffer = new char[length];
        var index = length;
        while (--digits >= 0 || value != 0) {
            var b = (int)(value & 0xF);
            buffer[--index] = (char)(b + (b < 10 ? '0' : hexBase));
            value >>= 4;
        }
        return new string(buffer, 0, buffer.Length);
    }

    private static string BinaryStr(ulong value, int digits) {
        if (digits < 1)
            digits = 1;
        var count = 0;
        var v = value;
        while (v != 0) {
            count++;
            v >>= 1;
        }
        var length = digits > count ? digits : count;
        var buffer = new char[length];
        var index = length;
        while (--digits >= 0 || value != 0) {
            buffer[--index] = (char)('0' + (int)(value & 0x1));
            value >>= 1;
        }
        return new string(buffer, 0, buffer.Length);
    }
}
