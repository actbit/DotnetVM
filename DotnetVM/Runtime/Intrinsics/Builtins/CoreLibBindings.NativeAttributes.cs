using DotnetVM.Binary;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    internal static VmRuntimeMethod RuntimeMethodInfo(IntrinsicContext ctx, StackSlot value) {
        if (value.ObjectValue is VmRuntimeMethod method) return method;
        if (value.ObjectValue is not VmClassInstance instance) throw new UnhandledGuestException("System.ArgumentException", "Runtime method expected.");
        var handle = ctx.Shared.RuntimeMetadata.Get(instance, instance.ClassType.FullName == "System.RuntimeMethodInfoStub" ? "m_value" : "m_handle");
        return handle.ObjectValue is VmStructValue ? NativeMethod(ctx, handle) : ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeMethod>(handle.Int64Value);
    }

    private static ReadOnlySpan<byte> AttributeBytes(StackSlot start, StackSlot end) {
        if (start.ObjectValue is not VmNativePointer pointer || end.ObjectValue is not VmNativePointer limit || !ReferenceEquals(pointer.Memory, limit.Memory))
            throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid custom attribute blob.");
        var length = checked(limit.ByteOffset - pointer.ByteOffset);
        if (length < 0) throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid custom attribute bounds.");
        pointer.EnsureBounds(length);
        return pointer.Bytes.AsSpan(pointer.ByteOffset, length);
    }

    private static void AddNativeAttributeImports(Action<string, string, IntrinsicImpl> import) {
        const string ca = "System.Reflection.CustomAttribute";
        import(ca, "CustomAttribute_CreateCustomAttributeInstance", static (ctx, a) => {
            var scope = NativeQCallModule(ctx, a[0]);
            var type = NativeTarget(NativeStackHandle(ctx, a[1]).Read());
            var method = RuntimeMethodInfo(ctx, NativeStackHandle(ctx, a[2]).Read());
            var start = ((VmByRef)a[3].ObjectValue!).Read();
            var pointer = start.ObjectValue as VmNativePointer
                ?? throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid custom attribute pointer.");
            var cacheKey = new CustomAttributeInstanceKey(method.Target, method.Target, type,
                pointer.Bytes, pointer.ByteOffset);
            if (ctx.Shared.CustomAttributeInstances.TryGetValue(cacheKey, out var cached)) {
                NativeStackHandle(ctx, a[6]).Write(StackSlot.OfObject(cached));
                return null;
            }
            var reader = new SpanReader(AttributeBytes(start, a[4]));
            if (reader.ReadUInt16() != 1) throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid attribute prolog.");
            var context = GenericContext.Of((method.ReflectedType as VmConstructedType)?.TypeArguments, method.MethodArguments);
            var target = method.Target;
            using var allocation = ctx.Heap.ReserveArray(target.Signature.ParamTypes.Length);
            var values = new StackSlot[target.Signature.ParamTypes.Length];
            for (var i = 0; i < values.Length; i++) values[i] = AttributeValue(ctx, scope, (target.Loader ?? scope).ResolveToken(target.Signature.ParamTypes[i], context), ref reader);
            var named = reader.ReadUInt16();
            var instance = ctx.NewInstanceHook!(type, target, values, context);
            if (instance is not null)
                ctx.Shared.CustomAttributeInstances.TryAdd(cacheKey, instance);
            NativeWrite(a[3], NativeOffset(start, reader.Offset));
            NativeWrite(a[5], StackSlot.OfInt32(named));
            NativeStackHandle(ctx, a[6]).Write(StackSlot.OfObject(instance));
            return null;
        });
        import(ca, "CustomAttribute_CreatePropertyOrFieldData", static (ctx, a) => {
            var scope = NativeQCallModule(ctx, a[0]);
            var start = ((VmByRef)a[1].ObjectValue!).Read();
            var reader = new SpanReader(AttributeBytes(start, a[2]));
            var kind = reader.ReadByte();
            if (kind is not (0x53 or 0x54)) throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid named attribute argument.");
            var type = AttributeEncodedType(ctx, scope, ref reader);
            var name = AttributeString(ref reader) ?? throw new UnhandledGuestException("System.CustomAttributeFormatException", "Missing named attribute argument.");
            var value = AttributeValue(ctx, scope, type, ref reader);
            NativeWrite(a[1], NativeOffset(start, reader.Offset));
            NativeStackHandle(ctx, a[3]).Write(StackSlot.OfObject(ctx.MakeString(name)));
            NativeWrite(a[4], StackSlot.OfInt32(kind == 0x54 ? 1 : 0));
            NativeStackHandle(ctx, a[5]).Write(DefaultIntrinsics.MakeRuntimeObject(ctx, type));
            NativeStackHandle(ctx, a[6]).Write(BoxReflectionResult(ctx, value, type));
            return null;
        });
        import(ca, "CustomAttribute_ParseAttributeUsageAttribute", static (ctx, a) => {
            if (a[0].ObjectValue is not VmNativePointer pointer) return StackSlot.OfInt32(0);
            pointer.EnsureBounds(a[1].AsInt32);
            var reader = new SpanReader(pointer.Bytes.AsSpan(pointer.ByteOffset, a[1].AsInt32));
            if (reader.ReadUInt16() != 1) return StackSlot.OfInt32(0);
            var targets = reader.ReadInt32(); var multiple = false; var inherited = true;
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++) {
                if (reader.ReadByte() != 0x54 || reader.ReadByte() != 2) return StackSlot.OfInt32(0);
                var name = AttributeString(ref reader); var value = reader.ReadByte() != 0;
                if (name == "AllowMultiple") multiple = value;
                else if (name == "Inherited") inherited = value;
                else return StackSlot.OfInt32(0);
            }
            if (reader.Remaining != 0) return StackSlot.OfInt32(0);
            NativeWrite(a[2], StackSlot.OfInt32(targets)); NativeWrite(a[3], StackSlot.OfInt32(multiple ? 1 : 0)); NativeWrite(a[4], StackSlot.OfInt32(inherited ? 1 : 0));
            return StackSlot.OfInt32(1);
        });
    }

    private static VmType AttributeEncodedType(IntrinsicContext ctx, TypeLoader scope, ref SpanReader reader) {
        var code = reader.ReadByte();
        if (code == 0x1d) return new VmArrayType { ElementType = AttributeEncodedType(ctx, scope, ref reader) };
        if (code == 0x55) return ResolveTypeName(ctx, AttributeString(ref reader)!) ?? throw new UnhandledGuestException("System.TypeLoadException", "Invalid attribute enum type.");
        if (code == 0x50) return FindAnyType(ctx, "System.Type")!;
        if (code == 0x51) return FindAnyType(ctx, "System.Object")!;
        if (code is < 2 or > 14) throw new UnhandledGuestException("System.CustomAttributeFormatException", "Invalid attribute type encoding.");
        return scope.ResolveToken(new SigType((SigKind)code));
    }
}
