using System.Reflection;
using System.Runtime.InteropServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using DotnetVM.IL;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    internal sealed record NativeReflectionImport(BindingKey Key, BindingOrigin Origin, IntrinsicImpl Implementation);
    internal static IReadOnlyList<NativeReflectionImport> NativeReflectionImports { get; } = CreateNativeReflectionImports();

    private static IReadOnlyList<NativeReflectionImport> CreateNativeReflectionImports() {
        var result = new List<NativeReflectionImport>();
        void Internal(string owner, string name, IntrinsicImpl implementation) => Add(owner, name, null, implementation);
        void Import(string owner, string entryPoint, IntrinsicImpl implementation) => Add(owner, null, entryPoint, implementation);
        void Add(string owner, string? name, string? entryPoint, IntrinsicImpl implementation) {
            var type = typeof(object).Assembly.GetType(owner) ?? throw new InvalidOperationException(owner);
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(method => method.GetMethodBody() is null && (entryPoint is not null
                    ? method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint == entryPoint
                    : method.Name == name && (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0)).ToArray();
            if (methods.Length != 1) throw new InvalidOperationException($"Expected one native runtime method: {owner}::{name ?? entryPoint}.");
            var method = methods[0];
            result.Add(new NativeReflectionImport(BindingKey.TrustedStatic(owner, method.Name,
                method.GetParameters().Select(parameter => NativeParameterName(parameter.ParameterType)).ToArray()),
                entryPoint is null ? BindingOrigin.InternalCall : BindingOrigin.PInvokeReplacement, implementation));
        }
        const string T = "System.RuntimeTypeHandle";
        Internal("System.Reflection.RuntimeAssembly", "GetManifestModule", static (ctx, a) => {
            var loader = a[0].ObjectValue is VmAssemblyObject assembly ? assembly.Loader : ctx.Shared.RuntimeMetadata.Resolve<TypeLoader>(ctx.Shared.RuntimeMetadata.Get((VmClassInstance)a[0].ObjectValue!, "m_assembly").Int64Value);
            return StackSlot.OfObject(ctx.Shared.RuntimeMetadata.Module(ctx, loader));
        });
        Internal("System.Reflection.RuntimeAssembly", "GetTokenInternal", static (_, _) => StackSlot.OfInt32(0x20000001));
        Internal(T, "GetElementTypeHandle", static (ctx, a) => {
            var type = ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value);
            var element = type switch { VmByRefType reference => reference.ElementType, VmArrayType array => array.ElementType, VmMultiDimArrayType array => array.ElementType, _ => null };
            return StackSlot.OfNativeInt(element is null ? 0 : ctx.Shared.RuntimeMetadata.TypeAddress(ctx, element));
        });
        foreach (var operation in new[] { "RegisterForGCReporting", "UnregisterForGCReporting" })
            Internal("System.Runtime.GCFrameRegistration", operation, static (_, a) => {
                // VM frames already root the argument slots; there is no host stack address to register.
                if (a[0].ObjectValue is not VmByRef) throw new UnhandledGuestException("System.ArgumentException", "Invalid GC frame address.");
                return null;
            });
        Import(T, "RuntimeTypeHandle_GetActivationInfo", NativeActivationInfo);
        Import("System.RuntimeType+BoxCache", "ReflectionInvocation_GetBoxInfo", static (ctx, a) => {
            var type = NativeQCallTarget(ctx, a[0]);
            var allocator = ctx.Heap.Allocate(new VmRuntimeCallback { OwnerType = type, Invoke = _ => {
                var value = new ObjectModel().DefaultForType(type, NativeLoader(type));
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(type, value.ObjectValue is VmStructValue structure ? structure.Fields : [value])));
            } });
            NativeWrite(a[1], StackSlot.OfObject(allocator));
            NativeWrite(a[2], StackSlot.OfNativeInt(ctx.Shared.RuntimeMetadata.TypeAddress(ctx, type)));
            NativeWrite(a[3], StackSlot.OfInt32(0));
            NativeWrite(a[4], StackSlot.OfInt32(MemoryOps.SizeOfRawType(type)));
            return null;
        });
        Internal(T, "GetAttributes", static (_, a) => StackSlot.OfInt32(unchecked((int)NativeTarget(a[0]).Flags)));
        Internal(T, "GetToken", static (_, a) => StackSlot.OfInt32(NativeDefinition(NativeTarget(a[0])) is VmClassType type ? 0x02000000 | type.TypeDefRid : 0x02000000));
        Internal(T, "IsGenericVariable", static (_, a) => StackSlot.OfInt32(NativeTarget(a[0]) is VmGenericParameterType ? 1 : 0));
        Internal(T, "GetNumVirtuals", static (_, a) => {
            var type = NativeDefinition(NativeTarget(a[0])) as VmClassType;
            return StackSlot.OfInt32(type is null ? 0 : type.Loader!.EnsureDispatchMaps(type).VTable.Values.Count(slot => (slot.Method.Flags & 0x40) != 0));
        });
        Import(T, "RuntimeTypeHandle_GetMethodAt", static (ctx, a) => {
            var type = ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value);
            var definition = NativeDefinition(type) as VmClassType
                ?? throw new UnhandledGuestException("System.ArgumentException", "Invalid method table.");
            var slots = definition.Loader!.EnsureDispatchMaps(definition).VTable.Values.Where(slot => slot.Method.IsVirtual).ToArray();
            var index = a[1].AsInt32;
            if ((uint)index >= (uint)slots.Length) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "slot");
            var method = (VmRuntimeMethod)DefaultIntrinsics.MakeRuntimeMethod(ctx, slots[index].Method, type).ObjectValue!;
            return ctx.Shared.RuntimeMetadata.Get(method.ManagedInstance!, "m_handle");
        });
        Internal(T, "GetModuleIfExists", static (ctx, a) => StackSlot.OfObject(ctx.Shared.RuntimeMetadata.Module(ctx, NativeLoader(NativeTarget(a[0])))));
        Internal("System.Reflection.MetadataImport", "GetMetadataImport", static (ctx, a) => ctx.Shared.RuntimeMetadata.Get((VmClassInstance)a[0].ObjectValue!, "m_pData"));
        Import(T, "QCall_GetGCHandleForTypeHandle", static (ctx, a) => {
            _ = NativeQCallTarget(ctx, a[0]);
            ctx.Heap.ChargeHostBuffer(64); ctx.Heap.DependentHandles = ctx.Shared.DependentHandles;
            return StackSlot.OfNativeInt(ctx.Shared.DependentHandles.Allocate(StackSlot.Null, StackSlot.Null, a[1].AsInt32 >= 2));
        });
        Import(T, "QCall_FreeGCHandleForTypeHandle", static (ctx, a) => {
            _ = NativeQCallTarget(ctx, a[0]);
            ctx.Shared.DependentHandles.Free(a[1].Int64Value); return StackSlot.OfNativeInt(0);
        });
        Import(T, "RuntimeTypeHandle_ConstructName", static (ctx, a) => {
            var target = NativeQCallTarget(ctx, a[0]);
            var name = NativeTypeName(target, a[1].AsInt32);
            NativeStackHandle(ctx, a[2]).Write(StackSlot.OfObject(ctx.MakeString(name))); return null;
        });
        Import(T, "RuntimeTypeHandle_GetInstantiation", static (ctx, a) => {
            var target = NativeQCallTarget(ctx, a[0]);
            var arguments = target is VmConstructedType constructed ? constructed.TypeArguments : Array.Empty<VmType>();
            NativeStackHandle(ctx, a[1]).Write(arguments.Length == 0 ? StackSlot.Null : MetadataArray(ctx,
                a[2].AsInt32 != 0 ? "System.RuntimeType" : "System.Type", arguments.Select(type => DefaultIntrinsics.MakeRuntimeObject(ctx, type))));
            return null;
        });
        Import("System.Runtime.CompilerServices.TypeHandle", "TypeHandle_GetCorElementType", static (ctx, a) =>
            StackSlot.OfInt32(NativeElementType(ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value))));
        Import("System.Runtime.CompilerServices.TypeHandle", "TypeHandle_CanCastTo_NoCacheLookup", static (ctx, a) =>
            StackSlot.OfInt32(ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value)
                .IsAssignableTo(ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[1].Int64Value)) ? 1 : 0));
        Import("System.Reflection.RuntimeModule", "RuntimeModule_GetScopeName", static (ctx, a) => {
            var loader = NativeQCallModule(ctx, a[0]);
            var image = loader.Image;
            NativeStackHandle(ctx, a[1]).Write(StackSlot.OfObject(ctx.MakeString(image.GetString(image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.Module, 1, 1)))));
            return null;
        });
        Import(T, "RuntimeTypeHandle_GetRuntimeTypeFromHandleSlow", static (ctx, a) => {
            NativeStackHandle(ctx, a[1]).Write(DefaultIntrinsics.MakeRuntimeObject(ctx, ctx.Shared.RuntimeMetadata.Resolve<VmType>(a[0].Int64Value))); return null;
        });
        Import("System.ModuleHandle", "ModuleHandle_GetModuleType", static (ctx, a) => {
            var loader = NativeQCallModule(ctx, a[0]);
            var globalType = loader.FindTypeByFullName("<Module>") ?? throw new UnhandledGuestException("System.TypeLoadException", "<Module>");
            NativeStackHandle(ctx, a[1]).Write(DefaultIntrinsics.MakeRuntimeObject(ctx, globalType)); return null;
        });
        AddNativeMemberImports(Internal, Import);
        AddNativeMetadataImports(Internal, Import);
        AddNativeAttributeImports(Import);
        Import("System.Reflection.Metadata.MetadataUpdater", "AssemblyNative_IsApplyUpdateSupported", static (_, _) => StackSlot.OfInt32(0));
        Import("System.Array", "Array_CreateInstance", static (ctx, a) => {
            var type = NativeQCallTarget(ctx, a[0]);
            if (a[1].AsInt32 != 1 || a[3].ObjectValue is not null || a[3].Int64Value != 0)
                throw new UnhandledGuestException("System.NotSupportedException", "Non-vector arrays are not supported.");
            var element = a[4].AsInt32 != 0 ? ((VmArrayType)type).ElementType : type;
            var length = MemoryOps.LoadIndirect(ILOp.Ldind_I4, a[2]).AsInt32;
            if (length < 0) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "length");
            using var allocation = ctx.Heap.ReserveArray(length);
            var storage = new StackSlot[length];
            var objects = new ObjectModel();
            for (var i = 0; i < length; i++) storage[i] = objects.DefaultForType(element, NativeLoader(element));
            var array = allocation.Commit(new VmArray(new VmArrayType { ElementType = element }, storage));
            NativeStackHandle(ctx, a[5]).Write(StackSlot.OfObject(array));
            return null;
        });
        return result;
    }

    private static string NativeParameterName(Type type) => type.IsFunctionPointer
        ? NativeParameterName(type.GetFunctionPointerReturnType()) + "(" + string.Join(",", type.GetFunctionPointerParameterTypes().Select(NativeParameterName)) + ")"
        : type.IsPointer ? NativeParameterName(type.GetElementType()!) + "*"
        : type.IsByRef ? NativeParameterName(type.GetElementType()!) + "&"
        : type.FullName!;

    private static StackSlot? NativeActivationInfo(IntrinsicContext ctx, StackSlot[] a) {
        var type = NativeTarget(NativeStackHandle(ctx, a[0]).Read());
        var definition = NativeDefinition(type) as VmClassType ?? throw new UnhandledGuestException("System.ArgumentException", "Invalid activation type.");
        var constructor = definition.Methods.FirstOrDefault(method => method.IsConstructor && !method.IsStatic && method.Signature.ParamTypes.Length == 0);
        if (constructor is null && !type.IsValueType) throw new UnhandledGuestException("System.MissingMethodException", "No parameterless constructor.");
        var context = GenericContext.Of((type as VmConstructedType)?.TypeArguments, null);
        var objects = new ObjectModel();
        var allocator = ctx.Heap.Allocate(new VmRuntimeCallback { OwnerType = type, Invoke = _ => {
            definition.Loader!.EnsureLive();
            if (type is VmConstructedType { Definition.FullName: "System.Nullable`1" }) return StackSlot.Null;
            if (type.IsValueType) {
                var value = objects.DefaultForType(type, definition.Loader!);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(type, value.ObjectValue is VmStructValue structure ? structure.Fields : [value])));
            }
            var instance = ctx.Heap.Allocate(new VmClassInstance(definition, objects.CreateInstanceStorage(definition, definition.Loader!, context), context?.ClassArgs));
            return StackSlot.OfObject(instance);
        } });
        NativeWrite(a[1], StackSlot.OfObject(allocator));
        NativeWrite(a[2], StackSlot.OfNativeInt(ctx.Shared.RuntimeMetadata.TypeAddress(ctx, type)));
        var initialize = ctx.Heap.Allocate(new VmRuntimeCallback { OwnerType = type, Invoke = args => {
            if (constructor is not null) ctx.InvokeGuestMethod!(constructor, args, context);
            return null;
        } });
        NativeWrite(a[3], StackSlot.OfObject(initialize));
        NativeWrite(a[4], type.IsValueType ? StackSlot.OfObject(initialize) : StackSlot.OfNativeInt(0));
        NativeWrite(a[5], StackSlot.OfInt32(constructor is null || constructor.IsPublic ? 1 : 0));
        return null;
    }

    private static void RegisterNativeReflection(IntrinsicRegistry registry) {
        foreach (var import in NativeReflectionImports) registry.RegisterBinding(import.Key, import.Implementation, import.Origin);
        registry.RegisterStaticField("System.Runtime.CompilerServices.CastHelpers", "s_table", ctx => ctx.Shared.RuntimeMetadata.EmptyCastCache(ctx));
    }
    private static VmType NativeDefinition(VmType type) => type is VmConstructedType constructed ? constructed.Definition : type;
    private static string NativeTypeName(VmType type, int flags) {
        var name = type switch {
            VmArrayType array => NativeTypeName(array.ElementType, flags & ~4) + "[]",
            VmMultiDimArrayType array => NativeTypeName(array.ElementType, flags & ~4) +
                (array.Rank == 1 ? "[*]" : "[" + new string(',', array.Rank - 1) + "]"),
            VmByRefType reference => NativeTypeName(reference.ElementType, flags & ~4) + "&",
            VmConstructedType constructed when flags != 0 => ((flags & 1) != 0 ? constructed.Definition.FullName : constructed.Definition.Name) +
                "[" + string.Join(",", constructed.TypeArguments.Select(argument => (flags & 2) != 0
                    ? "[" + NativeTypeName(argument, 7) + "]" : NativeTypeName(argument, flags & ~4))) + "]",
            _ => NativeDefinitionName(type, flags),
        };
        if ((flags & 4) != 0) {
            var identity = NativeLoader(type).Image.Identity;
            name += ", " + identity;
            if (!identity.IsStrongNamed) name += ", PublicKeyToken=null";
        }
        return name;
    }
    private static string NativeDefinitionName(VmType type, int flags) {
        type = NativeDefinition(type);
        var name = (flags & 1) != 0 ? type.FullName : type.Name;
        if ((flags & 3) == 1 && type is VmClassType { GenericParamCount: > 0 } definition) {
            var parameters = new SortedDictionary<int, string>();
            var image = definition.Image;
            for (var row = 1; row <= image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.GenericParam); row++) {
                var owner = image.Tables.DecodeCoded(DotnetVM.Metadata.TableKind.GenericParam, row, 2, DotnetVM.Metadata.CodedIndexKind.TypeOrMethodDef);
                if (owner.Table == DotnetVM.Metadata.TableKind.TypeDef && owner.Rid == definition.TypeDefRid)
                    parameters[image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.GenericParam, row, 0)] = image.GetString(image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.GenericParam, row, 3));
            }
            name += "[" + string.Join(",", parameters.Values) + "]";
        }
        return name;
    }
    private static int NativeElementType(VmType type) => type.FullName switch {
        "System.Void" => 1, "System.Boolean" => 2, "System.Char" => 3,
        "System.SByte" => 4, "System.Byte" => 5, "System.Int16" => 6, "System.UInt16" => 7,
        "System.Int32" => 8, "System.UInt32" => 9, "System.Int64" => 10, "System.UInt64" => 11,
        "System.Single" => 12, "System.Double" => 13, "System.String" => 14,
        "System.TypedReference" => 22, "System.IntPtr" => 24, "System.UIntPtr" => 25,
        "System.Object" => 28,
        _ => type switch {
            VmByRefType => 16, VmArrayType => 29, VmMultiDimArrayType => 20, VmFunctionPointerType => 27,
            VmGenericParameterType parameter => parameter.IsMethodParameter ? 30 : 19,
            _ when type.FullName.EndsWith("*", StringComparison.Ordinal) => 15,
            _ => type.IsValueType ? 17 : 18,
        },
    };
    private static VmType NativeTarget(StackSlot slot) => (slot.ObjectValue as VmRuntimeObject)?.Target
        ?? throw new UnhandledGuestException("System.ArgumentException", "RuntimeType expected.");
    private static TypeLoader NativeLoader(VmType type) => NativeDefinition(type) switch {
        VmClassType definition when definition.Loader is { } loader => loader,
        VmArrayType array => NativeLoader(array.ElementType),
        VmMultiDimArrayType array => NativeLoader(array.ElementType),
        VmByRefType reference => NativeLoader(reference.ElementType),
        _ => throw new UnhandledGuestException("System.TypeLoadException", type.FullName),
    };
    private static StackSlot NativeStructureField(IntrinsicContext ctx, StackSlot value, string name) {
        if (value.ObjectValue is VmByRef reference) value = reference.Read();
        return ctx.Shared.RuntimeMetadata.Get((VmStructValue)value.ObjectValue!, name);
    }
    private static VmType NativeQCallTarget(IntrinsicContext ctx, StackSlot value) => ctx.Shared.RuntimeMetadata.Resolve<VmType>(NativeStructureField(ctx, value, "_handle").Int64Value);
    private static TypeLoader NativeQCallModule(IntrinsicContext ctx, StackSlot value) {
        var module = (VmClassInstance)((VmByRef)NativeStructureField(ctx, value, "_ptr").ObjectValue!).Read().ObjectValue!;
        return ctx.Shared.RuntimeMetadata.Resolve<TypeLoader>(ctx.Shared.RuntimeMetadata.Get(module, "m_pData").Int64Value);
    }
    private static VmByRef NativeStackHandle(IntrinsicContext ctx, StackSlot value) => (VmByRef)NativeStructureField(ctx, value, "_ptr").ObjectValue!;
}
