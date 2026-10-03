using System.Text.Encodings.Web;
using DotnetVM.Runtime.Execution;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterTextEncoder(IntrinsicRegistry r) {
        const string JS = "System.Text.Encodings.Web.JavaScriptEncoder";
        BclFace(r, JS, "get_Default", false, [], static (ctx, _) => WrapBcl(ctx, JS, JavaScriptEncoder.Default));
        BclFace(r, JS, "get_UnsafeRelaxedJsonEscaping", false, [], static (ctx, _) => WrapBcl(ctx, JS, JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
        foreach (var type in new[] { JS, "System.Text.Encodings.Web.TextEncoder" }) {
            BclFace(r, type, "WillEncode", true, ["System.Int32"], static (_, a) => StackSlot.OfInt32(BclValue<TextEncoder>(a[0]).WillEncode(a[1].AsInt32) ? 1 : 0));
            BclFace(r, type, "Encode", true, ["System.ReadOnlySpan`1<System.Char>", "System.Span`1<System.Char>", "System.Int32&", "System.Int32&", "System.Boolean"], static (ctx, a) => {
                var source = EncodingText(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
                ctx.Heap.ChargeHostBuffer(dest.Length); ctx.Heap.ChargeHostWork(dest.Length); var chars = new char[dest.Length];
                var status = BclValue<TextEncoder>(a[0]).Encode(source.AsSpan(), chars, out var consumed, out var written, a[5].AsInt32 != 0);
                WriteChars(dest.Reference, new string(chars, 0, written)); WriteWritten(a[3], consumed); WriteWritten(a[4], written); return StackSlot.OfInt32((int)status);
            });
            BclFace(r, type, "FindFirstCharacterToEncode", true, ["System.Char*", "System.Int32"], static (ctx, a) => {
                var count = a[2].AsInt32; if (count < 0) throw new ArgumentOutOfRangeException("textLength");
                ctx.Heap.ChargeHostWork(count); var encoder = BclValue<TextEncoder>(a[0]);
                for (var i = 0; i < count; i++) {
                    var ch = (char)ReadCharAt(a[1], i); var scalar = (int)ch;
                    if (char.IsSurrogate(ch)) {
                        if (!char.IsHighSurrogate(ch) || i + 1 >= count || !char.IsLowSurrogate((char)ReadCharAt(a[1], i + 1))) return StackSlot.OfInt32(i);
                        scalar = char.ConvertToUtf32(ch, (char)ReadCharAt(a[1], i + 1));
                    }
                    if (encoder.WillEncode(scalar)) return StackSlot.OfInt32(i);
                    if (scalar > 0xFFFF) i++;
                }
                return StackSlot.OfInt32(-1);
            });
            BclFace(r, type, "FindFirstCharacterToEncodeUtf8", true, ["System.ReadOnlySpan`1<System.Byte>"], static (ctx, a) => StackSlot.OfInt32(BclValue<TextEncoder>(a[0]).FindFirstCharacterToEncodeUtf8(EncodingBytes(ctx, a[1], "span"))));
            BclFace(r, type, "EncodeUtf8", true, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Int32&", "System.Int32&", "System.Boolean"], static (ctx, a) => {
                var source = EncodingBytes(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
                ctx.Heap.ChargeHostBuffer(dest.Length); ctx.Heap.ChargeHostWork(dest.Length); var bytes = new byte[dest.Length];
                var status = BclValue<TextEncoder>(a[0]).EncodeUtf8(source, bytes, out var consumed, out var written, a[5].AsInt32 != 0);
                WriteByteSpan(dest.Reference, bytes.AsSpan(0, written)); WriteWritten(a[3], consumed); WriteWritten(a[4], written); return StackSlot.OfInt32((int)status);
            });
        }
    }
}
