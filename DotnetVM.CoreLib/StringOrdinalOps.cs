namespace DotnetVM.CoreLib;

/// <summary>
/// String の ordinal 面 (C5.5 Wave 5): CompareOrdinal / IndexOf(char) / LastIndexOf(char) /
/// Contains / Replace / Split 全 overload 群。実在 CoreLib の該当面は SpanHelpers の
/// SIMD intrinsic 面 (Vector128/256) や fixed byte* 比較で構成され VM のスロット表現に
/// 落ちないため、同一意味論の純粋 char ループ実装に置き換える (VmCoreLibSurfaces.Faces 経由)。
/// ordinal の比較 / 検索は culture に依存しないため、VM の不変カルチャ規約と矛盾しない。
///
/// dotnetVM.CoreLib 規約どおり StringBuilder / List / Array.Copy は使わず
/// char[] + インデックスの手動バッファと手動成長配列で構築する。
/// 正当性は CLR 上の差分テスト (VmCoreLibClrTests の Split / Replace 全面グリッド) で担保。
/// </summary>
public static class StringOrdinalOps {

    // ---- CompareOrdinal (本家: fixed byte* での UTF-16 コード単位比較) ----

    /// <summary>UTF-16 コード単位の ordinal 比較。本家 CompareOrdinalHelper
    /// (String.Comparison.cs) と同一意味論: 最初の差分位置のコード単位差そのもの
    /// (符号だけではない) を返す。片方が終端に達する差分は、位置 0 / 1 では本家が
    /// padding '\0' を読むため '\0' との char 差 (実測: "a" vs "" → 97、
    /// "a" vs "a"+U+0301 → -769)、共通 prefix が 2 文字以上では長さ差
    /// (実測: "ab" vs "abc" → -1)。null は null &lt; 非 null の順。</summary>
    public static int CompareOrdinal(string? strA, string? strB) {
        if (strA is null)
            return strB is null ? 0 : -1;
        if (strB is null)
            return 1;
        var limit = strA.Length < strB.Length ? strA.Length : strB.Length;
        for (var i = 0; i < limit; i++)
            if (strA[i] != strB[i])
                return strA[i] - strB[i];
        if (limit <= 1) {
            // 位置 0 / 1 での終端差分: 本家は padding '\0' を読んで char 差を返す
            var ca = strA.Length > limit ? strA[limit] : '\0';
            var cb = strB.Length > limit ? strB[limit] : '\0';
            return ca - cb;
        }
        // 共通 prefix ≥ 2 → 長さ差 (本家 NotLongerThan2 / SequenceCompareTo)
        return strA.Length - strB.Length;
    }

    // ---- 検索面 (本家: SpanHelpers の SIMD intrinsic 面 → 単純スキャンで同一意味論) ----

    public static int IndexOfChar(string value, char c) {
        for (var i = 0; i < value.Length; i++)
            if (value[i] == c)
                return i;
        return -1;
    }

    public static int LastIndexOfChar(string value, char c) {
        for (var i = value.Length - 1; i >= 0; i--)
            if (value[i] == c)
                return i;
        return -1;
    }

    /// <summary>Contains(value)。value null は本家どおり ArgumentNullException。
    /// 空針は位置 0 で一致扱い (本家どおり true)。</summary>
    public static bool Contains(string value, string needle) {
        if (needle is null)
            throw new ArgumentNullException("value");
        return OrdinalIndexOf(value, needle, 0) >= 0;
    }

    // ---- Replace (本家 ReplaceCore の 2 パス: 出現数計上 → 一括構築) ----

    /// <summary>Replace(oldValue, newValue)。newValue null は本家どおり空文字列扱い
    /// (例外ではない。実測: "abc".Replace("c", null) → "ab")。</summary>
    public static string Replace(string value, string oldValue, string? newValue) {
        if (oldValue is null)
            throw new ArgumentNullException("oldValue");
        if (oldValue.Length == 0)
            throw new ArgumentException("'oldValue' cannot be an empty string.", "oldValue");
        var replacement = newValue ?? string.Empty;
        // 出現数を数える (重なりは本家どおり非重複で数える)
        var count = 0;
        var idx = 0;
        while ((idx = OrdinalIndexOf(value, oldValue, idx)) >= 0) {
            count++;
            idx += oldValue.Length;
        }
        if (count == 0)
            return value; // 置換なし → 本家どおり同一インスタンス (文字列は不変)
        var result = new char[value.Length + count * (replacement.Length - oldValue.Length)];
        var r = 0;
        var src = 0;
        idx = 0;
        while ((idx = OrdinalIndexOf(value, oldValue, src)) >= 0) {
            for (var i = src; i < idx; i++)
                result[r++] = value[i];
            for (var i = 0; i < replacement.Length; i++)
                result[r++] = replacement[i];
            src = idx + oldValue.Length;
        }
        for (var i = src; i < value.Length; i++)
            result[r++] = value[i];
        return new string(result);
    }

    /// <summary>ordinal の部分文字列検索 (from 開始)。見つからなければ -1。
    /// needle 空文字は from を返す (本家 IndexOf(string) と同一: "" は常に位置 0 扱い)。</summary>
    private static int OrdinalIndexOf(string haystack, string needle, int from) {
        if (needle.Length == 0)
            return from <= haystack.Length ? from : -1;
        var limit = haystack.Length - needle.Length;
        var first = needle[0];
        for (var start = from; start <= limit; start++) {
            if (haystack[start] != first)
                continue;
            var matched = true;
            for (var i = 1; i < needle.Length; i++) {
                if (haystack[start + i] != needle[i]) {
                    matched = false;
                    break;
                }
            }
            if (matched)
                return start;
        }
        return -1;
    }

    // ---- Split (全 12 overload。本家の意味論: 非 overlap の最先端一致、
    //      RemoveEmptyEntries では空要素を数えない、詳細は SplitCore の doc 参照) ----

    public static string[] SplitChar(string value, char separator) =>
        SplitCore(value, null, separator, null, null, int.MaxValue, StringSplitOptions.None);

    public static string[] SplitCharCount(string value, char separator, int count) {
        ValidateCount(count);
        return SplitCore(value, null, separator, null, null, count, StringSplitOptions.None);
    }

    public static string[] SplitCharOptions(string value, char separator, StringSplitOptions options) {
        ValidateOptions(options);
        return SplitCore(value, null, separator, null, null, int.MaxValue, options);
    }

    public static string[] SplitCharFull(string value, char separator, int count, StringSplitOptions options) {
        ValidateCount(count);
        ValidateOptions(options);
        return SplitCore(value, null, separator, null, null, count, options);
    }

    public static string[] SplitCharArray(string value, char[]? separators) =>
        SplitCore(value, separators, null, null, null, int.MaxValue, StringSplitOptions.None);

    public static string[] SplitCharArrayCount(string value, char[]? separators, int count) {
        ValidateCount(count);
        return SplitCore(value, separators, null, null, null, count, StringSplitOptions.None);
    }

    public static string[] SplitCharArrayOptions(string value, char[]? separators, StringSplitOptions options) {
        ValidateOptions(options);
        return SplitCore(value, separators, null, null, null, int.MaxValue, options);
    }

    public static string[] SplitCharArrayFull(string value, char[]? separators, int count, StringSplitOptions options) {
        ValidateCount(count);
        ValidateOptions(options);
        return SplitCore(value, separators, null, null, null, count, options);
    }

    public static string[] SplitStringOptions(string value, string? separator, StringSplitOptions options) {
        ValidateOptions(options);
        return SplitStringCore(value, separator, int.MaxValue, options);
    }

    public static string[] SplitStringFull(string value, string? separator, int count, StringSplitOptions options) {
        ValidateCount(count);
        ValidateOptions(options);
        return SplitStringCore(value, separator, count, options);
    }

    /// <summary>本家 Split(string, ...) 系統: separator が null / 空文字列のときは
    /// 空白フォールバックせず分割点なし (「返される配列にはこのインスタンスを含む
    /// 1 つの要素が格納される」。空入力 + RemoveEmptyEntries のときのみ空配列)。
    /// 実測: "  x  y  ".Split((string?)null) → 1 要素 (char[] / string[] の null は
    /// 空白分割になるのと対照的)。</summary>
    private static string[] SplitStringCore(string value, string? separator, int count, StringSplitOptions options) {
        if (separator is null || separator.Length == 0) {
            var single = count != 0 &&
                !(options == StringSplitOptions.RemoveEmptyEntries && value.Length == 0);
            var result = new string[single ? 1 : 0];
            if (single)
                result[0] = value;
            return result;
        }
        return SplitCore(value, null, null, separator, null, count, options);
    }

    public static string[] SplitStringsOptions(string value, string?[]? separators, StringSplitOptions options) {
        ValidateOptions(options);
        return SplitCore(value, null, null, null, separators, int.MaxValue, options);
    }

    public static string[] SplitStringsFull(string value, string?[]? separators, int count, StringSplitOptions options) {
        ValidateCount(count);
        ValidateOptions(options);
        return SplitCore(value, null, null, null, separators, count, options);
    }

    private static void ValidateCount(int count) {
        if (count < 0)
            throw new ArgumentOutOfRangeException("count");
    }

    private static void ValidateOptions(StringSplitOptions options) {
        if (options is not (StringSplitOptions.None or StringSplitOptions.RemoveEmptyEntries))
            throw new ArgumentException("Invalid enumeration value.", "options");
    }

    /// <summary>共通核。charSeps / singleChar / singleSep / stringSeps のちょうど 1 つが有効。
    /// char[] 系統と string[] 系統では null / 空 = 本家どおり空白 (char.IsWhiteSpace)
    /// 区切り。singleSep は非 null で渡ること (string 単独系統の null / 空 は
    /// SplitStringCore が分割点なしとして先に処理する — 本家どおり)。
    /// count の意味論 (本家を実測で確定): count は「返す配列の要素数」だが
    /// RemoveEmptyEntries との組合わせでは空要素を数えない — 空要素を生む分割点は
    /// 追加済み数が上限に達した後も消費・スキップを続け、非空要素が count - 1 個
    /// 揃った時点の残りを最終要素とする。count = 1 は分割せず全体 1 要素
    /// (空入力 + RemoveEmptyEntries のときのみ空配列)。</summary>
    private static string[] SplitCore(string value, char[]? charSeps, char? singleChar,
        string? singleSep, string?[]? stringSeps, int count, StringSplitOptions options) {
        if (count == 0)
            return new string[0];
        var omitEmpty = options == StringSplitOptions.RemoveEmptyEntries;
        if (count == 1)
            return omitEmpty && value.Length == 0 ? new string[0] : new string[] { value };
        var length = value.Length;
        // 結果は最大 count 個だが、各分割点は 1 文字以上を消費するため
        // value.Length + 1 個を超えない (本家どおり)。count = int.MaxValue で
        // 満サイズ確保しないよう上限に丸める
        var results = new string[count <= length + 1 ? count : length + 1];
        var added = 0;
        var start = 0;
        var i = 0;
        while (i < length) {
            var sepLength = MatchSeparator(value, i, charSeps, singleChar, singleSep, stringSeps);
            if (sepLength > 0) {
                var part = value.Substring(start, i - start);
                i += sepLength;
                if (omitEmpty && part.Length == 0) {
                    // 空要素を生む分割点は追加済み数に数えず、上限に達した後も
                    // 消費・スキップを続ける (本家どおり)
                    start = i;
                } else if (added >= count - 1) {
                    break; // 非空で上限 → 残り全部を最終要素に
                } else {
                    results[added++] = part;
                    start = i;
                }
            } else {
                i++;
            }
        }
        var rest = value.Substring(start);
        if (!omitEmpty || rest.Length != 0)
            results[added++] = rest;
        // 手動で結果長へ縮める (Array.Copy / LINQ は DotnetVM.CoreLib の依存制約で不使用)
        if (added == results.Length)
            return results;
        var trimmed = new string[added];
        for (var j = 0; j < added; j++)
            trimmed[j] = results[j];
        return trimmed;
    }

    /// <summary>位置 index で区切りが一致するか判定し、一致した区切りの長さを返す
    /// (不一致は 0)。string[] セパレータは本家どおり配列順に照合し、最初に一致した
    /// 非 null / 非空の要素を使う。</summary>
    private static int MatchSeparator(string value, int index,
        char[]? charSeps, char? singleChar, string? singleSep, string?[]? stringSeps) {
        if (singleChar is { } single)
            return value[index] == single ? 1 : 0;
        if (charSeps is not null && charSeps.Length != 0) {
            var c = value[index];
            for (var i = 0; i < charSeps.Length; i++)
                if (charSeps[i] == c)
                    return 1;
            return 0;
        }
        if (singleSep is not null)
            return OrdinalStartsWith(value, index, singleSep) ? singleSep.Length : 0;
        if (stringSeps is not null && stringSeps.Length != 0) {
            for (var i = 0; i < stringSeps.Length; i++) {
                var sep = stringSeps[i];
                if (sep is null || sep.Length == 0)
                    continue; // 空 / null セパレータは本家どおり無視
                if (OrdinalStartsWith(value, index, sep))
                    return sep.Length;
            }
            return 0;
        }
        // セパレータ未指定 (char[] / string[] の null / 空) → 空白区切り (本家どおり)
        return char.IsWhiteSpace(value[index]) ? 1 : 0;
    }

    private static bool OrdinalStartsWith(string value, int index, string sep) {
        if (sep.Length == 0 || index + sep.Length > value.Length)
            return false;
        if (value[index] != sep[0])
            return false;
        for (var i = 1; i < sep.Length; i++)
            if (value[index + i] != sep[i])
                return false;
        return true;
    }
}
