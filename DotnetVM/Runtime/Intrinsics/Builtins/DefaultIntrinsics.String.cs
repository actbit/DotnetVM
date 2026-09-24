using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    /// <summary>System.Char の static 面 (String の CoreLib IL — Trim 等 — が呼ぶ)。
    /// Unicode の空白判定はホストの char.IsWhiteSpace と同一意味論。</summary>
    private static void RegisterChar(IntrinsicRegistry r) {
        const string T = "System.Char";
        r.Register(IntrinsicKey.Static(T, "IsWhiteSpace", 1),
            static (_, a) => StackSlot.OfInt32(char.IsWhiteSpace((char)a[0].AsInt32) ? 1 : 0));
    }

    // ---- System.String ----

    private static void RegisterString(IntrinsicRegistry r) {
        const string T = "System.String";
        var instanceTemplate = IntrinsicKey.Instance(T, "", 0);
        var staticTemplate = IntrinsicKey.Static(T, "", 0);

        // インスタンスメソッド (this + 引数) / 静的メソッド
        void Instance(string name, int ps, IntrinsicImpl impl) =>
            r.Register(instanceTemplate with { MethodName = name, Arity = ps + 1 }, impl);
        void Static(string name, int ps, IntrinsicImpl impl) =>
            r.Register(staticTemplate with { MethodName = name, Arity = ps }, impl);

        // 静的フィールド (ldsfld の TypeRef 親で参照される)。空文字列はプールから実体化
        r.RegisterStaticField(T, "Empty", static ctx => StackSlot.OfObject(ctx.MakeString("")));

        Instance(".ctor", 0, static (_, _) => null); // フォールバック (通常は newobj 経由で呼ばれない)
        Instance("get_Length", 0, static (_, a) => StackSlot.OfInt32(new Args(a).String(0).Value.Length));
        Instance("get_Chars", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var i = s.Int32(1);
            if ((uint)i >= (uint)value.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException", null);
            return StackSlot.OfInt32(value[i]);
        });
        Instance("Substring", 1, static (ctx, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var start = s.Int32(1);
            if ((uint)start > (uint)value.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            return StackSlot.OfObject(ctx.MakeString(value[start..]));
        });
        Instance("Substring", 2, static (ctx, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var start = s.Int32(1);
            var length = s.Int32(2);
            if ((uint)start > (uint)value.Length || length < 0 || start + length > value.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            return StackSlot.OfObject(ctx.MakeString(value.Substring(start, length)));
        });
        Instance("Equals", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[1].ObjectValue is VmString other && s.String(0).Value == other.Value ? 1 : 0);
        });
        Instance("CompareTo", 1, static (_, a) => {
            var s = new Args(a);
            // CLR の String.CompareTo は guest 呼出スコープに設定された culture で比較する。
            return StackSlot.OfInt32(string.Compare(s.String(0).Value, s.String(1).Value, StringComparison.CurrentCulture));
        });
        Instance("Contains", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.Contains(s.String(1).Value) ? 1 : 0);
        });
        Instance("StartsWith", 1, static (_, a) => {
            var s = new Args(a);
            // CLR の 1 引数 StartsWith/EndsWith は culture 比較。
            return StackSlot.OfInt32(s.String(0).Value.StartsWith(s.String(1).Value, StringComparison.CurrentCulture) ? 1 : 0);
        });
        Instance("EndsWith", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.EndsWith(s.String(1).Value, StringComparison.CurrentCulture) ? 1 : 0);
        });
        Instance("IndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.IndexOf(needle.Value, StringComparison.CurrentCulture))
                : StackSlot.OfInt32(value.IndexOf(s.Char(1)));
        });
        Instance("LastIndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.LastIndexOf(needle.Value, StringComparison.CurrentCulture))
                : StackSlot.OfInt32(value.LastIndexOf(s.Char(1)));
        });
        Instance("Replace", 2, static (ctx, a) => {
            var s = new Args(a);
            return StackSlot.OfObject(ctx.MakeString(s.String(0).Value.Replace(s.String(1).Value, s.String(2).Value)));
        });
        Instance("ToUpper", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.ToUpper())));
        Instance("ToLower", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.ToLower())));
        Instance("Trim", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.Trim())));
        Instance("ToString", 0, static (_, a) => StackSlot.OfObject(new Args(a).String(0)));

        // String.Concat は string / object の各オーバーロードが同一キーに集約されるため、
        // 引数を実行時にフォーマットして両方を処理する (ボックス化値は ToString 相当)
        Static("Concat", 2, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]))));
        Static("Concat", 3, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]) + FormatSlot(ctx, a[2]))));
        Static("Concat", 4, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]) + FormatSlot(ctx, a[2]) + FormatSlot(ctx, a[3]))));
        // String.Format (書式 + 値 1〜3 + params object[])。末尾 object[] は共通処理で展開
        Static("Format", 2, FormatImpl);
        Static("Format", 3, FormatImpl);
        Static("Format", 4, FormatImpl);
        Static("Format", 5, FormatImpl);
        // String.CompareOrdinal (C5.5 Wave 5 で ① バインドを (b) 置換面へ明け渡したため、
        // CoreLib 未ロード時の代替経路としてこの legacy キーを新設)
        Static("CompareOrdinal", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(string.CompareOrdinal(s.String(0).Value, s.String(1).Value));
        });
        // String.Split (char / char[] / string[] セパレータ。カウント / StringSplitOptions 付きも統合)
        Instance("Split", 1, SplitImpl);
        Instance("Split", 2, SplitImpl);
        // String.Join (区切り string/char、要素 string[]/object[])
        Static("Join", 2, JoinImpl);
        Static("Equals", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value == s.String(1).Value ? 1 : 0);
        });
        Static("op_Equality", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value == s.String(1).Value ? 1 : 0);
        });
        Static("op_Inequality", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value != s.String(1).Value ? 1 : 0);
        });
        Static("IsNullOrEmpty", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[0].ObjectValue is not VmString str || str.Value.Length == 0 ? 1 : 0);
        });
        Static("IsNullOrWhiteSpace", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[0].ObjectValue is not VmString str || string.IsNullOrWhiteSpace(str.Value) ? 1 : 0);
        });
        Static("Intern", 1, static (ctx, a) => StackSlot.OfObject(new Args(a).String(0))); // プール済み VmString を返す
    }

    /// <summary>String.Split 統合面。宣言パラメータ型で char / char[] / string[] を判別する。
    /// instance メソッドのため args[0] は this (= レシーバ文字列)、ParamAt(0) がセパレータの宣言型。
    /// C5 からは CoreLibBindings 側のランタイムバインド (SpanHelpers 依存の表現境界面) からも再利用する。</summary>
    internal static StackSlot? SplitImpl(IntrinsicContext ctx, StackSlot[] a) {
        var s = new Args(a);
        var value = s.String(0).Value;
        var t1 = ctx.ParamAt(0);
        string[] parts;
        if (a.Length == 2) {
            // this + セパレータのみ
            parts = t1 switch {
                "System.Char" => value.Split(s.Char(1)),
                "System.Char[]" => value.Split(ReadCharArray(a[1])),
                "System.String[]" => value.Split(ReadStringArray(ctx, a[1]), StringSplitOptions.None),
                _ => throw new InvalidOperationException($"String.Split 未対応のセパレータ型: {t1}"),
            };
        } else if (a.Length == 3) {
            // this + セパレータ + (count | StringSplitOptions)
            var t2 = ctx.ParamAt(1);
            parts = (t1, t2) switch {
                ("System.Char", "System.StringSplitOptions") => value.Split(s.Char(1), (StringSplitOptions)s.Int32(2)),
                ("System.Char", "System.Int32") => value.Split(s.Char(1), s.Int32(2)),
                ("System.Char[]", "System.StringSplitOptions") => value.Split(ReadCharArray(a[1]), (StringSplitOptions)s.Int32(2)),
                ("System.Char[]", "System.Int32") => value.Split(ReadCharArray(a[1]), s.Int32(2)),
                ("System.String[]", "System.StringSplitOptions") => value.Split(ReadStringArray(ctx, a[1]), (StringSplitOptions)s.Int32(2)),
                ("System.String[]", "System.Int32") => value.Split(ReadStringArray(ctx, a[1]), s.Int32(2), StringSplitOptions.None),
                _ => throw new InvalidOperationException($"String.Split 未対応の引数型: ({t1}, {t2})"),
            };
        } else {
            throw new InvalidOperationException($"String.Split の引数 {a.Length - 1} 個は未対応です。");
        }
        return StackSlot.OfObject(ctx.MakeStringArray(parts));
    }

    /// <summary>String.Join 統合面 (区切り string/char、要素 string[]/object[])。</summary>
    private static StackSlot? JoinImpl(IntrinsicContext ctx, StackSlot[] a) {
        var s = new Args(a);
        var separator = ctx.ParamAt(0) == "System.Char" ? s.Char(0).ToString() : s.String(0).Value;
        var parts = ReadObjectArray(a[1]).Select(v => v.ObjectValue switch {
            null => "",
            VmString str => str.Value,
            _ => ctx.InvokeToString(v)?.Value ?? FormatSlot(ctx, v),
        });
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator, parts)));
    }
}
