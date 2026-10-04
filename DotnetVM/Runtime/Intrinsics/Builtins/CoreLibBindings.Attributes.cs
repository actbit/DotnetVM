using DotnetVM.Binary;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static StackSlot MetadataArray(IntrinsicContext ctx, string elementType, IEnumerable<StackSlot> items) {
        var values = items.ToArray(); using var allocation = ctx.Heap.ReserveArray(values.Length);
        return StackSlot.OfObject(allocation.Commit(new VmArray(new VmArrayType { ElementType = FindAnyType(ctx, elementType)! }, values)));
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
        if (type.FullName == "System.Object") {
            var encodedType = AttributeEncodedType(ctx, loader, ref reader);
            return BoxReflectionResult(ctx, AttributeValue(ctx, loader, encodedType, ref reader), encodedType);
        }
        return type.FullName switch {
            "System.String" => AttributeString(ref reader) is { } text ? StackSlot.OfObject(ctx.MakeString(text)) : StackSlot.Null,
            "System.Type" => AttributeString(ref reader) is { } name ? DefaultIntrinsics.MakeRuntimeObject(ctx,
                ResolveTypeName(ctx, name) ?? throw new UnhandledGuestException("System.TypeLoadException", name)) : StackSlot.Null,
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

}
