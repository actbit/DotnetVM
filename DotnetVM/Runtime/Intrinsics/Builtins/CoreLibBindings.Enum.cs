using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
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
        // public override string Enum.ToString()
        // CoreLib の IL は FormatFeatures / CultureInfo 面を辿る culture 機構依存のため
        // 表現境界。リテラル表 (Constant テーブル) から G 書式名を解決する
        // (DayOfWeek 等の BCL enum もゲスト enum も同一経路。culture-out-of-scope)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format: null, ctx)));
            },
            BindingOrigin.InternalCall);
        // public string Enum.ToString(string? format)
        // 実 IL は castclass RuntimeType + 生ポインタ + ジェネリックヘルパーのため
        // VM 表現境界。リテラル表から G/D/X/F 書式を解決する。不正書式は FormatException
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString", "System.String"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                var format = (a[1].ObjectValue as VmString)?.Value;
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format, ctx)));
            },
            BindingOrigin.InternalCall);
        // string IFormattable.ToString(string?, IFormatProvider?) の明示的実装面
        // (短名 ToString の 2 引数 overload として解決される)。
        // string.Join 等が要素書式に辿る。EII 経路の ① 照合で受ける (ToString(string) と同一意味論)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString",
                "System.String", "System.IFormatProvider"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                var format = (a[1].ObjectValue as VmString)?.Value;
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format, ctx)));
            },
            BindingOrigin.InternalCall);
        // public static T Enum.Parse<T>(string) / (string, bool)
        // 実 IL は RuntimeType リフレクション (GetFields) のため VM 表現境界。
        // リテラル表の名前照合 (ignoreCase 対応) + 数値面で同一意味論を提供する。
        // T はメソッド型実引数で判別する
        r.RegisterBinding(BindingKey.StaticWithReturn("System.Enum", "Parse", "!!0", ["System.String"]),
            static (ctx, a) => EnumParseImpl(ctx, a[0], ignoreCase: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn("System.Enum", "Parse", "!!0",
                ["System.String", "System.Boolean"]),
            static (ctx, a) => EnumParseImpl(ctx, a[0], a[1].AsInt32 != 0),
            BindingOrigin.Managed);
        // public static bool Enum.TryParse<T>(string?, out T) / (string?, bool, out T)
        r.RegisterBinding(BindingKey.Static("System.Enum", "TryParse", "System.String", "!!0&"),
            static (ctx, a) => EnumTryParseImpl(ctx, a, ignoreCase: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "TryParse", "System.String", "System.Boolean", "!!0&"),
            static (ctx, a) => EnumTryParseImpl(ctx, a, a[1].AsInt32 != 0),
            BindingOrigin.Managed);
        // public static string[] Enum.GetNames<T>() / T[] Enum.GetValues<T>()
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetNames"),
            static (ctx, a) => {
                _ = a;
                var literals = EnumLiteralsOf(ResolveEnumType(ctx, null));
                return StackSlot.OfObject(ctx.MakeStringArray(literals.Select(l => l.Name).ToList()));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetValues"),
            static (ctx, a) => {
                _ = a;
                var enumType = ResolveEnumType(ctx, null);
                var literals = EnumLiteralsOf(enumType);
                var elementType = (VmType)enumType;
                var arrayType = new VmArrayType { ElementType = elementType };
                try {
                    arrayType.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
                } catch {
                }
                var elements = new StackSlot[literals.Count];
                for (var i = 0; i < literals.Count; i++)
                    elements[i] = StackSlot.OfObject(ctx.Heap.Allocate(
                        new VmBoxedValue(enumType, [RawSlot(literals[i].Bits, enumType)])));
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(arrayType, elements)));
            },
            BindingOrigin.Managed);
        // public static bool Enum.IsDefined(Type, object?) / (Type, string?)
        r.RegisterBinding(BindingKey.Static("System.Enum", "IsDefined", "System.Type", "System.Object"),
            static (_, a) => StackSlot.OfInt32(EnumIsDefined(a[0], a[1]) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "IsDefined", "System.Type", "System.String"),
            static (_, a) => StackSlot.OfInt32(EnumIsDefined(a[0], a[1]) ? 1 : 0),
            BindingOrigin.Managed);
        // public static string? Enum.GetName(Type, object?)
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetName", "System.Type", "System.Object"),
            static (ctx, a) => EnumGetName(a[0], a[1]) is { } name
                ? StackSlot.OfObject(ctx.MakeString(name)) : StackSlot.Null,
            BindingOrigin.Managed);
    }

    /// <summary>enum box の生ビット列 (ulong 正規化)。</summary>
    private static ulong RawBitsOf(VmBoxedValue box) => box.Fields[0].Kind switch {
        StackKind.Int64 => (ulong)box.Fields[0].Int64Value,
        StackKind.Float => (ulong)BitConverter.DoubleToInt64Bits(box.Fields[0].DoubleValue),
        _ => (ulong)(uint)box.Fields[0].AsInt32,
    };

    /// <summary>ジェネリック Enum 面の対象 enum 型 (メソッド型実引数 T)。
    /// 引数で直接型が渡る面 (Type, object) は typeSlot から取る。</summary>
    private static VmClassType ResolveEnumType(IntrinsicContext ctx, StackSlot? typeSlot) {
        VmType? target = null;
        if (typeSlot is { } slot && slot.ObjectValue is VmRuntimeObject runtime)
            target = runtime.Target;
        else {
            var name = ctx.MethodTypeArgAt(0);
            if (!string.IsNullOrEmpty(name) && name is not "!!0")
                target = FindAnyType(ctx, name);
        }
        if (target is VmClassType cls && (cls.IsEnum || cls.FullName == "System.Enum"))
            return cls;
        throw new UnhandledGuestException("System.ArgumentException", null);
    }

    private sealed record EnumLiteral(string Name, ulong Bits);

    /// <summary>enum 型のリテラル表 (名前 + 生ビット)。Constant テーブルから読む。</summary>
    private static List<EnumLiteral> EnumLiteralsOf(VmClassType? enumType) {
        var result = new List<EnumLiteral>();
        if (enumType is null)
            return result;
        var image = enumType.Image;
        var rowCount = image.Tables.GetRowCount(TableKind.Constant);
        foreach (var field in enumType.Fields) {
            if (!field.IsLiteral)
                continue;
            for (var rid = 1; rid <= rowCount; rid++) {
                var parent = image.Tables.DecodeCoded(TableKind.Constant, rid, 2, CodedIndexKind.HasConstant);
                if (parent.Table != TableKind.Field || parent.Rid != field.FieldRid)
                    continue;
                var blob = image.GetBlob(image.Tables.GetRowIndex(TableKind.Constant, rid, 3));
                result.Add(new EnumLiteral(field.Name, ReadConstantBits(blob)));
                break;
            }
        }
        return result;
    }

    private static ulong ReadConstantBits(System.ReadOnlySpan<byte> blob) => blob.Length switch {
        1 => blob[0],
        2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob),
        4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob),
        _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob),
    };

    /// <summary>enum 基底型の (バイト幅, 符号付き)。</summary>
    private static (int Size, bool Signed) EnumLayout(VmClassType? enumType) {
        var name = enumType?.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral)?.FieldType?.FullName;
        return name switch {
            "System.SByte" => (1, true),
            "System.Byte" or "System.Boolean" => (1, false),
            "System.Int16" => (2, true),
            "System.UInt16" or "System.Char" => (2, false),
            "System.Int32" => (4, true),
            "System.UInt32" => (4, false),
            "System.Int64" => (8, true),
            _ => (8, false),
        };
    }

    private static bool HasFlagsAttribute(VmClassType? enumType) {
        if (enumType is null)
            return false;
        var image = enumType.Image;
        var count = image.Tables.GetRowCount(TableKind.CustomAttribute);
        for (var rid = 1; rid <= count; rid++) {
            var parent = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 0, CodedIndexKind.HasCustomAttribute);
            if (parent.Table != TableKind.TypeDef || parent.Rid != enumType.TypeDefRid)
                continue;
            var ctor = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
            string? ownerName = null;
            if (ctor.Table == TableKind.MemberRef) {
                // 属性 .ctor の MemberRef (親は TypeRef / TypeDef のいずれか)
                var memberParent = image.Tables.DecodeCoded(
                    TableKind.MemberRef, ctor.Rid, 0, CodedIndexKind.MemberRefParent);
                if (memberParent.Table == TableKind.TypeRef) {
                    var (ns, n, _) = image.GetTypeRefName(memberParent.Rid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                } else if (memberParent.Table == TableKind.TypeDef) {
                    var (ns, n) = image.GetTypeDefName(memberParent.Rid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
            } else if (ctor.Table == TableKind.MethodDef) {
                // 同一画像の属性 .ctor 直接参照
                var ownerRid = FindMethodOwner(image, ctor.Rid);
                if (ownerRid != 0) {
                    var (ns, n) = image.GetTypeDefName(ownerRid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
            }
            if (ownerName == "System.FlagsAttribute")
                return true;
        }
        return false;
    }

    private static int FindMethodOwner(AssemblyImage image, int methodRid) {
        var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var methodCount = image.Tables.GetRowCount(TableKind.MethodDef);
            _ = methodCount;
            // MethodList 範囲で所有 TypeDef を特定する (簡易走査)
            var start = image.Tables.GetRowIndex(TableKind.TypeDef, typeRid, 5);
            var end = typeRid < typeDefs
                ? image.Tables.GetRowIndex(TableKind.TypeDef, typeRid + 1, 5)
                : image.Tables.GetRowCount(TableKind.MethodDef) + 1;
            if (methodRid >= start && methodRid < end)
                return typeRid;
        }
        return 0;
    }

    /// <summary>enum 値の書式化 (G/D/X/F。provider は不変カルチャ規約で無視)。</summary>
    private static string EnumToString(VmClassType? enumType, ulong bits, bool? signed, string? format, IntrinsicContext ctx) {
        _ = ctx;
        var (size, underlyingSigned) = EnumLayout(enumType);
        var isSigned = signed ?? underlyingSigned;
        var fmt = string.IsNullOrEmpty(format) ? "G" : format!;
        if (fmt.Length != 1)
            throw new UnhandledGuestException("System.FormatException", null);
        return fmt[0] switch {
            'G' or 'g' or 'F' or 'f' => EnumNameOrNumber(enumType, bits, size, isSigned),
            'D' or 'd' => isSigned ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString(),
            'X' or 'x' => TruncateBits(bits, size, false).ToString(fmt[0] == 'X' ? "X" + size * 2 : "x" + size * 2),
            _ => throw new UnhandledGuestException("System.FormatException", null),
        };
    }

    /// <summary>文字列連結に渡された boxed enum の既定 (G) 書式。</summary>
    internal static string FormatEnumForConcat(VmBoxedValue box) =>
        EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format: null, ctx: null!);

    private static ulong TruncateBits(ulong bits, int size, bool signed) => (size, signed) switch {
        (1, true) => (ulong)(long)(sbyte)(byte)bits,
        (1, false) => (byte)bits,
        (2, true) => (ulong)(long)(short)(ushort)bits,
        (2, false) => (ushort)bits,
        (4, true) => (ulong)(int)(uint)bits,
        (4, false) => (uint)bits,
        _ => bits,
    };

    private static string EnumNameOrNumber(VmClassType? enumType, ulong bits, int size, bool signed) {
        var literals = EnumLiteralsOf(enumType);
        foreach (var literal in literals) {
            if (TruncateBits(literal.Bits, size, signed) == TruncateBits(bits, size, signed))
                return literal.Name;
        }
        if (enumType is not null && HasFlagsAttribute(enumType))
            return FormatFlags(literals, bits, size, signed);
        return signed ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString();
    }

    private static string FormatFlags(List<EnumLiteral> literals, ulong bits, int size, bool signed) {
        if (bits == 0) {
            foreach (var literal in literals) {
                if (TruncateBits(literal.Bits, size, signed) == 0)
                    return literal.Name;
            }
            return "0";
        }
        var parts = new List<string>();
        var remaining = TruncateBits(bits, size, signed);
        foreach (var literal in literals
                     .Select(l => (l.Name, Value: TruncateBits(l.Bits, size, signed)))
                     .OrderByDescending(p => p.Value)) {
            if (literal.Value == 0 || (remaining & literal.Value) != literal.Value)
                continue;
            parts.Add(literal.Name);
            remaining &= ~literal.Value;
        }
        if (remaining != 0)
            parts.Add(signed ? ((long)remaining).ToString() : remaining.ToString());
        return parts.Count == 0
            ? signed ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString()
            : string.Join(", ", parts);
    }

    private static StackSlot RawSlot(ulong bits, VmClassType enumType) {
        var (size, _) = EnumLayout(enumType);
        return size == 8 ? StackSlot.OfInt64((long)bits) : StackSlot.OfInt32((int)(uint)bits);
    }

    private static StackSlot EnumParseImpl(IntrinsicContext ctx, in StackSlot nameSlot, bool ignoreCase) {
        var enumType = ResolveEnumType(ctx, null);
        var raw = (nameSlot.ObjectValue as VmString)?.Value?.Trim() ?? "";
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (string.Equals(literal.Name, raw, comparison))
                return RawSlot(literal.Bits, enumType);
        }
        // 数値面 ("2" 等。範囲内なら未定義値でも box 化するのが CLR 規約)
        var (size, signed) = EnumLayout(enumType);
        if (signed) {
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sparsed) &&
                FitsIn(sparsed, size, true))
                return size == 8 ? StackSlot.OfInt64(sparsed) : StackSlot.OfInt32((int)sparsed);
        } else {
            if (ulong.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var uparsed) &&
                FitsInU(uparsed, size))
                return size == 8 ? StackSlot.OfInt64((long)uparsed) : StackSlot.OfInt32((int)(uint)uparsed);
        }
        throw new UnhandledGuestException("System.ArgumentException", null);
    }

    private static bool FitsIn(long value, int size, bool signed) => (size, signed) switch {
        (1, true) => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        (1, false) => value is >= byte.MinValue and <= byte.MaxValue,
        (2, true) => value is >= short.MinValue and <= short.MaxValue,
        (2, false) => value is >= ushort.MinValue and <= ushort.MaxValue,
        (4, true) => value is >= int.MinValue and <= int.MaxValue,
        (4, false) => value is >= uint.MinValue and <= (long)uint.MaxValue,
        _ => true,
    };

    private static StackSlot EnumTryParseImpl(IntrinsicContext ctx, StackSlot[] a, bool ignoreCase) {
        // out T は ByRef (a[^1])。成功時は box せず生スロットを書く
        // (enum ローカルは i4/i8 スロット表現のため)
        var enumType = ResolveEnumType(ctx, null);
        var (size, signed) = EnumLayout(enumType);
        var outRef = a[^1].ObjectValue as VmByRef
            ?? throw new InvalidOperationException("Enum.TryParse の out 引数が参照ではありません。");
        var nameIndex = a.Length == 3 ? 1 : 0;
        var raw = ((a[nameIndex].ObjectValue as VmString)?.Value ?? "").Trim();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (string.Equals(literal.Name, raw, comparison)) {
            outRef.Write(RawSlot(literal.Bits, enumType));
                return StackSlot.OfInt32(1);
            }
        }
        var parsedOk = false;
        ulong parsedBits = 0;
        if (signed) {
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sparsed) &&
                FitsIn(sparsed, size, true)) {
                parsedOk = true;
                parsedBits = unchecked((ulong)sparsed);
            }
        } else {
            if (ulong.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var uparsed) &&
                FitsInU(uparsed, size)) {
                parsedOk = true;
                parsedBits = uparsed;
            }
        }
        if (parsedOk) {
            outRef.Write(RawSlot(parsedBits, enumType));
            return StackSlot.OfInt32(1);
        }
        outRef.Write(size == 8 ? StackSlot.OfInt64(0) : StackSlot.OfInt32(0));
        return StackSlot.OfInt32(0);
    }

    private static bool FitsInU(ulong value, int size) => size switch {
        1 => value <= byte.MaxValue,
        2 => value <= ushort.MaxValue,
        4 => value <= uint.MaxValue,
        _ => true,
    };

    private static bool EnumIsDefined(in StackSlot typeSlot, in StackSlot valueSlot) {
        if (typeSlot.ObjectValue is not VmRuntimeObject runtime || runtime.Target is not VmClassType enumType ||
            (!enumType.IsEnum && enumType.FullName != "System.Enum"))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var literals = EnumLiteralsOf(enumType);
        var (size, signed) = EnumLayout(enumType);
        if (valueSlot.ObjectValue is VmString name) {
            return literals.Any(l => l.Name == name.Value);
        }
        if (valueSlot.ObjectValue is VmBoxedValue box) {
            var bits = TruncateBits(RawBitsOf(box), size, signed);
            return literals.Any(l => TruncateBits(l.Bits, size, signed) == bits);
        }
        // 基底型の生値スロット (int 等が直接来る形)
        if (valueSlot.Kind is StackKind.Int32 or StackKind.Int64) {
            var bits = valueSlot.Kind == StackKind.Int64
                ? (ulong)valueSlot.Int64Value : (ulong)(uint)valueSlot.AsInt32;
            return literals.Any(l => TruncateBits(l.Bits, size, signed) == TruncateBits(bits, size, signed));
        }
        return false;
    }

    private static string? EnumGetName(in StackSlot typeSlot, in StackSlot valueSlot) {
        if (typeSlot.ObjectValue is not VmRuntimeObject runtime || runtime.Target is not VmClassType enumType ||
            (!enumType.IsEnum && enumType.FullName != "System.Enum"))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var (size, signed) = EnumLayout(enumType);
        ulong bits;
        if (valueSlot.ObjectValue is VmBoxedValue box)
            bits = TruncateBits(RawBitsOf(box), size, signed);
        else if (valueSlot.Kind is StackKind.Int32 or StackKind.Int64)
            bits = valueSlot.Kind == StackKind.Int64
                ? TruncateBits((ulong)valueSlot.Int64Value, size, signed)
                : TruncateBits((ulong)(uint)valueSlot.AsInt32, size, signed);
        else
            throw new UnhandledGuestException("System.ArgumentException", null);
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (TruncateBits(literal.Bits, size, signed) == bits)
                return literal.Name;
        }
        return null;
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
}
