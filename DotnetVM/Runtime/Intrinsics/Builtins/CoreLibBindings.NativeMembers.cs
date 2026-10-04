using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static VmRuntimeMethod NativeMethod(IntrinsicContext ctx, StackSlot handle) =>
        ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeMethod>(NativeStructureField(ctx, handle, "m_handle").Int64Value);

    private static void AddNativeMemberImports(System.Action<string, string, IntrinsicImpl> internalCall, System.Action<string, string, IntrinsicImpl> import) {
        const string method = "System.RuntimeMethodHandle";
        internalCall("System.RuntimeFieldHandle", "GetToken", static (ctx, a) => StackSlot.OfInt32(0x04000000 | ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeField>(a[0].Int64Value).Target.FieldRid));
        internalCall("System.RuntimeFieldHandle", "GetAttributes", static (ctx, a) => StackSlot.OfInt32((int)ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeField>(NativeStructureField(ctx, a[0], "m_handle").Int64Value).Target.Flags));
        internalCall("System.RuntimeFieldHandle", "GetApproxDeclaringMethodTable", static (ctx, a) => StackSlot.OfNativeInt(ctx.Shared.RuntimeMetadata.TypeAddress(ctx,
            ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeField>(NativeStructureField(ctx, a[0], "m_handle").Int64Value).Target.DeclaringType)));
        internalCall(method, "GetAttributes", static (ctx, a) => StackSlot.OfInt32((int)NativeMethod(ctx, a[0]).Target.Flags));
        internalCall(method, "GetImplAttributes", static (ctx, a) => StackSlot.OfInt32((int)RuntimeMethodInfo(ctx, a[0]).Target.ImplFlags));
        internalCall(method, "GetMethodTable", static (ctx, a) => {
            var value = NativeMethod(ctx, a[0]);
            return StackSlot.OfNativeInt(ctx.Shared.RuntimeMetadata.TypeAddress(ctx, value.ReflectedType ?? value.Target.DeclaringType));
        });
        internalCall(method, "GetMethodDef", static (ctx, a) => StackSlot.OfInt32(0x06000000 | NativeMethod(ctx, a[0]).Target.MethodDefRid));
        internalCall(method, "GetSlot", static (ctx, a) => {
            var target = NativeMethod(ctx, a[0]).Target;
            if ((target.Flags & 0x40) == 0) return StackSlot.OfInt32(int.MaxValue);
            var slots = target.Loader!.EnsureDispatchMaps((VmClassType)target.DeclaringType).VTable.Values.Where(slot => (slot.Method.Flags & 0x40) != 0).ToArray();
            var index = Array.FindIndex(slots, slot => slot.Method.SlotKey == target.SlotKey);
            return StackSlot.OfInt32(index < 0 ? int.MaxValue : index);
        });
        internalCall(method, "HasMethodInstantiation", static (ctx, a) => StackSlot.OfInt32(NativeMethod(ctx, a[0]).Target.Signature.GenericParamCount != 0 ? 1 : 0));
        internalCall(method, "IsGenericMethodDefinition", static (ctx, a) => {
            var value = NativeMethod(ctx, a[0]);
            return StackSlot.OfInt32(value.MethodArguments.Length == 0 && value.Target.Signature.GenericParamCount != 0 ? 1 : 0);
        });
        internalCall(method, "IsConstructor", static (ctx, a) => StackSlot.OfInt32(NativeMethod(ctx, a[0]).Target.IsConstructor ? 1 : 0));
        internalCall(method, "IsDynamicMethod", static (ctx, a) => { _ = NativeMethod(ctx, a[0]); return StackSlot.OfInt32(0); });
        internalCall(method, "GetLoaderAllocatorInternal", static (ctx, a) => { _ = NativeMethod(ctx, a[0]); return StackSlot.Null; });
        import(method, "RuntimeMethodHandle_GetIsCollectible", static (ctx, a) => { _ = NativeMethod(ctx, a[0]); return StackSlot.OfInt32(0); });
        import("System.Signature", "Signature_Init", static (ctx, a) => {
            var metadata = ctx.Shared.RuntimeMetadata;
            var signature = (VmClassInstance)NativeStackHandle(ctx, a[0]).Read().ObjectValue!;
            if (a[1].ObjectValue is VmNativePointer blob) {
                var declaring = NativeTarget(metadata.Get(signature, "_declaringType"));
                var scope = NativeLoader(declaring);
                var genericContext = GenericContext.Of((declaring as VmConstructedType)?.TypeArguments, null);
                blob.EnsureBounds(a[2].AsInt32);
                var decoded = SignatureDecoder.DecodePropertyCallableSignature(blob.Bytes.AsSpan(blob.ByteOffset, a[2].AsInt32));
                metadata.Set(signature, "_arguments", MetadataArray(ctx, "System.RuntimeType", decoded.ParamTypes.Select(p => DefaultIntrinsics.MakeRuntimeObject(ctx, scope.ResolveToken(p, genericContext)))));
                metadata.Set(signature, "_returnTypeORfieldType", DefaultIntrinsics.MakeRuntimeObject(ctx, scope.ResolveToken(decoded.ReturnType, genericContext)));
                metadata.Set(signature, "_managedCallingConventionAndArgIteratorFlags", StackSlot.OfInt32((decoded.HasThis ? 32 : 0) | 1));
                return null;
            }
            var value = NativeMethod(ctx, a[4]);
            var target = value.Target;
            var context = GenericContext.Of((value.ReflectedType as VmConstructedType)?.TypeArguments, value.MethodArguments);
            var loader = target.Loader ?? ctx.Types;
            metadata.Set(signature, "_arguments", MetadataArray(ctx, "System.RuntimeType", target.Signature.ParamTypes.Select(p => DefaultIntrinsics.MakeRuntimeObject(ctx, loader.ResolveToken(p, context)))));
            metadata.Set(signature, "_returnTypeORfieldType", DefaultIntrinsics.MakeRuntimeObject(ctx, loader.ResolveToken(target.Signature.ReturnType, context)));
            metadata.Set(signature, "_pMethod", a[4]);
            metadata.Set(signature, "_managedCallingConventionAndArgIteratorFlags", StackSlot.OfInt32((target.IsStatic ? 0 : 32) | 1));
            return null;
        });
        import(method, "RuntimeMethodHandle_InvokeMethod", NativeInvokeMethod);
        import(method, "RuntimeMethodHandle_IsCAVisibleFromDecoratedType", static (ctx, a) => {
            var type = NativeQCallTarget(ctx, a[0]);
            var module = NativeQCallModule(ctx, a[3]);
            var handle = NativeStructureField(ctx, a[1], "m_handle").Int64Value;
            var ctor = handle == 0 ? type.Methods.FirstOrDefault(m => m.IsConstructor && m.Signature.ParamTypes.Length == 0)
                : ctx.Shared.RuntimeMetadata.Resolve<VmRuntimeMethod>(handle).Target;
            var sameAssembly = ReferenceEquals(NativeLoader(type), module);
            var visible = ctor is not null && (sameAssembly || ctor.IsPublic && (type.Flags & 7) is 1 or 2);
            return StackSlot.OfInt32(visible ? 1 : 0);
        });
        import("System.Runtime.CompilerServices.CastHelpers", "IsInstanceOf_NoCacheLookup", static (ctx, a) => {
            var type = ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value);
            var value = NativeStackHandle(ctx, a[2]).Read();
            var matches = value.ObjectValue is not null && DefaultIntrinsics.RuntimeTypeOf(ctx, value).IsAssignableTo(type);
            if (!matches && a[1].AsInt32 != 0) throw new UnhandledGuestException("System.InvalidCastException", null);
            if (!matches) NativeStackHandle(ctx, a[2]).Write(StackSlot.Null);
            return StackSlot.OfInt32(matches ? 1 : 0);
        });
    }

    private static StackSlot? NativeInvokeMethod(IntrinsicContext ctx, StackSlot[] a) {
        var receiver = NativeStackHandle(ctx, a[0]).Read();
        var signature = (VmClassInstance)NativeStackHandle(ctx, a[2]).Read().ObjectValue!;
        var value = NativeMethod(ctx, ctx.Shared.RuntimeMetadata.Get(signature, "_pMethod"));
        var target = value.Target;
        var context = GenericContext.Of((value.ReflectedType as VmConstructedType)?.TypeArguments, value.MethodArguments);
        var loader = target.Loader ?? ctx.Types;
        var args = new StackSlot[target.Signature.ParamTypes.Length];
        for (var i = 0; i < args.Length; i++) {
            var pointer = MemoryOps.TryPointerArithmetic(ILOp.Add, a[1], StackSlot.OfInt32(i * VmPrimitiveTypes.NativeIntSizeBytes)) ?? a[1];
            var address = MemoryOps.LoadIndirect(ILOp.Ldind_Ref, pointer);
            if (address.ObjectValue is VmStructValue byReference && byReference.StructType.FullName == "System.ByReference") address = byReference.Fields[0];
            var expected = loader.ResolveToken(target.Signature.ParamTypes[i], context);
            args[i] = expected is VmByRefType ? address : address.ObjectValue is VmByRef reference ? SlotOps.PushCopyOfValue(reference.Read())
                : throw new UnhandledGuestException("System.ArgumentException", "Invalid reflection argument address.");
        }
        StackSlot result;
        if (a[3].AsInt32 != 0) result = StackSlot.OfObject(ctx.NewInstanceHook!(value.ReflectedType ?? target.DeclaringType, target, args, context));
        else {
            var implementation = target.IsVirtual ? ctx.ResolveVirtualMethod!(target, receiver) : target;
            result = ctx.InvokeGuestMethod!(implementation, target.IsStatic ? args : [receiver, .. args], context);
            var returnType = loader.ResolveToken(target.Signature.ReturnType, context);
            if (returnType.FullName == "System.Void") result = StackSlot.Null;
            else result = BoxReflectionResult(ctx, result, returnType);
        }
        NativeStackHandle(ctx, a[4]).Write(result);
        return null;
    }
}
