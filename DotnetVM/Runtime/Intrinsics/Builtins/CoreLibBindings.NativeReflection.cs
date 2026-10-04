using System.Reflection;
using System.Runtime.InteropServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

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
                method.GetParameters().Select(parameter => parameter.ParameterType.FullName!).ToArray()),
                entryPoint is null ? BindingOrigin.InternalCall : BindingOrigin.PInvokeReplacement, implementation));
        }
        const string T = "System.RuntimeTypeHandle";
        Internal(T, "GetAttributes", static (_, a) => StackSlot.OfInt32(unchecked((int)NativeTarget(a[0]).Flags)));
        Internal(T, "GetToken", static (_, a) => StackSlot.OfInt32(NativeDefinition(NativeTarget(a[0])) is VmClassType type ? 0x02000000 | type.TypeDefRid : 0x02000000));
        Internal(T, "IsGenericVariable", static (_, a) => StackSlot.OfInt32(NativeTarget(a[0]) is VmGenericParameterType ? 1 : 0));
        Internal(T, "GetNumVirtuals", static (_, a) => {
            var type = NativeDefinition(NativeTarget(a[0])) as VmClassType;
            return StackSlot.OfInt32(type is null ? 0 : type.Loader!.EnsureDispatchMaps(type).VTable.Count);
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
        return result;
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
            VmByRefType => 16, VmArrayType => 29, VmMultiDimArrayType => 20,
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
