using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    internal static StackSlot MakeRuntimeAssembly(IntrinsicContext ctx, TypeLoader loader) {
        var result = ctx.Heap.Allocate(new VmAssemblyObject { Loader = loader });
        if (ctx.Types.TryResolveTrustedUnifiedType("System.RuntimeType") is not VmClassType { Loader.IsTrustedCoreLib: true }) return StackSlot.OfObject(result);
        var metadata = ctx.Shared.RuntimeMetadata;
        result.ManagedInstance = metadata.Instance(ctx, "System.Reflection.RuntimeAssembly");
        metadata.Set(result.ManagedInstance, "m_assembly", StackSlot.OfNativeInt(metadata.Identity(ctx, loader)));
        return StackSlot.OfObject(result);
    }
    internal static StackSlot MakeRuntimeField(IntrinsicContext ctx, VmField field) {
        var result = ctx.Heap.Allocate(new VmRuntimeField { Target = field });
        if (ctx.Types.TryResolveTrustedUnifiedType("System.RuntimeType") is not VmClassType { Loader.IsTrustedCoreLib: true }) return StackSlot.OfObject(result);
        var metadata = ctx.Shared.RuntimeMetadata;
        var backing = metadata.Instance(ctx, "System.Reflection.RtFieldInfo");
        result.ManagedInstance = backing;
        metadata.Set(backing, "m_fieldHandle", StackSlot.OfNativeInt(metadata.Identity(ctx, result)));
        metadata.Set(backing, "m_fieldAttributes", StackSlot.OfInt32((int)field.Flags));
        metadata.Set(backing, "m_name", StackSlot.OfObject(ctx.MakeString(field.Name)));
        metadata.Set(backing, "m_fieldType", MakeRuntimeObject(ctx, field.FieldType!));
        metadata.Set(backing, "m_declaringType", MakeRuntimeObject(ctx, field.DeclaringType));
        metadata.Set(backing, "m_bindingFlags", StackSlot.OfInt32(((field.Flags & 7) == 6 ? 16 : 32) | (field.IsStatic ? 8 : 4)));
        var cache = metadata.Instance(ctx, "System.RuntimeType+RuntimeTypeCache");
        metadata.Set(cache, "m_runtimeType", MakeRuntimeObject(ctx, field.DeclaringType));
        metadata.Set(backing, "m_reflectedTypeCache", StackSlot.OfObject(cache));
        return StackSlot.OfObject(result);
    }
    internal static StackSlot MakeRuntimeProperty(IntrinsicContext ctx, string name, VmMethod getter, VmMethod? setter, VmType reflectedType) {
        var result = ctx.Heap.Allocate(new VmRuntimeProperty { Name = name, Getter = getter, Setter = setter, ReflectedType = reflectedType });
        if (ctx.Types.TryResolveTrustedUnifiedType("System.RuntimeType") is not VmClassType { Loader.IsTrustedCoreLib: true }) return StackSlot.OfObject(result);
        var definition = (VmClassType)getter.DeclaringType;
        var tables = definition.Image.Tables;
        for (var rid = 1; rid <= tables.GetRowCount(DotnetVM.Metadata.TableKind.MethodSemantics); rid++) {
            if (tables.GetRowIndex(DotnetVM.Metadata.TableKind.MethodSemantics, rid, 1) != getter.MethodDefRid) continue;
            var association = tables.DecodeCoded(DotnetVM.Metadata.TableKind.MethodSemantics, rid, 2, DotnetVM.Metadata.CodedIndexKind.HasSemantics);
            if (association.Table != DotnetVM.Metadata.TableKind.Property) continue;
            var metadata = ctx.Shared.RuntimeMetadata;
            var type = VmRuntimeMetadata.CoreType(ctx, "System.Reflection.RuntimePropertyInfo");
            var constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Signature.ParamTypes.Length == 4);
            var cache = metadata.Instance(ctx, "System.RuntimeType+RuntimeTypeCache");
            metadata.Set(cache, "m_runtimeType", MakeRuntimeObject(ctx, reflectedType));
            var isPrivate = new[] { StackSlot.OfInt32(0) };
            result.ManagedInstance = ctx.NewInstanceHook!(type, constructor, [StackSlot.OfInt32(0x17000000 | association.Rid),
                MakeRuntimeObject(ctx, reflectedType is VmConstructedType ? reflectedType : definition), StackSlot.OfObject(cache), StackSlot.OfByRef(new VmByRef(isPrivate, 0))], null);
            return StackSlot.OfObject(result);
        }
        throw new DotnetVM.Policy.UnhandledGuestException("System.MissingMemberException", name);
    }
    internal static StackSlot MakeRuntimeMethod(IntrinsicContext ctx, VmMethod method, VmType? reflectedType = null, VmType[]? methodArguments = null) {
        var result = ctx.Heap.Allocate(new VmRuntimeMethod { Target = method, ReflectedType = reflectedType, MethodArguments = methodArguments ?? [] });
        if (ctx.Types.TryResolveTrustedUnifiedType("System.RuntimeType") is not VmClassType { Loader.IsTrustedCoreLib: true })
            return StackSlot.OfObject(result);
        var metadata = ctx.Shared.RuntimeMetadata;
        var backing = metadata.Instance(ctx, method.IsConstructor ? "System.Reflection.RuntimeConstructorInfo" : "System.Reflection.RuntimeMethodInfo");
        result.ManagedInstance = backing;
        metadata.Set(backing, "m_handle", StackSlot.OfNativeInt(metadata.Identity(ctx, result)));
        metadata.Set(backing, "m_declaringType", MakeRuntimeObject(ctx, reflectedType is VmConstructedType ? reflectedType : method.DeclaringType));
        metadata.Set(backing, "m_methodAttributes", StackSlot.OfInt32((int)method.Flags));
        var cache = metadata.Instance(ctx, "System.RuntimeType+RuntimeTypeCache");
        metadata.Set(cache, "m_runtimeType", MakeRuntimeObject(ctx, reflectedType ?? method.DeclaringType));
        metadata.Set(backing, "m_reflectedTypeCache", StackSlot.OfObject(cache));
        return StackSlot.OfObject(result);
    }
}
