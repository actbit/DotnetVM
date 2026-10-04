using DotnetVM.Binary;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private sealed record VmAttributeDescription(VmMethod Constructor, StackSlot[] Values);
    private static StackSlot MetadataArray(IntrinsicContext ctx, string elementType, IEnumerable<StackSlot> items) {
        var values = items.ToArray(); using var allocation = ctx.Heap.ReserveArray(values.Length);
        return StackSlot.OfObject(allocation.Commit(new VmArray(new VmArrayType { ElementType = FindAnyType(ctx, elementType)! }, values)));
    }
    private static void RegisterAttributeReflection(IntrinsicRegistry r) {
        foreach (var type in new[] { "System.Reflection.ParameterInfo", "System.Reflection.MemberInfo", "System.Reflection.Assembly", "System.Type", "System.Reflection.PropertyInfo", "System.Reflection.FieldInfo", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo" })
            BclFace(r, type, "GetCustomAttributesData", true, [], static (ctx, a) => ReadVmAttributes(ctx, a[0], null, false, dataOnly: true));
        BclFace(r, "System.Reflection.CustomAttributeData", "get_AttributeType", true, [], static (ctx, a) => DefaultIntrinsics.MakeRuntimeObject(ctx, BclValue<VmAttributeDescription>(a[0]).Constructor.DeclaringType));
        BclFace(r, "System.Reflection.CustomAttributeData", "get_Constructor", true, [], static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = BclValue<VmAttributeDescription>(a[0]).Constructor })));
        BclFace(r, "System.Reflection.CustomAttributeData", "get_ConstructorArguments", true, [], static (ctx, a) => {
            var attribute = BclValue<VmAttributeDescription>(a[0]);
            StackSlot Typed(VmType type, StackSlot value) {
                var argumentType = FindAnyType(ctx, "System.Reflection.CustomAttributeTypedArgument")!;
                if (value.ObjectValue is VmArray array) value = MetadataArray(ctx, argumentType.FullName, array.Elements.Select(v => Typed(array.ArrayType.ElementType, v)));
                else if (type.IsValueType) value = StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(type, value.ObjectValue is VmStructValue structure ? structure.Fields : [value])));
                var fields = argumentType.Fields.Where(f => !f.IsStatic && !f.IsLiteral).Select(f => f.Name.Contains("argumentType", StringComparison.OrdinalIgnoreCase) ? DefaultIntrinsics.MakeRuntimeObject(ctx, type) : value).ToArray();
                return StackSlot.OfValueType(new VmStructValue(argumentType, fields));
            }
            return MetadataArray(ctx, "System.Reflection.CustomAttributeTypedArgument", attribute.Values.Select((v, i) => Typed(attribute.Constructor.Loader!.ResolveToken(attribute.Constructor.Signature.ParamTypes[i]), v)));
        });
        foreach (var type in new[] { "System.Reflection.MemberInfo", "System.Type", "System.Reflection.PropertyInfo", "System.Reflection.FieldInfo", "System.Reflection.MethodInfo", "System.Reflection.MethodBase" })
            BclFace(r, type, "get_MemberType", true, [], static (_, a) => StackSlot.OfInt32(a[0].ObjectValue switch {
                VmRuntimeObject rt => rt.Target is VmClassType { DeclaringType: not null } ? 128 : 32,
                VmRuntimeMethod rm => rm.Target.IsConstructor ? 1 : 8, VmRuntimeField => 4, VmRuntimeProperty => 16, _ => 0,
            }));
        foreach (var type in new[] { "System.Reflection.Assembly", "System.Reflection.MemberInfo", "System.Type", "System.RuntimeType", "System.Reflection.PropertyInfo", "System.Reflection.FieldInfo", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo" }) {
            BclFace(r, type, "GetCustomAttributes", true, ["System.Type", "System.Boolean"], static (ctx, a) =>
                ReadVmAttributes(ctx, a[0], ((VmRuntimeObject)a[1].ObjectValue!).Target, a[2].AsInt32 != 0));
            BclFace(r, type, "GetCustomAttributes", true, ["System.Boolean"], static (ctx, a) => ReadVmAttributes(ctx, a[0], null, a[1].AsInt32 != 0));
            BclFace(r, type, "IsDefined", true, ["System.Type", "System.Boolean"], static (ctx, a) =>
                StackSlot.OfInt32(((VmArray)ReadVmAttributes(ctx, a[0], ((VmRuntimeObject)a[1].ObjectValue!).Target, a[2].AsInt32 != 0).ObjectValue!).Length > 0 ? 1 : 0));
        }
    }

    private static string? AttributeString(ref SpanReader reader) {
        if (reader.ReadByte() == 255) return null;
        reader.Seek(reader.Offset - 1);
        var length = checked((int)reader.ReadCompressedUInt32());
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static StackSlot AttributeValue(IntrinsicContext ctx, TypeLoader loader, VmType type, ref SpanReader reader) {
        if (type is VmArrayType arrayType) {
            var count = reader.ReadInt32(); if (count == -1) return StackSlot.Null;
            if (count < 0 || count > reader.Remaining) throw new BadImageFormatException("Invalid attribute array length.");
            using var allocation = ctx.Heap.ReserveArray(count); var values = new StackSlot[count];
            for (int i = 0; i < count; i++) values[i] = AttributeValue(ctx, loader, arrayType.ElementType, ref reader);
            return StackSlot.OfObject(allocation.Commit(new VmArray(arrayType, values)));
        }
        if (type.IsEnum) type = type.Fields.First(f => f.Name == "value__").FieldType!;
        return type.FullName switch {
            "System.String" => AttributeString(ref reader) is { } text ? StackSlot.OfObject(ctx.MakeString(text)) : StackSlot.Null,
            "System.Type" => AttributeString(ref reader) is { } name && FindAnyType(ctx, name.Split(',')[0]) is { } t ? DefaultIntrinsics.MakeRuntimeObject(ctx, t) : StackSlot.Null,
            "System.Boolean" or "System.Byte" => StackSlot.OfInt32(reader.ReadByte()),
            "System.SByte" => StackSlot.OfInt32((sbyte)reader.ReadByte()),
            "System.Char" or "System.UInt16" => StackSlot.OfInt32(reader.ReadUInt16()),
            "System.Int16" => StackSlot.OfInt32(reader.ReadInt16()),
            "System.Int32" or "System.UInt32" => StackSlot.OfInt32(reader.ReadInt32()),
            "System.Int64" or "System.UInt64" => StackSlot.OfInt64(reader.ReadInt64()),
            "System.Single" => StackSlot.OfFloat(reader.ReadSingle()),
            "System.Double" => StackSlot.OfFloat(reader.ReadDouble()),
            _ => throw new UnhandledGuestException("System.NotSupportedException", "Attribute argument type is unsupported: " + type.FullName),
        };
    }

    private static StackSlot ReadVmAttributes(IntrinsicContext ctx, StackSlot receiver, VmType? filter, bool inherit, bool dataOnly = false) {
        var type = receiver.ObjectValue switch { VmRuntimeObject rt => rt.Target as VmClassType, VmRuntimeMethod rm => rm.Target.DeclaringType as VmClassType,
            VmRuntimeField rf => rf.Target.DeclaringType as VmClassType, VmRuntimeProperty rp => rp.Getter.DeclaringType as VmClassType, _ => null };
        var attributes = new List<StackSlot>();
        var parameter = receiver.ObjectValue is VmObject o && BclStates.TryGetValue(o, out var state) ? state.Value as ParameterDescription : null;
        if (parameter is not null) type = parameter.Method.DeclaringType as VmClassType;
        var assembly = receiver.ObjectValue as VmAssemblyObject;
        if (type is null && assembly is null) return AttributeArray(ctx, attributes, dataOnly ? FindAnyType(ctx, "System.Reflection.CustomAttributeData") : filter);
        var ownerTable = assembly is not null ? TableKind.Assembly : receiver.ObjectValue is VmRuntimeMethod ? TableKind.MethodDef : receiver.ObjectValue is VmRuntimeField ? TableKind.Field : TableKind.TypeDef;
        var ownerRid = receiver.ObjectValue switch { VmAssemblyObject => 1, VmRuntimeMethod rm => rm.Target.MethodDefRid, VmRuntimeField rf => rf.Target.FieldRid, _ => type!.TypeDefRid };
        if (parameter is not null) { ownerTable = TableKind.Param; ownerRid = ParameterRid(parameter); }
        if (receiver.ObjectValue is VmRuntimeProperty property) {
            ownerTable = TableKind.Property;
            for (int rid = 1; rid <= type!.Image.Tables.GetRowCount(TableKind.MethodSemantics); rid++)
                if (type.Image.Tables.GetRowIndex(TableKind.MethodSemantics, rid, 1) == property.Getter.MethodDefRid) {
                    var association = type.Image.Tables.DecodeCoded(TableKind.MethodSemantics, rid, 2, CodedIndexKind.HasSemantics);
                    if (association.Table == TableKind.Property) { ownerRid = association.Rid; break; }
                }
        }
        var loader = assembly?.Loader ?? type!.Loader!; var image = loader.Image;
        var count = image.Tables.GetRowCount(TableKind.CustomAttribute); ctx.Heap.ChargeHostWork(count);
        for (int rid = 1; rid <= count; rid++) {
            var parent = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 0, CodedIndexKind.HasCustomAttribute);
            if (parent.Table != ownerTable || parent.Rid != ownerRid) continue;
            var ctorToken = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
            VmMethod? constructor;
            if (ctorToken.Table == TableKind.MethodDef) constructor = loader.GetTypeDef(FindMethodOwner(image, ctorToken.Rid)).Methods.First(m => m.MethodDefRid == ctorToken.Rid);
            else {
                var owner = image.Tables.DecodeCoded(TableKind.MemberRef, ctorToken.Rid, 0, CodedIndexKind.MemberRefParent);
                var attributeType = loader.ResolveToken(new SigType(SigKind.TypeToken, Token: Token.From(owner.Table, owner.Rid).Value));
                if (filter is not null && !attributeType.IsAssignableTo(filter)) continue;
                var signature = SignatureDecoder.DecodeMethodSignature(image.GetMemberRefSignature(ctorToken.Rid));
                var names = signature.ParamTypes.Select(t => loader.ResolveToken(t).FullName).ToArray();
                constructor = attributeType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Signature.ParamTypes.Length == names.Length && m.Signature.ParamTypes.Select(t => (m.Loader ?? loader).ResolveToken(t).FullName).SequenceEqual(names));
            }
            if (constructor is null || filter is not null && !constructor.DeclaringType.IsAssignableTo(filter)) continue;
            var blob = image.GetBlob(image.Tables.GetRowIndex(TableKind.CustomAttribute, rid, 2)); ctx.Heap.ChargeHostBuffer(blob.Length);
            var reader = new SpanReader(blob); if (reader.ReadUInt16() != 1) throw new BadImageFormatException("Invalid custom attribute prolog.");
            var values = new StackSlot[constructor.Signature.ParamTypes.Length];
            for (int i = 0; i < values.Length; i++) values[i] = AttributeValue(ctx, loader, (constructor.Loader ?? loader).ResolveToken(constructor.Signature.ParamTypes[i]), ref reader);
            if (dataOnly) {
                var data = WrapBcl(ctx, "System.Reflection.CustomAttributeData", new VmAttributeDescription(constructor, values));
                ((VmObject)data.ObjectValue!).BclReferences = values.Select(v => v.ObjectValue).OfType<VmObject>().ToArray();
                attributes.Add(data); continue;
            }
            var instance = ctx.NewInstanceHook!(constructor.DeclaringType, constructor, values, null); var slot = StackSlot.OfObject(instance);
            var named = reader.ReadUInt16();
            for (int i = 0; i < named; i++) {
                var kind = reader.ReadByte(); var code = reader.ReadByte();
                var memberType = code == 0x55 ? FindAnyType(ctx, AttributeString(ref reader)!.Split(',')[0])! : code == 0x50 ? FindAnyType(ctx, "System.Type")! : loader.ResolveToken(new SigType((SigKind)code));
                var name = AttributeString(ref reader)!; var value = AttributeValue(ctx, loader, memberType, ref reader);
                if (kind == 0x54) ctx.InvokeGuestInstanceMethod!(slot, "set_" + name, [value]);
                else { var field = constructor.DeclaringType.Fields.First(f => f.Name == name); var layout = new ObjectModel().GetLayout((VmClassType)constructor.DeclaringType); instance.Fields[layout[field]] = value; }
            }
            attributes.Add(slot);
        }
        if (inherit && ownerTable == TableKind.TypeDef && type!.BaseType is VmClassType baseType) {
            var inherited = (VmArray)ReadVmAttributes(ctx, DefaultIntrinsics.MakeRuntimeObject(ctx, baseType), filter, true).ObjectValue!;
            attributes.AddRange(inherited.Elements);
        }
        return AttributeArray(ctx, attributes, dataOnly ? FindAnyType(ctx, "System.Reflection.CustomAttributeData") : filter);
    }
    private static StackSlot AttributeArray(IntrinsicContext ctx, List<StackSlot> attributes, VmType? filter) {
        using var allocation = ctx.Heap.ReserveArray(attributes.Count);
        return StackSlot.OfObject(allocation.Commit(new VmArray(new VmArrayType { ElementType = filter ?? FindAnyType(ctx, "System.Attribute")! }, attributes.ToArray())));
    }
}
