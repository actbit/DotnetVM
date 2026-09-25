using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- プリミティブ instance ToString (ボックス化仮想呼出面) ----

    /// <summary>プリミティブの instance ToString バインド。String.Concat(object, object)
    /// 等、CoreLib IL 内の `boxedPrimitive?.ToString()` の callvirt が culture 機構依存の
    /// managed IL に仮想解決されるのを受ける面。
    /// C5.5 Wave 2 で整数 8 型 + Boolean/Char、Wave 3 で Single/Double の全 ToString 面
    /// (無引数 / format / format+IFormatProvider / IFormatProvider) を VmCoreLibSurfaces
    /// (DotnetVM.CoreLib FormatSpecifiers / DoubleFormatting の managed IL) に置換したため、
    /// ここには載せない (載せると ① が ②'/置換面を塞いでしまう)。</summary>
    private static void RegisterPrimitiveToString(IntrinsicRegistry r) {
        RegisterCultureLegacyNumericFaces(r);
    }

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
            return x.DoubleValue.Equals(y.DoubleValue);
        var lx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var ly = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return lx == ly;
    }

    // ---- ISpanFormattable.TryFormat (JoinCore 等の要素書式面) ----

    /// <summary>ISpanFormattable.TryFormat(span, out int, format, provider) の同等意味論。
    /// string.JoinCore 等が要素書式に辿る。本家 IL は Number.TryFormat* (char* / culture 機構)
    /// のため VM 表現境界。ホストの不変カルチャ書式で文字列化し、出力 span へ書き込む。
    /// provider は不変カルチャ規約で無視する (全書式面と同一方針)。長さ不足は false
    /// (例外なし。Try パターン規約)。不正書式は CLR どおり FormatException。
    /// 整数 4 型 + 浮動小数点 2 型を列挙する (他は fail-closed のまま)。</summary>
    private static void RegisterSpanFormattable(IntrinsicRegistry r) {
        const string SpanChar = "System.Span`1<System.Char>";
        const string ReadOnlySpanChar = "System.ReadOnlySpan`1<System.Char>";
        const string OutInt = "System.Int32&";
        const string Provider = "System.IFormatProvider";
        static void Face(IntrinsicRegistry reg, string type, Func<StackSlot, string?, string> format) =>
            reg.RegisterBinding(
                BindingKey.Instance(type, "TryFormat", SpanChar, OutInt, ReadOnlySpanChar, Provider),
                (ctx, a) => TryFormatImpl(ctx, a, format),
                BindingOrigin.Managed);
        static string Inv(int v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Unv(uint v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Lng(long v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Ulng(ulong v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Flt(float v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Dbl(double v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        Face(r, "System.Int32", static (v, f) => Inv((int)v.Int64Value, f));
        Face(r, "System.UInt32", static (v, f) => Unv((uint)v.Int64Value, f));
        Face(r, "System.Int64", static (v, f) => Lng(v.Int64Value, f));
        Face(r, "System.UInt64", static (v, f) => Ulng((ulong)v.Int64Value, f));
        Face(r, "System.Single", static (v, f) => Flt((float)v.DoubleValue, f));
        Face(r, "System.Double", static (v, f) => Dbl(v.DoubleValue, f));
    }

    private static StackSlot? TryFormatImpl(IntrinsicContext ctx, StackSlot[] a, Func<StackSlot, string?, string> format) {
        // 書式 span (空 = null 書式相当)
        var formatText = ReadCharSpanOrEmpty(a[3]);
        string text;
        try {
            text = format(a[0], formatText);
        } catch (FormatException) {
            throw new UnhandledGuestException("System.FormatException", null);
        }
        // 出力 span の容量検査と書込
        var dest = ReadSpanParts(a[1]);
        if (text.Length > dest.Length) {
            WriteWritten(a[2], 0);
            return StackSlot.OfInt32(0);
        }
        if (text.Length > 0)
            WriteChars(dest.Reference, text);
        WriteWritten(a[2], text.Length);
        return StackSlot.OfInt32(1);
    }

    /// <summary>ReadOnlySpan&lt;char&gt; 構造体値から文字列を読む (空 span は "")。</summary>
    private static string ReadCharSpanOrEmpty(in StackSlot span) {
        var (reference, length) = ReadSpanParts(span);
        if (length == 0)
            return "";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)ReadCharAt(reference, i);
        return new string(chars);
    }

    private static (StackSlot Reference, int Length) ReadSpanParts(in StackSlot span) {
        if (span.ObjectValue is not VmStructValue sv)
            throw new InvalidOperationException($"span ではありません ({span.Kind})。");
        var definition = sv.StructType is VmConstructedType constructed
            ? constructed.Definition as VmClassType
            : sv.StructType as VmClassType;
        if (definition is null)
            throw new InvalidOperationException("span の型を解決できません。");
        // 配置は GetLayout (正規レイアウト) で引く (StaticFieldIndex は静的専用のため不可)
        var layout = new ObjectModel().GetLayout(definition);
        StackSlot reference = default;
        var length = 0;
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            if (!layout.TryGetValue(field, out var index) || index >= sv.Fields.Length)
                throw new InvalidOperationException($"span のフィールド {field.Name} の配置を解決できません。");
            if (field.Name == "_reference")
                reference = sv.Fields[index];
            else if (field.Name == "_length")
                length = sv.Fields[index].AsInt32;
        }
        return (reference, length);
    }

    private static int ReadCharAt(in StackSlot reference, int index) {
        if (reference.Kind == StackKind.ByRef && reference.ObjectValue is VmByRef byRef) {
            if (index < 0 || byRef.Index + index >= byRef.Container.Length)
                throw new InvalidOperationException($"span の範囲外を読みます (index={index})。");
            return byRef.Container[byRef.Index + index].AsInt32;
        }
        if (reference.ObjectValue is VmNativePointer native) {
            var offset = native.ByteOffset + index * 2;
            if (offset < 0 || offset + 2 > native.Bytes.Length)
                throw new InvalidOperationException($"span の範囲外を読みます (index={index})。");
            return native.Bytes[offset] | native.Bytes[offset + 1] << 8;
        }
        throw new InvalidOperationException($"span の参照を解釈できません ({reference.Kind})。");
    }

    private static void WriteChars(in StackSlot reference, string text) {
        if (reference.Kind == StackKind.ByRef && reference.ObjectValue is VmByRef byRef) {
            if (byRef.Index + text.Length > byRef.Container.Length)
                throw new InvalidOperationException("span の範囲外に書きます。");
            for (var i = 0; i < text.Length; i++)
                byRef.Container[byRef.Index + i] = StackSlot.OfInt32(text[i]);
            return;
        }
        if (reference.ObjectValue is VmNativePointer native) {
            if ((long)native.ByteOffset + text.Length * 2 > native.Bytes.Length)
                throw new InvalidOperationException("span の範囲外に書きます。");
            for (var i = 0; i < text.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                    native.Bytes.AsSpan(native.ByteOffset + i * 2, 2), text[i]);
            return;
        }
        throw new InvalidOperationException($"span の参照を解釈できません ({reference.Kind})。");
    }

    private static void WriteWritten(in StackSlot slot, int value) {
        if (slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef)
            byRef.Write(StackSlot.OfInt32(value));
        else
            throw new InvalidOperationException("TryFormat の out 引数が参照ではありません。");
    }
}
