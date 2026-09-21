namespace DotnetVM.CoreLib;

/// <summary>
/// decimal の標準書式 + カスタム書式エンジン (実在 CoreLib の System.Number 書式面の
/// decimal 経路の再構築)。
///
/// dotnet/runtime (MIT) の System.Private.CoreLib
/// (Number.Formatting.cs: FormatDecimal / DecimalToNumber、Decimal.DecCalc.cs:
/// DecDivMod1E9、Number.Formatting.Common.cs: NumberToString 'G' case の decimal 分岐 /
/// FormatGeneral suppressScientific / RoundNumber) の意味論を、VM で IL 実行できる形に
/// 限定して移植したもの (byte* ポインタ / stackalloc / ref decimal による in-place
/// 96 ビット除算を GetBits + ulong 筆算に置換)。
///
/// 書式エンジン本体 (NumberToString / NumberToStringFormat / RoundNumber / FormatGeneral /
/// Out / NumberBuffer) は整数と共用 (FormatSpecifiers の internal 共用面)。decimal 固有の
/// 分岐は次の 3 点:
/// - 'G' 精度 -1 (ToString() 無引数): noRounding (ECMA 規約: 小数末尾の 0 を有効数字と
///   して出力) で丸めを飛ばし、指数表記に切り替えない (FormatGeneral suppressScientific)。
///   値 0 は符号を出さない (本家 goto SkipSign)
/// - 'D' / 'X' / 'B' は decimal では無効 (本家 NumberToString の default = FormatException。
///   整数の D/X/B は高速経路で先に処理されるため case 自体が無い)
/// - 値 0 でも小数桁 (Scale) を保持する (Scale = 桁数 - decScale。例: 0.000m → "0.000")
///
/// culture は不変カルチャ固定 (FormatSpecifiers と同じ固定値: 負号 "-"、小数点 "."、
/// 桁区切り ","、通貨記号 "¤" 等)。96 ビット分解 (lo/mid/hi/flags) は実在 CoreLib の
/// System.Decimal.GetBits (managed IL: private プロパティ経由) を VM の IL 実行で辿る。
/// CLR の不変カルチャ書式と同一の結果を返す (VmCoreLibClrTests のグリッドで突合)。
/// </summary>
public static class DecimalFormatting {
    /// <summary>96 ビットの桁数上限 (79228162514264337593543950335 = 29 桁) +
    /// 丸め上がり (Scale++) / パーセントスケール (Scale += 2) の余裕。</summary>
    private const int DecimalDigitBufferSize = 34;

    /// <summary>本家 DecCalc.TenToPowerNine と同じ uint 定数。余りの算出
    /// ((uint)num - div * 1e9) は本家どおり unchecked uint 演算 (mod 2^32 折り返し) で
    /// 行う必要がある — num mod 1e9 は 32bit に収まるため折り返し後の値は正しい余りと
    /// 一致する (ulong 空間に昇格させると lo &lt; div*1e9 のとき 2^64 ラップして破綻)。</summary>
    private const uint TenToPowerNine = 1000000000;

    public static string DecimalToString(decimal value) => Format(value, null);

    public static string DecimalToString(decimal value, string? format) => Format(value, format);

    /// <summary>本家 FormatDecimal の構成 (ParseFormatSpecifier → DecimalToNumber →
    /// NumberToString / NumberToStringFormat)。</summary>
    private static string Format(decimal value, string? format) {
        var (fmt, digits) = FormatSpecifiers.ParseFormatSpecifier(format);
        var number = ToNumber(value);
        var output = new FormatSpecifiers.Out();
        if (fmt != '\0') {
            var upper = (char)(fmt & '￟'); // 大文字化 (本家 c & 0xFFDF)
            if (upper is 'D' or 'X' or 'B')
                throw new FormatException($"書式指定子 '{fmt}' は有効ではありません。");
            FormatSpecifiers.NumberToString(output, number, fmt, digits, isDecimalKind: true);
        } else {
            FormatSpecifiers.NumberToStringFormat(output, number, format!);
        }
        return output.Build();
    }

    // ---- 96 ビット → 桁列 (本家 DecimalToNumber + DecCalc.DecDivMod1E9 の移植) ----

    /// <summary>decimal の内部値 (lo + mid × 2^32 + hi × 2^64、符号、小数桁) を
    /// 桁列 NumberBuffer (Digits / Count / Scale = 桁位置 / Negative) へ変換する。
    /// 本家は ref decimal を DecDivMod1E9 で in-place 除算するが、VM では GetBits で
    /// 取り出した ulong 筆算で同じ桁取り出しを行う (下位 9 桁ずつ後方詰め)。</summary>
    private static FormatSpecifiers.NumberBuffer ToNumber(decimal value) {
        // 実在 CoreLib の GetBits (managed IL) を辿る: [lo, mid, hi, flags]
        var bits = System.Decimal.GetBits(value);
        var lo = (uint)bits[0];
        var mid = (uint)bits[1];
        var hi = (uint)bits[2];
        var flags = bits[3];
        var decScale = (flags >> 16) & 0xFF;

        var number = new FormatSpecifiers.NumberBuffer(DecimalDigitBufferSize) {
            Negative = flags < 0 // SignMask (0x80000000)。値 0 の符号は各書式経路で本家どおり処理
        };
        var digits = number.Digits;
        var index = DecimalDigitBufferSize;
        // 本家: while ((d.Mid | d.High) != 0) { 9 桁ずつ (1e9 除算) }
        while ((mid | hi) != 0) {
            var high64 = ((ulong)hi << 32) + mid;
            var div64 = high64 / (ulong)TenToPowerNine;
            hi = (uint)(div64 >> 32);
            mid = (uint)div64;
            var num = ((high64 - (uint)div64 * (ulong)TenToPowerNine) << 32) + lo;
            var div = (uint)(num / (ulong)TenToPowerNine);
            lo = div;
            // 本家 DecDivMod1E9 の戻り: unchecked uint 演算で余りを取り出す
            var remainder = (uint)num - div * TenToPowerNine;
            // UInt32ToDecChars(p, remainder, 9): 9 桁固定 (上位 0 埋め) で後方詰め
            for (var i = 0; i < 9; i++) {
                digits[--index] = (char)('0' + (int)(remainder % 10));
                remainder /= 10;
            }
        }
        // 本家: UInt32ToDecChars(p, d.Low, 0) — minDigits 0 なので値 0 は 1 文字も出さない
        while (lo != 0) {
            digits[--index] = (char)('0' + (int)(lo % 10));
            lo /= 10;
        }
        var count = DecimalDigitBufferSize - index;
        number.Count = count;
        // 本家: number.Scale = i - d.Scale (桁位置 = 桁数 - 小数桁)。値 0 では
        // -decScale となり "0.000" のような小数桁保持が達成される
        number.Scale = count - decScale;
        // 前詰めする
        for (var i = 0; i < count; i++)
            digits[i] = digits[index + i];
        return number;
    }
}
