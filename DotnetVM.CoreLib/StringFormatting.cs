namespace DotnetVM.CoreLib;

/// <summary>
/// String.Format 複合書式エンジン (C5.5 Wave 5)。
///
/// dotnet/runtime (MIT) の System.Private.CoreLib StringBuilder.AppendFormatHelper
/// (src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs、
/// AppendFormat(IFormatProvider, string, ReadOnlySpan&lt;object?&gt;)) の正確な移植。
/// 本家は StringBuilder (m_ChunkChars 可変チャンク) + Span で構成され VM の表現モデルに
/// 落ちないため、解析部のアルゴリズムを 1 文字ずつ等価な形で char[] 出力バッファ
/// (FormatSpecifiers.Out — 共用の ValueListBuilder&lt;char&gt; 置換) に移植した。
///
/// - 本家の ISpanFormattable 高速経路は省略している (出力に影響しないフォールバックのみの省略:
///   本家も失敗時 / パディング必要時は IFormattable 経路に落ちるため結果は同一)
/// - ICustomFormatter (provider?.GetFormat) は culture 機構のため VM では無効 (provider 無視)
/// - index / width の上限 (IndexLimit / WidthLimit = 1,000,000) と、異常書式の
///   FormatException 分類 (先頭文字非数字 / 閉じていないホール / ホール内 '{' /
///   インデックス範囲外) を本家どおり再現する
/// - provider 付き overload も provider を無視して同一結果を返す (不変カルチャ規約)
///
/// 正当性は CLR 上の差分テスト (VmCoreLibClrTests) で担保してから VmCoreLibSurfaces に配線する。
/// </summary>
public static class StringFormatting {

    // 本家 AppendFormatHelper の Undocumented な上限 (そのまま)
    private const int IndexLimit = 1_000_000; // Note:            0 <= ArgIndex < IndexLimit
    private const int WidthLimit = 1_000_000; // Note:  -WidthLimit <  ArgAlign < WidthLimit

    // ---- 公開エントリ (実在 CoreLib の String.Format overload 8 面) ----

    public static string Format2(string? format, object? arg0) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0]);
        return output.Build();
    }

    public static string Format3(string? format, object? arg0, object? arg1) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0, arg1]);
        return output.Build();
    }

    public static string Format4(string? format, object? arg0, object? arg1, object? arg2) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0, arg1, arg2]);
        return output.Build();
    }

    public static string FormatArray(string? format, object?[]? args) {
        // 本家 String.Format(string, params object[]): args null 時は format も null なら
        // format について、そうでなければ args について例外を出す (本家どおり)
        if (args is null)
            throw new ArgumentNullException(format is null ? "format" : "args");
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, args);
        return output.Build();
    }

    public static string FormatProvider3(object? provider, string? format, object? arg0) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0]);
        return output.Build();
    }

    public static string FormatProvider4(object? provider, string? format, object? arg0, object? arg1) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0, arg1]);
        return output.Build();
    }

    public static string FormatProvider5(object? provider, string? format, object? arg0, object? arg1, object? arg2) {
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, [arg0, arg1, arg2]);
        return output.Build();
    }

    public static string FormatProviderArray(object? provider, string? format, object?[]? args) {
        if (args is null)
            throw new ArgumentNullException(format is null ? "format" : "args");
        var output = new FormatSpecifiers.Out();
        AppendFormat(output, format, args);
        return output.Build();
    }

    // ---- 本家 AppendFormatHelper の移植 (provider / ICustomFormatter 無効化のみの差分) ----

    private static void AppendFormat(FormatSpecifiers.Out output, string? format, object?[] args) {
        if (format is null)
            throw new ArgumentNullException("format");

        // Repeatedly find the next hole and process it.
        var pos = 0;
        while (true) {
            // テキスト部の走査: 次の '{' / '}' まで出力する ('{' '{' / '}' '}' はエスケープ)
            while (true) {
                if ((uint)pos >= (uint)format.Length)
                    return; // 入力の残りがなくホールもない → 完了
                var brace = format[pos];
                if (brace == '{' || brace == '}') {
                    if (pos + 1 >= format.Length) {
                        pos++;
                        // 閉じホールの直後でなければ「閉じていない書式項目」
                        throw FormatExceptionAt(pos, "入力文字列の形式が正しくありません。");
                    }
                    var ch = format[pos + 1];
                    if (brace == ch) {
                        output.Append(ch); // エスケープ: 2 文字を 1 文字へ
                        pos += 2;
                        continue;
                    }
                    if (brace == '}') {
                        pos++;
                        throw FormatExceptionAt(pos, "入力文字列の形式が正しくありません。"); // 対にならない '}'
                    }
                    pos++; // ホール解析へ (pos は '{' の直後の文字を指す)
                    break;
                }
                output.Append(brace);
                pos++;
            }

            // ホール解析: '{' + インデックス + (省略可能な ',' + 幅) + (省略可能な ':' + 書式) + '}'
            var index = format[pos] - '0';
            if ((uint)index >= 10u)
                throw FormatExceptionAt(pos, "インデックス (ゼロ ベース) は 0 以上の数字である必要があります。");
            pos = MoveNext(format, pos);
            var width = 0;
            var leftJustify = false;
            var itemFormatStart = -1;
            if (format[pos] != '}') {
                // 追加のインデックス桁を消費する (上限に達したら桁の消費をやめる — 本家どおり
                // 続く文字が '}' / ':' でなければ閉じていない書式項目として失敗する)
                while (char.IsAsciiDigit(format[pos]) && index < IndexLimit) {
                    index = index * 10 + format[pos] - '0';
                    pos = MoveNext(format, pos);
                }

                // インデックス後の省略可能な空白 (' ' のみ。本家どおり)
                while (format[pos] == ' ')
                    pos = MoveNext(format, pos);

                // 省略可能なアラインメント: ',' 空白* '-'? 数字+ 空白*
                if (format[pos] == ',') {
                    do {
                        pos = MoveNext(format, pos);
                    } while (format[pos] == ' ');

                    if (format[pos] == '-') {
                        leftJustify = true;
                        pos = MoveNext(format, pos);
                    }

                    // 幅の最初の文字は数字でなければならない
                    width = format[pos] - '0';
                    if ((uint)width >= 10u)
                        throw FormatExceptionAt(pos, "アラインメント (カンマの後ろ) は 0 以上の数字である必要があります。");
                    pos = MoveNext(format, pos);
                    while (char.IsAsciiDigit(format[pos]) && width < WidthLimit) {
                        width = width * 10 + format[pos] - '0';
                        pos = MoveNext(format, pos);
                    }

                    while (format[pos] == ' ')
                        pos = MoveNext(format, pos);
                }

                if (format[pos] != '}') {
                    if (format[pos] != ':')
                        throw FormatExceptionAt(pos, "入力文字列の形式が正しくありません。"); // 閉じていない書式項目
                    // ':' の後ろから '}' までが書式指定 (ホール内の '{' は禁止)
                    itemFormatStart = pos + 1;
                    while (true) {
                        pos = MoveNext(format, pos);
                        if (format[pos] == '}')
                            break; // ホールの閉じ
                        if (format[pos] == '{')
                            throw FormatExceptionAt(pos, "入力文字列の形式が正しくありません。"); // ホール内の '{'
                    }
                }
            }

            // 書式項目の出力 ('}' の位置は pos。本家どおり共通経路)
            pos++; // '}' を飛ばす
            if ((uint)index >= (uint)args.Length)
                throw new FormatException(
                    $"インデックス (ゼロ ベース) は {args.Length} 以上の値である必要があります。");
            var arg = args[index];
            string? s;
            if (arg is IFormattable formattable) {
                // 本家どおり書式指定が空なら null を渡す (G 既定 = ToString() と同一面)
                var itemFormat = itemFormatStart >= 0
                    ? format.Substring(itemFormatStart, pos - 1 - itemFormatStart)
                    : null;
                s = formattable.ToString(itemFormat, formatProvider: null);
            } else {
                s = arg?.ToString();
            }
            s ??= string.Empty;

            if (width <= s.Length) {
                AppendString(output, s);
            } else if (leftJustify) {
                AppendString(output, s);
                AppendRepeat(output, ' ', width - s.Length);
            } else {
                AppendRepeat(output, ' ', width - s.Length);
                AppendString(output, s);
            }
        }
    }

    /// <summary>本家 MoveNext: pos を 1 進めてからその位置の文字を返す。
    /// 入力の終わりに達したら「閉じていない書式項目」の FormatException。</summary>
    private static int MoveNext(string format, int pos) {
        pos++;
        if ((uint)pos >= (uint)format.Length)
            throw FormatExceptionAt(pos, "入力文字列の形式が正しくありません。");
        return pos;
    }

    private static void AppendString(FormatSpecifiers.Out output, string s) {
        for (var i = 0; i < s.Length; i++)
            output.Append(s[i]);
    }

    private static void AppendRepeat(FormatSpecifiers.Out output, char c, int count) {
        for (var i = 0; i < count; i++)
            output.Append(c);
    }

    private static FormatException FormatExceptionAt(int pos, string message) =>
        new($"入力文字列の形式が正しくありません (位置 {pos}): {message}");
}
