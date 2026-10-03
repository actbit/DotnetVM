using System.Buffers.Binary;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // Software vector values use the actual CoreLib nested ulong layout. Hardware
    // capability getters continue to be false, so ISA-specific paths stay disabled.
    private static void RegisterSoftwareVectors(IntrinsicRegistry r) {
        foreach (var width in new[] { 8, 16, 32, 64 }) {
            var type = "System.Runtime.Intrinsics.Vector" + width * 8;
            var generic = type + "`1";
            var argument = generic + "<!!0>";
            string Element(IntrinsicContext ctx) => ctx.MethodTypeArgAt(0) is { Length: > 0 } name ? name : ctx.ClassTypeArgAt(0);
            r.RegisterBinding(BindingKey.Static(generic, "get_Count"), (ctx, _) => {
                var element = Element(ctx);
                if (!VectorElementSupported(element)) throw new UnhandledGuestException("System.NotSupportedException", "Unsupported vector element type.");
                return StackSlot.OfInt32(width / SlotStride(element));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(generic, "get_IsSupported"), (ctx, _) => StackSlot.OfInt32(VectorElementSupported(Element(ctx)) ? 1 : 0), BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(generic, "get_Zero"), (ctx, _) => MakeVector(ctx, width, Element(ctx), new byte[width]), BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(type, "Create", "!!0"), (ctx, a) => {
                var element = Element(ctx); var stride = SlotStride(element);
                ctx.Heap.ChargeHostWork(width / stride);
                var bytes = new byte[width];
                var pointer = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = bytes } };
                for (var i = 0; i < width; i += stride) WriteNativeElement(pointer, i, a[0], element);
                return MakeVector(ctx, width, element, bytes);
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(type, "GetElement", argument, "System.Int32"), (ctx, a) => VectorElement(ctx, a[0], a[1].AsInt32, width, Element(ctx)), BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(generic, "GetElement", "System.Int32"), (ctx, a) => VectorElement(ctx, a[0], a[1].AsInt32, width, Element(ctx)), BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(type, "WithElement", argument, "System.Int32", "!!0"), (ctx, a) => {
                var element = Element(ctx); var stride = SlotStride(element);
                if ((uint)a[1].AsInt32 >= (uint)(width / stride)) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "index");
                var bytes = VectorBytes(a[0], width);
                WriteNativeElement(new VmNativePointer { Memory = new VmLocallocMemory { Bytes = bytes } }, a[1].AsInt32 * stride, a[2], element);
                return MakeVector(ctx, width, element, bytes);
            }, BindingOrigin.InternalCall);
            foreach (var method in new[] { "Add", "Subtract", "Multiply" }) {
                StackSlot? Operation(IntrinsicContext ctx, StackSlot[] a) {
                    var element = Element(ctx); var stride = SlotStride(element);
                    var left = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = VectorBytes(a[0], width) } };
                    var right = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = VectorBytes(a[1], width) } };
                    var bytes = new byte[width];
                    var dest = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = bytes } };
                    ctx.Heap.ChargeHostWork(width / stride);
                    for (var i = 0; i < width; i += stride) {
                        var x = ReadNativeElement(left, i, element); var y = ReadNativeElement(right, i, element);
                        var v = element is "System.Single" or "System.Double"
                            ? StackSlot.OfFloat(method == "Add" ? x.DoubleValue + y.DoubleValue : method == "Subtract" ? x.DoubleValue - y.DoubleValue : x.DoubleValue * y.DoubleValue)
                            : StackSlot.OfInt64(method == "Add" ? unchecked(x.Int64Value + y.Int64Value) : method == "Subtract" ? unchecked(x.Int64Value - y.Int64Value) : unchecked(x.Int64Value * y.Int64Value));
                        WriteNativeElement(dest, i, v, element);
                    }
                    return MakeVector(ctx, width, element, bytes);
                }
                r.RegisterBinding(BindingKey.Static(type, method, argument, argument), Operation, BindingOrigin.InternalCall);
                r.RegisterBinding(BindingKey.Static(generic, "op_" + (method == "Add" ? "Addition" : method == "Subtract" ? "Subtraction" : "Multiply"), generic + "<!0>", generic + "<!0>"), Operation, BindingOrigin.InternalCall);
            }
        }
    }

    private static bool VectorElementSupported(string type) => type is "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.IntPtr" or "System.UIntPtr";

    private static StackSlot VectorElement(IntrinsicContext ctx, in StackSlot value, int index, int width, string element) {
        var stride = SlotStride(element);
        if ((uint)index >= (uint)(width / stride)) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "index");
        var pointer = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = VectorBytes(value, width) } };
        return ReadNativeElement(pointer, index * stride, element);
    }

    private static byte[] VectorBytes(in StackSlot slot, int width) {
        var value = slot.ObjectValue is VmByRef byRef ? byRef.Read() : slot;
        if (value.ObjectValue is not VmStructValue vector) throw new UnhandledGuestException("System.InvalidProgramException", "Expected a vector value.");
        var bytes = new byte[width];
        if (width <= 16) {
            for (var i = 0; i < width / 8; i++) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8), vector.Fields[i].Int64Value);
        } else {
            VectorBytes(vector.Fields[0], width / 2).CopyTo(bytes, 0);
            VectorBytes(vector.Fields[1], width / 2).CopyTo(bytes, width / 2);
        }
        return bytes;
    }

    private static StackSlot MakeVector(IntrinsicContext ctx, int width, string element, byte[] bytes) {
        if (!VectorElementSupported(element)) throw new UnhandledGuestException("System.NotSupportedException", "Unsupported vector element.");
        var definition = FindAnyType(ctx, "System.Runtime.Intrinsics.Vector" + width * 8 + "`1") ?? throw new InvalidOperationException("Vector type is unavailable.");
        var elementType = FindAnyType(ctx, element) ?? throw new InvalidOperationException(element);
        var constructed = new VmConstructedType { Definition = definition, TypeArguments = [elementType] };
        StackSlot[] fields = width <= 16
            ? Enumerable.Range(0, width / 8).Select(i => StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(i * 8)))).ToArray()
            : [MakeVector(ctx, width / 2, element, bytes[..(width / 2)]), MakeVector(ctx, width / 2, element, bytes[(width / 2)..])];
        return StackSlot.OfValueType(new VmStructValue(constructed, fields, [elementType]));
    }
}
