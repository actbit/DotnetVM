using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static TypeLoader MetadataScope(IntrinsicContext ctx, StackSlot scope) => ctx.Shared.RuntimeMetadata.Resolve<TypeLoader>(scope.Int64Value);
    private static Token MetadataToken(StackSlot value) => new(unchecked((uint)value.AsInt32));
    private static void NativeWrite(StackSlot address, StackSlot value) => ((VmByRef)address.ObjectValue!).Write(value);
    private static StackSlot NativeOffset(StackSlot address, int bytes) => MemoryOps.TryPointerArithmetic(ILOp.Add, address, StackSlot.OfInt32(bytes)) ?? address;

    private static StackSlot MetadataBytes(IntrinsicContext ctx, ReadOnlySpan<byte> bytes) {
        using var allocation = ctx.Heap.ReserveLocalloc(bytes.Length);
        return StackSlot.OfObject(new VmNativePointer { Memory = allocation.Commit(new VmLocallocMemory { Bytes = bytes.ToArray() }), IsReadOnly = true });
    }

    private static StackSlot MetadataBlob(IntrinsicContext ctx, ReadOnlySpan<byte> bytes) {
        var result = ctx.Shared.RuntimeMetadata.Structure(ctx, "System.Reflection.ConstArray");
        ctx.Shared.RuntimeMetadata.Set(result, "m_length", StackSlot.OfInt32(bytes.Length));
        ctx.Shared.RuntimeMetadata.Set(result, "m_constArray", MetadataBytes(ctx, bytes));
        return StackSlot.OfValueType(result);
    }

    private static void AddNativeMetadataImports(Action<string, string, IntrinsicImpl> internalCall, Action<string, string, IntrinsicImpl> import) {
        const string md = "System.Reflection.MetadataImport";
        internalCall(md, "GetFieldMarshal", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]); var tables = scope.Image.Tables;
            var blob = ReadOnlySpan<byte>.Empty;
            ctx.Heap.ChargeHostWork(tables.GetRowCount(TableKind.FieldMarshal));
            for (var rid = 1; rid <= tables.GetRowCount(TableKind.FieldMarshal); rid++) {
                if (tables.DecodeCoded(TableKind.FieldMarshal, rid, 0, CodedIndexKind.HasFieldMarshal) != (token.Table, token.Rid)) continue;
                blob = scope.Image.GetBlob(tables.GetRowIndex(TableKind.FieldMarshal, rid, 1)); break;
            }
            NativeWrite(a[2], MetadataBlob(ctx, blob)); return StackSlot.OfInt32(0);
        });
        internalCall(md, "GetDefaultValue", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]); var tables = scope.Image.Tables;
            NativeWrite(a[2], StackSlot.OfInt64(0)); NativeWrite(a[3], StackSlot.OfNativeInt(0));
            NativeWrite(a[4], StackSlot.OfInt32(0)); NativeWrite(a[5], StackSlot.OfInt32(1));
            ctx.Heap.ChargeHostWork(tables.GetRowCount(TableKind.Constant));
            for (var rid = 1; rid <= tables.GetRowCount(TableKind.Constant); rid++) {
                if (tables.DecodeCoded(TableKind.Constant, rid, 2, CodedIndexKind.HasConstant) != (token.Table, token.Rid)) continue;
                var code = (int)tables.GetCell(TableKind.Constant, rid, 0);
                var bytes = scope.Image.GetBlob(tables.GetRowIndex(TableKind.Constant, rid, 3));
                NativeWrite(a[5], StackSlot.OfInt32(code));
                if (code == 14) {
                    NativeWrite(a[3], MetadataBytes(ctx, bytes)); NativeWrite(a[4], StackSlot.OfInt32(bytes.Length / 2));
                } else {
                    if (bytes.Length > 8) throw new BadImageFormatException("Invalid metadata constant.");
                    long bits = 0;
                    for (var i = 0; i < bytes.Length; i++) bits |= (long)bytes[i] << (i * 8);
                    NativeWrite(a[2], StackSlot.OfInt64(bits));
                }
                break;
            }
            return StackSlot.OfInt32(0);
        });
        import(md, "MetadataImport_Enum", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var tables = scope.Image.Tables;
            var table = (TableKind)((uint)a[1].AsInt32 >> 24); var parent = MetadataToken(a[2]);
            var values = new List<StackSlot>();
            ctx.Heap.ChargeHostWork(tables.GetRowCount(table));
            if (table == TableKind.MethodDef && parent.Table is TableKind.Property or TableKind.Event) {
                ctx.Heap.ChargeHostWork(tables.GetRowCount(TableKind.MethodSemantics));
                for (var rid = 1; rid <= tables.GetRowCount(TableKind.MethodSemantics); rid++) {
                    if (tables.DecodeCoded(TableKind.MethodSemantics, rid, 2, CodedIndexKind.HasSemantics) != (parent.Table, parent.Rid)) continue;
                    values.Add(StackSlot.OfInt32(unchecked((int)Token.From(TableKind.MethodDef, tables.GetRowIndex(TableKind.MethodSemantics, rid, 1)).Value)));
                    values.Add(StackSlot.OfInt32((int)tables.GetCell(TableKind.MethodSemantics, rid, 0)));
                }
            } else for (var rid = 1; rid <= tables.GetRowCount(table); rid++) {
                bool include;
                if (table == TableKind.CustomAttribute) include = tables.DecodeCoded(table, rid, 0, CodedIndexKind.HasCustomAttribute) == (parent.Table, parent.Rid);
                else if (table == TableKind.Param && parent.Table == TableKind.MethodDef) {
                    var start = tables.GetRowIndex(TableKind.MethodDef, parent.Rid, 5);
                    var end = parent.Rid < tables.GetRowCount(TableKind.MethodDef) ? tables.GetRowIndex(TableKind.MethodDef, parent.Rid + 1, 5) : tables.GetRowCount(TableKind.Param) + 1;
                    include = rid >= start && rid < end;
                } else if (table == TableKind.Field && parent.Table == TableKind.TypeDef) {
                    var start = tables.GetRowIndex(TableKind.TypeDef, parent.Rid, 4);
                    var end = parent.Rid < tables.GetRowCount(TableKind.TypeDef) ? tables.GetRowIndex(TableKind.TypeDef, parent.Rid + 1, 4) : tables.GetRowCount(TableKind.Field) + 1;
                    include = rid >= start && rid < end;
                } else if (table == TableKind.Property && parent.Table == TableKind.TypeDef) {
                    include = false;
                    for (var map = 1; map <= tables.GetRowCount(TableKind.PropertyMap); map++) {
                        if (tables.GetRowIndex(TableKind.PropertyMap, map, 0) != parent.Rid) continue;
                        var start = tables.GetRowIndex(TableKind.PropertyMap, map, 1);
                        var end = map < tables.GetRowCount(TableKind.PropertyMap)
                            ? tables.GetRowIndex(TableKind.PropertyMap, map + 1, 1)
                            : tables.GetRowCount(TableKind.Property) + 1;
                        include = rid >= start && rid < end;
                        break;
                    }
                } else throw new UnhandledGuestException("System.NotSupportedException", "Unsupported metadata enumeration.");
                if (include) values.Add(StackSlot.OfInt32(unchecked((int)Token.From(table, rid).Value)));
            }
            var capacity = ((VmByRef)a[3].ObjectValue!).Read().AsInt32;
            if (values.Count > capacity) NativeStackHandle(ctx, a[5]).Write(MetadataArray(ctx, "System.Int32", values));
            else for (var i = 0; i < values.Count; i++) MemoryOps.StoreIndirect(ILOp.Stind_I4, NativeOffset(a[4], i * 4), values[i]);
            NativeWrite(a[3], StackSlot.OfInt32(values.Count)); return null;
        });
        internalCall(md, "GetCustomAttributeProps", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var rid = MetadataToken(a[1]).Rid;
            var ctor = scope.Image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
            NativeWrite(a[2], StackSlot.OfInt32(unchecked((int)Token.From(ctor.Table, ctor.Rid).Value)));
            NativeWrite(a[3], MetadataBlob(ctx, scope.Image.GetBlob(scope.Image.Tables.GetRowIndex(TableKind.CustomAttribute, rid, 2))));
            return StackSlot.OfInt32(0);
        });
        internalCall(md, "GetParentToken", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]); var tables = scope.Image.Tables;
            Token parent;
            if (token.Table == TableKind.MemberRef) { var p = tables.DecodeCoded(token.Table, token.Rid, 0, CodedIndexKind.MemberRefParent); parent = Token.From(p.Table, p.Rid); }
            else if (token.Table == TableKind.MethodDef) parent = Token.From(TableKind.TypeDef, ((VmClassType)scope.GetMethodByToken(token.Value)!.DeclaringType).TypeDefRid);
            else if (token.Table is TableKind.Field or TableKind.Param) {
                var ownerTable = token.Table == TableKind.Field ? TableKind.TypeDef : TableKind.MethodDef;
                var column = token.Table == TableKind.Field ? 4 : 5;
                var owner = 0;
                for (var rid = 1; rid <= tables.GetRowCount(ownerTable); rid++) {
                    var start = tables.GetRowIndex(ownerTable, rid, column);
                    var end = rid < tables.GetRowCount(ownerTable) ? tables.GetRowIndex(ownerTable, rid + 1, column) : tables.GetRowCount(token.Table) + 1;
                    if (token.Rid >= start && token.Rid < end) { owner = rid; break; }
                }
                parent = Token.From(ownerTable, owner);
            }
            else if (token.Table == TableKind.Property) {
                var owner = 0;
                for (var map = 1; map <= tables.GetRowCount(TableKind.PropertyMap); map++) {
                    var start = tables.GetRowIndex(TableKind.PropertyMap, map, 1);
                    var end = map < tables.GetRowCount(TableKind.PropertyMap) ? tables.GetRowIndex(TableKind.PropertyMap, map + 1, 1) : tables.GetRowCount(TableKind.Property) + 1;
                    if (token.Rid >= start && token.Rid < end) { owner = tables.GetRowIndex(TableKind.PropertyMap, map, 0); break; }
                }
                parent = Token.From(TableKind.TypeDef, owner);
            }
            else throw new UnhandledGuestException("System.NotSupportedException", "Unsupported metadata parent.");
            NativeWrite(a[2], StackSlot.OfInt32(unchecked((int)parent.Value))); return StackSlot.OfInt32(0);
        });
        internalCall(md, "GetParamDefProps", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var rid = MetadataToken(a[1]).Rid;
            NativeWrite(a[2], StackSlot.OfInt32((int)scope.Image.Tables.GetCell(TableKind.Param, rid, 1)));
            NativeWrite(a[3], StackSlot.OfInt32((int)scope.Image.Tables.GetCell(TableKind.Param, rid, 0))); return StackSlot.OfInt32(0);
        });
        internalCall(md, "GetPropertyProps", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]);
            var token = MetadataToken(a[1]);
            if (token.Table != TableKind.Property || token.Rid <= 0 || token.Rid > scope.Image.Tables.GetRowCount(TableKind.Property))
                throw new UnhandledGuestException("System.ArgumentException", "Invalid property token.");
            var row = scope.Image.Tables.GetRowIndex(TableKind.Property, token.Rid, 1);
            var name = scope.Image.GetString(row);
            NativeWrite(a[2], MetadataBytes(ctx, System.Text.Encoding.UTF8.GetBytes(name + "\0")));
            NativeWrite(a[3], StackSlot.OfInt32((int)scope.Image.Tables.GetCell(TableKind.Property, token.Rid, 0)));
            NativeWrite(a[4], MetadataBlob(ctx, scope.Image.GetBlob(scope.Image.Tables.GetRowIndex(TableKind.Property, token.Rid, 2))));
            return StackSlot.OfInt32(0);
        });
        internalCall(md, "GetName", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]);
            var column = token.Table switch { TableKind.TypeDef => 1, TableKind.TypeRef => 1, TableKind.MethodDef => 3, TableKind.MemberRef => 1, TableKind.Field => 1, TableKind.Param => 2, TableKind.Property => 1, _ => throw new BadImageFormatException("Invalid metadata name token.") };
            var name = scope.Image.GetString(scope.Image.Tables.GetRowIndex(token.Table, token.Rid, column));
            NativeWrite(a[2], MetadataBytes(ctx, System.Text.Encoding.UTF8.GetBytes(name + "\0"))); return StackSlot.OfInt32(0);
        });
        internalCall(md, "IsValidToken", static (ctx, a) => {
            var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]);
            return StackSlot.OfInt32((uint)token.Table <= 0x2c && token.Rid > 0 && token.Rid <= scope.Image.Tables.GetRowCount(token.Table) ? 1 : 0);
        });
        foreach (var method in new[] { "GetSigOfMethodDef", "GetMemberRefProps", "GetSignatureFromToken" })
            internalCall(md, method, static (ctx, a) => {
                var scope = MetadataScope(ctx, a[0]); var token = MetadataToken(a[1]);
                var column = token.Table switch { TableKind.MethodDef => 4, TableKind.MemberRef => 2, TableKind.StandAloneSig => 0, _ => throw new BadImageFormatException("Invalid signature token.") };
                NativeWrite(a[2], MetadataBlob(ctx, scope.Image.GetBlob(scope.Image.Tables.GetRowIndex(token.Table, token.Rid, column)))); return StackSlot.OfInt32(0);
            });
        import("System.ModuleHandle", "ModuleHandle_ResolveMethod", static (ctx, a) => {
            var scope = NativeQCallModule(ctx, a[0]);
            var context = GenericContext.Of(NativeTypeArguments(ctx, a[2], a[3].AsInt32), NativeTypeArguments(ctx, a[4], a[5].AsInt32));
            var resolved = ctx.ResolveReflectionMethod!(scope, a[1].AsInt32, context);
            var declaring = resolved.Context?.ClassArgs is { Length: > 0 } classArgs ? new VmConstructedType { Definition = resolved.Method.DeclaringType, TypeArguments = classArgs } : resolved.Method.DeclaringType;
            var value = (VmRuntimeMethod)DefaultIntrinsics.MakeRuntimeMethod(ctx, resolved.Method, declaring, resolved.Context?.MethodArgs).ObjectValue!;
            var handle = ctx.Shared.RuntimeMetadata.Structure(ctx, "System.RuntimeMethodHandleInternal");
            ctx.Shared.RuntimeMetadata.Set(handle, "m_handle", ctx.Shared.RuntimeMetadata.Get(value.ManagedInstance!, "m_handle"));
            return StackSlot.OfValueType(handle);
        });
        import("System.ModuleHandle", "ModuleHandle_ResolveType", static (ctx, a) => {
            var scope = NativeQCallModule(ctx, a[0]);
            var context = GenericContext.Of(NativeTypeArguments(ctx, a[2], a[3].AsInt32), NativeTypeArguments(ctx, a[4], a[5].AsInt32));
            NativeStackHandle(ctx, a[6]).Write(DefaultIntrinsics.MakeRuntimeObject(ctx, scope.ResolveToken(new SigType(SigKind.TypeToken, Token: unchecked((uint)a[1].AsInt32)), context))); return null;
        });
        internalCall("System.RuntimeMethodHandle", "GetStubIfNeededInternal", static (ctx, a) => { _ = NativeMethod(ctx, a[0]); return a[0]; });
    }

    private static VmType[] NativeTypeArguments(IntrinsicContext ctx, StackSlot address, int count) {
        if (count < 0) throw new UnhandledGuestException("System.ArgumentException", "Invalid instantiation count.");
        using var allocation = ctx.Heap.ReserveArray(count);
        var types = new VmType[count];
        for (var i = 0; i < count; i++) types[i] = ctx.Shared.RuntimeMetadata.Resolve<VmType>(MemoryOps.LoadIndirect(ILOp.Ldind_I, NativeOffset(address, i * VmPrimitiveTypes.NativeIntSizeBytes)).Int64Value);
        return types;
    }
}
