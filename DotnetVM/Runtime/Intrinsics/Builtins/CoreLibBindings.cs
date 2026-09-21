using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// CoreLib 画像 (System.Private.CoreLib) の面に対するランタイムバインド。
/// fail-driven に発見した InternalCall 面 / IL 実行が表現境界で保留されている面を
/// 署名キー + BindingOrigin 付きで登録する。すべての実装は intrinsic 契約
/// (VM オブジェクトモデル正規化 / ヒープ計上 / デバイス・ゲートウェイ経由 I/O) に従う。
/// ジェネリック メソッドは開いたキー (!!n / !n) で 1 件登録し、実引数は
/// IntrinsicContext.ParameterTypeNames 経由で判別する。
/// </summary>
internal static class CoreLibBindings {
    private const string RuntimeHelpersType = "System.Runtime.CompilerServices.RuntimeHelpers";

    public static void RegisterAll(IntrinsicRegistry r) {
        RegisterString(r);
        RegisterStringInternals(r);
        RegisterRuntimeHelpers(r);
        RegisterObject(r);
        RegisterEnum(r);
        RegisterThreading(r);
        RegisterComparableInterfaces(r);
        RegisterPrimitiveToString(r);
        RegisterSystemSr(r);
    }

    // ---- プリミティブ instance ToString (ボックス化仮想呼出面) ----

    /// <summary>プリミティブの instance ToString バインド。String.Concat(object, object)
    /// 等、CoreLib IL 内の `boxedPrimitive?.ToString()` の callvirt が culture 機構依存の
    /// managed IL に仮想解決されるのを受ける面。
    /// 整数 4 型 (Int32/Int64/UInt32/UInt64) は ToString() の無パラメータ面を
    /// VmCoreLibSurfaces (DotnetVM.CoreLib の managed IL) に置換するためここには載せない
    /// (載せると ① が ②/置換面を塞いでしまう)。パラメータ付きオーバーロード
    /// (書式 / IFormatProvider 面) のみ明示キーで委譲を続ける。</summary>
    private static void RegisterPrimitiveToString(IntrinsicRegistry r) {
        // 置換面に任せる整数 4 型: 無パラメータ ToString() / ToString(IFormatProvider) は
        // バインドしない (VmCoreLibSurfaces.Faces の置換 IL が受ける — IFormatProvider は
        // 不変カルチャ固定で無視)。監査闭合テストのシャドウ禁止 (① が (b) を塞ぐ退行の防止)
        foreach (var t in new[] { "System.Int32", "System.UInt32", "System.Int64", "System.UInt64" }) {
            r.RegisterBinding(BindingKey.Instance(t, "ToString", "System.String"),
                (ctx, a) => PrimitiveToString(ctx, a[0], t), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance(t, "ToString", "System.String,System.IFormatProvider"),
                (ctx, a) => PrimitiveToString(ctx, a[0], t), BindingOrigin.Managed);
        }
        // 残りのプリミティブは従来どおり全オーバーロードを委譲 (IFormatProvider は無視 =
        // 常に CurrentCulture。CLR の不変カルチャ書式との差分は culture 面の課題)
        var primitives = new[] {
            "System.Int16", "System.UInt16", "System.SByte", "System.Byte",
            "System.Char", "System.Boolean", "System.Single", "System.Double",
        };
        foreach (var t in primitives)
            r.RegisterBinding(BindingKey.InstanceAnyParams(t, "ToString"),
                (ctx, a) => PrimitiveToString(ctx, a[0], t), BindingOrigin.Managed);
    }

    private static StackSlot PrimitiveToString(IntrinsicContext ctx, in StackSlot value, string typeName) =>
        StackSlot.OfObject(ctx.MakeString(DefaultIntrinsics.FormatPrimitiveToString(value, typeName)));

    // ---- 構築ジェネリック インターフェース (プリミティブ実体化面) ----

    /// <summary>System.IComparable`1&lt;T&gt;::CompareTo と System.IEquatable`1&lt;T&gt;::Equals の
    /// プリミティブ実体化面。ArgumentOutOfRangeException.ThrowIfGreaterThan 等、CoreLib IL の
    /// T: IComparable&lt;T&gt; / IEquatable&lt;T&gt; 制約付き callvirt から呼ばれる。
    /// CLR と同じく T の型で符号 / 浮動小数点の比較規約を替える。</summary>
    private static void RegisterComparableInterfaces(IntrinsicRegistry r) {
        const string comparable = "System.IComparable`1";
        const string equatable = "System.IEquatable`1";
        // (実体化 T 名, 符号なし, 浮動小数点)
        var instantiations = new[] {
            ("System.Int32", false, false), ("System.UInt32", true, false),
            ("System.Int64", false, false), ("System.UInt64", true, false),
            ("System.Int16", false, false), ("System.UInt16", true, false),
            ("System.SByte", false, false), ("System.Byte", true, false),
            ("System.Char", true, false), ("System.Boolean", true, false),
            ("System.Single", false, true), ("System.Double", false, true),
        };
        foreach (var (typeName, unsigned, floating) in instantiations) {
            r.RegisterBinding(BindingKey.Instance(comparable, "CompareTo", typeName),
                (_, a) => StackSlot.OfInt32(ComparePrimitives(a[0], a[1], unsigned, floating)),
                BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance(equatable, "Equals", typeName),
                (_, a) => StackSlot.OfInt32(EqualsPrimitives(a[0], a[1], floating) ? 1 : 0),
                BindingOrigin.Managed);
        }
    }

    private static int ComparePrimitives(in StackSlot x, in StackSlot y, bool unsigned, bool floating) {
        if (floating)
            return x.DoubleValue.CompareTo(y.DoubleValue);
        if (unsigned) {
            var ux = x.Kind == StackKind.Int64 ? (ulong)x.Int64Value : (uint)x.AsInt32;
            var uy = y.Kind == StackKind.Int64 ? (ulong)y.Int64Value : (uint)y.AsInt32;
            return ux.CompareTo(uy);
        }
        var sx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var sy = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return sx.CompareTo(sy);
    }

    private static bool EqualsPrimitives(in StackSlot x, in StackSlot y, bool floating) {
        if (floating)
            return x.DoubleValue == y.DoubleValue;
        var lx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var ly = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return lx == ly;
    }

    // ---- System.Enum ([Intrinsic] 面: 実 CLR も JIT が IL を置き換えるため VM もバインドで提供) ----

    private static void RegisterEnum(IntrinsicRegistry r) {
        // public bool Enum.HasFlag(Enum flag)
        // CoreLib の IL は GetRawData() (boxed 値への生ポインタ) と Unsafe.As によるビット演算で、
        // VM のオブジェクト表現では実行できない。実 CLR でも HasFlag は [Intrinsic] として JIT が
        // 丸ごと置き換える面であり、VM は同一意味論 (両者の生データのビットごと AND) をバインドで
        // 提供する。符号拡張は両辺で同一に行われるため AND 一致判定は生データ比較と等価
        r.RegisterBinding(BindingKey.Instance("System.Enum", "HasFlag", "System.Enum"),
            static (_, a) => StackSlot.OfInt32(HasFlagImpl(a) ? 1 : 0),
            BindingOrigin.Managed);
        // internal CorElementType Enum.InternalGetCorElementType()
        // CoreLib の IL は RuntimeHelpers.GetMethodTable(this) → MethodTable::GetPrimitiveCorElementType()
        // というランタイム内部表現 (MethodTable ポインタ) への直接アクセスで構成されるため実行できない。
        // Enum.IConvertible.GetTypeCode 等の実 IL が依存するリーフとして、box の型 (enum の
        // value__ フィールド型) から同一の CorElementType 値 (ECMA-335 の要素型コード) を返す
        r.RegisterBinding(BindingKey.Instance("System.Enum", "InternalGetCorElementType"),
            static (_, a) => StackSlot.OfInt32(CorElementTypeCodeOf(a[0])),
            BindingOrigin.InternalCall);
        // internal object Enum.GetValue()
        // CoreLib の IL は GetRawData() (box への生ポインタ) を Unsafe.As で基底型幅ごとに読むため
        // 実行できない。Enum の IConvertible EII (Convert.ToXxx(GetValue()) 形) が依存するリーフ。
        // VM では box の生値スロット (Fields[0]) を基底型で再ボックス化して返す (同一意味論)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "GetValue"),
            static (ctx, a) => GetEnumValueImpl(ctx, a[0]),
            BindingOrigin.InternalCall);
    }

    /// <summary>ECMA-335 の CorElementType コード (enum の許容基底型のみ)。</summary>
    private const int CorElementBoolean = 0x02, CorElementChar = 0x03, CorElementI1 = 0x04,
        CorElementU1 = 0x05, CorElementI2 = 0x06, CorElementU2 = 0x07, CorElementI4 = 0x08,
        CorElementU4 = 0x09, CorElementI8 = 0x0a, CorElementU8 = 0x0b;

    private static int CorElementTypeCodeOf(in StackSlot receiver) {
        var underlying = EnumUnderlyingType(receiver);
        return underlying?.FullName switch {
            "System.Boolean" => CorElementBoolean,
            "System.Char" => CorElementChar,
            "System.SByte" => CorElementI1,
            "System.Byte" => CorElementU1,
            "System.Int16" => CorElementI2,
            "System.UInt16" => CorElementU2,
            "System.Int32" => CorElementI4,
            "System.UInt32" => CorElementU4,
            "System.Int64" => CorElementI8,
            "System.UInt64" => CorElementU8,
            // value__ フィールドが未解決の異常画像はスロット幅でフォールバック
            _ => SlotIs64(receiver) ? CorElementI8 : CorElementI4,
        };
    }

    private static bool SlotIs64(in StackSlot slot) {
        var value = slot.ObjectValue is VmBoxedValue box ? box.Fields[0] : default(StackSlot?);
        return value is { Kind: StackKind.Int64 };
    }

    private static StackSlot GetEnumValueImpl(IntrinsicContext ctx, in StackSlot receiver) {
        var box = receiver.ObjectValue as VmBoxedValue ??
            throw new UnhandledGuestException("System.InvalidCastException", null);
        var underlying = EnumUnderlyingType(receiver) ??
            ctx.Types.FindIntrinsicType(SlotIs64(receiver) ? "System.Int64" : "System.Int32") ??
            throw new InvalidOperationException("enum の基底型が解決できません (value__ フィールドが解決不能な画像です)。");
        return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(underlying, [box.Fields[0]])));
    }

    /// <summary>enum box の基底型 (value__ フィールドの型) を辿る。</summary>
    private static VmType? EnumUnderlyingType(in StackSlot receiver) {
        if (receiver.ObjectValue is not VmBoxedValue box)
            return null;
        for (var t = box.Type; t is not null; t = t.BaseType) {
            var field = t.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral);
            if (field?.FieldType is { } fieldType)
                return fieldType;
        }
        return null;
    }

    // ---- System.SR (CoreLib 内部リソース文字列: 例外既定文言の culture インフラ) ----

    // CoreLib の実 IL は例外生成時に SR.Overflow_Int32 等のリソース getter を辿る
    // (GetResourceString → ResourceManager → culture 機構)。VM では例外文言の culture 機構は
    // スコープ外 (不変カルチャ規約) のため、ホストが動いている同一 CoreLib から同一キーの
    // 資源文字列を取得して委譲する (ゲストに文字列として渡すだけ = I/O なし)

    private static void RegisterSystemSr(IntrinsicRegistry r) {
        const string T = "System.SR";
        r.RegisterBinding(BindingKey.Static(T, "GetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "InternalGetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        // リソースキー直返しモードのスイッチ値 (AppContext 機構には依存しない = false 固定)
        r.RegisterBinding(BindingKey.Static(T, "UsingResourceKeys"),
            static (_, _) => StackSlot.OfInt32(0),
            BindingOrigin.Managed);
    }

    private static readonly Func<string, string?> HostCoreLibResource = CreateHostResourceLookup();

    private static Func<string, string?> CreateHostResourceLookup() {
        var sr = typeof(object).Assembly.GetType("System.SR", throwOnError: false);
        var method = sr?.GetMethod("GetResourceString",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (method is null)
            return _ => null; // CoreLib 未配置環境ではキー直返し (UsingResourceKeys 相当) にフォールバック
        return key => {
            try {
                return method.Invoke(null, [key]) as string;
            } catch {
                return null;
            }
        };
    }

    private static StackSlot ResourceString(IntrinsicContext ctx, StackSlot[] a) {
        var key = a[0].ObjectValue as VmString ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
        var text = HostCoreLibResource(key.Value) ?? key.Value;
        return StackSlot.OfObject(ctx.MakeString(text));
    }

    private static bool HasFlagImpl(StackSlot[] a) {
        var thisBox = a[0].ObjectValue as VmBoxedValue;
        var flagBox = a[1].ObjectValue as VmBoxedValue;
        // CLR の型検査 (GetType() != flag.GetType() で ArgumentException) に対応。
        // ボックス化されていない面の呼び出しは形状違反として同様に扱う
        if (thisBox is null || flagBox is null ||
            !string.Equals(thisBox.Type.FullName, flagBox.Type.FullName, StringComparison.Ordinal))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var thisBits = RawBits(thisBox);
        var flagBits = RawBits(flagBox);
        return (thisBits & flagBits) == flagBits;
    }

    private static ulong RawBits(VmBoxedValue box) =>
        box.Fields[0].Kind switch {
            StackKind.Int64 => (ulong)box.Fields[0].Int64Value,
            _ => (ulong)box.Fields[0].AsInt32,
        };

    // ---- System.Object (CoreLib managed IL が内部表現依存面を辿る面の代替) ----

    private static void RegisterObject(IntrinsicRegistry r) {
        // public extern Type Object.GetType()
        // CoreLib の managed IL は GetMethodTable → MethodTable::AuxiliaryData という
        // ランタイム内部表現への直接アクセスを辿るため、VM では実行時型ファサード
        // (VmRuntimeObject) を返すバインドで優先提供する (① ランタイムバインドが ② IL より先)
        r.RegisterBinding(BindingKey.Instance("System.Object", "GetType"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.Managed);
    }

    // ---- System.Threading.Monitor (VM は単一スレッド実行のため競合なしのロック面) ----

    private static void RegisterThreading(IntrinsicRegistry r) {
        // CoreLib Monitor の InternalCall 面 4 件 (Enter/Exit の IL から呼ばれる)。
        // VM にはスレッドがなく同期ブロックもないため、常に即時取得・競合なしで即解放する:
        //   TryEnter_FastPath(obj)              → true (競合しないので FastPath で取得成功。
        //                                                  bool 返し (Enter IL が brtrue で分岐))
        //   TryEnter_FastPath_WithTimeout(...)  → true (待ちなしで取得成功)
        //   Exit_FastPath(obj)                  → void (保持解除は no-op)
        //   IsEnteredNative(obj)                → false (ロックを保持しないモデルと整合)
        // パラメータ構成は CoreLib のバージョンで揺れうるため、面名ごとの全引数一致キーにする
        const string T = "System.Threading.Monitor";
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "TryEnter_FastPath"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "TryEnter_FastPath_WithTimeout"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Exit_FastPath"),
            static (_, _) => null, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "IsEnteredNative"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
    }

    // ---- System.String (culture 依存面のバインド: 署名キーで ② IL より先に解決される) ----

    private static void RegisterString(IntrinsicRegistry r) {
        const string T = "System.String";
        // 4 項以上の連結は Roslyn が String.Concat(params string[]) / (params object[]) に
        // コンパイルする。要素は CLR 規約どおりフォーマットする (null は空文字列)
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.String[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.Object[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        // 比較面 (legacy intrinsic 未登録)。CLR の 2 引数 Compare は CurrentCulture 比較
        static VmString Str(StackSlot[] a, int i) =>
            a[i].ObjectValue as VmString ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        r.RegisterBinding(BindingKey.Static(T, "Compare", "System.String", "System.String"),
            static (_, a) => StackSlot.OfInt32(string.Compare(Str(a, 0).Value, Str(a, 1).Value, StringComparison.CurrentCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "CompareOrdinal", "System.String", "System.String"),
            static (_, a) => StackSlot.OfInt32(string.CompareOrdinal(Str(a, 0).Value, Str(a, 1).Value)),
            BindingOrigin.Managed);
        // 検索 / 置換面。CoreLib IL は SpanHelpers の SIMD intrinsic (Vector128/256 面) に落ちるため
        // VM のスロット表現では実行できない。CLR 同一の意味論 (1 引数 IndexOf/LastIndexOf/
        // StartsWith/EndsWith は CurrentCulture、Contains/Replace は Ordinal) をバインドで優先提供する
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", "System.Char"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.IndexOf(Ch(a, 1))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.IndexOf(Str(a, 1).Value, StringComparison.CurrentCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", "System.Char"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.LastIndexOf(Ch(a, 1))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.LastIndexOf(Str(a, 1).Value, StringComparison.CurrentCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Contains", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.Contains(Str(a, 1).Value) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "StartsWith", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.StartsWith(Str(a, 1).Value, StringComparison.CurrentCulture) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "EndsWith", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.EndsWith(Str(a, 1).Value, StringComparison.CurrentCulture) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Replace", "System.String", "System.String"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.Replace(Str(a, 1).Value, Str(a, 2).Value))),
            BindingOrigin.Managed);
        // 1 文字の文字列生成 (InternalCall 面。char.ToString() の実 IL が string.CreateFromChar
        // を辿る — VmString 生成は既存の文字列内部面と同一経路)
        r.RegisterBinding(BindingKey.Static(T, "CreateFromChar", "System.Char"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Ch(a, 0).ToString())),
            BindingOrigin.InternalCall);
        // String.Format / String.Split (culture 依存面 / SpanHelpers 依存の表現境界面)。
        // CoreLib IL (CultureInfo / ReadOnlySpan<char> の内部表現) に落とさず、既存の統合面
        // (DefaultIntrinsics.FormatImpl / SplitImpl — CLR 同等意味論) をバインドで優先提供する
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Format"),
            DefaultIntrinsics.FormatImpl, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceAnyParams(T, "Split"),
            DefaultIntrinsics.SplitImpl, BindingOrigin.Managed);
    }

    private static char Ch(StackSlot[] a, int i) => (char)a[i].AsInt32;

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
        // internal static extern string String.FastAllocateString(int charCount)
        // CoreLib の InternalCall 面。実 CLR の確保点 (FastAllocateString) と同じ位置で
        // VmString の可変 char バッファを VmHeap 会計つきで確保する
        r.RegisterBinding(BindingKey.StaticAnyParams("System.String", "FastAllocateString"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[0].AsInt32)),
            BindingOrigin.InternalCall);
        // static void Buffer.Memmove<T>(ref T destination, ref T source, nuint elementCount)
        // String 構築 IL (InternalSubString 等) が ref char で呼ぶ。実 CLR では JIT intrinsic だが
        // VM はバイト実体 (VmString.Bytes / VmLocallocMemory) 間の memmove として同等意味論を提供する
        r.RegisterBinding(BindingKey.StaticAnyParams("System.Buffer", "Memmove"),
            static (ctx, a) => MemmoveImpl(ctx, a), BindingOrigin.InternalCall);
        // Unsafe.* ([Intrinsic]: 実 CLR も JIT が IL を置き換える面。CoreLib IL 内では
        // ref 値の byte 単位のアドレス演算として現れるため、バイト実体ポインタで同等に提供する)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "Add"),
            static (ctx, a) => AddImpl(ctx, a, elementStride: true), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AddByteOffset"),
            static (ctx, a) => AddImpl(ctx, a, elementStride: false), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "As"),
            static (_, a) => StackSlot.OfObject(RequirePointer(a[0], "Unsafe.As")),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AreSame"),
            static (_, a) => StackSlot.OfInt32(AreSameImpl(a) ? 1 : 0), BindingOrigin.InternalCall);
        // ref TTo Unsafe.AsRef<T>(void*) / ref readonly T Unsafe.AsRef<T>(in T)
        // ([Intrinsic]: ReadOnlySpan(in T&) ctor の IL が呼ぶ。実 IL はダミーで
        // PlatformNotSupportedException を投げるため、参照をそのまま透過させる同等意味論を提供する)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AsRef"),
            static (_, a) =>
                a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef
                    ? StackSlot.OfByRef(byRef)
                    : throw new InvalidOperationException(
                        $"Unsafe.AsRef の引数をスロット列参照として解釈できませんでした ({a[0].Kind})。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "SizeOf"),
            static (ctx, _) => StackSlot.OfInt32(SlotStride(ctx.ParamAt(0))), BindingOrigin.InternalCall);
        // static TTo Unsafe.BitCast<TFrom, TTo>(TFrom from)
        // ([Intrinsic]: 実 IL はダミー throw。same-size 値型のビット再解釈として同等意味論を
        // 提供する。Math.Abs(double) の IL が BitConverter.DoubleToUInt64Bits 経由で呼ぶ)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "BitCast"),
            static (ctx, a) => BitCastImpl(ctx.ParamAt(0), a[0]), BindingOrigin.InternalCall);
        // ref T MemoryMarshal.GetArrayDataReference<T>(T[] array) / ref byte (Array array)
        // ([Intrinsic]: 実 IL は配列データ先頭へのランタイム内部参照。IL を実行させると
        // 同名オーバーロードへの自己再帰に落ちるため、VM は要素格納列の先頭スロットへの
        // VmByRef で同等意味論を提供する (Span<T> 構築 IL の GetArrayDataReference 面用)
        r.RegisterBinding(BindingKey.StaticAnyParams("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference"),
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
            var target = outer.Slot; // 指し先スロットの値
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

    private static StackSlot? MemmoveImpl(IntrinsicContext ctx, StackSlot[] a) {
        // T の要素サイズ (elementCount は要素数) を宣言上のパラメータ型名から取る。
        // 型引数が解決できない呼出は VM 表現に落とせないため fail-closed にする
        var stride = SlotStride(ctx.ParamAt(0));
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
                    // バイト実体 → スロット列
                    dstRef.Container[dstRef.Index + i] =
                        ReadNativeElement(srcNative!, srcNative.ByteOffset + i * stride, elementName);
                } else {
                    // スロット列 → バイト実体
                    WriteNativeElement(dstNative!, dstNative.ByteOffset + i * stride,
                        srcRef!.Container[srcRef.Index + i], elementName);
                }
            }
            return null;
        }
        var byteCount = count * stride;
        // 境界検査: 実 CLR では未定義動作になる参照先の越境は VM では拒否する
        if ((long)dstNative!.ByteOffset + byteCount > dstNative.Bytes.Length ||
            (long)srcNative!.ByteOffset + byteCount > srcNative.Bytes.Length)
            throw new InvalidOperationException(
                $"Buffer.Memmove がブロック外を参照します (dst offset={dstNative.ByteOffset}, src offset={srcNative.ByteOffset}, " +
                $"{byteCount} バイト, ブロック {dstNative.Bytes.Length} / {srcNative.Bytes.Length} バイト)。");
        // Array.Copy は同一配列内の重なりを memmove と同じく正しく扱う
        Array.Copy(srcNative.Bytes, srcNative.ByteOffset, dstNative.Bytes, dstNative.ByteOffset, byteCount);
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
            if (target < 0 || target >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.Add の結果がスロット列の範囲外です (index {target}, 要素数 {slotRef.Container.Length})。");
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

    // ---- System.Runtime.CompilerServices.RuntimeHelpers (InternalCall 面) ----

    private static void RegisterRuntimeHelpers(IntrinsicRegistry r) {
        // internal static RuntimeType GetMethodTable(object obj)
        // CoreLib 上の IL は「ldarg.0; call 自分自身; ret」のダミー自己再帰本体で、実 CLR では
        // JIT が [Intrinsic] として置き換えるため決して実行されない。VM では実行時型ファサード
        // (VmRuntimeObject) を返すことで同意味論を提供する (未バインドだと無限再帰に落ちる)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetMethodTable", "System.Object"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.InternalCall);
        // internal static bool IsReferenceOrContainsReferences(RuntimeType type)
        // (ジェネリック ラッパー IsReferenceOrContainsReferences<T>() の IL から呼ばれる)。
        // 「参照型であるか、参照型フィールドを再帰的に含むか」を VM 型モデルで判定する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences", "System.RuntimeType"),
            static (_, a) => StackSlot.OfInt32(
                a[0].ObjectValue is VmRuntimeObject runtimeType && ContainsReferences(runtimeType.Target) ? 1 : 0),
            BindingOrigin.InternalCall);
        // ジェネリック ラッパー IsReferenceOrContainsReferences<T>() (MethodSpec 面)。
        // 実 IL がランタイム内部表現に直接依存するため IL 実行させず、T の実引数型名
        // (MethodSpec 解決で置換される。未解決なら開いた名 !!0) から VM 型モデルで判定する。
        // 正確なキーが先に照合され、この AnyParams 面は generic ラッパー (キー不一致) のみ受ける
        r.RegisterBinding(BindingKey.StaticAnyParams(RuntimeHelpersType, "IsReferenceOrContainsReferences"),
            static (ctx, _) => {
                // ジェネリック ラッパーは値パラメータ 0 個のため T はメソッド型実引数で来る
                var name = ctx.MethodTypeArgAt(0);
                var type = name is "" or "!!0" ? null
                    : (VmType?)ctx.Types.FindIntrinsicType(name) ?? ctx.Types.FindTypeByFullName(name);
                // 未解決は安全側 (参照を含む = 遅い経路を選ぶだけなので過大判定は安全)
                return StackSlot.OfInt32(type is null || ContainsReferences(type) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>CLR の IsReferenceOrContainsReferences 意味論: 型が参照型であるか、
    /// 参照型フィールド (再帰的に) を含むか。プリミティブ / enum は false。
    /// 未解決フィールドは安全側 (true) に倒す (呼出側は遅い経路を選ぶだけなので過大判定は安全)。</summary>
    private static bool ContainsReferences(VmType type) {
        var definition = type is VmConstructedType constructed ? constructed.Definition : type;
        if (definition is VmArrayType or VmMultiDimArrayType or VmByRefType)
            return true;
        if (!definition.IsValueType)
            return true;
        if (definition.FullName == "System.Enum" || definition.IsEnum ||
            VmPrimitiveTypes.IsSlotPrimitive(definition.FullName))
            return false;
        var visited = new HashSet<VmType>();
        return ContainsFields(definition, visited);
    }

    private static bool ContainsFields(VmType definition, HashSet<VmType> visited) {
        if (!visited.Add(definition))
            return false; // 循環するフィールド構造 (異常画像) は打ち切り
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            var fieldType = field.FieldType;
            if (fieldType is null)
                return true; // 未解決フィールドは安全側に倒す
            // 構築ジェネリック型のフィールド (!0 等) は実引数で置換して判定する
            if (definition is VmConstructedType constructed)
                fieldType = GenericSubstitutor.Substitute(fieldType,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
            if (ContainsReferences(fieldType))
                return true;
        }
        return false;
    }
}
