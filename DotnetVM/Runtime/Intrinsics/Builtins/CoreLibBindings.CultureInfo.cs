using System.Globalization;
using System.Runtime.CompilerServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // Host BCL state stays behind VM objects; arbitrary guest objects never become
    // host providers. Weak keys keep constructor state tied to the guest lifetime.
    private sealed class BclState { public required object Value; }
    private static readonly ConditionalWeakTable<VmObject, BclState> BclStates = new();

    private static StackSlot WrapBcl(IntrinsicContext ctx, string type, object value) {
        var vmType = FindAnyType(ctx, type)
            ?? throw new InvalidOperationException($"BCL 型 {type} が見つかりません。");
        var instance = ctx.Heap.Allocate(new VmBclObject(vmType));
        BclStates.Add(instance, new BclState { Value = value });
        return StackSlot.OfObject(instance);
    }

    private static T BclValue<T>(in StackSlot slot) where T : class =>
        slot.ObjectValue is VmObject obj && BclStates.TryGetValue(obj, out var state) && state.Value is T value
            ? value : throw new UnhandledGuestException("System.InvalidProgramException", "BCL state is unavailable.");

    private static void SetBclValue(in StackSlot slot, object value) {
        if (slot.ObjectValue is not VmObject obj)
            throw new UnhandledGuestException("System.InvalidProgramException", "BCL receiver is unavailable.");
        BclStates.GetValue(obj, _ => new BclState { Value = value }).Value = value;
    }

    private static IFormatProvider GuestProvider(IntrinsicContext ctx, in StackSlot slot) =>
        slot.ObjectValue is null ? ctx.Shared.CurrentCulture : BclValue<IFormatProvider>(slot);

    private static IFormatProvider InvocationProvider(IntrinsicContext ctx, StackSlot[] args) =>
        ctx.ParameterTypeNames.LastOrDefault() == "System.IFormatProvider"
            ? GuestProvider(ctx, args[^1]) : ctx.Shared.CurrentCulture;

    private static StackSlot? BclCall(Func<StackSlot?> call) {
        try { return call(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException or System.Net.Http.HttpRequestException) {
            throw new UnhandledGuestException(ex.GetType().FullName!, ex.Message);
        }
    }

    private static void RegisterCultureInfo(IntrinsicRegistry r) {
        const string T = "System.Globalization.CultureInfo";
        void Static(string method, string[] parameters, IntrinsicImpl impl) =>
            r.RegisterBinding(BindingKey.Static(T, method, parameters),
                (ctx, a) => BclCall(() => impl(ctx, a)), BindingOrigin.Managed);
        void Instance(string method, string[] parameters, IntrinsicImpl impl) =>
            r.RegisterBinding(BindingKey.Instance(T, method, parameters),
                (ctx, a) => BclCall(() => impl(ctx, a)), BindingOrigin.Managed);
        Static(".cctor", [], static (_, _) => null);
        foreach (var count in new[] { 1, 2 })
            r.Register(IntrinsicKey.Instance(T, ".ctor", count), (ctx, a) => BclCall(() => {
                var useOverride = a.Length < 3 || a[2].AsInt32 != 0;
                SetBclValue(a[0], a[1].Kind == StackKind.Int32 ? new CultureInfo(a[1].AsInt32, useOverride) : new CultureInfo(StringValue(a[1])!, useOverride));
                return null;
            }));
        foreach (var nameType in new[] { "System.String", "System.Int32" }) {
            foreach (var useOverride in new[] { false, true }) {
                var parameters = useOverride ? new[] { nameType, "System.Boolean" } : [nameType];
                Instance(".ctor", parameters, (ctx, a) => {
                    ctx.Heap.ChargeHostWork(1);
                    var culture = nameType == "System.String"
                        ? new CultureInfo(StringValue(a[1])!, !useOverride || a[2].AsInt32 != 0)
                        : new CultureInfo(a[1].AsInt32, !useOverride || a[2].AsInt32 != 0);
                    SetBclValue(a[0], culture);
                    return null;
                });
            }
            Static("GetCultureInfo", [nameType], (ctx, a) => WrapBcl(ctx, T, nameType == "System.String"
                ? CultureInfo.GetCultureInfo(StringValue(a[0])!) : CultureInfo.GetCultureInfo(a[0].AsInt32)));
        }
        foreach (var (name, get) in new (string, Func<IntrinsicContext, CultureInfo>)[] {
            ("InvariantCulture", static _ => CultureInfo.InvariantCulture),
            ("CurrentCulture", static ctx => ctx.Shared.CurrentCulture),
            ("CurrentUICulture", static ctx => ctx.Shared.CurrentUICulture),
        })
            Static("get_" + name, [], (ctx, _) => WrapBcl(ctx, T, get(ctx)));
        Static("set_CurrentCulture", [T], static (ctx, a) => {
            if (a[0].ObjectValue is null) throw new ArgumentNullException("value");
            ctx.Shared.CurrentCulture = BclValue<CultureInfo>(a[0]);
            CultureInfo.CurrentCulture = ctx.Shared.CurrentCulture;
            return null;
        });
        Static("set_CurrentUICulture", [T], static (ctx, a) => {
            if (a[0].ObjectValue is null) throw new ArgumentNullException("value");
            ctx.Shared.CurrentUICulture = BclValue<CultureInfo>(a[0]);
            CultureInfo.CurrentUICulture = ctx.Shared.CurrentUICulture;
            return null;
        });
        Static("ReadOnly", [T], static (ctx, a) => WrapBcl(ctx, T, CultureInfo.ReadOnly(BclValue<CultureInfo>(a[0]))));
        Instance("Clone", [], static (ctx, a) => WrapBcl(ctx, T, BclValue<CultureInfo>(a[0]).Clone()));
        Instance("ToString", [], static (ctx, a) => StackSlot.OfObject(ctx.MakeString(BclValue<CultureInfo>(a[0]).ToString())));
        Instance("Equals", ["System.Object"], static (_, a) => StackSlot.OfInt32(
            a[1].ObjectValue is VmObject obj && BclStates.TryGetValue(obj, out var state) &&
            BclValue<CultureInfo>(a[0]).Equals(state.Value) ? 1 : 0));
        Instance("GetHashCode", [], static (_, a) => StackSlot.OfInt32(BclValue<CultureInfo>(a[0]).GetHashCode()));
        foreach (var property in new[] { "Name", "DisplayName", "EnglishName", "NativeName", "TwoLetterISOLanguageName", "ThreeLetterISOLanguageName" }) {
            var info = typeof(CultureInfo).GetProperty(property)!;
            Instance("get_" + property, [], (ctx, a) => StackSlot.OfObject(ctx.MakeString((string)info.GetValue(BclValue<CultureInfo>(a[0]))!)));
        }
        Instance("get_LCID", [], static (_, a) => StackSlot.OfInt32(BclValue<CultureInfo>(a[0]).LCID));
        Instance("get_IsReadOnly", [], static (_, a) => StackSlot.OfInt32(BclValue<CultureInfo>(a[0]).IsReadOnly ? 1 : 0));
        Instance("get_IsNeutralCulture", [], static (_, a) => StackSlot.OfInt32(BclValue<CultureInfo>(a[0]).IsNeutralCulture ? 1 : 0));
        Instance("get_Parent", [], static (ctx, a) => WrapBcl(ctx, T, BclValue<CultureInfo>(a[0]).Parent));
        foreach (var (property, type) in new[] { ("NumberFormat", "NumberFormatInfo"), ("DateTimeFormat", "DateTimeFormatInfo"), ("TextInfo", "TextInfo"), ("CompareInfo", "CompareInfo") }) {
            var info = typeof(CultureInfo).GetProperty(property)!;
            Instance("get_" + property, [], (ctx, a) => WrapBcl(ctx, "System.Globalization." + type, info.GetValue(BclValue<CultureInfo>(a[0]))!));
        }
        Instance("GetFormat", ["System.Type"], static (ctx, a) => {
            var target = (a[1].ObjectValue as VmRuntimeObject)?.Target.FullName;
            var culture = BclValue<CultureInfo>(a[0]);
            return target switch {
                "System.Globalization.NumberFormatInfo" => WrapBcl(ctx, target, culture.NumberFormat),
                "System.Globalization.DateTimeFormatInfo" => WrapBcl(ctx, target, culture.DateTimeFormat),
                _ => StackSlot.Null,
            };
        });
        RegisterNumberFormatInfo(r);
        RegisterProviderBridge(r);
        foreach (var method in new[] { "ToUpper", "ToLower" }) {
            r.RegisterBinding(BindingKey.Instance("System.Globalization.TextInfo", method, "System.String"),
                (ctx, a) => BclCall(() => StackSlot.OfObject(ctx.MakeString(method == "ToUpper"
                    ? BclValue<TextInfo>(a[0]).ToUpper(StringValue(a[1])!) : BclValue<TextInfo>(a[0]).ToLower(StringValue(a[1])!)))), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance("System.String", method, T),
                (ctx, a) => BclCall(() => StackSlot.OfObject(ctx.MakeString(method == "ToUpper"
                    ? Text(a[0]).ToUpper(BclValue<CultureInfo>(a[1])) : Text(a[0]).ToLower(BclValue<CultureInfo>(a[1]))))), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Static("System.Char", method, "System.Char", T),
                (_, a) => BclCall(() => StackSlot.OfInt32(method == "ToUpper"
                    ? char.ToUpper((char)a[0].AsInt32, BclValue<CultureInfo>(a[1])) : char.ToLower((char)a[0].AsInt32, BclValue<CultureInfo>(a[1])))), BindingOrigin.Managed);
        }
    }

    private static void RegisterProviderBridge(IntrinsicRegistry r) {
        const string T = "DotnetVM.CoreLib.CultureSettings";
        foreach (var (suffix, type) in new[] {
            ("Byte", "System.Byte"), ("SByte", "System.SByte"), ("Int16", "System.Int16"), ("UInt16", "System.UInt16"),
            ("Int32", "System.Int32"), ("UInt32", "System.UInt32"), ("Int64", "System.Int64"), ("UInt64", "System.UInt64"),
            ("Single", "System.Double"), ("Double", "System.Double"), ("Decimal", "System.Decimal"),
        }) {
            r.RegisterBinding(BindingKey.Static(T, "Format" + suffix, type, "System.String", "System.Object"),
                (ctx, a) => BclCall(() => StackSlot.OfObject(ctx.MakeString(suffix == "Decimal"
                    ? ToDecimalValue(a[0]).ToString(StringValue(a[1]), GuestProvider(ctx, a[2]))
                    : FormatPrimitive(ctx, a[0], "System." + suffix, StringValue(a[1]), GuestProvider(ctx, a[2]))))), BindingOrigin.Managed);
        }
        foreach (var suffix in new[] { "Int32", "Int64", "UInt64", "Single", "Double" })
            r.RegisterBinding(BindingKey.Static(T, "Parse" + suffix, "System.String", "System.Int32", "System.Object"),
                (ctx, a) => BclCall(() => ParseProviderNumber(ctx, "System." + suffix, Text(a[0]), (NumberStyles)a[1].AsInt32, GuestProvider(ctx, a[2]))), BindingOrigin.Managed);
        foreach (var type in new[] { "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64" }) {
            r.RegisterBinding(BindingKey.Static(type, "Parse", "System.String", "System.IFormatProvider"),
                (ctx, a) => BclCall(() => ParseProviderNumber(ctx, type, Text(a[0]), NumberStyles.Integer, GuestProvider(ctx, a[1]))), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Static(type, "Parse", "System.String", "System.Globalization.NumberStyles", "System.IFormatProvider"),
                (ctx, a) => BclCall(() => ParseProviderNumber(ctx, type, Text(a[0]), (NumberStyles)a[1].AsInt32, GuestProvider(ctx, a[2]))), BindingOrigin.Managed);
        }
    }

    private static StackSlot ParseProviderNumber(IntrinsicContext ctx, string type, string text, NumberStyles styles, IFormatProvider provider) {
        ctx.Heap.ChargeHostWork(text.Length);
        return type switch {
            "System.Byte" => StackSlot.OfInt32(byte.Parse(text, styles, provider)),
            "System.SByte" => StackSlot.OfInt32(sbyte.Parse(text, styles, provider)),
            "System.Int16" => StackSlot.OfInt32(short.Parse(text, styles, provider)),
            "System.UInt16" => StackSlot.OfInt32(ushort.Parse(text, styles, provider)),
            "System.Int32" => StackSlot.OfInt32(int.Parse(text, styles, provider)),
            "System.UInt32" => StackSlot.OfInt32(unchecked((int)uint.Parse(text, styles, provider))),
            "System.Int64" => StackSlot.OfInt64(long.Parse(text, styles, provider)),
            "System.UInt64" => StackSlot.OfInt64(unchecked((long)ulong.Parse(text, styles, provider))),
            "System.Single" => StackSlot.OfFloat(float.Parse(text, styles, provider)),
            "System.Double" => StackSlot.OfFloat(double.Parse(text, styles, provider)),
            _ => throw new InvalidOperationException(type),
        };
    }

    private static void RegisterNumberFormatInfo(IntrinsicRegistry r) {
        const string T = "System.Globalization.NumberFormatInfo";
        r.Register(IntrinsicKey.Instance(T, ".ctor", 0), static (_, a) => { SetBclValue(a[0], new NumberFormatInfo()); return null; });
        r.RegisterBinding(BindingKey.Instance(T, ".ctor"), static (_, a) => { SetBclValue(a[0], new NumberFormatInfo()); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Clone"), static (ctx, a) => WrapBcl(ctx, T, BclValue<NumberFormatInfo>(a[0]).Clone()), BindingOrigin.Managed);
        foreach (var property in new[] { "NumberDecimalSeparator", "NumberGroupSeparator", "NegativeSign", "PositiveSign", "CurrencySymbol", "CurrencyDecimalSeparator", "PercentSymbol", "NaNSymbol", "PositiveInfinitySymbol", "NegativeInfinitySymbol" }) {
            var info = typeof(NumberFormatInfo).GetProperty(property)!;
            r.RegisterBinding(BindingKey.Instance(T, "get_" + property), (ctx, a) => StackSlot.OfObject(ctx.MakeString((string)info.GetValue(BclValue<NumberFormatInfo>(a[0]))!)), BindingOrigin.Managed);
            // Use strongly typed setters to preserve host exception types (reflection wraps them).
            var setter = info.SetMethod!.CreateDelegate<Action<NumberFormatInfo, string>>();
            r.RegisterBinding(BindingKey.Instance(T, "set_" + property, "System.String"), (_, a) => BclCall(() => { setter(BclValue<NumberFormatInfo>(a[0]), StringValue(a[1])!); return null; }), BindingOrigin.Managed);
        }
        r.RegisterBinding(BindingKey.Instance(T, "get_IsReadOnly"), static (_, a) => StackSlot.OfInt32(BclValue<NumberFormatInfo>(a[0]).IsReadOnly ? 1 : 0), BindingOrigin.Managed);
    }
}
