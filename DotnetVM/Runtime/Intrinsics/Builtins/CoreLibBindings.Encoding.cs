using System.Text;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterEncoding(IntrinsicRegistry r) {
        RegisterEncodingState(r);
        const string E = "System.Text.Encoding";
        const string U = "System.Text.UTF8Encoding";
        foreach (var count in new[] { 0, 1, 2 })
            r.Register(IntrinsicKey.Instance(U, ".ctor", count), (_, a) => { SetBclValue(a[0], new UTF8Encoding(a.Length > 1 && a[1].AsInt32 != 0, a.Length > 2 && a[2].AsInt32 != 0)); return null; });
        r.RegisterBinding(BindingKey.Instance(U, ".ctor"), static (_, a) => { SetBclValue(a[0], new UTF8Encoding()); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(U, ".ctor", "System.Boolean"), static (_, a) => { SetBclValue(a[0], new UTF8Encoding(a[1].AsInt32 != 0)); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(U, ".ctor", "System.Boolean", "System.Boolean"), static (_, a) => { SetBclValue(a[0], new UTF8Encoding(a[1].AsInt32 != 0, a[2].AsInt32 != 0)); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(E, "get_UTF8"), static (ctx, _) => WrapBcl(ctx, U, Encoding.UTF8), BindingOrigin.Managed);
        foreach (var type in new[] { E, U }) {
            void Face(string name, string[] parameters, IntrinsicImpl impl) => r.RegisterBinding(BindingKey.Instance(type, name, parameters),
                (ctx, a) => BclCall(() => impl(ctx, a)), BindingOrigin.Managed);
            Face("GetPreamble", [], static (ctx, a) => StackSlot.OfObject(ctx.MakeByteArray(BclValue<Encoding>(a[0]).GetPreamble())));
            Face("get_WebName", [], static (ctx, a) => StackSlot.OfObject(ctx.MakeString(BclValue<Encoding>(a[0]).WebName)));
            Face("get_EncodingName", [], static (ctx, a) => StackSlot.OfObject(ctx.MakeString(BclValue<Encoding>(a[0]).EncodingName)));
            Face("get_CodePage", [], static (_, a) => StackSlot.OfInt32(BclValue<Encoding>(a[0]).CodePage));
            Face("get_IsReadOnly", [], static (_, a) => StackSlot.OfInt32(BclValue<Encoding>(a[0]).IsReadOnly ? 1 : 0));
            Face("Clone", [], static (ctx, a) => WrapBcl(ctx, U, BclValue<Encoding>(a[0]).Clone()));
            Face("GetChars", ["System.Byte[]"], static (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[1], "System.Byte[]"); ctx.Heap.ChargeHostBuffer(bytes.Length);
                return StackSlot.OfObject(ctx.MakeCharArray(BclValue<Encoding>(a[0]).GetChars(bytes)));
            });
            Face("GetByteCount", ["System.Char[]", "System.Int32", "System.Int32"], static (ctx, a) => {
                var text = EncodingText(ctx, a[1], "System.Char[]"); CheckSlice(text.Length, a[2].AsInt32, a[3].AsInt32);
                return StackSlot.OfInt32(BclValue<Encoding>(a[0]).GetByteCount(text.AsSpan(a[2].AsInt32, a[3].AsInt32)));
            });
            Face("GetBytes", ["System.Char[]", "System.Int32", "System.Int32"], static (ctx, a) => {
                var text = EncodingText(ctx, a[1], "System.Char[]"); CheckSlice(text.Length, a[2].AsInt32, a[3].AsInt32);
                var source = text.AsSpan(a[2].AsInt32, a[3].AsInt32); var encoding = BclValue<Encoding>(a[0]);
                var count = encoding.GetByteCount(source); ctx.Heap.ChargeHostBuffer(count); var bytes = new byte[count]; encoding.GetBytes(source, bytes);
                return StackSlot.OfObject(ctx.MakeByteArray(bytes));
            });
            Face("GetMaxByteCount", ["System.Int32"], static (_, a) => StackSlot.OfInt32(BclValue<Encoding>(a[0]).GetMaxByteCount(a[1].AsInt32)));
            Face("GetMaxCharCount", ["System.Int32"], static (_, a) => StackSlot.OfInt32(BclValue<Encoding>(a[0]).GetMaxCharCount(a[1].AsInt32)));
            foreach (var input in new[] { "System.String", "System.Char[]", "System.ReadOnlySpan`1<System.Char>" }) {
                Face("GetByteCount", [input], (ctx, a) => {
                    var text = EncodingText(ctx, a[1], input);
                    return StackSlot.OfInt32(BclValue<Encoding>(a[0]).GetByteCount(text));
                });
                if (!input.Contains("Span", StringComparison.Ordinal))
                    Face("GetBytes", [input], (ctx, a) => {
                        var text = EncodingText(ctx, a[1], input);
                        var encoding = BclValue<Encoding>(a[0]);
                        var count = encoding.GetByteCount(text);
                        ctx.Heap.ChargeHostBuffer(count);
                        return StackSlot.OfObject(ctx.MakeByteArray(encoding.GetBytes(text)));
                    });
            }
            foreach (var input in new[] { "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>" }) {
                Face("GetCharCount", [input], (ctx, a) => StackSlot.OfInt32(BclValue<Encoding>(a[0]).GetCharCount(EncodingBytes(ctx, a[1], input))));
                Face("GetString", [input], (ctx, a) => {
                    var bytes = EncodingBytes(ctx, a[1], input);
                    ctx.Heap.ChargeHostBuffer(bytes.Length);
                    return StackSlot.OfObject(ctx.MakeString(BclValue<Encoding>(a[0]).GetString(bytes)));
                });
            }
            Face("GetString", ["System.Byte[]", "System.Int32", "System.Int32"], static (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[1], "System.Byte[]");
                ctx.Heap.ChargeHostBuffer(bytes.Length);
                return StackSlot.OfObject(ctx.MakeString(BclValue<Encoding>(a[0]).GetString(bytes, a[2].AsInt32, a[3].AsInt32)));
            });
            Face("GetBytes", ["System.String", "System.Int32", "System.Int32", "System.Byte[]", "System.Int32"], static (ctx, a) => {
                var text = StringValue(a[1]) ?? throw new ArgumentNullException("s");
                var offset = a[2].AsInt32; var count = a[3].AsInt32;
                CheckSlice(text.Length, offset, count);
                var destination = RequireEncodingArray(a[4], "bytes");
                var start = a[5].AsInt32;
                CheckSlice(destination.Length, start, 0);
                ctx.Heap.ChargeHostWork(count);
                var encoding = BclValue<Encoding>(a[0]);
                var length = encoding.GetByteCount(text.AsSpan(offset, count));
                if (length > destination.Length - start) throw new ArgumentException("Destination is too small.", "bytes");
                ctx.Heap.ChargeHostBuffer(length);
                var bytes = new byte[length];
                encoding.GetBytes(text.AsSpan(offset, count), bytes);
                for (var i = 0; i < length; i++) destination.Elements[start + i] = StackSlot.OfInt32(bytes[i]);
                return StackSlot.OfInt32(length);
            });
            Face("GetChars", ["System.Byte[]", "System.Int32", "System.Int32", "System.Char[]", "System.Int32"], static (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[1], "System.Byte[]");
                var offset = a[2].AsInt32; var count = a[3].AsInt32;
                CheckSlice(bytes.Length, offset, count);
                var dest = RequireEncodingArray(a[4], "chars"); var start = a[5].AsInt32;
                CheckSlice(dest.Length, start, 0);
                var encoding = BclValue<Encoding>(a[0]);
                var length = encoding.GetCharCount(bytes, offset, count);
                if (length > dest.Length - start) throw new ArgumentException("Destination is too small.", "chars");
                ctx.Heap.ChargeHostBuffer(length);
                var chars = encoding.GetChars(bytes, offset, count);
                for (var i = 0; i < chars.Length; i++) dest.Elements[start + i] = StackSlot.OfInt32(chars[i]);
                return StackSlot.OfInt32(chars.Length);
            });
            Face("GetBytes", ["System.ReadOnlySpan`1<System.Char>", "System.Span`1<System.Byte>"], static (ctx, a) => {
                var text = EncodingText(ctx, a[1], "System.ReadOnlySpan`1<System.Char>");
                var dest = ReadSpanParts(a[2]);
                var encoding = BclValue<Encoding>(a[0]);
                var count = encoding.GetByteCount(text);
                if (count > dest.Length) throw new ArgumentException("Destination is too small.", "bytes");
                ctx.Heap.ChargeHostBuffer(count);
                var bytes = encoding.GetBytes(text);
                WriteByteSpan(dest.Reference, bytes);
                return StackSlot.OfInt32(bytes.Length);
            });
            Face("GetChars", ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Char>"], static (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[1], "System.ReadOnlySpan`1<System.Byte>");
                var dest = ReadSpanParts(a[2]);
                var encoding = BclValue<Encoding>(a[0]);
                var count = encoding.GetCharCount(bytes);
                if (count > dest.Length) throw new ArgumentException("Destination is too small.", "chars");
                ctx.Heap.ChargeHostBuffer(count);
                var text = encoding.GetString(bytes);
                WriteChars(dest.Reference, text);
                return StackSlot.OfInt32(text.Length);
            });
        }
    }

    private static VmArray RequireEncodingArray(in StackSlot slot, string name) => slot.ObjectValue as VmArray ?? throw new ArgumentNullException(name);
    private static void CheckSlice(int length, int offset, int count) {
        if ((uint)offset > (uint)length || (uint)count > (uint)(length - offset)) throw new ArgumentOutOfRangeException();
    }
    private static string EncodingText(IntrinsicContext ctx, in StackSlot slot, string type) {
        if (type == "System.String") {
            var text = StringValue(slot) ?? throw new ArgumentNullException("s");
            ctx.Heap.ChargeHostWork(text.Length);
            return text;
        }
        var length = type == "System.Char[]" ? RequireEncodingArray(slot, "chars").Length : ReadSpanParts(slot).Length;
        ctx.Heap.ChargeHostWork(length);
        ctx.Heap.ChargeHostBuffer(length);
        if (type != "System.Char[]") return ReadCharSpanOrEmpty(slot);
        var array = RequireEncodingArray(slot, "chars");
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)array.Elements[i].AsInt32;
        return new string(chars);
    }
    private static byte[] EncodingBytes(IntrinsicContext ctx, in StackSlot slot, string type) {
        if (type == "System.Byte[]") {
            var array = RequireEncodingArray(slot, "bytes");
            ctx.Heap.ChargeHostWork(array.Length);
            ctx.Heap.ChargeHostBuffer(array.Length);
            return ctx.ReadByteArray(slot);
        }
        var parts = ReadSpanParts(slot);
        ctx.Heap.ChargeHostWork(parts.Length);
        ctx.Heap.ChargeHostBuffer(parts.Length);
        var result = new byte[parts.Length];
        var (native, reference) = parts.Length == 0 ? (null, (VmByRef?)null) : ResolvePointerBase(parts.Reference, "byte span");
        if (native is not null) CheckSlice(native.Bytes.Length, native.ByteOffset, parts.Length);
        else if (reference is not null) CheckSlice(reference.Container.Length, reference.Index, parts.Length);
        for (var i = 0; i < result.Length; i++) result[i] = native is not null
            ? native.Bytes[native.ByteOffset + i] : (byte)reference!.Container[reference.Index + i].AsInt32;
        return result;
    }
    private static void WriteByteSpan(in StackSlot reference, ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return;
        var (native, location) = ResolvePointerBase(reference, "byte span");
        if (native is not null) {
            native.EnsureWritable();
            CheckSlice(native.Bytes.Length, native.ByteOffset, bytes.Length);
            bytes.CopyTo(native.Bytes.AsSpan(native.ByteOffset));
        } else {
            location!.EnsureWritable();
            CheckSlice(location.Container.Length, location.Index, bytes.Length);
            for (var i = 0; i < bytes.Length; i++) location.Container[location.Index + i] = StackSlot.OfInt32(bytes[i]);
        }
    }
}
