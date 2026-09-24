using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
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
        // 値パラメータ 0 個のため無引数の正確キーで登録する (AnyParams 不要)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences"),
            static (ctx, _) => {
                // ジェネリック ラッパーは値パラメータ 0 個のため T はメソッド型実引数で来る
                var name = ctx.MethodTypeArgAt(0);
                var type = name is "" or "!!0" ? null
                    : (VmType?)ctx.Types.FindIntrinsicType(name) ?? ctx.Types.FindTypeByFullName(name);
                // 未解決は安全側 (参照を含む = 遅い経路を選ぶだけなので過大判定は安全)
                return StackSlot.OfInt32(type is null || ContainsReferences(type) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static T? IsBitwiseEquatable<T>()  ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic)。
        // SpanHelpers.SequenceEqual 等が T を bitwise 比較可能か判定する面 (typeof(T) の呼び出し
        // はメソッド型実引数で判別可能)。プリミティブ数値 / char / bool 系のみ true (Span IL の
        // 早抜け経路に誘導。非 primitive は false = 比較 delegate 経路にフォールバック)。
        // 値パラメータ 0 個のため無引数の正確キーで登録する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsBitwiseEquatable"),
            static (ctx, _) => {
                var name = ctx.MethodTypeArgAt(0);
                var primitive = name is "System.Byte" or "System.SByte" or "System.Char" or "System.Int16"
                    or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64"
                    or "System.UInt64" or "System.Boolean" or "System.IntPtr" or "System.UIntPtr"
                    or "System.Single" or "System.Double";
                return StackSlot.OfInt32(primitive ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static bool IsKnownConstant<T>(T value)  ([Intrinsic]):
        // 本家 IL はダミー throw の JIT intrinsic。VM は JIT 定数畳み込みを持たないため false を返す
        // (未定義でも呼出 IL の fallback 分岐が動くが、false 固定にして恒常経路に誘導する)。
        // .NET 10 の既知署名 4 件を列挙する (非ジェネリック 3 overload + ジェネリック面は開いたキー)
        foreach (var knownConstantParams in new[] {
            new[] { "System.Char" },
            new[] { "System.String" },
            new[] { "System.Type" },
            new[] { "!!0" },
        })
            r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsKnownConstant", knownConstantParams),
                static (_, _) => StackSlot.OfInt32(0),
                BindingOrigin.InternalCall);
        // static ReadOnlySpan<T> RuntimeHelpers.CreateSpan<T>(RuntimeFieldHandle fieldHandle)
        // 本家 IL は GetSpanDataFrom (MethodTable / ネイティブ型ハンドルのランタイム内部) を
        // 辿るため VM 表現境界。ldtoken Field の結果 (VmFieldRvaData の初期データ列) から
        // ReadOnlySpan<T> 構造体値 (_reference + _length) を直接構築する。
        // HashHelpers の素数表等の FieldRVA 静的テーブル面が使う。T はメソッド型実引数で判別する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "CreateSpan", "System.RuntimeFieldHandle"),
            static (ctx, a) => CreateSpanImpl(ctx, a),
            BindingOrigin.InternalCall);
        // static ref byte RuntimeHelpers.GetRawData(object obj)
        // 本家 IL は box への生ポインタを Unsafe.As で基底型幅ごとに読む。
        // VM では box / インスタンスの先頭フィールドへの ByRef、文字列はバイト実体で提供する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetRawData", "System.Object"),
            static (_, a) => a[0].ObjectValue switch {
                VmBoxedValue box => StackSlot.OfByRef(new VmByRef(box.Fields, 0)),
                VmClassInstance instance when instance.Fields.Length > 0 =>
                    StackSlot.OfByRef(new VmByRef(instance.Fields, 0)),
                VmString str => StackSlot.OfObject(new VmNativePointer {
                    Memory = str.PointerMemory,
                    ByteOffset = VmString.CharDataByteOffset,
                }),
                _ => throw new InvalidOperationException(
                    $"RuntimeHelpers.GetRawData の引数を生データ参照にできません ({a[0].Kind})。"),
            },
            BindingOrigin.InternalCall);
    }

    private static void RegisterMemoryMarshal(IntrinsicRegistry r) {
        // static ReadOnlySpan<T> MemoryMarshal.CreateReadOnlySpan(ref T reference, int length)
        // .NET 10 uses this intrinsic when params ReadOnlySpan<T> overloads are selected (notably
        // Task.WaitAll/WhenAll/WhenAny).  The VM span representation is a value containing the
        // source byref and logical length; no host span or raw pointer escapes the VM.
        r.RegisterBinding(BindingKey.Static(
                "System.Runtime.InteropServices.MemoryMarshal", "CreateReadOnlySpan", "!!0&", "System.Int32"),
            static (ctx, a) => CreateReadOnlySpanFromReference(ctx, a),
            BindingOrigin.InternalCall);
    }

    private static StackSlot CreateReadOnlySpanFromReference(IntrinsicContext ctx, StackSlot[] args) {
        if (args[0].ObjectValue is not VmByRef reference)
            throw new InvalidOperationException("MemoryMarshal.CreateReadOnlySpan の参照引数が ByRef ではありません。");
        var length = args[1].AsInt32;
        if (length < 0 || reference.Index + length > reference.Container.Length)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "length");

        var elementName = ctx.MethodTypeArgAt(0);
        if (string.IsNullOrEmpty(elementName) || elementName is "!!0")
            throw new InvalidOperationException("MemoryMarshal.CreateReadOnlySpan の型引数 T を判別できませんでした。");
        var elementType = FindAnyType(ctx, elementName)
            ?? throw new InvalidOperationException($"span の要素型 {elementName} を解決できません。");
        var definition = FindAnyType(ctx, "System.ReadOnlySpan`1")
            ?? throw new InvalidOperationException("System.ReadOnlySpan`1 がロードされていません。");
        var spanType = new VmConstructedType { Definition = definition, TypeArguments = [elementType] };
        return StackSlot.OfValueType(new VmStructValue(spanType,
            [StackSlot.OfByRef(reference), StackSlot.OfInt32(length)], [elementType]));
    }

    private static StackSlot CreateSpanImpl(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].ObjectValue is not VmFieldRvaData rva)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の引数がフィールド RVA データではありません ({a[0].Kind})。");
        var tName = ctx.MethodTypeArgAt(0);
        if (string.IsNullOrEmpty(tName) || tName is "!!0")
            throw new InvalidOperationException(
                "RuntimeHelpers.CreateSpan のジェネリック型引数 T を判別できませんでした。");
        var stride = SlotStride(tName);
        var bytes = rva.Data.ToArray();
        if (bytes.Length % stride != 0)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の初期データ ({bytes.Length} バイト) が要素幅 {stride} で割り切れません。");
        var t = FindAnyType(ctx, tName)
            ?? throw new InvalidOperationException($"RuntimeHelpers.CreateSpan の要素型 {tName} を解決できません。");
        var def = ctx.Types.FindTypeByFullName("System.ReadOnlySpan`1") as VmClassType
            ?? FindAnyType(ctx, "System.ReadOnlySpan`1") as VmClassType
            ?? throw new InvalidOperationException("System.ReadOnlySpan`1 がロードされていません。");
        if (def.GenericParamCount != 1)
            throw new InvalidOperationException("System.ReadOnlySpan`1 の型引数個数が不正です。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [t] };
        var native = new VmNativePointer {
            Memory = new VmLocallocMemory { Bytes = bytes },
            ByteOffset = 0,
        };
        // フィールド配置は GetLayout (正規レイアウト) に合わせる
        // (FieldLocation 等の読み側と一致させるため宣言順の独自採番はしない)
        var layout = new ObjectModel().GetLayout(def);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var field in def.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            fields[layout[field]] = field.Name switch {
                "_reference" => StackSlot.OfObject(native),
                "_length" => StackSlot.OfInt32(bytes.Length / stride),
                _ => throw new InvalidOperationException(
                    $"System.ReadOnlySpan`1 の未知フィールド {field.Name} があります。"),
            };
        }
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }


    // ---- System.Runtime.Intrinsics.Vector64/128/256/512 (JIT intrinsic 判定面) ----

    /// <summary>本家 get_IsHardwareAccelerated は [Intrinsic] 付きのダミー自己再帰 IL
    /// (return IsHardwareAccelerated;) で、実 CLR では JIT がハードウェア判定へ置換するため
    /// 決して実行されない (RuntimeHelpers.GetMethodTable と同型)。VM は SIMD 値型
    /// (Vector128&lt;T&gt; 等の 16/32/64 バイト inline 表現) を持たないため SIMD パスの IL を
    /// 実行できず、true を返すと SpanHelpers 等が SIMD 面で fail-closed に落ちる。
    /// そこで false を返して scalar フォールバック IL に誘導する (string SCI overload 群が
    /// 同一結果を得る経路)。SIMD 演算面自体は未バインド → fail-closed。ゲストがこの面を
    /// 直接観測した場合の実 x64 CLR (true) との既知差異は SIMD 表現境界として監査表に記載。</summary>
    private static void RegisterVectorIntrinsics(IntrinsicRegistry r) {
        foreach (var vectorType in new[] {
            "System.Runtime.Intrinsics.Vector64",
            "System.Runtime.Intrinsics.Vector128",
            "System.Runtime.Intrinsics.Vector256",
            "System.Runtime.Intrinsics.Vector512",
        }) {
            r.RegisterBinding(BindingKey.Static(vectorType, "get_IsHardwareAccelerated"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
        // X86.Sse2.get_IsSupported は同じくダミー自己再帰 IL (return IsSupported;) で、
        // SpanHelpers.IndexOfValueType 等が PackedSpanHelpers 経由で参照する。
        // false を返して scalar フォールバック IL に誘導する (Vector 面と同一方針)。
        // X86 の他面 (Sse41 等) は到達した面から順次追加する (fail-driven)。
        r.RegisterBinding(BindingKey.Static("System.Runtime.Intrinsics.X86.Sse2", "get_IsSupported"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        foreach (var x86Type in new[] {
            "System.Runtime.Intrinsics.X86.Avx2",
            "System.Runtime.Intrinsics.X86.Lzcnt",
        }) {
            r.RegisterBinding(BindingKey.Static(x86Type, "get_IsSupported"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
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
