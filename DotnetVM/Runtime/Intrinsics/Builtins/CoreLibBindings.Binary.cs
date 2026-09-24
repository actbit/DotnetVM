using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Convert (Base64) / System.BitConverter (バイト列変換面) ----

    /// <summary>Base64 / BitConverter の純粋変換面。
    /// 本家 IL は byte* 生ポインタ + Span 内部 (ConvertToBase64Array 等) で構成され
    /// VM のスロット表現に落ちないため、ホストの同一意味論へ委譲する。
    /// 不正入力の FormatException 分類も CLR と同一 (ゲスト例外化する)。</summary>
    private static void RegisterConvertBinary(IntrinsicRegistry r) {
        const string Convert = "System.Convert";
        r.RegisterBinding(BindingKey.Static(Convert, "ToBase64String", "System.Byte[]"),
            static (ctx, a) => {
                if (a[0].ObjectValue is null)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    System.Convert.ToBase64String(ctx.ReadByteArray(a[0]))));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(Convert, "FromBase64String", "System.String"),
            static (ctx, a) => {
                var s = (a[0].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                try {
                    return StackSlot.OfObject(ctx.MakeByteArray(System.Convert.FromBase64String(s)));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        const string BitConverter = "System.BitConverter";
        static StackSlot GetBytes(IntrinsicContext ctx, byte[] data) =>
            StackSlot.OfObject(ctx.MakeByteArray(data));
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Boolean"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].AsInt32 != 0)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Char"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((char)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int16"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((short)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt16"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((ushort)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int32"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt32"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((uint)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int64"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].Int64Value)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt64"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((ulong)a[0].Int64Value)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Single"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((float)a[0].DoubleValue)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Double"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].DoubleValue)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "ToString", "System.Byte[]"),
            static (ctx, a) => {
                if (a[0].ObjectValue is null)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    System.BitConverter.ToString(ctx.ReadByteArray(a[0]))));
            },
            BindingOrigin.Managed);
    }


    // ---- System.Guid (解析・書式・比較面) ----

    /// <summary>Guid の Parse / TryParse / ToString / Equals / CompareTo / GetHashCode 面。
    /// 本家 IL は span 16 進解析 (IUtfChar / RawData 生ポインタ) + SIMD フォーマットで
    /// VM のスロット表現に落ちないため、ホストの同一意味論へ委譲する。
    /// Guid 書式 (N/D/B/P/X) に culture 要素は無く完全一致する。
    /// VM 内表現は CoreLib の Guid 構造体 (_a.._k の 11 フィールド) に正規化する
    /// (フィールド名で対応付け、宣言順依存にしない)。不正入力の FormatException 分類も同一。
    /// new Guid(string) の .ctor 実 IL は Parse へ委譲するためここで受ける。</summary>
    private static void RegisterGuid(IntrinsicRegistry r) {
        const string T = "System.Guid";
        static StackSlot MakeGuidStruct(IntrinsicContext ctx, Guid value) {
            var cls = FindCoreLibType(ctx, T)
                ?? throw new InvalidOperationException("System.Guid (CoreLib 実型) がロードされていません。");
            var bytes = value.ToByteArray();
            var fields = new StackSlot[InstanceFieldCount(cls)];
            var index = 0;
            foreach (var field in cls.Fields) {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                fields[index++] = field.Name switch {
                    "_a" => StackSlot.OfInt32(BitConverter.ToInt32(bytes, 0)),
                    "_b" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 4)),
                    "_c" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 6)),
                    "_d" => StackSlot.OfInt32(bytes[8]),
                    "_e" => StackSlot.OfInt32(bytes[9]),
                    "_f" => StackSlot.OfInt32(bytes[10]),
                    "_g" => StackSlot.OfInt32(bytes[11]),
                    "_h" => StackSlot.OfInt32(bytes[12]),
                    "_i" => StackSlot.OfInt32(bytes[13]),
                    "_j" => StackSlot.OfInt32(bytes[14]),
                    "_k" => StackSlot.OfInt32(bytes[15]),
                    _ => SlotDefaultZero(field),
                };
            }
            return StackSlot.OfValueType(new VmStructValue(cls, fields));
        }
        static Guid GuidOf(in StackSlot slot) {
            if (slot.ObjectValue is not VmStructValue sv || sv.StructType.FullName != T)
                throw new InvalidOperationException(
                    $"Guid 面の引数が CoreLib Guid 構造体値ではありません ({slot.Kind})。");
            var bytes = new byte[16];
            foreach (var (field, index) in FieldSlots(sv)) {
                var value = sv.Fields[index].AsInt32;
                switch (field) {
                    case "_a":
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), value);
                        break;
                    case "_b":
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4, 2), (short)value);
                        break;
                    case "_c":
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6, 2), (short)value);
                        break;
                    case "_d": bytes[8] = (byte)value; break;
                    case "_e": bytes[9] = (byte)value; break;
                    case "_f": bytes[10] = (byte)value; break;
                    case "_g": bytes[11] = (byte)value; break;
                    case "_h": bytes[12] = (byte)value; break;
                    case "_i": bytes[13] = (byte)value; break;
                    case "_j": bytes[14] = (byte)value; break;
                    case "_k": bytes[15] = (byte)value; break;
                }
            }
            return new Guid(bytes);
        }
        static string? S(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value;
        // Guid.NewGuid は CoreLib の OS P/Invoke に到達するため、任意の native import を
        // 許可せずホスト BCL の暗号学的 GUID 生成だけを委譲する。
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "NewGuid", T, []),
            static (ctx, _) => MakeGuidStruct(ctx, Guid.NewGuid()),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", T, ["System.String"]),
            static (ctx, a) => {
                try {
                    return MakeGuidStruct(ctx, Guid.Parse(S(a[0])
                        ?? throw new UnhandledGuestException("System.ArgumentNullException", null)));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryParse", "System.Boolean",
                ["System.String", "System.Guid&"]),
            static (ctx, a) => {
                var ok = Guid.TryParse(S(a[0]) ?? "", out var value);
                if (a[1].ObjectValue is VmByRef byRef)
                    byRef.Write(MakeGuidStruct(ctx, value));
                else
                    throw new InvalidOperationException("Guid.TryParse の out 引数が参照ではありません。");
                return StackSlot.OfInt32(ok ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String", []),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString())),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String", ["System.String"]),
            static (ctx, a) => {
                try {
                    return StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString(S(a[1]))));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String",
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => {
                try {
                    return StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString(S(a[1]))));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "Equals", "System.Boolean", ["System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "CompareTo", "System.Int32", ["System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).CompareTo(GuidOf(a[1]))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "GetHashCode", "System.Int32", []),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).GetHashCode()),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Equality", "System.Boolean", ["System.Guid", "System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Inequality", "System.Boolean", ["System.Guid", "System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 0 : 1),
            BindingOrigin.Managed);
    }

    /// <summary>構造体値の (フィールド名, インデックス) 列挙 (レイアウト順)。</summary>
    private static System.Collections.Generic.IEnumerable<(string Name, int Index)> FieldSlots(VmStructValue sv) {
        var definition = sv.StructType is VmConstructedType constructed
            ? constructed.Definition as VmClassType
            : sv.StructType as VmClassType;
        if (definition is null)
            yield break;
        var layout = new ObjectModel().GetLayout(definition);
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            if (layout.TryGetValue(field, out var index) && index < sv.Fields.Length)
                yield return (field.Name, index);
        }
    }
}
