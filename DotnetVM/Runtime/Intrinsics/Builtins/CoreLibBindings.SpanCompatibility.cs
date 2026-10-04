using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterSpanCompatibility(IntrinsicRegistry r) {
        RegisterUtf8Parser(r);
        const string M = "System.Runtime.InteropServices.MemoryMarshal";
        r.RegisterBinding(BindingKey.Static("System.SpanHelpers", "ClearWithoutReferences", "System.Byte&", "System.UIntPtr"),
            static (ctx, a) => ClearSpanMemory(ctx, a, references: false), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.SpanHelpers", "ClearWithReferences", "System.IntPtr&", "System.UIntPtr"),
            static (ctx, a) => ClearSpanMemory(ctx, a, references: true), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(M, "CreateSpan", "!!0&", "System.Int32"),
            static (ctx, a) => CreateReadOnlySpanFromReference(ctx, a, readOnly: false), BindingOrigin.InternalCall);
        foreach (var type in new[] { "System.Span`1<!!0>", "System.ReadOnlySpan`1<!!0>" })
            r.RegisterBinding(BindingKey.Static(M, "GetReference", type), static (_, a) => ReadSpanParts(a[0]).Reference, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "NullRef"), static (_, _) => StackSlot.OfByRef(new VmByRef([], 0)), BindingOrigin.InternalCall);
        foreach (var offsetType in new[] { "System.Int32", "System.IntPtr", "System.UIntPtr" })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "Subtract", "!!0&", offsetType),
                static (ctx, a) => AddImpl(ctx, [a[0], StackSlot.OfInt64(checked(-a[1].Int64Value))], elementStride: true), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "ByteOffset", "!!0&", "!!0&"),
            static (ctx, a) => StackSlot.OfNativeInt(ReferenceByteOffset(ctx, a)), BindingOrigin.InternalCall);
        foreach (var method in new[] { "IsAddressGreaterThan", "IsAddressLessThan" })
            r.RegisterBinding(BindingKey.Static(UnsafeType, method, "!!0&", "!!0&"),
                (ctx, a) => StackSlot.OfInt32((method == "IsAddressLessThan" ? ReferenceByteOffset(ctx, a) > 0 : ReferenceByteOffset(ctx, a) < 0) ? 1 : 0), BindingOrigin.InternalCall);
        RegisterSoftwareVectors(r);
        RegisterSearchValues(r);
        foreach (var kind in new[] { "System.Memory`1", "System.ReadOnlyMemory`1" })
            r.RegisterBinding(BindingKey.Instance(kind, "get_Span"), (ctx, a) => MemorySpan(ctx, a[0], kind.Contains("ReadOnly", StringComparison.Ordinal)), BindingOrigin.Managed);
    }

    private static StackSlot? ClearSpanMemory(IntrinsicContext ctx, StackSlot[] a, bool references) {
        var count = a[1].Int64Value;
        if (count == 0) return null;
        if (count < 0 || count > int.MaxValue) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "length");
        var (native, location) = ResolvePointerBase(a[0], "Span.Clear");
        ctx.Heap.ChargeHostWork(count);
        var bytes = references ? checked(count * VmPrimitiveTypes.NativeIntSizeBytes) : count;
        if (native is not null) {
            if (bytes > int.MaxValue) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "length");
            native.EnsureWritable(); CheckSlice(native.Bytes.Length, native.ByteOffset, (int)bytes);
            native.Bytes.AsSpan(native.ByteOffset, (int)bytes).Clear();
        } else {
            location!.EnsureWritable();
            var type = (location.Owner as VmArray)?.ArrayType.ElementType;
            var stride = type is null ? references ? VmPrimitiveTypes.NativeIntSizeBytes : 1
                : !type.IsValueType ? VmPrimitiveTypes.NativeIntSizeBytes : ContainsReferences(type) ? MemoryOps.SizeOfType(type) : MemoryOps.SizeOfRawType(type);
            if (bytes % stride != 0 || bytes / stride > int.MaxValue) throw new UnhandledGuestException("System.NotSupportedException", "Partial slot clear.");
            var elements = (int)(bytes / stride);
            CheckSlice(location.Container.Length, location.Index, elements);
            var zero = type is null ? default : new ObjectModel().DefaultForType(type, (type as VmClassType)?.Loader ?? ctx.Types);
            for (var i = 0; i < elements; i++) {
                var current = location.Container[location.Index + i];
                location.Container[location.Index + i] = type is not null
                    ? zero.ObjectValue is VmStructValue value ? StackSlot.OfValueType(value.Clone()) : zero
                    : references ? StackSlot.Null : current.Kind == StackKind.Float ? StackSlot.OfFloat(0) : current.Kind == StackKind.Int64 ? StackSlot.OfInt64(0) : current.Kind == StackKind.NativeInt ? StackSlot.OfNativeInt(0) : StackSlot.OfInt32(0);
            }
        }
        return null;
    }

    private static StackSlot MemorySpan(IntrinsicContext ctx, in StackSlot value, bool readOnly) {
        var slot = value.ObjectValue is VmByRef byRef ? byRef.Read() : value;
        if (slot.ObjectValue is not VmStructValue memory) throw new InvalidOperationException("Expected Memory<T>.");
        var obj = memory.Fields[0].ObjectValue;
        var index = memory.Fields[1].AsInt32 & int.MaxValue;
        var length = memory.Fields[2].AsInt32;
        var reference = obj switch {
            VmString text => StackSlot.OfObject(new VmNativePointer { Memory = text.PointerMemory, ByteOffset = VmString.CharDataByteOffset + index * 2, IsReadOnly = readOnly }),
            VmArray array => StackSlot.OfByRef(VmByRef.ArrayElement(array, index, readOnly)),
            null when length == 0 => StackSlot.OfByRef(new VmByRef([], 0, readOnly)),
            _ => throw new UnhandledGuestException("System.NotSupportedException", "Unsupported memory owner."),
        };
        var element = memory.TypeArguments.FirstOrDefault() ?? ctx.ClassTypeArguments.FirstOrDefault() ?? throw new InvalidOperationException("Memory element type is unavailable.");
        var definition = FindAnyType(ctx, readOnly ? "System.ReadOnlySpan`1" : "System.Span`1") ?? throw new InvalidOperationException("Span type is unavailable.");
        return StackSlot.OfValueType(new VmStructValue(new VmConstructedType { Definition = definition, TypeArguments = [element] }, [reference, StackSlot.OfInt32(length)], [element]));
    }

    private static long ReferenceByteOffset(IntrinsicContext ctx, StackSlot[] a) {
        var (left, leftRef) = ResolvePointerBase(a[0], "Unsafe.ByteOffset");
        var (right, rightRef) = ResolvePointerBase(a[1], "Unsafe.ByteOffset");
        if (left is not null && right is not null && ReferenceEquals(left.Memory, right.Memory))
            return (long)right.ByteOffset - left.ByteOffset;
        if (leftRef is not null && rightRef is not null && ReferenceEquals(leftRef.Container, rightRef.Container))
            return ((long)rightRef.Index - leftRef.Index) * SlotStride(ctx.MethodTypeArgAt(0));
        var leftAddress = left is not null ? ctx.Shared.RuntimeMetadata.MemoryAddress(ctx, left.Memory) + left.ByteOffset
            : ctx.Shared.RuntimeMetadata.MemoryAddress(ctx, leftRef!.Container) + (long)leftRef.Index * SlotStride(ctx.MethodTypeArgAt(0));
        var rightAddress = right is not null ? ctx.Shared.RuntimeMetadata.MemoryAddress(ctx, right.Memory) + right.ByteOffset
            : ctx.Shared.RuntimeMetadata.MemoryAddress(ctx, rightRef!.Container) + (long)rightRef.Index * SlotStride(ctx.MethodTypeArgAt(0));
        return rightAddress - leftAddress;
    }

    private static StackSlot BitCastValue(IntrinsicContext ctx, in StackSlot value) {
        var from = ctx.MethodTypeArguments.ElementAtOrDefault(0) ?? FindAnyType(ctx, ctx.MethodTypeArgAt(0));
        var to = ctx.MethodTypeArguments.ElementAtOrDefault(1) ?? FindAnyType(ctx, ctx.MethodTypeArgAt(1));
        if (from is null || to is null || !from.IsValueType || !to.IsValueType || ContainsReferences(from) || ContainsReferences(to))
            throw new UnhandledGuestException("System.NotSupportedException", "BitCast requires values without references.");
        var size = MemoryOps.SizeOfRawType(from);
        if (size != MemoryOps.SizeOfRawType(to)) throw new UnhandledGuestException("System.NotSupportedException", "BitCast sizes differ.");
        if (size > 256) ctx.Heap.ChargeHostBuffer(size);
        Span<byte> bytes = size <= 256 ? stackalloc byte[size] : new byte[size];
        MemoryOps.BytesOfValue(value, from, size, bytes);
        return MemoryOps.ValueFromBytes(bytes, to, size);
    }
}
