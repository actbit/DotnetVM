using System.Globalization;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    /// <summary>CoreLib 未ロード時にも同じ VM culture を使う legacy fallback。</summary>
    private static void RegisterCultureLegacyNumericFaces(IntrinsicRegistry r) {
        foreach (var type in new[] {
            "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32",
            "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double",
        }) {
            r.Register(IntrinsicKey.Instance(type, "ToString", 1), (ctx, a) =>
                FormatGuest(ctx, () => FormatPrimitive(ctx, a[0], type, StringValue(a[1]))));
        }

        r.Register(IntrinsicKey.Static("System.Int32", "Parse", 2), static (ctx, a) =>
            ParseInt32Culture(ctx, a[0], a[1].AsInt32));
        r.Register(IntrinsicKey.Static("System.Double", "Parse", 2), static (ctx, a) =>
            ParseDoubleCulture(ctx, a[0], a[1].AsInt32));
    }

    private static string? StringValue(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value;

    private static StackSlot FormatGuest(IntrinsicContext ctx, Func<string> format) {
        try { return StackSlot.OfObject(ctx.MakeString(format())); }
        catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
    }

    private static string FormatPrimitive(IntrinsicContext ctx, in StackSlot receiver,
        string type, string? format) {
        var value = receiver.ObjectValue is VmBoxedValue boxed ? boxed.Fields[0] : receiver;
        return type switch {
            "System.Byte" => ((byte)value.AsInt32).ToString(format, ctx.Shared.Culture),
            "System.SByte" => ((sbyte)value.AsInt32).ToString(format, ctx.Shared.Culture),
            "System.Int16" => ((short)value.AsInt32).ToString(format, ctx.Shared.Culture),
            "System.UInt16" => ((ushort)value.AsInt32).ToString(format, ctx.Shared.Culture),
            "System.Int32" => value.AsInt32.ToString(format, ctx.Shared.Culture),
            "System.UInt32" => ((uint)value.AsInt32).ToString(format, ctx.Shared.Culture),
            "System.Int64" => value.Int64Value.ToString(format, ctx.Shared.Culture),
            "System.UInt64" => ((ulong)value.Int64Value).ToString(format, ctx.Shared.Culture),
            "System.Single" => ((float)value.DoubleValue).ToString(format, ctx.Shared.Culture),
            "System.Double" => value.DoubleValue.ToString(format, ctx.Shared.Culture),
            _ => throw new InvalidOperationException($"未対応の数値型です: {type}"),
        };
    }

    private static string Text(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value
        ?? throw new UnhandledGuestException("System.ArgumentNullException", "value");

    private static StackSlot ParseInt32Culture(IntrinsicContext ctx, in StackSlot value, int styles) {
        try { return StackSlot.OfInt32(int.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
        catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
        catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
        catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
    }

    private static StackSlot ParseDoubleCulture(IntrinsicContext ctx, in StackSlot value, int styles) {
        try { return StackSlot.OfFloat(double.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
        catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
        catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
        catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
    }

    /// <summary>
    /// DotnetVM.CoreLib の置換 IL が使う culture bridge。置換先 assembly の型に直接
    /// binding を公開するのではなく、専用の trusted VM CoreLib marker を持つ画像だけを
    /// CallEngine が受けるため、guest が同名 assembly を持ち込んでも VM の CultureInfo を
    /// 読み出せない。実装は呼出しごとに shared state の culture を渡し、CurrentCulture の
    /// process/async-local 状態に依存しない。
    /// </summary>
    private static void RegisterVmCoreLibCultureSettings(IntrinsicRegistry r) {
        const string T = "DotnetVM.CoreLib.CultureSettings";
        static string? Format(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value;

        void FormatFace(string method, string valueType, Func<IntrinsicContext, StackSlot[], string> format) =>
            r.RegisterBinding(BindingKey.StaticWithReturn(T, method, "System.String", [valueType, "System.String"]),
                (ctx, a) => FormatGuest(ctx, () => format(ctx, a)), BindingOrigin.Managed);

        FormatFace("FormatByte", "System.Byte", (ctx, a) => ((byte)a[0].AsInt32).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatSByte", "System.SByte", (ctx, a) => ((sbyte)a[0].AsInt32).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatInt16", "System.Int16", (ctx, a) => ((short)a[0].AsInt32).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatUInt16", "System.UInt16", (ctx, a) => ((ushort)a[0].AsInt32).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatInt32", "System.Int32", (ctx, a) => a[0].AsInt32.ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatUInt32", "System.UInt32", (ctx, a) => ((uint)a[0].AsInt32).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatInt64", "System.Int64", (ctx, a) => a[0].Int64Value.ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatUInt64", "System.UInt64", (ctx, a) => ((ulong)a[0].Int64Value).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatSingle", "System.Double", (ctx, a) => ((float)a[0].DoubleValue).ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatDouble", "System.Double", (ctx, a) => a[0].DoubleValue.ToString(Format(a[1]), ctx.Shared.Culture));
        FormatFace("FormatDecimal", "System.Decimal", (ctx, a) =>
            ToDecimalValue(a[0]).ToString(Format(a[1]), ctx.Shared.Culture));

        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ParseInt32", "System.Int32", ["System.String", "System.Int32"]),
            static (ctx, a) => ParseInt32(ctx, a[0], a[1].AsInt32), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ParseInt64", "System.Int64", ["System.String", "System.Int32"]),
            static (ctx, a) => ParseInt64(ctx, a[0], a[1].AsInt32), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ParseSingle", "System.Double", ["System.String", "System.Int32"]),
            static (ctx, a) => ParseSingle(ctx, a[0], a[1].AsInt32), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ParseDouble", "System.Double", ["System.String", "System.Int32"]),
            static (ctx, a) => ParseDouble(ctx, a[0], a[1].AsInt32), BindingOrigin.Managed);

        static StackSlot ParseInt32(IntrinsicContext ctx, in StackSlot value, int styles) {
            try { return StackSlot.OfInt32(int.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
            catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
            catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
            catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
        }
        static StackSlot ParseInt64(IntrinsicContext ctx, in StackSlot value, int styles) {
            try { return StackSlot.OfInt64(long.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
            catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
            catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
            catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
        }
        static StackSlot ParseSingle(IntrinsicContext ctx, in StackSlot value, int styles) {
            try { return StackSlot.OfFloat(float.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
            catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
            catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
            catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
        }
        static StackSlot ParseDouble(IntrinsicContext ctx, in StackSlot value, int styles) {
            try { return StackSlot.OfFloat(double.Parse(Text(value), (NumberStyles)styles, ctx.Shared.Culture)); }
            catch (FormatException) { throw new UnhandledGuestException("System.FormatException", null); }
            catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
            catch (ArgumentException) { throw new UnhandledGuestException("System.ArgumentException", null); }
        }
    }
}
