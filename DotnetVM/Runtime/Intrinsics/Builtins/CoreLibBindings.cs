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
        RegisterRuntimeHelpers(r);
        RegisterObject(r);
        RegisterEnum(r);
        RegisterThreading(r);
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

    // ---- System.String (表現境界により IL 実行が保留されている面の代替) ----

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
    }

    private static StackSlot? ConcatArray(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].Kind != StackKind.Object || a[0].ObjectValue is not VmArray array)
            throw new InvalidOperationException("String.Concat の引数が配列ではありません。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Concat(parts)));
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
