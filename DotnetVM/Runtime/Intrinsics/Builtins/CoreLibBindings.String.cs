using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using System.Text;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.String (culture 依存面のバインド: 署名キーで ② IL より先に解決される) ----

    private static void RegisterString(IntrinsicRegistry r) {
        const string T = "System.String";
        // 4 項以上の連結は Roslyn が String.Concat(params string[]) / (params object[]) に
        // コンパイルする。要素は CLR 規約どおりフォーマットする (null は空文字列)。
        // 連結本体は実 IL の wstrcpy 生ポインタコピー面のため委譲継続 (C5.5 Wave 5 確定)
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.String[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.Object[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        // ---- culture 相当必須面 (C5.5 Wave 5 確定): 本家 IL は CultureInfo /
        //      CompareInfo (culture 機構) で構成され culture スコープ外のため、
        //      VmHostOptions.Culture で設定された CurrentCulture 面をホスト BCL へ委譲する
        //      (不変カルチャ比較 IL 移植候補とのファズ突合で非 ASCII 差分が証明済み —
        //      ordinal 近似では ß/ss 等のインバリアント等価が再現できない)。
        //      対応する ordinal / 置換面 (CompareOrdinal / IndexOf(char) / Contains /
        //      Replace / Split / Format) は VmCoreLibSurfaces の (b) 置換面に移行済みで
        //      ここには載せない (載せると ① が ②'/置換面を塞ぐ退行)。
        //      null 許容の静的比較面のみ本家どおり null を通す (Compare(null, x) = 負)
        static string? Ns(StackSlot[] a, int i) => (a[i].ObjectValue as VmString)?.Value;
        static VmString Str(StackSlot[] a, int i) =>
            a[i].ObjectValue as VmString ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        static void ChargeCultureWork(IntrinsicContext ctx, string? left, string? right = null) =>
            ctx.Heap.ChargeHostWork(HostWorkChars(left, right));
        r.RegisterBinding(BindingKey.Static(T, "Compare", "System.String", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Ns(a, 0), Ns(a, 1));
                return StackSlot.OfInt32(string.Compare(Ns(a, 0), Ns(a, 1), StringComparison.CurrentCulture));
            },
            BindingOrigin.Managed);
        // Equals(String, String) (op_Equality の呼び先): 本家 IL 末尾が SpanHelpers.SequenceEqual
        // (ref byte, ref byte, nuint) で、その scalar フォールバック自体が GSJA 抽象化
        // (ISimdVector static abstract = Vector128<T> inline 表現依存) のため VM 表現境界。
        // ordinal 等価は culture 無関当面のためホスト ordinal 等価へ委譲する
        r.RegisterBinding(BindingKey.Static(T, "Equals", "System.String", "System.String"),
            static (_, a) => StackSlot.OfInt32(string.Equals(Ns(a, 0), Ns(a, 1), StringComparison.Ordinal) ? 1 : 0),
            BindingOrigin.Managed);
        // StringComparison overload 群 (Equals static / Equals instance / Compare SCI):
        // 本家 IL は StringComparison 分岐後、ordinal 面 (String.EqualsFast / String.CompareOrdinal)
        // を直接辿る面と culture 機構面 (CompareInfo / SpanHelpers SIMD 抽象化 —
        // EqualsIgnoreCase_Vector は ISimdVector<TVector,T> static abstract = GSJA 抽象化で
        // Vector128<T> inline 表現依存) に分かれる。SIMD 抽象化面は VM 表現境界のため、
        // SCI 面 3 overload を上記 culture 面と同じホスト BCL 委譲に統一する。
        // CurrentCulture 系は guest 呼出スコープで設定されたカルチャを使い、Invariant / Ordinal 系はそのまま通す
        static StringComparison ConfiguredComparison(int comparison) => comparison switch {
            0 => StringComparison.CurrentCulture,
            1 => StringComparison.CurrentCultureIgnoreCase,
            2 => StringComparison.InvariantCulture,
            3 => StringComparison.InvariantCultureIgnoreCase,
            4 => StringComparison.Ordinal,
            _ => StringComparison.OrdinalIgnoreCase,
        };
        r.RegisterBinding(BindingKey.Static(T, "Equals", "System.String", "System.String", "System.StringComparison"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Ns(a, 0), Ns(a, 1));
                return StackSlot.OfInt32(string.Equals(Ns(a, 0), Ns(a, 1), ConfiguredComparison(a[2].AsInt32)) ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Equals", "System.String", "System.StringComparison"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.Equals(Str(a, 1).Value, ConfiguredComparison(a[2].AsInt32)) ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Compare", "System.String", "System.String", "System.StringComparison"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Ns(a, 0), Ns(a, 1));
                return StackSlot.OfInt32(string.Compare(Ns(a, 0), Ns(a, 1), ConfiguredComparison(a[2].AsInt32)));
            },
            BindingOrigin.Managed);
        // CompareTo (instance): 本家 IL も CurrentCulture の Compare を呼ぶ文化面。
        // ① が ② IL より先に解決されるため、無いと IL 実行時に CompareInfo で fail-closed になる
        r.RegisterBinding(BindingKey.Instance(T, "CompareTo", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(string.Compare(Str(a, 0).Value, Str(a, 1).Value, StringComparison.CurrentCulture));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.IndexOf(Str(a, 1).Value, StringComparison.CurrentCulture));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.LastIndexOf(Str(a, 1).Value, StringComparison.CurrentCulture));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "StartsWith", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.StartsWith(Str(a, 1).Value, StringComparison.CurrentCulture) ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "EndsWith", "System.String"),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.EndsWith(Str(a, 1).Value, StringComparison.CurrentCulture) ? 1 : 0);
            },
            BindingOrigin.Managed);
        // 大文字小文字面: 本家 IL は CultureInfo.CurrentCulture.TextInfo (culture 機構 +
        // InternalCall) を辿るため、設定カルチャを適用した guest 呼出スコープでホスト BCL に委譲する。
        // ToUpperInvariant / ToLowerInvariant も本家 IL は TextInfo を辿るため同様に委譲する
        r.RegisterBinding(BindingKey.Instance(T, "ToUpper"),
            static (ctx, a) => {
                var value = Str(a, 0).Value;
                ctx.Heap.ChargeHostWork(value.Length);
                return StackSlot.OfObject(ctx.MakeString(value.ToUpper()));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToLower"),
            static (ctx, a) => {
                var value = Str(a, 0).Value;
                ctx.Heap.ChargeHostWork(value.Length);
                return StackSlot.OfObject(ctx.MakeString(value.ToLower()));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToUpperInvariant"),
            static (ctx, a) => {
                var value = Str(a, 0).Value;
                ctx.Heap.ChargeHostWork(value.Length);
                return StackSlot.OfObject(ctx.MakeString(value.ToUpperInvariant()));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToLowerInvariant"),
            static (ctx, a) => {
                var value = Str(a, 0).Value;
                ctx.Heap.ChargeHostWork(value.Length);
                return StackSlot.OfObject(ctx.MakeString(value.ToLowerInvariant()));
            },
            BindingOrigin.Managed);
        // String.Normalize / IsNormalized の CoreLib 実装は Interop.Globalization の
        // P/Invoke (System.Globalization.Native) に降りる。VM 内ではホストの managed BCL
        // に委譲し、文字列値だけを境界越しに渡す。
        r.RegisterBinding(BindingKey.Instance(T, "Normalize"),
            static (ctx, a) => NormalizeString(ctx, a, hasForm: false, isCheck: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Normalize", "System.Text.NormalizationForm"),
            static (ctx, a) => NormalizeString(ctx, a, hasForm: true, isCheck: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IsNormalized"),
            static (ctx, a) => NormalizeString(ctx, a, hasForm: false, isCheck: true),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IsNormalized", "System.Text.NormalizationForm"),
            static (ctx, a) => NormalizeString(ctx, a, hasForm: true, isCheck: true),
            BindingOrigin.Managed);
        // 1 文字の文字列生成 (InternalCall 面。char.ToString() の実 IL が string.CreateFromChar
        // を辿る — VmString 生成は既存の文字列内部面と同一経路)
        r.RegisterBinding(BindingKey.Static(T, "CreateFromChar", "System.Char"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Ch(a, 0).ToString())),
            BindingOrigin.InternalCall);
        // ---- System.Char culture 面 (C5.5 Wave 5 継続): 本家 IL は
        //      CultureInfo.CurrentCulture.TextInfo (culture 機構 + InternalCall) を辿るため
        //      設定カルチャを使うホスト面へ委譲 (1 引数面のみ。CultureInfo 引数の
        //      overload は呼び出し側 IL が CultureInfo 構築を必要とするため当面未対応)。
        //      GetNumericValue は Unicode 数字値 (ネイティブ Unicode テーブル InternalCall)
        r.RegisterBinding(BindingKey.Static("System.Char", "ToUpper", "System.Char"),
            static (ctx, a) => { ctx.Heap.ChargeHostWork(1); return StackSlot.OfInt32(char.ToUpper(Ch(a, 0), System.Globalization.CultureInfo.CurrentCulture)); },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Char", "ToLower", "System.Char"),
            static (ctx, a) => { ctx.Heap.ChargeHostWork(1); return StackSlot.OfInt32(char.ToLower(Ch(a, 0), System.Globalization.CultureInfo.CurrentCulture)); },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Char", "GetNumericValue", "System.Char"),
            static (_, a) => StackSlot.OfFloat(char.GetNumericValue(Ch(a, 0))),
            BindingOrigin.InternalCall);

        // ---- 文字整形・部分文字列面 (C5.5 探査継続):
        //      本家 IL は Span / fixed char* 内部 (PadLeft の SpanFill、Remove の Substring
        //      InnerAlloc 連鎖、EndsWith/IndexOf の StringComparison 抽象化面) を辿るため、
        //      VM の表現境界として同一意味論のホスト ordinal / 不変面へ委譲する。
        //      Join (params object[]) も CLR 同一規約 (null → 空、要素はフォーマット相当)
        //      でホスト面を通す (要素の ToString は VM の暗黙 ToString フック経由で正規化)。
        r.RegisterBinding(BindingKey.Instance(T, "Remove", ["System.Int32", "System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.Remove(a[1].AsInt32, a[2].AsInt32))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadLeft", ["System.Int32", "System.Char"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadLeft(a[1].AsInt32, Ch(a, 2)))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadLeft", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadLeft(a[1].AsInt32))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadRight", ["System.Int32", "System.Char"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadRight(a[1].AsInt32, Ch(a, 2)))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadRight", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadRight(a[1].AsInt32))),
            BindingOrigin.Managed);
        // —— SCI overload 群 (IndexOf(string[, SCI]) / EndsWith / StartsWith):
        //      Ordinal 面 (IndexOf(Char, SCI) の被評価 IL は string.IndexOf(char) や
        //      SpanHelpers を辿る。SIMD ignore-case 抽象化 = GSJA 抽象化のため culture 無関
        //      … ordinal 等価は文化に依存しない = ordinal のホスト面に渡す
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String", ["System.String", "System.String[]"]),
            static (ctx, a) => JoinedFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String", ["System.String", "System.Object[]"]),
            static (ctx, a) => JoinedFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String",
                ["System.String", "System.Collections.Generic.IEnumerable`1<!!0>"]),
            static (ctx, a) => JoinedEnumerableFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "StartsWith", ["System.String", "System.StringComparison"]),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.StartsWith(Str(a, 1).Value, ConfiguredComparison(a[2].AsInt32)) ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "EndsWith", ["System.String", "System.StringComparison"]),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.EndsWith(Str(a, 1).Value, ConfiguredComparison(a[2].AsInt32)) ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", ["System.String", "System.StringComparison"]),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.IndexOf(Str(a, 1).Value, ConfiguredComparison(a[2].AsInt32)));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", ["System.String", "System.StringComparison"]),
            static (ctx, a) => {
                ChargeCultureWork(ctx, Str(a, 0).Value, Str(a, 1).Value);
                return StackSlot.OfInt32(Str(a, 0).Value.LastIndexOf(Str(a, 1).Value, ConfiguredComparison(a[2].AsInt32)));
            },
            BindingOrigin.Managed);
    }

    /// <summary>string.Join の要素の ToString 面 (VM オブジェクトを正規化して連結する)。</summary>
    private static StackSlot? JoinedFace(IntrinsicContext ctx, string? separator, StackSlot valuesSlot) {
        if (valuesSlot.ObjectValue is not VmArray array)
            throw new InvalidOperationException("string.Join の第 2 引数が配列ではありません。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator ?? string.Empty, parts)));
    }

    /// <summary>string.Join&lt;T&gt;(string, IEnumerable&lt;T&gt;)。配列由来の列挙面は VM 配列を
    /// 直接走査し、要素の ToString は暗黙 ToString フックへ渡す。</summary>
    private static StackSlot? JoinedEnumerableFace(IntrinsicContext ctx, string? separator, StackSlot valuesSlot) {
        if (valuesSlot.ObjectValue is null)
            return StackSlot.OfObject(ctx.MakeString(string.Empty));
        if (valuesSlot.ObjectValue is not VmArray array)
            throw new NotSupportedException(
                $"string.Join<T> actual={valuesSlot.ObjectValue.GetType().Name}/{valuesSlot.Kind}; VM 配列の IEnumerable<T> 面のみ対応しています。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator ?? string.Empty, parts)));
    }

    private static char Ch(StackSlot[] a, int i) => (char)a[i].AsInt32;

    private static StackSlot? NormalizeString(IntrinsicContext ctx, StackSlot[] a, bool hasForm, bool isCheck) {
        var value = (a[0].ObjectValue as VmString)?.Value
            ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        ctx.Heap.ChargeHostWork(value.Length);
        var form = hasForm ? (NormalizationForm)a[1].AsInt32 : NormalizationForm.FormC;
        try {
            if (isCheck)
                return StackSlot.OfInt32(value.IsNormalized(form) ? 1 : 0);
            return StackSlot.OfObject(ctx.MakeString(value.Normalize(form)));
        } catch (ArgumentException ex) {
            throw new UnhandledGuestException("System.ArgumentException", ex.Message);
        }
    }

    private static StackSlot? ConcatArray(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].Kind != StackKind.Object || a[0].ObjectValue is not VmArray array)
            throw new InvalidOperationException("String.Concat の引数が配列ではありません。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Concat(parts)));
    }


    // ---- System.String / System.Buffer / System.Runtime.CompilerServices.Unsafe
    //      (String IL 実行の全面化で CoreLib IL が呼ぶランタイム内部面) ----

    private const string UnsafeType = "System.Runtime.CompilerServices.Unsafe";

    private static void RegisterStringInternals(IntrinsicRegistry r) {
        // internal static string String.FastAllocateString(nint charCount)
        // CoreLib の InternalCall 面。実 CLR の確保点 (FastAllocateString) と同じ位置で
        // VmString の可変 char バッファを VmHeap 会計つきで確保する。
        // .NET 10 の既知署名 2 件を列挙する (単引数面と MethodTable 付き面。後者の長さは第 2 引数)
        r.RegisterBinding(BindingKey.Static("System.String", "FastAllocateString", "System.IntPtr"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[0].AsInt32)),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.String", "FastAllocateString",
                "System.Runtime.CompilerServices.MethodTable*", "System.IntPtr"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[1].AsInt32)),
            BindingOrigin.InternalCall);
        // static void Buffer.Memmove<T>(ref T destination, ref T source, nuint elementCount)
        // String 構築 IL (InternalSubString 等) が ref char で呼ぶ。実 CLR では JIT intrinsic だが
        // VM はバイト実体 (VmString.Bytes / VmLocallocMemory) 間の memmove として同等意味論を提供する。
        // ジェネリック面のため開いたキー (!!0) で 1 件登録し、T は実行時の具体名で判別する
        r.RegisterBinding(BindingKey.Static("System.Buffer", "Memmove", "!!0&", "!!0&", "System.UIntPtr"),
            static (ctx, a) => MemmoveImpl(ctx, a), BindingOrigin.InternalCall);
        // Unsafe.* ([Intrinsic]: 実 CLR も JIT が IL を置き換える面。CoreLib IL 内では
        // ref 値の byte 単位のアドレス演算として現れるため、バイト実体ポインタで同等に提供する)。
        // .NET 10 の既知署名を列挙する (ジェネリック面は開いたキー !!0 で 1 件ずつ)。
        // 呼出側の具体名 (例: System.Char&) は CallEngine の開いたキー照合で解決される
        foreach (var addParams in new[] {
            new[] { "!!0&", "System.Int32" },
            new[] { "!!0&", "System.IntPtr" },
            new[] { "!!0&", "System.UIntPtr" },
            new[] { "System.Void*", "System.Int32" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "Add", addParams),
                static (ctx, a) => AddImpl(ctx, a, elementStride: true), BindingOrigin.InternalCall);
        foreach (var addByteOffsetParams in new[] {
            new[] { "!!0&", "System.IntPtr" },
            new[] { "!!0&", "System.UIntPtr" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "AddByteOffset", addByteOffsetParams),
                static (ctx, a) => AddImpl(ctx, a, elementStride: false), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "As", "System.Object"),
            // static T Unsafe.As<T>(object source): 参照の型視点再解釈 (アドレス不変)。
            // SZArrayHelper.GetEnumerator 等が this (配列実体) を T[] 視点で受け取る面。
            // 検証は呼出側 (ldflda/ldobj/castclass) に委ね、ここでは素通しする
            // (box も unbox せずそのまま渡す。GetRawData 経路の ldflda は
            // GetRawData バインド側で受ける)
            static (_, a) => a[0],
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "As", "!!0&"),
            // static TTo Unsafe.As<TFrom, TTo>(ref TFrom source): 参照の型視点再解釈
            // (アドレス不変)。バイト実体ポインタは素通り、スロット列参照 (string 内部 char
            // 配列等の配列データ面) も同一アドレスとして素通りさせる — decimal 演算面の
            // scalar IL が Unsafe.As<char, byte>(ref char) をスロット列参照で呼ぶ
            static (_, a) => {
                var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.As");
                if (native is not null)
                    return StackSlot.OfObject(native);
                return StackSlot.OfByRef(slotRef!);
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "AreSame", "!!0&", "!!0&"),
            static (_, a) => StackSlot.OfInt32(AreSameImpl(a) ? 1 : 0), BindingOrigin.InternalCall);
        // ref T Unsafe.AsRef<T>(in T) ([Intrinsic]: ReadOnlySpan(in T&) ctor の IL が呼ぶ。
        // 実 IL はダミーで PlatformNotSupportedException を投げるため、参照をそのまま透過させる同等意味論を提供する)。
        // .NET 10 の既知署名 2 件 (ref 面と void* 面) を列挙する。
        // void* 面には NativeInt スロット (NullRef() の ldc.i4.0+conv.u 等の null ポインタ) が
        // 来る: 0 なら null 参照 (空コンテナ ByRef。未初期化 ByRef と同一の「null byref 相当」
        // 表現。未参照経路では触れられず、参照時は境界検査で fail-closed) を返し、
        // 非 0 (実アドレスの整数化 — VM に実体なし) は fail-closed。
        foreach (var asRefParams in new[] {
            new[] { "!!0&" },
            new[] { "System.Void*" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "AsRef", asRefParams),
                static (_, a) =>
                    a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef
                        ? StackSlot.OfByRef(byRef)
                        : a[0].Kind == StackKind.Object && a[0].ObjectValue is VmNativePointer native
                            ? StackSlot.OfObject(native)
                            : a[0].Kind == StackKind.NativeInt && a[0].Int64Value == 0
                                ? StackSlot.OfByRef(new VmByRef([], 0))
                                : throw new InvalidOperationException(
                                    $"Unsafe.AsRef の引数をスロット列参照として解釈できませんでした ({a[0].Kind})。"),
                BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "SizeOf"),
            static (ctx, _) => StackSlot.OfInt32(SlotStride(ctx.ParamAt(0))), BindingOrigin.InternalCall);
        // static bool Unsafe.IsNullRef<T>(ref T source)
        // ([Intrinsic]: 本家 IL は AsPointer との比較で構成されるが AsPointer 自体が
        // ダミー throw のため VM では null 参照表現 (空コンテナ ByRef) の直接判定で提供する。
        // 未初期化 ByRef ローカル (null byref 相当) も null と判定する
        r.RegisterBinding(BindingKey.Static(UnsafeType, "IsNullRef", "!!0&"),
            static (_, a) => StackSlot.OfInt32(
                a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef &&
                byRef.Container.Length == 0 ? 1 : 0),
            BindingOrigin.InternalCall);
        // static TTo Unsafe.BitCast<TFrom, TTo>(TFrom from)
        // ([Intrinsic]: 実 IL はダミー throw。same-size 値型のビット再解釈として同等意味論を
        // 提供する。Math.Abs(double) の IL が BitConverter.DoubleToUInt64Bits 経由で呼ぶ)。
        // ジェネリック面のため開いたキー (!!0) で登録する
        r.RegisterBinding(BindingKey.Static(UnsafeType, "BitCast", "!!0"),
            static (ctx, a) => BitCastImpl(ctx.ParamAt(0), a[0]), BindingOrigin.InternalCall);
        // static void Unsafe.CopyBlockUnaligned(ref byte destination, ref byte source, uint byteCount)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。String / Span IL がバイト実体コ
        // ピーに使うため Buffer.Memmove 相当の memmove で同等意味論を提供する)。
        // .NET 10 の既知署名 2 件 (ref byte 面と void* 面) を列挙する
        foreach (var copyParams in new[] {
            new[] { "System.Byte&", "System.Byte&", "System.UInt32" },
            new[] { "System.Void*", "System.Void*", "System.UInt32" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "CopyBlockUnaligned", copyParams),
                static (ctx, a) => MemmoveImpl(ctx, a, strideOverride: 1), BindingOrigin.InternalCall);
        // ref T MemoryMarshal.GetArrayDataReference<T>(T[] array) / ref byte (Array array)
        // ([Intrinsic]: 実 IL は配列データ先頭へのランタイム内部参照。IL を実行させると
        // 同名オーバーロードへの自己再帰に落ちるため、VM は要素格納列の先頭スロットへの
        // VmByRef で同等意味論を提供する (Span<T> 構築 IL の GetArrayDataReference 面用))。
        // .NET 10 の既知署名 2 件 (ジェネリック面は開いたキー !!0[] で登録)
        r.RegisterBinding(BindingKey.Static("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "System.Array"),
            static (_, a) => {
                // 配列は通常 Object スロットで来るが、呼出側が配列ローカルを ldarga する
                // 形 (ByRef スロット経由) もあるため展開して受ける
                var value = a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef ? byRef.Slot : a[0];
                return value.ObjectValue is VmArray array
                    ? StackSlot.OfByRef(new VmByRef(array.Elements, 0))
                    : throw new InvalidOperationException(
                        $"MemoryMarshal.GetArrayDataReference の引数が配列ではありません ({value.Kind})。");
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "!!0[]"),
            static (_, a) => {
                var value = a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef ? byRef.Slot : a[0];
                return value.ObjectValue is VmArray array
                    ? StackSlot.OfByRef(new VmByRef(array.Elements, 0))
                    : throw new InvalidOperationException(
                        $"MemoryMarshal.GetArrayDataReference の引数が配列ではありません ({value.Kind})。");
            },
            BindingOrigin.InternalCall);
        // static void Unsafe.WriteUnaligned<T>(ref byte destination, T value) / (void*, T)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。BitConverter.TryWriteBytes 等が
        // Span へのプリミティブ書込に使う。Read/Write 対称で同等意味論を提供する)
        // .NET 10 の既知署名 2 件ずつ (T は開いたキー !!0 で 1 件ずつ)
        foreach (var writeParams in new[] {
            new[] { "System.Byte&", "!!0" },
            new[] { "System.Void*", "!!0" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "WriteUnaligned", writeParams),
                static (ctx, a) => WriteUnalignedImpl(ctx, a), BindingOrigin.InternalCall);
        // static T Unsafe.ReadUnaligned<T>(ref byte source) / (void*)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。対称面)
        foreach (var readParams in new[] {
            new[] { "System.Byte&" },
            new[] { "System.Void*" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "ReadUnaligned", readParams),
                static (ctx, a) => ReadUnalignedImpl(ctx, a), BindingOrigin.InternalCall);
        // static void Unsafe.SkipInit<T>(out T value)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。実 CLR も何もしない
        // (初期化のスキップ) ため no-op が同一意味論)
        r.RegisterBinding(BindingKey.Static(UnsafeType, "SkipInit", "!!0&"),
            static (_, _) => null, BindingOrigin.InternalCall);
    }

    /// <summary>Read/WriteUnaligned の T の要素型名。メソッド型実引数 (具体名) を優先し、
    /// 無い場合のみ宣言パラメータから推定する。ポインタ自体 (void*) や byte 参照は
    /// T ではないため候補から除く (誤った stride での無音破壊より fail-closed を優先)。</summary>
    private static string UnalignedElementName(IntrinsicContext ctx) {
        var methodArg = ctx.MethodTypeArgAt(0);
        if (!string.IsNullOrEmpty(methodArg) && methodArg is not "!!0")
            return methodArg;
        foreach (var i in new[] { 1, 0 }) {
            var name = ctx.ParamAt(i);
            if (string.IsNullOrEmpty(name) || name is "!!0" or "!!0&" or "System.Void*" or "System.Byte&")
                continue;
            return name.EndsWith("&", StringComparison.Ordinal) ? name[..^1] : name;
        }
        throw new InvalidOperationException(
            "Unaligned 面のジェネリック型引数 T を判別できませんでした。");
    }

    private static StackSlot? WriteUnalignedImpl(IntrinsicContext ctx, StackSlot[] a) {
        var elementName = UnalignedElementName(ctx);
        var stride = SlotStride(elementName);
        var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.WriteUnaligned");
        if (slotRef is not null) {
            // スロット列への T 丸ごと書込 (1 要素 = 1 スロット。Span<byte> 以外の
            // Span<T> 参照が来る形。部分重なりは単一スロット代入で正確)
            if (slotRef.Index < 0 || slotRef.Index >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.WriteUnaligned がスロット列の範囲外を参照します (index={slotRef.Index}, 要素数 {slotRef.Container.Length})。");
            slotRef.Container[slotRef.Index] = a[1];
            return null;
        }
        if ((long)native!.ByteOffset + stride > native.Bytes.Length)
            throw new InvalidOperationException(
                $"Unsafe.WriteUnaligned がブロック外を参照します (offset={native.ByteOffset}, {stride} バイト, ブロック {native.Bytes.Length} バイト)。");
        WriteNativeElement(native, native.ByteOffset, a[1], elementName);
        return null;
    }

    private static StackSlot ReadUnalignedImpl(IntrinsicContext ctx, StackSlot[] a) {
        var elementName = UnalignedElementName(ctx);
        var stride = SlotStride(elementName);
        var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.ReadUnaligned");
        if (slotRef is not null) {
            if (slotRef.Index < 0 || slotRef.Index >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.ReadUnaligned がスロット列の範囲外を参照します (index={slotRef.Index}, 要素数 {slotRef.Container.Length})。");
            return slotRef.Container[slotRef.Index];
        }
        if ((long)native!.ByteOffset + stride > native.Bytes.Length)
            throw new InvalidOperationException(
                $"Unsafe.ReadUnaligned がブロック外を参照します (offset={native.ByteOffset}, {stride} バイト, ブロック {native.Bytes.Length} バイト)。");
        return ReadNativeElement(native, native.ByteOffset, elementName);
    }

    /// <summary>ref 引数のスロットから「ポインタの指し先」を解決する。byref 引数は 2 つの
    /// 形で来る (① byref 値そのもの: Span 構造体の byref フィールドを ldfld で取り出した形、
    /// ② byref が格納されたスロットのアドレス: 同フィールドを ldflda した形)。指し先スロットの
    /// 値がさらに byref / ネイティブポインタを包むなら 1 段降りて解釈する。
    /// VmNativePointer (バイト実体) と VmByRef (スロット列: VmArray.Elements 等の
    /// 配列データ面) の両方を返しうる。</summary>
    private static (VmNativePointer? Native, VmByRef? SlotRef) ResolvePointerBase(in StackSlot slot, string face) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmNativePointer directNative)
            return (directNative, null);
        if (slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef outer) {
            var target = outer.Read(); // 指し先スロットの値
            if (target.Kind == StackKind.ByRef && target.ObjectValue is VmByRef inner)
                return (null, inner); // ② アドレス先スロットに格納された byref 値
            if (target.Kind == StackKind.Object && target.ObjectValue is VmNativePointer innerNative)
                return (innerNative, null); // ② アドレス先スロットに格納されたネイティブポインタ
            return (null, outer); // ① byref 値そのもの (外側の参照 = ポインタ)
        }
        throw new InvalidOperationException($"面 {face} の参照引数をポインタとして解釈できませんでした: {slot.Kind}");
    }

    /// <summary>ref 引数のスロットをバイト実体ポインタに読み替える (バイト実体を持つ面用)。
    /// VmNativePointer (ldflda 結果 / localloc ブロック) を素通りさせ、
    /// VmByRef 中に VmNativePointer を包む形も展開する。それ以外は fail-closed。</summary>
    private static VmNativePointer RequirePointer(in StackSlot slot, string face) {
        var (native, _) = ResolvePointerBase(slot, face);
        if (native is not null)
            return native;
        throw new InvalidOperationException(
            $"面 {face} の参照引数をバイト実体ポインタとして解釈できませんでした: {slot.Kind}");
    }

    /// <summary>static TTo Unsafe.BitCast&lt;TFrom, TTo&gt;(TFrom from) の同等意味論。
    /// TFrom (実引数型) のビット列をそのままのバイト幅で TTo として読み替える。スロット表現上
    /// float32/float64 は Float スロット、8 バイト整数は Int64 スロットに正規化されるため、
    /// TFrom 側の型名で出力スロットの種類を決める (TTo の読み手は IL 上のスロット種で解釈する)。</summary>
    private static StackSlot BitCastImpl(string fromTypeName, in StackSlot value) {
        // ByRef 署名 (System.Char& 等) の接尾辞と未実体化の型引数を剥がす
        var name = fromTypeName.EndsWith("&", StringComparison.Ordinal)
            ? fromTypeName[..^1]
            : fromTypeName;
        return name switch {
            "System.Double" => StackSlot.OfInt64(BitConverter.DoubleToInt64Bits(value.DoubleValue)),
            "System.Single" => StackSlot.OfInt32(
                BitConverter.SingleToInt32Bits((float)value.DoubleValue)),
            "System.Int64" or "System.UInt64" => StackSlot.OfFloat(
                BitConverter.Int64BitsToDouble(value.Int64Value)),
            "System.Int32" or "System.UInt32" => StackSlot.OfFloat(
                BitConverter.Int32BitsToSingle((int)value.Int64Value)),
            // 同一スロット表現の型 (bool/char/byte/enum 等) はビット再解釈なしで素通り
            _ => value,
        };
    }

    /// <summary>ジェネリック型引数のスロット上の要素サイズ。未対応の型は fail-closed
    /// (スロット表現に無い参照型 T 等の memmove / ポインタ演算は実行しない)。</summary>
    private static int SlotStride(string typeName) {
        // ByRef 署名 (System.Char& 等) の接尾辞と未実体化の型引数を剥がす
        var name = typeName.EndsWith("&", StringComparison.Ordinal)
            ? typeName[..^1]
            : typeName;
        return name switch {
            "System.Byte" or "System.SByte" or "System.Boolean" => 1,
            "System.Char" or "System.Int16" or "System.UInt16" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
            _ => throw new InvalidOperationException(
                $"面のジェネリック型引数 {typeName} は VM のスロット表現に対応していません (バイト幅が確定しません)。"),
        };
    }

    private static StackSlot? MemmoveImpl(IntrinsicContext ctx, StackSlot[] a, int? strideOverride = null) {
        // T の要素サイズ (elementCount は要素数) を宣言のパラメータ型から解決する。
        // 型名が解決できず呼出 VM 形状に乗らないものは fail-closed にする。
        // strideOverride は CopyBlockUnaligned 等の「byteCount リテラルとバイト長が一致する面」
        // (全型 1 バイト固定 stride) 用。
        var stride = strideOverride ?? SlotStride(ctx.ParamAt(0));
        var (dstNative, dstRef) = ResolvePointerBase(a[0], "Buffer.Memmove");
        var (srcNative, srcRef) = ResolvePointerBase(a[1], "Buffer.Memmove");
        var count = a[2].Kind == StackKind.Int64 ? a[2].Int64Value : a[2].AsInt32;
        if (count < 0)
            throw new InvalidOperationException("Buffer.Memmove の要素数が負です。");
        // スロット列 ↔ スロット列 (Span._reference がローカル/配列スロットを指す形):
        // 1 要素 = 1 スロットとして要素ごとにコピーする
        if (dstRef is not null && srcRef is not null) {
            if (dstRef.Index + count > dstRef.Container.Length ||
                srcRef.Index + count > srcRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (dst index={dstRef.Index}, src index={srcRef.Index}, {count} 要素)。" +
                    $"ブロック {dstRef.Container.Length} / {srcRef.Container.Length} スロット。");
            // memmove 意味論 (重なりがあっても正しく) のため送信側を退避してから書く
            var tmp = new StackSlot[count];
            for (var i = 0; i < count; i++)
                tmp[i] = srcRef.Container[srcRef.Index + i];
            for (var i = 0; i < count; i++)
                dstRef.Container[dstRef.Index + i] = tmp[i];
            return null;
        }
        // バイト実体 ↔ スロット列の混在 (Span<char> が VmString バッファとローカル char を繋ぐ形):
        // 要素をバイト列 LE とスロット値の間で変換する
        if (dstRef is not null || srcRef is not null) {
            var elementName = ctx.ParamAt(0).EndsWith("&", StringComparison.Ordinal)
                ? ctx.ParamAt(0)[..^1] : ctx.ParamAt(0);
            if (srcRef is not null && srcRef.Index + count > srcRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (src index={srcRef.Index}, {count} 要素, ブロック {srcRef.Container.Length} スロット)。");
            if (dstRef is not null && dstRef.Index + count > dstRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (dst index={dstRef.Index}, {count} 要素, ブロック {dstRef.Container.Length} スロット)。");
            if (srcNative is not null && (long)srcNative.ByteOffset + count * stride > srcNative.Bytes.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がブロック外を参照します (src offset={srcNative.ByteOffset}, {count * stride} バイト, ブロック {srcNative.Bytes.Length} バイト)。");
            if (dstNative is not null && (long)dstNative.ByteOffset + count * stride > dstNative.Bytes.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がブロック外を参照します (dst offset={dstNative.ByteOffset}, {count * stride} バイト, ブロック {dstNative.Bytes.Length} バイト)。");
            for (var i = 0; i < count; i++) {
                if (dstRef is not null) {
                    var sourceNative = srcNative ?? throw new InvalidOperationException(
                        "Buffer.Memmove のバイト入力がありません。");
                    // バイト実体 → スロット列
                    dstRef.Container[dstRef.Index + i] =
                        ReadNativeElement(sourceNative, sourceNative.ByteOffset + i * stride, elementName);
                } else {
                    var destinationNative = dstNative ?? throw new InvalidOperationException(
                        "Buffer.Memmove のバイト出力がありません。");
                    var sourceRef = srcRef ?? throw new InvalidOperationException(
                        "Buffer.Memmove のスロット入力がありません。");
                    // スロット列 → バイト実体
                    WriteNativeElement(destinationNative, destinationNative.ByteOffset + i * stride,
                        sourceRef.Container[sourceRef.Index + i], elementName);
                }
            }
            return null;
        }
        var byteCount = count * stride;
        var destination = dstNative ?? throw new InvalidOperationException("Buffer.Memmove の出力がありません。");
        var source = srcNative ?? throw new InvalidOperationException("Buffer.Memmove の入力がありません。");
        // 境界検査: 実 CLR では未定義動作になる参照先の越境は VM では拒否する
        if ((long)destination.ByteOffset + byteCount > destination.Bytes.Length ||
            (long)source.ByteOffset + byteCount > source.Bytes.Length)
            throw new InvalidOperationException(
                $"Buffer.Memmove がブロック外を参照します (dst offset={destination.ByteOffset}, src offset={source.ByteOffset}, " +
                $"{byteCount} バイト, ブロック {destination.Bytes.Length} / {source.Bytes.Length} バイト)。");
        // Array.Copy は同一配列内の重なりを memmove と同じく正しく扱う
        Array.Copy(source.Bytes, source.ByteOffset, destination.Bytes, destination.ByteOffset, byteCount);
        return null;
    }

    /// <summary>バイト実体から 1 要素分のスロット値を読む (LE)。</summary>
    private static StackSlot ReadNativeElement(VmNativePointer memory, int byteOffset, string elementName) {
        var bytes = memory.Bytes;
        return elementName switch {
            "System.Byte" => StackSlot.OfInt32(bytes[byteOffset]),
            "System.SByte" => StackSlot.OfInt32((sbyte)bytes[byteOffset]),
            "System.Boolean" => StackSlot.OfInt32(bytes[byteOffset] != 0 ? 1 : 0),
            "System.Char" or "System.Int16" or "System.UInt16" => StackSlot.OfInt32(
                bytes[byteOffset] | bytes[byteOffset + 1] << 8),
            "System.Int32" or "System.UInt32" or "System.Single" => StackSlot.OfInt32(
                bytes[byteOffset] | bytes[byteOffset + 1] << 8 | bytes[byteOffset + 2] << 16 | bytes[byteOffset + 3] << 24),
            "System.Int64" or "System.UInt64" or "System.Double" => StackSlot.OfInt64(
                BitConverter.ToInt64(bytes, byteOffset)),
            "System.IntPtr" or "System.UIntPtr" => StackSlot.OfNativeInt(BitConverter.ToInt64(bytes, byteOffset)),
            _ => throw new InvalidOperationException(
                $"Buffer.Memmove の要素型 {elementName} はバイト実体 ↔ スロット列の変換に対応していません。"),
        };
    }

    /// <summary>スロット値を 1 要素分バイト実体へ書く (LE)。</summary>
    private static void WriteNativeElement(VmNativePointer memory, int byteOffset, in StackSlot slot, string elementName) {
        var bytes = memory.Bytes;
        switch (elementName) {
            case "System.Byte":
                bytes[byteOffset] = (byte)slot.Int64Value;
                break;
            case "System.SByte":
                bytes[byteOffset] = unchecked((byte)(sbyte)slot.Int64Value);
                break;
            case "System.Boolean":
                bytes[byteOffset] = slot.Int64Value != 0 ? (byte)1 : (byte)0;
                break;
            case "System.Char":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((ushort)slot.Int64Value >> 8);
                break;
            case "System.Int16":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((short)slot.Int64Value >> 8);
                break;
            case "System.UInt16":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((ushort)slot.Int64Value >> 8);
                break;
            case "System.Int32":
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(byteOffset), (int)slot.Int64Value);
                break;
            case "System.UInt32":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(byteOffset), (uint)slot.Int64Value);
                break;
            case "System.Single":
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(byteOffset), (float)slot.DoubleValue);
                break;
            case "System.Int64":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(byteOffset), slot.Int64Value);
                break;
            case "System.UInt64":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(byteOffset), (ulong)slot.Int64Value);
                break;
            case "System.Double":
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(byteOffset), slot.DoubleValue);
                break;
            case "System.IntPtr" or "System.UIntPtr":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(byteOffset), slot.Int64Value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Buffer.Memmove の要素型 {elementName} はバイト実体 ↔ スロット列の変換に対応していません。");
        }
    }

    private static StackSlot AddImpl(IntrinsicContext ctx, StackSlot[] a, bool elementStride) {
        var (native, slotRef) = ResolvePointerBase(a[0], elementStride ? "Unsafe.Add" : "Unsafe.AddByteOffset");
        var offset = a[1].Kind == StackKind.Int64 ? a[1].Int64Value : a[1].AsInt32;
        // スロット列ベース (VmArray.Elements 等の配列データ面): 要素加算はスロット
        // インデックスの移動として表現する (1 要素 = 1 スロット)。バイトオフセット面
        // (AddByteOffset) はスロット列では表現できないため fail-closed
        if (slotRef is not null) {
            if (!elementStride)
                throw new InvalidOperationException(
                    "Unsafe.AddByteOffset はバイト実体を持たないスロット列参照には対応していません。");
            var target = slotRef.Index + (int)offset;
            // Unsafe.Add の pointer arithmetic では、ループ終端の比較用に配列末尾の 1 つ先を
            // 作ることがある。形成だけ許可し、実アクセス時の範囲検査は読み書き面に委ねる。
            if (target < 0 || target > slotRef.Container.Length) {
                System.IO.File.AppendAllText(@"C:\Users\Binary_number\AppData\Local\Temp\opencode\frames.log",
                    $"ADDBOOM base0kind={slotRef.Container[0].Kind} baselen={slotRef.Container.Length} index={slotRef.Index} offset={offset} p0={ctx.ParamAt(0)}\n");
                throw new InvalidOperationException(
                    $"Unsafe.Add の結果がスロット列の範囲外です (index {target}, 要素数 {slotRef.Container.Length})。");
            }
            return StackSlot.OfByRef(new VmByRef(slotRef.Container, target));
        }
        var stride = elementStride ? SlotStride(ctx.ParamAt(0)) : 1;
        return StackSlot.OfObject(new VmNativePointer {
            Memory = native!.Memory,
            ByteOffset = native.ByteOffset + (int)(offset * stride),
        });
    }

    private static bool AreSameImpl(StackSlot[] a) {
        var (leftNative, leftRef) = ResolvePointerBase(a[0], "Unsafe.AreSame");
        var (rightNative, rightRef) = ResolvePointerBase(a[1], "Unsafe.AreSame");
        if (leftNative is not null && rightNative is not null)
            return ReferenceEquals(leftNative.Bytes, rightNative.Bytes) &&
                leftNative.ByteOffset == rightNative.ByteOffset;
        if (leftRef is not null && rightRef is not null)
            return ReferenceEquals(leftRef.Container, rightRef.Container) && leftRef.Index == rightRef.Index;
        // バイト実体とスロット列は別の記憶域なので同一になることはない
        return false;
    }
}
