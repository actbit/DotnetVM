using System.Reflection;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private sealed record ParameterDescription(VmMethod Method, int Position);
    private static int ParameterRid(ParameterDescription p) {
        var image = p.Method.Loader!.Image;
        var start = image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.MethodDef, p.Method.MethodDefRid, 5);
        var end = p.Method.MethodDefRid < image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.MethodDef) ? image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.MethodDef, p.Method.MethodDefRid + 1, 5) : image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.Param) + 1;
        for (int rid = start; rid < end; rid++) if (image.Tables.GetCell(DotnetVM.Metadata.TableKind.Param, rid, 1) == p.Position + 1) return rid;
        return 0;
    }
    private static StackSlot MethodParameters(IntrinsicContext ctx, VmMethod method) => MetadataArray(ctx, "System.Reflection.ParameterInfo",
        Enumerable.Range(0, method.Signature.ParamTypes.Length).Select(i => WrapBcl(ctx, "System.Reflection.ParameterInfo", new ParameterDescription(method, i))));
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
            BclFace(r, type, "GetParameters", true, [], static (ctx, a) => MethodParameters(ctx, ((VmRuntimeMethod)a[0].ObjectValue!).Target));
            BclFace(r, type, "get_Attributes", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeMethod)a[0].ObjectValue!).Target.Flags));
            BclFace(r, type, "GetMethodImplementationFlags", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeMethod)a[0].ObjectValue!).Target.ImplFlags));
        }
        StackSlot[] InvocationArguments(IntrinsicContext ctx, VmRuntimeMethod method, StackSlot argumentSlot) {
            var args = argumentSlot.ObjectValue is VmArray array ? array.Elements : [];
            if (args.Length != method.Target.Signature.ParamTypes.Length) throw new UnhandledGuestException("System.Reflection.TargetParameterCountException", null);
            return args.Select((v, i) => v.ObjectValue is VmBoxedValue boxed && (method.Target.Loader ?? ctx.Types).ResolveToken(method.Target.Signature.ParamTypes[i]).IsValueType
                ? boxed.Fields.Length == 1 ? boxed.Fields[0] : StackSlot.OfValueType(new VmStructValue(boxed.Type, boxed.Fields)) : v).ToArray();
        }
        foreach (var parameters in new[] { new[] { "System.Object[]" }, new[] { "System.Reflection.BindingFlags", "System.Reflection.Binder", "System.Object[]", "System.Globalization.CultureInfo" } })
            BclFace(r, "System.Reflection.ConstructorInfo", "Invoke", true, parameters, (ctx, a) => {
                var method = (VmRuntimeMethod)a[0].ObjectValue!;
                return StackSlot.OfObject(ctx.NewInstanceHook!(method.ReflectedType ?? method.Target.DeclaringType, method.Target, InvocationArguments(ctx, method, a[parameters.Length == 1 ? 1 : 3]), null));
            });
        BclFace(r, "System.Reflection.MethodInfo", "MakeGenericMethod", true, ["System.Type[]"], static (ctx, a) => {
            var method = (VmRuntimeMethod)a[0].ObjectValue!;
            var arguments = ((VmArray)a[1].ObjectValue!).Elements.Select(v => ((VmRuntimeObject)v.ObjectValue!).Target).ToArray();
            if (arguments.Length != method.Target.Signature.GenericParamCount) throw new UnhandledGuestException("System.ArgumentException", "Invalid method type arguments.");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = method.Target, ReflectedType = method.ReflectedType, MethodArguments = arguments }));
        });
        BclFace(r, "System.Reflection.MethodBase", "Invoke", true, ["System.Object", "System.Object[]"], (ctx, a) => {
            var method = (VmRuntimeMethod)a[0].ObjectValue!;
            var values = InvocationArguments(ctx, method, a[2]);
            var context = GenericContext.Of((method.ReflectedType as VmConstructedType)?.TypeArguments, method.MethodArguments);
            var result = ctx.InvokeGuestMethod!(method.Target, method.Target.IsStatic ? values : [a[1], .. values], context);
            var returnType = (method.Target.Loader ?? ctx.Types).ResolveToken(method.Target.Signature.ReturnType, context);
            if (returnType.FullName == "System.Void") return StackSlot.Null;
            if (returnType.IsValueType) return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(returnType, result.ObjectValue is VmStructValue structure ? structure.Fields : [result])));
            return result;
        });
        BclFace(r, "System.Reflection.ParameterInfo", "get_ParameterType", true, [], static (ctx, a) => {
            var p = BclValue<ParameterDescription>(a[0]); return DefaultIntrinsics.MakeRuntimeObject(ctx, (p.Method.Loader ?? ctx.Types).ResolveToken(p.Position < 0 ? p.Method.Signature.ReturnType : p.Method.Signature.ParamTypes[p.Position]));
        });
        BclFace(r, "System.Reflection.MethodInfo", "get_ReturnParameter", true, [], static (ctx, a) => WrapBcl(ctx, "System.Reflection.ParameterInfo", new ParameterDescription(((VmRuntimeMethod)a[0].ObjectValue!).Target, -1)));
        BclFace(r, "System.Reflection.ParameterInfo", "get_Position", true, [], static (_, a) => StackSlot.OfInt32(BclValue<ParameterDescription>(a[0]).Position));
        BclFace(r, "System.Reflection.ParameterInfo", "get_Member", true, [], static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = BclValue<ParameterDescription>(a[0]).Method })));
        BclFace(r, "System.Reflection.ParameterInfo", "get_Attributes", true, [], static (_, a) => {
            var p = BclValue<ParameterDescription>(a[0]); var rid = ParameterRid(p);
            return StackSlot.OfInt32(rid == 0 ? 0 : (int)p.Method.Loader!.Image.Tables.GetCell(DotnetVM.Metadata.TableKind.Param, rid, 0));
        });
        BclFace(r, "System.Reflection.ParameterInfo", "get_HasDefaultValue", true, [], static (_, a) => {
            var p = BclValue<ParameterDescription>(a[0]); var rid = ParameterRid(p);
            return StackSlot.OfInt32(rid != 0 && (p.Method.Loader!.Image.Tables.GetCell(DotnetVM.Metadata.TableKind.Param, rid, 0) & 0x1000) != 0 ? 1 : 0);
        });
        foreach (var name in new[] { "get_DefaultValue", "get_RawDefaultValue" }) BclFace(r, "System.Reflection.ParameterInfo", name, true, [], static (ctx, a) => {
            var p = BclValue<ParameterDescription>(a[0]); var image = p.Method.Loader!.Image; var parameter = ParameterRid(p);
            for (int rid = 1; rid <= image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.Constant); rid++) {
                var parent = image.Tables.DecodeCoded(DotnetVM.Metadata.TableKind.Constant, rid, 1, DotnetVM.Metadata.CodedIndexKind.HasConstant);
                if (parent.Table != DotnetVM.Metadata.TableKind.Param || parent.Rid != parameter) continue;
                var code = (DotnetVM.Metadata.Signatures.SigKind)image.Tables.GetCell(DotnetVM.Metadata.TableKind.Constant, rid, 0);
                var blob = image.GetBlob(image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.Constant, rid, 2));
                if (code == DotnetVM.Metadata.Signatures.SigKind.Object) return StackSlot.Null;
                if (code == DotnetVM.Metadata.Signatures.SigKind.String) return StackSlot.OfObject(ctx.MakeString(System.Text.Encoding.Unicode.GetString(blob)));
                var type = p.Method.Loader.ResolveToken(p.Method.Signature.ParamTypes[p.Position]);
                var reader = new DotnetVM.Binary.SpanReader(blob); var value = AttributeValue(ctx, p.Method.Loader, type, ref reader);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(type, value.ObjectValue is VmStructValue structure ? structure.Fields : [value])));
            }
            return WrapBcl(ctx, "System.DBNull", DBNull.Value);
        });
        BclFace(r, "System.Reflection.ParameterInfo", "get_Name", true, [], static (ctx, a) => {
            var p = BclValue<ParameterDescription>(a[0]); var image = p.Method.Loader!.Image;
            var start = image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.MethodDef, p.Method.MethodDefRid, 5);
            var end = p.Method.MethodDefRid < image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.MethodDef) ? image.Tables.GetRowIndex(DotnetVM.Metadata.TableKind.MethodDef, p.Method.MethodDefRid + 1, 5) : image.Tables.GetRowCount(DotnetVM.Metadata.TableKind.Param) + 1;
            for (int rid = start; rid < end; rid++) if (image.Tables.GetCell(DotnetVM.Metadata.TableKind.Param, rid, 1) == p.Position + 1)
                return StackSlot.OfObject(ctx.MakeString(image.GetString((int)image.Tables.GetCell(DotnetVM.Metadata.TableKind.Param, rid, 2))));
            return StackSlot.Null;
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
            BclFace(r, type, "GetProperties", true, ["System.Reflection.BindingFlags"], static (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target; if (target is VmConstructedType c) target = c.Definition;
                var flags = (BindingFlags)a[1].AsInt32;
                return MetadataArray(ctx, "System.Reflection.PropertyInfo", target.Methods.Where(m => m.Name.StartsWith("get_", StringComparison.Ordinal) &&
                    (m.IsStatic ? flags.HasFlag(BindingFlags.Static) : flags.HasFlag(BindingFlags.Instance)) && (m.IsPublic ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic)))
                    .Select(m => StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeProperty { Name = m.Name[4..], Getter = m }))));
            });
            BclFace(r, type, "GetFields", true, ["System.Reflection.BindingFlags"], static (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target; if (target is VmConstructedType c) target = c.Definition;
                var flags = (BindingFlags)a[1].AsInt32;
                return MetadataArray(ctx, "System.Reflection.FieldInfo", target.Fields.Where(f => (f.IsStatic ? flags.HasFlag(BindingFlags.Static) : flags.HasFlag(BindingFlags.Instance)) &&
                    ((f.Flags & 7) == 6 ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic)))
                    .Select(f => StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeField { Target = f }))));
            });
        }
        BclFace(r, "System.Reflection.PropertyInfo", "get_PropertyType", true, [], static (ctx, a) => {
            var m = ((VmRuntimeProperty)a[0].ObjectValue!).Getter; return DefaultIntrinsics.MakeRuntimeObject(ctx, (m.Loader ?? ctx.Types).ResolveToken(m.Signature.ReturnType));
        });
        BclFace(r, "System.Reflection.PropertyInfo", "GetIndexParameters", true, [], static (ctx, a) => MethodParameters(ctx, ((VmRuntimeProperty)a[0].ObjectValue!).Getter));
        foreach (var get in new[] { true, false }) BclFace(r, "System.Reflection.PropertyInfo", get ? "GetGetMethod" : "GetSetMethod", true, ["System.Boolean"], (ctx, a) => {
            var property = (VmRuntimeProperty)a[0].ObjectValue!;
            var method = get ? property.Getter : property.Getter.DeclaringType.Methods.FirstOrDefault(m => m.Name == "set_" + property.Name);
            return method is null || !method.IsPublic && a[1].AsInt32 == 0 ? StackSlot.Null : StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = method }));
        });
        BclFace(r, "System.Reflection.FieldInfo", "get_FieldType", true, [], static (ctx, a) => DefaultIntrinsics.MakeRuntimeObject(ctx, ((VmRuntimeField)a[0].ObjectValue!).Target.FieldType!));
        BclFace(r, "System.Reflection.FieldInfo", "get_Attributes", true, [], static (_, a) => StackSlot.OfInt32((int)((VmRuntimeField)a[0].ObjectValue!).Target.Flags));
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

