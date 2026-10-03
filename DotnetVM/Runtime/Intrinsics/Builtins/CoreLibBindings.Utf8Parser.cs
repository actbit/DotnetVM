using System.Buffers.Text;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterUtf8Parser(IntrinsicRegistry r) {
        BclFace(r, "System.Text.Unicode.Utf8", "FromUtf16", false, ["System.ReadOnlySpan`1<System.Char>", "System.Span`1<System.Byte>", "System.Int32&", "System.Int32&", "System.Boolean", "System.Boolean"], static (ctx, a) => {
            var input = EncodingText(ctx, a[0], "span"); var dest = ReadSpanParts(a[1]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var bytes = new byte[dest.Length];
            var status = System.Text.Unicode.Utf8.FromUtf16(input.AsSpan(), bytes, out var consumed, out var written, a[4].AsInt32 != 0, a[5].AsInt32 != 0);
            WriteByteSpan(dest.Reference, bytes.AsSpan(0, written)); WriteWritten(a[2], consumed); WriteWritten(a[3], written); return StackSlot.OfInt32((int)status);
        });
        BclFace(r, "System.Text.Unicode.Utf8", "ToUtf16", false, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Char>", "System.Int32&", "System.Int32&", "System.Boolean", "System.Boolean"], static (ctx, a) => {
            var input = EncodingBytes(ctx, a[0], "span"); var dest = ReadSpanParts(a[1]);
            ctx.Heap.ChargeHostBuffer(dest.Length); var chars = new char[dest.Length];
            var status = System.Text.Unicode.Utf8.ToUtf16(input, chars, out var consumed, out var written, a[4].AsInt32 != 0, a[5].AsInt32 != 0);
            WriteChars(dest.Reference, new string(chars, 0, written)); WriteWritten(a[2], consumed); WriteWritten(a[3], written); return StackSlot.OfInt32((int)status);
        });
        foreach (var type in new[] { "System.Int32", "System.Int64", "System.UInt32", "System.UInt64", "System.Single", "System.Double" })
            BclFace(r, "System.Buffers.Text.Utf8Parser", "TryParse", false, ["System.ReadOnlySpan`1<System.Byte>", type + "&", "System.Int32&", "System.Char"], (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[0], "span"); var format = (char)a[3].AsInt32; bool success; int consumed; StackSlot value;
                switch (type) {
                    case "System.Int32": success = Utf8Parser.TryParse(bytes, out int i, out consumed, format); value = StackSlot.OfInt32(i); break;
                    case "System.Int64": success = Utf8Parser.TryParse(bytes, out long l, out consumed, format); value = StackSlot.OfInt64(l); break;
                    case "System.UInt32": success = Utf8Parser.TryParse(bytes, out uint u, out consumed, format); value = StackSlot.OfInt32(unchecked((int)u)); break;
                    case "System.UInt64": success = Utf8Parser.TryParse(bytes, out ulong ul, out consumed, format); value = StackSlot.OfInt64(unchecked((long)ul)); break;
                    case "System.Single": success = Utf8Parser.TryParse(bytes, out float f, out consumed, format); value = StackSlot.OfFloat(f); break;
                    default: success = Utf8Parser.TryParse(bytes, out double d, out consumed, format); value = StackSlot.OfFloat(d); break;
                }
                (a[1].ObjectValue as VmByRef ?? throw new UnhandledGuestException("System.InvalidProgramException", "Parse destination is unavailable.")).Write(value);
                WriteWritten(a[2], consumed); return StackSlot.OfInt32(success ? 1 : 0);
            });
    }
}
