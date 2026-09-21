namespace DotnetVM.CoreLib;

/// <summary>
/// 浮動小数点 (Double / Single) の標準書式 + カスタム書式エンジン。
///
/// dotnet/runtime (MIT) release/10.0 の System.Number 浮動小数点書式面
/// (Number.Formatting.cs: FormatFloat / GetFloatingPointMaxDigitsAndPrecision /
/// ExtractFractionAndBiasedExponent、Number.Formatting.Common.cs: NumberToString /
/// NumberToStringFormat / FormatCurrency / FormatFixed / FormatNumber / FormatScientific /
/// FormatGeneral / FormatExponent / FormatPercent / RoundNumber / FindSection) を、
/// VM で IL 実行できる形に限定して移植したもの (byte* ポインタ / stackalloc /
/// ValueListBuilder / Span を char[] + インデックス + FormatSpecifiers.Out に置換。
/// Math.Max 等も FloatOps の手実装に置換)。
///
/// ジェネリック IBinaryFloatParseAndFormatInfo&lt;TNumber&gt; 機構は VM IL で実行できないため
/// FloatTraits (参照型) の引数渡しで置換。Single は double への拡大格納で扱い、bit 分解
/// 時に (float) 再丸めすることで正しい bit 列が得られる。
///
/// culture は不変カルチャ固定 (NumberFormatInfo.InvariantInfo の固定値を定数として持つ)。
/// </summary>
public static class DoubleFormatting {
    // ---- 不変カルチャ (NumberFormatInfo.InvariantInfo) の固定値 ----
    private const string NegativeSign = "-";
    private const string PositiveSign = "+";
    private const string DecimalSeparator = ".";
    private const string GroupSeparator = ",";
    private const string CurrencySymbol = "¤"; // U+00A4
    private const string PercentSymbol = "%";
    private const string PerMilleSymbol = "‰"; // U+2030
    private const string NaNSymbol = "NaN";
    private const string PositiveInfinitySymbol = "Infinity";
    private const string NegativeInfinitySymbol = "-Infinity";
    private const int DecimalDigits = 2;                // Number/Currency/PercentDecimalDigits (不変カルチャは全部 2)
    private const int DefaultPrecisionExponentialFormat = 6;
    private static readonly int[] NumberGroupSizes = [3];

    // パターン表 (invariant のインデックス固定: NumberNegativePattern=1, CurrencyPos=0,
    // CurrencyNeg=0, PercentPos=0, PercentNeg=0)。'_' = 数値部, 'c' = 通貨記号,
    // 'p' = パーセント記号, 'n' = 負号 (本家は "#" が数値部)
    private const string PosNumberFormat = "_";
    private const string NumberNegativePattern = "n_";
    private const string CurrencyPositivePattern = "c_";
    private const string CurrencyNegativePattern = "(c_)";
    private const string PercentPositivePattern = "_ p";   // invariant PercentPositivePattern=0 → "n %"
    private const string PercentNegativePattern = "n_ p";  // invariant PercentNegativePattern=0 → "-n %"

    // ---- 公開エントリ (面ごと)。provider は不変カルチャ固定で無視 ----

    public static string DoubleToString(double value) => FormatFloat(FloatTraits.Double, value, null);
    public static string DoubleToString(double value, string? format) => FormatFloat(FloatTraits.Double, value, format);
    public static string DoubleToString(double value, string? format, object? provider) => FormatFloat(FloatTraits.Double, value, format);
    public static string DoubleToString(double value, object? provider) => FormatFloat(FloatTraits.Double, value, null);

    // Single は double に拡大格納して渡る (impl は SingleTraits で処理)。
    public static string SingleToString(double value) => FormatFloat(FloatTraits.Single, value, null);
    public static string SingleToString(double value, string? format) => FormatFloat(FloatTraits.Single, value, format);
    public static string SingleToString(double value, string? format, object? provider) => FormatFloat(FloatTraits.Single, value, format);
    public static string SingleToString(double value, object? provider) => FormatFloat(FloatTraits.Single, value, null);

    // ---- 本家 FormatFloat の移植 ----

    private static string FormatFloat(FloatTraits traits, double value, string? format) {
        if (!FloatBits.IsFinite(traits, value)) {
            if (FloatBits.IsNaN(traits, value))
                return NaNSymbol;
            return FloatBits.IsNegative(value) ? NegativeInfinitySymbol : PositiveInfinitySymbol;
        }

        (char fmt, int precision) = FormatSpecifiers.ParseFormatSpecifier(format);

        if (fmt == '\0') {
            // カスタム書式は MaxPrecisionCustomFormat 桁の数字列を作ってから
            // RoundNumber (isCorrectlyRounded: false) で丸める (本家と同じ二段構え)
            precision = traits.MaxPrecisionCustomFormat;
        }

        var number = new FloatNumberBuffer(traits);
        number.IsNegative = FloatBits.IsNegative(value);

        // 一部の書式は 0 を受けたり追加の補正を要するため、要求された元の精度を追跡する
        int nMaxDigits = GetFloatingPointMaxDigitsAndPrecision(fmt, ref precision, out bool isSignificantDigits);

        if (value != default && (!isSignificantDigits || !Grisu3.TryRun(traits, value, precision, number))) {
            Dragon4Engine.Dragon4(traits, value, precision, isSignificantDigits, number);
        }

        var output = new FormatSpecifiers.Out();

        if (fmt != 0) {
            if (precision == -1) {
                // 最短 roundtrip 可能文字列を返すときは DigitsCount と MaxRoundTripDigits の
                // 大きい方へ更新する ("-60" が "-6E+01" にならないように。本家と同じ)
                nMaxDigits = number.DigitsCount > traits.MaxRoundTripDigits
                    ? number.DigitsCount : traits.MaxRoundTripDigits;
            }
            NumberToString(output, number, fmt, nMaxDigits);
        } else {
            NumberToStringFormat(output, number, format!);
        }
        return output.Build();
    }

    /// <summary>本家 GetFloatingPointMaxDigitsAndPrecision の移植。
    /// precision は書式ごとの既定値補正を受けた値へ更新され、戻り値は元の精度
    /// (nMaxDigits)。isSignificantDigits は Grisu3/Dragon4 へのヒント。</summary>
    private static int GetFloatingPointMaxDigitsAndPrecision(char fmt, ref int precision, out bool isSignificantDigits) {
        if (fmt == 0) {
            isSignificantDigits = true;
            return precision;
        }

        int maxDigits = precision;

        switch (fmt | 0x20) {
            case 'c':
                // 通貨書式は精度で小数桁数を示す (既定 CurrencyDecimalDigits)
                if (precision == -1) {
                    precision = DecimalDigits;
                }
                isSignificantDigits = false;
                break;

            case 'e':
                // 指数書式は精度で小数桁数を示す (既定 6)。常に整数 1 桁 + 小数 precision 桁
                // なので、有効桁数扱いへ +1 する
                if (precision == -1) {
                    precision = DefaultPrecisionExponentialFormat;
                }
                precision++;
                isSignificantDigits = true;
                break;

            case 'f':
            case 'n':
                // 固定小数点 / 数値書式は精度で小数桁数を示す (既定 NumberDecimalDigits)
                if (precision == -1) {
                    precision = DecimalDigits;
                }
                isSignificantDigits = false;
                break;

            case 'g':
                // 一般書式は精度で有効桁数を示す (既定と 0 は最短 roundtrip 文字列)
                if (precision == 0) {
                    precision = -1;
                }
                isSignificantDigits = true;
                break;

            case 'p':
                // パーセント書式は精度で小数桁数を示すが ×100 されるので +2 する
                if (precision == -1) {
                    precision = DecimalDigits;
                }
                precision += 2;
                isSignificantDigits = false;
                break;

            case 'r':
                // roundtrip 書式は精度を無視し常に最短 roundtrip 文字列
                precision = -1;
                isSignificantDigits = true;
                break;

            default:
                throw new FormatException($"書式指定子 '{fmt}' は有効ではありません。");
        }

        return maxDigits;
    }

    /// <summary>本家 NumberToString (浮動小数点) の移植。number.Kind は常時
    /// FloatingPoint (isCorrectlyRounded = true)。</summary>
    private static void NumberToString(FormatSpecifiers.Out output, FloatNumberBuffer number, char format, int nMaxDigits) {
        // 浮動小数点は正確に丸められた数字列が Grisu3/Dragon4 で生成済み
        bool isCorrectlyRounded = true;

        switch (format) {
            case 'C':
            case 'c': {
                if (nMaxDigits < 0) {
                    nMaxDigits = DecimalDigits;
                }

                RoundNumber(number, number.Scale + nMaxDigits, isCorrectlyRounded);

                FormatCurrency(output, number, nMaxDigits);
                break;
            }

            case 'F':
            case 'f': {
                if (nMaxDigits < 0) {
                    nMaxDigits = DecimalDigits;
                }

                RoundNumber(number, number.Scale + nMaxDigits, isCorrectlyRounded);

                if (number.IsNegative) {
                    output.Append(NegativeSign);
                }

                FormatFixed(output, number, nMaxDigits, null, DecimalSeparator, null);
                break;
            }

            case 'N':
            case 'n': {
                if (nMaxDigits < 0) {
                    nMaxDigits = DecimalDigits;
                }

                RoundNumber(number, number.Scale + nMaxDigits, isCorrectlyRounded);

                FormatNumber(output, number, nMaxDigits);
                break;
            }

            case 'E':
            case 'e': {
                if (nMaxDigits < 0) {
                    nMaxDigits = DefaultPrecisionExponentialFormat;
                }
                nMaxDigits++;

                RoundNumber(number, nMaxDigits, isCorrectlyRounded);

                if (number.IsNegative) {
                    output.Append(NegativeSign);
                }

                FormatScientific(output, number, nMaxDigits, format);
                break;
            }

            case 'G':
            case 'g': {
                if (nMaxDigits < 1) {
                    // 既定精度は DigitsCount で埋める (本家と同じ)
                    nMaxDigits = number.DigitsCount;
                }

                RoundNumber(number, nMaxDigits, isCorrectlyRounded);

                if (number.IsNegative) {
                    output.Append(NegativeSign);
                }

                FormatGeneral(output, number, nMaxDigits, (char)(format - ('G' - 'E')), suppressScientific: false);
                break;
            }

            case 'P':
            case 'p': {
                if (nMaxDigits < 0) {
                    nMaxDigits = DecimalDigits;
                }
                number.Scale += 2;

                RoundNumber(number, number.Scale + nMaxDigits, isCorrectlyRounded);

                FormatPercent(output, number, nMaxDigits);
                break;
            }

            case 'R':
            case 'r': {
                format = (char)(format - ('R' - 'G'));
                goto case 'G';
            }

            default:
                throw new FormatException($"書式指定子 '{format}' は有効ではありません。");
        }
    }

    /// <summary>本家 FormatCurrency の移植 (invariant: CurrencyNegativePattern=0 /
    /// CurrencyPositivePattern=0 → "(¤n)" / "¤n")。</summary>
    private static void FormatCurrency(FormatSpecifiers.Out output, FloatNumberBuffer number, int nMaxDigits) {
        string fmt = number.IsNegative ? CurrencyNegativePattern : CurrencyPositivePattern;

        for (int i = 0; i < fmt.Length; i++) {
            char ch = fmt[i];
            switch (ch) {
                case '_':
                    FormatFixed(output, number, nMaxDigits, NumberGroupSizes, DecimalSeparator, GroupSeparator);
                    break;
                case 'c':
                    output.Append(CurrencySymbol);
                    break;
                case 'n':
                    output.Append(NegativeSign);
                    break;
                default:
                    output.Append(ch);
                    break;
            }
        }
    }

    /// <summary>本家 FormatNumber の移植 (invariant: NumberNegativePattern=1 → "-n")。</summary>
    private static void FormatNumber(FormatSpecifiers.Out output, FloatNumberBuffer number, int nMaxDigits) {
        string fmt = number.IsNegative ? NumberNegativePattern : PosNumberFormat;

        for (int i = 0; i < fmt.Length; i++) {
            char ch = fmt[i];
            switch (ch) {
                case '_':
                    FormatFixed(output, number, nMaxDigits, NumberGroupSizes, DecimalSeparator, GroupSeparator);
                    break;
                case 'n':
                    output.Append(NegativeSign);
                    break;
                default:
                    output.Append(ch);
                    break;
            }
        }
    }

    /// <summary>本家 FormatPercent の移植 (invariant: PercentNegativePattern=0 /
    /// PercentPositivePattern=0 → "-n%" / "n%")。</summary>
    private static void FormatPercent(FormatSpecifiers.Out output, FloatNumberBuffer number, int nMaxDigits) {
        string fmt = number.IsNegative ? PercentNegativePattern : PercentPositivePattern;

        for (int i = 0; i < fmt.Length; i++) {
            char ch = fmt[i];
            switch (ch) {
                case '_':
                    FormatFixed(output, number, nMaxDigits, NumberGroupSizes, DecimalSeparator, GroupSeparator);
                    break;
                case 'p':
                    output.Append(PercentSymbol);
                    break;
                case 'n':
                    output.Append(NegativeSign);
                    break;
                default:
                    output.Append(ch);
                    break;
            }
        }
    }

    /// <summary>本家 FormatFixed の移植。digits バッファは '\0' 終端で、桁進行は
    /// digIndex (本家の byte* dig) で追跡する。sGroup が null なら区切りなし。</summary>
    private static void FormatFixed(FormatSpecifiers.Out output, FloatNumberBuffer number,
        int nMaxDigits, int[]? groupDigits, string sDecimal, string? sGroup) {
        int digPos = number.Scale;
        char[] dig = number.Digits;
        int digIndex = 0;

        if (digPos > 0) {
            if (groupDigits != null) {
                int groupSizeIndex = 0;               // groupDigits へのインデックス
                int bufferSize = digPos;              // 結果バッファの長さ
                int groupSize = 0;                    // 現在のグループサイズ

                // 結果バッファのサイズを求める
                if (groupDigits.Length != 0) {
                    int groupSizeCount = groupDigits[groupSizeIndex];

                    while (digPos > groupSizeCount) {
                        groupSize = groupDigits[groupSizeIndex];
                        if (groupSize == 0) {
                            break;
                        }

                        bufferSize += sGroup!.Length;
                        if (groupSizeIndex < groupDigits.Length - 1) {
                            groupSizeIndex++;
                        }

                        groupSizeCount += groupDigits[groupSizeIndex];
                    }

                    // 要素 0 の配列を渡された場合は groupSizeCount == 0
                    groupSize = groupSizeCount == 0 ? 0 : groupDigits[0];
                }

                groupSizeIndex = 0;
                int digitCount = 0;
                int digLength = number.DigitsCount;
                int digStart = digPos < digLength ? digPos : digLength;

                var temp = new char[bufferSize];
                int p = bufferSize - 1;
                for (int i = digPos - 1; i >= 0; i--) {
                    temp[p--] = i < digStart ? dig[i] : '0';

                    if (groupSize > 0) {
                        digitCount++;
                        if (digitCount == groupSize && i != 0) {
                            for (int j = sGroup!.Length - 1; j >= 0; j--) {
                                temp[p--] = sGroup[j];
                            }

                            if (groupSizeIndex < groupDigits.Length - 1) {
                                groupSizeIndex++;
                                groupSize = groupDigits[groupSizeIndex];
                            }
                            digitCount = 0;
                        }
                    }
                }

                output.Append(temp, 0, bufferSize);
                digIndex += digStart;
            } else {
                do {
                    output.Append(dig[digIndex] != 0 ? dig[digIndex++] : '0');
                } while (--digPos > 0);
            }
        } else {
            output.Append('0');
        }

        if (nMaxDigits > 0) {
            output.Append(sDecimal);
            if (digPos < 0 && nMaxDigits > 0) {
                int zeroes = -digPos < nMaxDigits ? -digPos : nMaxDigits;
                for (int i = 0; i < zeroes; i++) {
                    output.Append('0');
                }
                digPos += zeroes;
                nMaxDigits -= zeroes;
            }

            while (nMaxDigits > 0) {
                output.Append(dig[digIndex] != 0 ? dig[digIndex++] : '0');
                nMaxDigits--;
            }
        }
    }

    /// <summary>本家 FormatScientific の移植。</summary>
    private static void FormatScientific(FormatSpecifiers.Out output, FloatNumberBuffer number, int nMaxDigits, char expChar) {
        char[] dig = number.Digits;
        int digIndex = 0;

        output.Append(dig[digIndex] != 0 ? dig[digIndex++] : '0');

        if (nMaxDigits != 1) {
            // E0 では小数点を抑制する
            output.Append(DecimalSeparator);
        }

        while (--nMaxDigits > 0) {
            output.Append(dig[digIndex] != 0 ? dig[digIndex++] : '0');
        }

        int e = number.Digits[0] == 0 ? 0 : number.Scale - 1;
        FormatExponent(output, e, expChar, 3, positiveSign: true);
    }

    /// <summary>本家 FormatExponent の移植。value (指数値) を minDigits 桁以上の
    /// 10 進で出力する。</summary>
    private static void FormatExponent(FormatSpecifiers.Out output, int value, char expChar, int minDigits, bool positiveSign) {
        output.Append(expChar);

        if (value < 0) {
            output.Append(NegativeSign);
            value = -value;
        } else {
            if (positiveSign) {
                output.Append(PositiveSign);
            }
        }

        AppendDecPadded(output, (uint)value, minDigits);
    }

    /// <summary>本家 UInt32ToDecChars の移植 (最小 minDigits 桁の 10 進を末尾から構築)。</summary>
    private static void AppendDecPadded(FormatSpecifiers.Out output, uint value, int minDigits) {
        var tmp = new char[12];
        int i = tmp.Length;
        while (value != 0 || minDigits > 0) {
            minDigits--;
            tmp[--i] = (char)('0' + value % 10);
            value /= 10;
        }
        output.Append(tmp, i, tmp.Length - i);
    }

    /// <summary>本家 FormatGeneral の移植。</summary>
    private static void FormatGeneral(FormatSpecifiers.Out output, FloatNumberBuffer number, int nMaxDigits, char expChar, bool suppressScientific) {
        int digPos = number.Scale;
        char[] dig = number.Digits;
        int digIndex = 0;
        bool scientific = false;

        if (!suppressScientific) {
            // 指数記法へ切り替えるかどうか
            if (digPos > nMaxDigits || digPos < -3) {
                digPos = 1;
                scientific = true;
            }
        }

        if (digPos > 0) {
            do {
                output.Append(dig[digIndex] != 0 ? dig[digIndex++] : '0');
            } while (--digPos > 0);
        } else {
            output.Append('0');
        }

        if (dig[digIndex] != 0 || digPos < 0) {
            output.Append(DecimalSeparator);

            while (digPos < 0) {
                output.Append('0');
                digPos++;
            }

            while (dig[digIndex] != 0) {
                output.Append(dig[digIndex++]);
            }
        }

        if (scientific) {
            FormatExponent(output, number.Scale - 1, expChar, 2, positiveSign: true);
        }
    }

    /// <summary>本家 RoundNumber (浮動小数点) の移植。
    /// isCorrectlyRounded は標準書式で true (数字列は既に正確)、カスタム書式で false
    /// (dig[pos] &gt;= '5' で切り上げる)。</summary>
    private static void RoundNumber(FloatNumberBuffer number, int pos, bool isCorrectlyRounded) {
        char[] dig = number.Digits;

        int i = 0;
        while (i < pos && dig[i] != '\0') {
            i++;
        }

        if (i == pos && ShouldRoundUp(dig, i, isCorrectlyRounded)) {
            while (i > 0 && dig[i - 1] == '9') {
                i--;
            }

            if (i > 0) {
                dig[i - 1]++;
            } else {
                number.Scale++;
                dig[0] = '1';
                i = 1;
            }
        } else {
            while (i > 0 && dig[i - 1] == '0') {
                i--;
            }
        }

        if (i == 0) {
            // 浮動小数点は -0 の概念を持つため IsNegative は触らない (本家と同じ)
            number.Scale = 0;
        }

        dig[i] = '\0';
        number.DigitsCount = i;
    }

    private static bool ShouldRoundUp(char[] dig, int i, bool isCorrectlyRounded) {
        // 浮動小数点で標準書式の場合は正確に丸められた数字列が既に生成済み
        // (pos が終端 '\0' を指すので false)。カスタム書式の場合は
        // MaxPrecisionCustomFormat 桁の数字列をこの関数で丸める (本家と同じ)

        char digit = dig[i];

        if (digit == '\0' || isCorrectlyRounded) {
            // 丸め不要の共通ケース
            return false;
        }

        // 5 以上は切り上げ
        return digit >= '5';
    }

    /// <summary>本家 FindSection の移植。カスタム書式のセクション区切り ';' を
    /// section 番号まで進めてその開始位置を返す (見つからなければ 0)。</summary>
    private static int FindSection(string format, int section) {
        if (section == 0) {
            return 0;
        }

        int src = 0;
        while (true) {
            if (src >= format.Length) {
                return 0;
            }

            char ch = format[src++];
            switch (ch) {
                case '\'':
                case '"':
                    while (src < format.Length && format[src] != 0 && format[src++] != ch) ;
                    break;

                case '\\':
                    if (src < format.Length && format[src] != 0) {
                        src++;
                    }
                    break;

                case ';':
                    if (--section != 0) {
                        break;
                    }

                    if (src < format.Length && format[src] != 0 && format[src] != ';') {
                        return src;
                    }
                    return 0;

                case '\0':
                    return 0;
            }
        }
    }

    /// <summary>本家 NumberToStringFormat (浮動小数点) の移植。dig ポインタは
    /// digIndex / curIndex で追跡する。</summary>
    private static void NumberToStringFormat(FormatSpecifiers.Out output, FloatNumberBuffer number, string format) {
        int digitCount;
        int decimalPos;
        int firstDigit;
        int lastDigit;
        int digPos;
        bool scientific;
        int thousandPos;
        int thousandCount = 0;
        bool thousandSeps;
        int scaleAdjust;
        int adjust;

        int section;
        int src;
        char[] dig = number.Digits;
        char ch;

        section = FindSection(format, dig[0] == 0 ? 2 : number.IsNegative ? 1 : 0);

        while (true) {
            digitCount = 0;
            decimalPos = -1;
            firstDigit = 0x7FFFFFFF;
            lastDigit = 0;
            scientific = false;
            thousandPos = -1;
            thousandSeps = false;
            scaleAdjust = 0;
            src = section;

            while (src < format.Length && (ch = format[src++]) != 0 && ch != ';') {
                switch (ch) {
                    case '#':
                        digitCount++;
                        break;

                    case '0':
                        if (firstDigit == 0x7FFFFFFF) {
                            firstDigit = digitCount;
                        }
                        digitCount++;
                        lastDigit = digitCount;
                        break;

                    case '.':
                        if (decimalPos < 0) {
                            decimalPos = digitCount;
                        }
                        break;

                    case ',':
                        if (digitCount > 0 && decimalPos < 0) {
                            if (thousandPos >= 0) {
                                if (thousandPos == digitCount) {
                                    thousandCount++;
                                    break;
                                }
                                thousandSeps = true;
                            }
                            thousandPos = digitCount;
                            thousandCount = 1;
                        }
                        break;

                    case '%':
                        scaleAdjust += 2;
                        break;

                    case '‰':
                        scaleAdjust += 3;
                        break;

                    case '\'':
                    case '"':
                        while (src < format.Length && format[src] != 0 && format[src++] != ch) ;
                        break;

                    case '\\':
                        if (src < format.Length && format[src] != 0) {
                            src++;
                        }
                        break;

                    case 'E':
                    case 'e':
                        if ((src < format.Length && format[src] == '0') ||
                            (src + 1 < format.Length && (format[src] == '+' || format[src] == '-') && format[src + 1] == '0')) {
                            while (++src < format.Length && format[src] == '0') ;
                            scientific = true;
                        }
                        break;
                }
            }

            if (decimalPos < 0) {
                decimalPos = digitCount;
            }

            if (thousandPos >= 0) {
                if (thousandPos == decimalPos) {
                    scaleAdjust -= thousandCount * 3;
                } else {
                    thousandSeps = true;
                }
            }

            if (dig[0] != 0) {
                number.Scale += scaleAdjust;
                int pos = scientific ? digitCount : number.Scale + digitCount - decimalPos;
                RoundNumber(number, pos, isCorrectlyRounded: false);
                if (dig[0] == 0) {
                    src = FindSection(format, 2);
                    if (src != section) {
                        section = src;
                        continue;
                    }
                }
            } else {
                // 浮動小数点は -0 の概念を持つため IsNegative は触らない
                number.Scale = 0;
            }

            break;
        }

        firstDigit = firstDigit < decimalPos ? decimalPos - firstDigit : 0;
        lastDigit = lastDigit > decimalPos ? decimalPos - lastDigit : 0;
        if (scientific) {
            digPos = decimalPos;
            adjust = 0;
        } else {
            digPos = number.Scale > decimalPos ? number.Scale : decimalPos;
            adjust = number.Scale - decimalPos;
        }
        src = section;

        // adjust は負になり得る。書式文字列より多い文字数 (adjust &gt; 0) または
        // 少ない文字数 (adjust &lt; 0) の補正を表す
        int[] thousandsSepPos = new int[4];
        int thousandsSepCtr = -1;

        if (thousandSeps && GroupSeparator.Length > 0) {
            // 桁区切りを挿入する位置を事前計算する (前方走査用。digPos まで計算すれば
            // 十分。書式 "000,000.." のような形も扱えるよう上限は不定で配列を倍々拡張)
            int[] groupDigits = NumberGroupSizes;

            int groupSizeIndex = 0;
            int groupTotalSizeCount = 0;
            int groupSizeLen = groupDigits.Length;
            if (groupSizeLen != 0) {
                groupTotalSizeCount = groupDigits[groupSizeIndex];
            }
            int groupSize = groupTotalSizeCount;

            int totalDigits = digPos + (adjust < 0 ? adjust : 0); // 出力の実際の桁数
            int numDigits = firstDigit > totalDigits ? firstDigit : totalDigits;
            while (numDigits > groupTotalSizeCount) {
                if (groupSize == 0) {
                    break;
                }

                ++thousandsSepCtr;
                if (thousandsSepCtr >= thousandsSepPos.Length) {
                    var newThousandsSepPos = new int[thousandsSepPos.Length * 2];
                    for (int i = 0; i < thousandsSepPos.Length; i++)
                        newThousandsSepPos[i] = thousandsSepPos[i];
                    thousandsSepPos = newThousandsSepPos;
                }

                thousandsSepPos[thousandsSepCtr] = groupTotalSizeCount;
                if (groupSizeIndex < groupSizeLen - 1) {
                    groupSizeIndex++;
                    groupSize = groupDigits[groupSizeIndex];
                }
                groupTotalSizeCount += groupSize;
            }
        }

        if (number.IsNegative && section == 0 && number.Scale != 0) {
            output.Append(NegativeSign);
        }

        bool decimalWritten = false;
        int curIndex = 0;

        while (src < format.Length && (ch = format[src++]) != 0 && ch != ';') {
            if (adjust > 0) {
                switch (ch) {
                    case '#':
                    case '0':
                    case '.':
                        while (adjust > 0) {
                            // digPos は thousandsSepPos[thousandsSepCtr] より 1 大きい
                            // (区切り直後の文字にいるため)
                            output.Append(dig[curIndex] != 0 ? dig[curIndex++] : '0');
                            if (thousandSeps && digPos > 1 && thousandsSepCtr >= 0) {
                                if (digPos == thousandsSepPos[thousandsSepCtr] + 1) {
                                    output.Append(GroupSeparator);
                                    thousandsSepCtr--;
                                }
                            }
                            digPos--;
                            adjust--;
                        }
                        break;
                }
            }

            switch (ch) {
                case '#':
                case '0': {
                    char outCh;
                    if (adjust < 0) {
                        adjust++;
                        outCh = digPos <= firstDigit ? '0' : '\0';
                    } else {
                        outCh = dig[curIndex] != 0 ? dig[curIndex++] : digPos > lastDigit ? '0' : '\0';
                    }

                    if (outCh != 0) {
                        output.Append(outCh);
                        if (thousandSeps && digPos > 1 && thousandsSepCtr >= 0) {
                            if (digPos == thousandsSepPos[thousandsSepCtr] + 1) {
                                output.Append(GroupSeparator);
                                thousandsSepCtr--;
                            }
                        }
                    }

                    digPos--;
                    break;
                }

                case '.': {
                    if (digPos != 0 || decimalWritten) {
                        // 互換性のため繰り返された小数点は出力しない
                        break;
                    }

                    // 書式が末尾 0 を持つ、または書式が小数点を持ち桁が残っている
                    if (lastDigit < 0 || (decimalPos < digitCount && dig[curIndex] != 0)) {
                        output.Append(DecimalSeparator);
                        decimalWritten = true;
                    }
                    break;
                }

                case '‰':
                    output.Append(PerMilleSymbol);
                    break;

                case '%':
                    output.Append(PercentSymbol);
                    break;

                case ',':
                    break;

                case '\'':
                case '"':
                    while (src < format.Length && format[src] != 0 && format[src] != ch) {
                        output.Append(format[src++]);
                    }

                    if (src < format.Length && format[src] != 0) {
                        src++;
                    }
                    break;

                case '\\':
                    if (src < format.Length && format[src] != 0) {
                        output.Append(format[src++]);
                    }
                    break;

                case 'E':
                case 'e': {
                    bool positiveSign = false;
                    int i = 0;
                    if (scientific) {
                        if (src < format.Length && format[src] == '0') {
                            // E0 は E-0 と同じ
                            i++;
                        } else if (src + 1 < format.Length && format[src] == '+' && format[src + 1] == '0') {
                            // E+0
                            positiveSign = true;
                        } else if (src + 1 < format.Length && format[src] == '-' && format[src + 1] == '0') {
                            // E-0 (ループを抜けないためのプレースホルダ)
                        } else {
                            output.Append(ch);
                            break;
                        }

                        while (++src < format.Length && format[src] == '0') {
                            i++;
                        }

                        if (i > 10) {
                            i = 10;
                        }

                        int exp = dig[0] == 0 ? 0 : number.Scale - decimalPos;
                        FormatExponent(output, exp, ch, i, positiveSign);
                        scientific = false;
                    } else {
                        output.Append(ch);
                        if (src < format.Length) {
                            if (format[src] == '+' || format[src] == '-') {
                                output.Append(format[src++]);
                            }

                            while (src < format.Length && format[src] == '0') {
                                output.Append(format[src++]);
                            }
                        }
                    }
                    break;
                }

                default:
                    output.Append(ch);
                    break;
            }
        }

        if (number.IsNegative && section == 0 && number.Scale == 0 && output.Length > 0) {
            output.InsertAtFront('-');
        }
    }
}
