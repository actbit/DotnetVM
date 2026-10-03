using System.Text;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterEncodingState(IntrinsicRegistry r) {
        const string EncoderType = "System.Text.Encoder";
        const string DecoderType = "System.Text.Decoder";
        foreach (var type in new[] { "System.Text.Encoding", "System.Text.UTF8Encoding" }) {
            BclFace(r, type, "GetEncoder", true, [], static (ctx, a) => WrapBcl(ctx, EncoderType, BclValue<Encoding>(a[0]).GetEncoder()));
            BclFace(r, type, "GetDecoder", true, [], static (ctx, a) => WrapBcl(ctx, DecoderType, BclValue<Encoding>(a[0]).GetDecoder()));
        }
        BclFace(r, EncoderType, "Reset", true, [], static (_, a) => { BclValue<Encoder>(a[0]).Reset(); return null; });
        BclFace(r, DecoderType, "Reset", true, [], static (_, a) => { BclValue<Decoder>(a[0]).Reset(); return null; });
        foreach (var encode in new[] { false, true }) {
            var type = encode ? EncoderType : DecoderType;
            var sourceType = encode ? "System.Char[]" : "System.Byte[]";
            var destType = encode ? "System.Byte[]" : "System.Char[]";
            BclFace(r, type, "Convert", true, [sourceType, "System.Int32", "System.Int32", destType, "System.Int32", "System.Int32", "System.Boolean", "System.Int32&", "System.Int32&", "System.Boolean&"], (ctx, a) => {
                var source = RequireEncodingArray(a[1], "source"); var dest = RequireEncodingArray(a[4], "destination");
                CheckSlice(source.Length, a[2].AsInt32, a[3].AsInt32); CheckSlice(dest.Length, a[5].AsInt32, a[6].AsInt32);
                var reference = StackSlot.OfByRef(VmByRef.ArrayElement(dest, a[5].AsInt32));
                ctx.Heap.ChargeHostBuffer(a[6].AsInt32);
                int consumed, written; bool completed;
                if (encode) {
                    var text = EncodingText(ctx, a[1], sourceType); var bytes = new byte[a[6].AsInt32];
                    BclValue<Encoder>(a[0]).Convert(text.AsSpan(a[2].AsInt32, a[3].AsInt32), bytes, a[7].AsInt32 != 0, out consumed, out written, out completed);
                    WriteByteSpan(reference, bytes.AsSpan(0, written));
                } else {
                    var bytes = EncodingBytes(ctx, a[1], sourceType); var chars = new char[a[6].AsInt32];
                    BclValue<Decoder>(a[0]).Convert(bytes.AsSpan(a[2].AsInt32, a[3].AsInt32), chars, a[7].AsInt32 != 0, out consumed, out written, out completed);
                    WriteChars(reference, new string(chars, 0, written));
                }
                WriteWritten(a[8], consumed); WriteWritten(a[9], written); WriteWritten(a[10], completed ? 1 : 0); return null;
            });
            BclFace(r, type, encode ? "GetByteCount" : "GetCharCount", true, [sourceType, "System.Int32", "System.Int32", "System.Boolean"], (ctx, a) => {
                CheckSlice(RequireEncodingArray(a[1], "source").Length, a[2].AsInt32, a[3].AsInt32);
                return StackSlot.OfInt32(encode
                    ? BclValue<Encoder>(a[0]).GetByteCount(EncodingText(ctx, a[1], sourceType).AsSpan(a[2].AsInt32, a[3].AsInt32), a[4].AsInt32 != 0)
                    : BclValue<Decoder>(a[0]).GetCharCount(EncodingBytes(ctx, a[1], sourceType).AsSpan(a[2].AsInt32, a[3].AsInt32), a[4].AsInt32 != 0));
            });
            BclFace(r, type, encode ? "GetBytes" : "GetChars", true, [sourceType, "System.Int32", "System.Int32", destType, "System.Int32", "System.Boolean"], (ctx, a) => {
                CheckSlice(RequireEncodingArray(a[1], "source").Length, a[2].AsInt32, a[3].AsInt32);
                var dest = RequireEncodingArray(a[4], "destination"); CheckSlice(dest.Length, a[5].AsInt32, 0);
                var reference = StackSlot.OfByRef(VmByRef.ArrayElement(dest, a[5].AsInt32)); var available = dest.Length - a[5].AsInt32;
                ctx.Heap.ChargeHostBuffer(available); int written;
                if (encode) {
                    var text = EncodingText(ctx, a[1], sourceType); var bytes = new byte[available];
                    written = BclValue<Encoder>(a[0]).GetBytes(text.AsSpan(a[2].AsInt32, a[3].AsInt32), bytes, a[6].AsInt32 != 0);
                    WriteByteSpan(reference, bytes.AsSpan(0, written));
                } else {
                    var bytes = EncodingBytes(ctx, a[1], sourceType); var chars = new char[available];
                    written = BclValue<Decoder>(a[0]).GetChars(bytes.AsSpan(a[2].AsInt32, a[3].AsInt32), chars, a[6].AsInt32 != 0);
                    WriteChars(reference, new string(chars, 0, written));
                }
                return StackSlot.OfInt32(written);
            });
        }
        BclFace(r, EncoderType, "GetByteCount", true, ["System.ReadOnlySpan`1<System.Char>", "System.Boolean"], static (ctx, a) =>
            StackSlot.OfInt32(BclValue<Encoder>(a[0]).GetByteCount(EncodingText(ctx, a[1], "span").AsSpan(), a[2].AsInt32 != 0)));
        BclFace(r, DecoderType, "GetCharCount", true, ["System.ReadOnlySpan`1<System.Byte>", "System.Boolean"], static (ctx, a) =>
            StackSlot.OfInt32(BclValue<Decoder>(a[0]).GetCharCount(EncodingBytes(ctx, a[1], "span"), a[2].AsInt32 != 0)));
        BclFace(r, EncoderType, "GetBytes", true, ["System.ReadOnlySpan`1<System.Char>", "System.Span`1<System.Byte>", "System.Boolean"], static (ctx, a) => {
            var source = EncodingText(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var bytes = new byte[dest.Length];
            var written = BclValue<Encoder>(a[0]).GetBytes(source.AsSpan(), bytes, a[3].AsInt32 != 0);
            WriteByteSpan(dest.Reference, bytes.AsSpan(0, written)); return StackSlot.OfInt32(written);
        });
        BclFace(r, DecoderType, "GetChars", true, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Char>", "System.Boolean"], static (ctx, a) => {
            var source = EncodingBytes(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var chars = new char[dest.Length];
            var written = BclValue<Decoder>(a[0]).GetChars(source, chars, a[3].AsInt32 != 0);
            WriteChars(dest.Reference, new string(chars, 0, written)); return StackSlot.OfInt32(written);
        });
        BclFace(r, EncoderType, "Convert", true, ["System.ReadOnlySpan`1<System.Char>", "System.Span`1<System.Byte>", "System.Boolean", "System.Int32&", "System.Int32&", "System.Boolean&"], static (ctx, a) => {
            var source = EncodingText(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var bytes = new byte[dest.Length];
            BclValue<Encoder>(a[0]).Convert(source.AsSpan(), bytes, a[3].AsInt32 != 0, out var consumed, out var written, out var completed);
            WriteByteSpan(dest.Reference, bytes.AsSpan(0, written));
            WriteWritten(a[4], consumed); WriteWritten(a[5], written); WriteWritten(a[6], completed ? 1 : 0); return null;
        });
        BclFace(r, DecoderType, "Convert", true, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Char>", "System.Boolean", "System.Int32&", "System.Int32&", "System.Boolean&"], static (ctx, a) => {
            var source = EncodingBytes(ctx, a[1], "span"); var dest = ReadSpanParts(a[2]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var chars = new char[dest.Length];
            BclValue<Decoder>(a[0]).Convert(source, chars, a[3].AsInt32 != 0, out var consumed, out var written, out var completed);
            WriteChars(dest.Reference, new string(chars, 0, written));
            WriteWritten(a[4], consumed); WriteWritten(a[5], written); WriteWritten(a[6], completed ? 1 : 0); return null;
        });
    }
}
