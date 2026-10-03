using System.Buffers;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterSearchValues(IntrinsicRegistry r) {
        foreach (var method in new[] { "IndexOfAny", "IndexOfAnyExcept" })
            r.RegisterBinding(BindingKey.Instance("System.Buffers.SearchValues`1", method, "System.ReadOnlySpan`1<!0>"), (ctx, a) => {
                var except = method.EndsWith("Except", StringComparison.Ordinal);
                if (ctx.ClassTypeArgAt(0) == "System.Byte") {
                    var input = EncodingBytes(ctx, a[1], "System.ReadOnlySpan`1<System.Byte>");
                    var values = BclValue<SearchValues<byte>>(a[0]);
                    return StackSlot.OfInt32(except ? input.AsSpan().IndexOfAnyExcept(values) : input.AsSpan().IndexOfAny(values));
                }
                var chars = EncodingText(ctx, a[1], "System.ReadOnlySpan`1<System.Char>");
                var charValues = BclValue<SearchValues<char>>(a[0]);
                return StackSlot.OfInt32(except ? chars.AsSpan().IndexOfAnyExcept(charValues) : chars.AsSpan().IndexOfAny(charValues));
            }, BindingOrigin.Managed);
        foreach (var element in new[] { "System.Byte", "System.Char" }) {
            var span = "System.ReadOnlySpan`1<" + element + ">";
            var search = "System.Buffers.SearchValues`1<" + element + ">";
            r.RegisterBinding(BindingKey.Static("System.Buffers.SearchValues", "Create", span), (ctx, a) => {
                object values = element == "System.Byte"
                    ? SearchValues.Create(EncodingBytes(ctx, a[0], span))
                    : SearchValues.Create(EncodingText(ctx, a[0], span).AsSpan());
                return WrapBcl(ctx, search, values);
            }, BindingOrigin.Managed);
            foreach (var method in new[] { "IndexOfAny", "IndexOfAnyExcept", "ContainsAny", "ContainsAnyExcept" })
                foreach (var owner in new[] { "System.ReadOnlySpan`1<", "System.Span`1<" })
                    r.RegisterBinding(BindingKey.Static("System.MemoryExtensions", method, owner + element + ">", search), (ctx, a) => {
                        int index;
                        var except = method.EndsWith("Except", StringComparison.Ordinal);
                        if (element == "System.Byte") {
                            var input = EncodingBytes(ctx, a[0], span);
                            var values = BclValue<SearchValues<byte>>(a[1]);
                            index = except ? input.AsSpan().IndexOfAnyExcept(values) : input.AsSpan().IndexOfAny(values);
                        } else {
                            var input = EncodingText(ctx, a[0], span);
                            var values = BclValue<SearchValues<char>>(a[1]);
                            index = except ? input.AsSpan().IndexOfAnyExcept(values) : input.AsSpan().IndexOfAny(values);
                        }
                        return StackSlot.OfInt32(method.StartsWith("Contains", StringComparison.Ordinal) ? index >= 0 ? 1 : 0 : index);
                    }, BindingOrigin.Managed);
        }
    }
}
