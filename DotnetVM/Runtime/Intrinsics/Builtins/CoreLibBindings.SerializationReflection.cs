using System.Reflection;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {

    private static VmType ReflectionDefinition(VmType type) => type is VmConstructedType constructed ? constructed.Definition : type;


    private static StackSlot ReflectionFields(IntrinsicContext ctx, VmType type, BindingFlags flags) {
        type = ReflectionDefinition(type);
        return MetadataArray(ctx, "System.Reflection.FieldInfo", type.Fields
            .Where(field => (field.IsStatic ? flags.HasFlag(BindingFlags.Static) : flags.HasFlag(BindingFlags.Instance))
                && ((field.Flags & 7) == 6 ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic)))
            .Select(field => DefaultIntrinsics.MakeRuntimeField(ctx, field)));
    }

    private static StackSlot ReflectionArgument(IntrinsicContext ctx, StackSlot value, VmType expected) {
        if (value.ObjectValue is VmBoxedValue boxed && expected.IsValueType)
            return boxed.Fields.Length == 1 ? boxed.Fields[0] : StackSlot.OfValueType(new VmStructValue(expected, boxed.Fields));
        return value;
    }


    private static StackSlot BoxReflectionResult(IntrinsicContext ctx, StackSlot result, VmType returnType) {
        if (!returnType.IsValueType || result.ObjectValue is VmBoxedValue) return result;
        var fields = result.ObjectValue is VmStructValue structure ? structure.Fields : [result];
        return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(returnType, fields)));
    }


    private static StackSlot InvokeReflectedFieldGet(IntrinsicContext ctx, VmRuntimeField field, StackSlot receiver) {
        var target = field.Target;
        var result = ctx.ReadFieldHook!(receiver, target);
        return BoxReflectionResult(ctx, result, target.FieldType!);
    }

    private static StackSlot InvokeReflectedFieldSet(IntrinsicContext ctx, VmRuntimeField field, StackSlot receiver, StackSlot value) {
        var target = field.Target;
        ctx.WriteFieldHook!(receiver, target, ReflectionArgument(ctx, value, target.FieldType!));
        return StackSlot.Null;
    }
    private static void RegisterSerializationReflection(IntrinsicRegistry r) {
        foreach (var type in new[] { "System.Reflection.MemberInfo", "System.Reflection.PropertyInfo", "System.Reflection.FieldInfo" })
            BclFace(r, type, "get_DeclaringType", true, [], static (ctx, a) => {
                var declaring = a[0].ObjectValue switch {
                VmRuntimeObject t => t.Target is VmClassType c ? c.DeclaringType : null,
                VmRuntimeProperty p => p.Getter.DeclaringType, VmRuntimeField f => f.Target.DeclaringType, VmRuntimeMethod m => m.Target.DeclaringType,
                _ => throw new InvalidOperationException(),
                };
                return declaring is null ? StackSlot.Null : DefaultIntrinsics.MakeRuntimeObject(ctx, declaring);
            });
        BclFace(r, "System.Exception", "set_Source", true, ["System.String"], static (_, a) => {
            if (a[0].ObjectValue is VmExceptionObject e) e.Source = a[1].ObjectValue as VmString;
            else if (a[0].ObjectValue is VmClassInstance c) {
                var layout = new ObjectModel().GetLayout(c.ClassType);
                var source = layout.Keys.First(f => f.Name == "_source"); c.Fields[layout[source]] = a[1];
            } return null;
        });
        BclFace(r, "System.Exception", "get_Source", true, [], static (_, a) => a[0].ObjectValue is VmExceptionObject e ? StackSlot.OfObject(e.Source) : StackSlot.Null);
        foreach (var type in new[] { "System.Reflection.MethodBase", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo" }) {
            BclFace(r, type, "get_Attributes", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeMethod)a[0].ObjectValue!).Target.Flags));
            BclFace(r, type, "GetMethodImplementationFlags", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeMethod)a[0].ObjectValue!).Target.ImplFlags));
        }
        BclFace(r, "System.Reflection.MethodInfo", "MakeGenericMethod", true, ["System.Type[]"], static (ctx, a) => {
            var method = (VmRuntimeMethod)a[0].ObjectValue!;
            var arguments = ((VmArray)a[1].ObjectValue!).Elements.Select(v => ((VmRuntimeObject)v.ObjectValue!).Target).ToArray();
            if (arguments.Length != method.Target.Signature.GenericParamCount) throw new UnhandledGuestException("System.ArgumentException", "Invalid method type arguments.");
            return DefaultIntrinsics.MakeRuntimeMethod(ctx, method.Target, method.ReflectedType, arguments);
        });
        foreach (var type in new[] { "System.Type", "System.RuntimeType" }) {
            BclFace(r, type, "GetArrayRank", true, [], static (_, a) => ((VmRuntimeObject)a[0].ObjectValue!).Target switch {
                VmArrayType => StackSlot.OfInt32(1), VmMultiDimArrayType array => StackSlot.OfInt32(array.Rank),
                _ => throw new UnhandledGuestException("System.ArgumentException", "Type is not an array."),
            });
            BclFace(r, type, "GetElementType", true, [], static (ctx, a) => {
                var element = ((VmRuntimeObject)a[0].ObjectValue!).Target switch { VmArrayType array => array.ElementType, VmMultiDimArrayType array => array.ElementType, VmByRefType reference => reference.ElementType, _ => null };
                return element is null ? StackSlot.Null : DefaultIntrinsics.MakeRuntimeObject(ctx, element);
            });
            BclFace(r, type, "GetFields", true, ["System.Reflection.BindingFlags"], static (ctx, a) =>
                ReflectionFields(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target, (BindingFlags)a[1].AsInt32));
        }
        BclFace(r, "System.Reflection.FieldInfo", "get_FieldType", true, [], static (ctx, a) => DefaultIntrinsics.MakeRuntimeObject(ctx, ((VmRuntimeField)a[0].ObjectValue!).Target.FieldType!));
        BclFace(r, "System.Reflection.FieldInfo", "get_Attributes", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeField)a[0].ObjectValue!).Target.Flags));
        BclFace(r, "System.Reflection.FieldInfo", "GetValue", true, ["System.Object"], static (ctx, a) =>
            InvokeReflectedFieldGet(ctx, (VmRuntimeField)a[0].ObjectValue!, a[1]));
        BclFace(r, "System.Reflection.FieldInfo", "SetValue", true, ["System.Object", "System.Object"], static (ctx, a) =>
            InvokeReflectedFieldSet(ctx, (VmRuntimeField)a[0].ObjectValue!, a[1], a[2]));
        foreach (var parameters in new[] { new[] { "System.Type", "System.Object[]" }, new[] { "System.Type", "System.Reflection.BindingFlags", "System.Reflection.Binder", "System.Object[]", "System.Globalization.CultureInfo" } })
            BclFace(r, "System.Activator", "CreateInstance", false, parameters, (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                var definition = target is VmConstructedType c ? c.Definition : target;
                var arguments = a[parameters.Length == 2 ? 1 : 3].ObjectValue is VmArray array ? array.Elements : [];
                var flags = parameters.Length == 2 ? BindingFlags.Public | BindingFlags.Instance : (BindingFlags)a[1].AsInt32;
                var constructors = definition.Methods.Where(m => m.Name == ".ctor" && m.Signature.ParamTypes.Length == arguments.Length && (m.IsPublic ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic))).ToArray();
                if (constructors.Length != 1) throw new UnhandledGuestException("System.MissingMethodException", "No unambiguous constructor matches the arguments.");
                var constructor = constructors[0];
                var values = arguments.Select((v, i) => v.ObjectValue is VmBoxedValue boxed && (constructor.Loader ?? ctx.Types).ResolveToken(constructor.Signature.ParamTypes[i]).IsValueType ? boxed.Fields.Length == 1 ? boxed.Fields[0] : StackSlot.OfValueType(new VmStructValue(boxed.Type, boxed.Fields)) : v).ToArray();
                return StackSlot.OfObject(ctx.NewInstanceHook!(target, constructor, values, null));
            });
    }
}

