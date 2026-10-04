using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>配列要素アクセス・生メモリ (cpblk/initblk/localloc ポインタ)・間接アクセス
/// (ldind/stind)・sizeof の純粋演算群 (状態を持たない)。
/// マネージポインタ (ByRef: スロット配列+インデックス) と unmanaged ポインタ
/// (VmNativePointer: 実バイト列+オフセット) の 2 種類のメモリを命令幅どおりに扱う。</summary>
internal static class MemoryOps {

    private sealed class LayoutMetadata {
        public required Dictionary<int, (int PackingSize, uint ClassSize)> Types { get; init; }
        public required Dictionary<int, uint> Fields { get; init; }

        public static LayoutMetadata Read(AssemblyImage image) {
            var types = new Dictionary<int, (int, uint)>();
            var fields = new Dictionary<int, uint>();
            var tables = image.Tables;
            for (var rid = 1; rid <= tables.GetRowCount(TableKind.ClassLayout); rid++) {
                var typeRid = tables.GetRowIndex(TableKind.ClassLayout, rid, 2);
                types[typeRid] = ((int)tables.GetCell(TableKind.ClassLayout, rid, 0),
                    tables.GetCell(TableKind.ClassLayout, rid, 1));
            }
            for (var rid = 1; rid <= tables.GetRowCount(TableKind.FieldLayout); rid++)
                fields[tables.GetRowIndex(TableKind.FieldLayout, rid, 1)] =
                    tables.GetCell(TableKind.FieldLayout, rid, 0);
            return new LayoutMetadata { Types = types, Fields = fields };
        }
    }

    private static readonly ConditionalWeakTable<AssemblyImage, LayoutMetadata> s_layoutMetadata = new();
    private sealed record ByteField(VmType Type, int Slot, int Offset, int Size, VmField? Definition = null);
    private sealed record ByteLayout(int Size, int Alignment, ByteField[] Fields);
    private static readonly ConditionalWeakTable<VmType, ByteLayout> s_byteLayouts = new();
    private sealed record FixedStorage(VmType? ElementType, int Length);
    private static readonly ConditionalWeakTable<VmClassType, FixedStorage> s_fixedStorage = new();

    internal static (VmType ElementType, int Length)? FixedBufferStorage(VmClassType type) {
        var storage = s_fixedStorage.GetValue(type, static definition => {
            var fields = definition.Fields.Where(field => !field.IsStatic && !field.IsLiteral).ToArray();
            if (fields is not [{ FieldType: { } element }]) return new(null, 0);
            var inlineLength = InlineArrayLength(definition);
            if (inlineLength > 0) return new(element, inlineLength);
            if (fields[0].Name != "FixedElementField" || !VmPrimitiveTypes.IsSlotPrimitive(element.FullName)) return new(null, 0);
            var metadata = s_layoutMetadata.GetValue(definition.Image, static image => LayoutMetadata.Read(image));
            var size = DeclaredClassSize(definition, metadata);
            var stride = SizeOfType(element);
            if (size < stride || size % stride != 0) return new(null, 0);
            var tables = definition.Image.Tables;
            for (var rid = 1; rid <= tables.GetRowCount(TableKind.CustomAttribute); rid++) {
                var parent = tables.DecodeCoded(TableKind.CustomAttribute, rid, 0, CodedIndexKind.HasCustomAttribute);
                if (parent.Table != TableKind.TypeDef || parent.Rid != definition.TypeDefRid) continue;
                var constructor = tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
                if (constructor.Table != TableKind.MemberRef) continue;
                var owner = tables.DecodeCoded(TableKind.MemberRef, constructor.Rid, 0, CodedIndexKind.MemberRefParent);
                if (owner.Table != TableKind.TypeRef) continue;
                var (ns, name, _) = definition.Image.GetTypeRefName(owner.Rid);
                if (ns == "System.Runtime.CompilerServices" && name == "UnsafeValueTypeAttribute")
                    return new(element, size / stride);
            }
            return new(null, 0);
        });
        return storage.ElementType is { } elementType ? (elementType, storage.Length) : null;
    }

    private static int InlineArrayLength(VmClassType definition) {
        var tables = definition.Image.Tables;
        for (var rid = 1; rid <= tables.GetRowCount(TableKind.CustomAttribute); rid++) {
            var parent = tables.DecodeCoded(TableKind.CustomAttribute, rid, 0, CodedIndexKind.HasCustomAttribute);
            if (parent.Table != TableKind.TypeDef || parent.Rid != definition.TypeDefRid) continue;
            var constructor = tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
            var owner = constructor.Table == TableKind.MemberRef
                ? tables.DecodeCoded(TableKind.MemberRef, constructor.Rid, 0, CodedIndexKind.MemberRefParent) : default;
            string? name = null;
            if (owner.Table == TableKind.TypeRef) { var type = definition.Image.GetTypeRefName(owner.Rid); name = type.Namespace + "." + type.Name; }
            else if (constructor.Table == TableKind.MethodDef) {
                for (var typeRid = tables.GetRowCount(TableKind.TypeDef); typeRid > 0; typeRid--)
                    if (tables.GetRowIndex(TableKind.TypeDef, typeRid, 5) <= constructor.Rid) {
                        name = definition.Loader!.GetTypeDef(typeRid).FullName; break;
                    }
            }
            if (name != "System.Runtime.CompilerServices.InlineArrayAttribute") continue;
            var blob = definition.Image.GetBlob(tables.GetRowIndex(TableKind.CustomAttribute, rid, 2));
            if (blob.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(blob) != 1) throw new BadImageFormatException("Invalid inline array metadata.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(blob[2..]);
            if (length <= 0) throw new BadImageFormatException("Invalid inline array length.");
            return length;
        }
        return 0;
    }

    private static bool IsBlittableStruct(VmType type) => type.IsValueType && !type.IsEnum &&
        !VmPrimitiveTypes.IsSlotPrimitive(type.FullName) && type is VmClassType or VmConstructedType;

    internal static int SizeOfRawType(VmType type) => IsBlittableStruct(type) ? GetByteLayout(type).Size : SizeOfType(type);

    internal static VmNativePointer RawFieldAddress(VmNativePointer pointer, VmField field, bool isReadOnly = false) {
        var layout = GetByteLayout(field.DeclaringType);
        var member = layout.Fields.First(member => ReferenceEquals(member.Definition, field));
        var result = new VmNativePointer { Memory = pointer.Memory,
            ByteOffset = checked(pointer.ByteOffset + member.Offset), IsReadOnly = pointer.IsReadOnly || isReadOnly };
        result.EnsureBounds(member.Size);
        return result;
    }

    private static ByteLayout GetByteLayout(VmType type, HashSet<VmType>? active = null) {
        if (s_byteLayouts.TryGetValue(type, out var cached)) return cached;
        active ??= [];
        if (active.Count >= 64 || !active.Add(type)) throw new BadImageFormatException("Recursive raw struct layout.");
        try {
            return s_byteLayouts.GetValue(type, _ => {
                var constructed = type as VmConstructedType;
                var definition = (constructed?.Definition ?? type) as VmClassType ?? throw new InvalidOperationException("Struct definition is unavailable.");
                var metadata = s_layoutMetadata.GetValue(definition.Image, static image => LayoutMetadata.Read(image));
                var packing = EffectivePackingSize(definition, metadata);
                var slots = new ObjectModel().GetLayout(definition);
                var fields = new List<ByteField>();
                if (FixedBufferStorage(definition) is { } fixedStorage) {
                    var element = fixedStorage.ElementType;
                    if (constructed is not null) element = GenericSubstitutor.Substitute(element, new GenericContext { ClassArgs = constructed.TypeArguments });
                    if (!element.IsValueType && element is not VmByRefType) throw new UnhandledGuestException("System.ArgumentException", "Raw structs cannot contain references.");
                    var nested = IsBlittableStruct(element) ? GetByteLayout(element, active) : null;
                    var stride = nested?.Size ?? SizeOfType(element);
                    var field = definition.Fields.Single(field => !field.IsStatic && !field.IsLiteral);
                    for (var i = 0; i < fixedStorage.Length; i++)
                        fields.Add(new ByteField(element, i, checked(i * stride), stride, field));
                    return new ByteLayout(checked(stride * fixedStorage.Length), Math.Min(nested?.Alignment ?? stride, packing), fields.ToArray());
                }
                var end = 0; var alignment = 1;
                foreach (var field in definition.Fields) {
                    if (field.IsStatic || field.IsLiteral) continue;
                    var fieldType = field.FieldType ?? throw new InvalidOperationException("Field type is unavailable.");
                    if (constructed is not null) fieldType = GenericSubstitutor.Substitute(fieldType, new GenericContext { ClassArgs = constructed.TypeArguments });
                    if (!fieldType.IsValueType && fieldType is not VmByRefType) throw new UnhandledGuestException("System.ArgumentException", "Raw structs cannot contain references.");
                    var nested = IsBlittableStruct(fieldType) ? GetByteLayout(fieldType, active) : null;
                    var size = nested?.Size ?? SizeOfType(fieldType);
                    var align = Math.Min(nested?.Alignment ?? size, packing);
                    var explicitOffset = GetFieldOffset(field, metadata);
                    if ((definition.Flags & 0x18) == 0x10 && explicitOffset is null) throw new BadImageFormatException("Explicit struct field has no offset.");
                    var offset = explicitOffset ?? checked((end + align - 1) / align * align);
                    fields.Add(new ByteField(fieldType, slots[field], offset, size, field));
                    end = checked(Math.Max(end, offset + size)); alignment = Math.Max(alignment, align);
                }
                end = Math.Max(end, DeclaredClassSize(definition, metadata));
                return new ByteLayout(Math.Max(1, checked((end + alignment - 1) / alignment * alignment)), alignment, fields.ToArray());
            });
        } finally { active.Remove(type); }
    }

    // ---- 配列要素 ----

    internal static StackSlot ReadPointerValue(VmNativePointer pointer, VmType type) {
        if (IsBlittableStruct(type)) {
            var layout = GetByteLayout(type);
            pointer.EnsureBounds(layout.Size);
            var fields = new StackSlot[layout.Fields.Length == 0 ? 0 : layout.Fields.Max(field => field.Slot) + 1];
            foreach (var field in layout.Fields) fields[field.Slot] = ReadPointerValue(new VmNativePointer { Memory = pointer.Memory, ByteOffset = pointer.ByteOffset + field.Offset, IsReadOnly = pointer.IsReadOnly }, field.Type);
            return StackSlot.OfValueType(new VmStructValue(type, fields, (type as VmConstructedType)?.TypeArguments));
        }
        if (type is VmByRefType) return LoadIndirect(ILOp.Ldind_I, StackSlot.OfObject(pointer));
        var size = SizeOfRawType(type);
        pointer.EnsureBounds(size);
        return ValueFromBytes(pointer.Bytes.AsSpan(pointer.ByteOffset, size), type, size);
    }

    internal static void WritePointerValue(VmNativePointer pointer, VmType type, StackSlot value) {
        pointer.EnsureWritable();
        if (IsBlittableStruct(type)) {
            var layout = GetByteLayout(type);
            pointer.EnsureBounds(layout.Size);
            var fields = value.ObjectValue switch { VmStructValue structure => structure.Fields, VmBoxedValue boxed => boxed.Fields, _ => throw new UnhandledGuestException("System.InvalidProgramException", "Struct value expected.") };
            foreach (var field in layout.Fields) WritePointerValue(new VmNativePointer { Memory = pointer.Memory, ByteOffset = pointer.ByteOffset + field.Offset }, field.Type, fields[field.Slot]);
            return;
        }
        if (type is VmByRefType) { StoreIndirect(ILOp.Stind_I, StackSlot.OfObject(pointer), value); return; }
        var size = SizeOfRawType(type);
        pointer.EnsureBounds(size);
        pointer.Memory.ClearReferences(pointer.ByteOffset, size);
        BytesOfValue(value, type, size, pointer.Bytes.AsSpan(pointer.ByteOffset, size));
    }

    /// <summary>
    /// 配列命令がスタックへ返す/配列へ格納する表現。小整数は IL の signed/unsigned
    /// 拡張幅を保持する。storage 自体は VM の正規化スロットだが、opcode ごとの拡張を
    /// ここで明示し、Interpreter/JIT の境界で同じ結果になるようにする。
    /// </summary>
    public enum ArrayElementKind {
        Int32,
        SignedByte,
        UnsignedByte,
        SignedShort,
        UnsignedShort,
        UnsignedInt32,
        Int64,
        NativeInt,
        Float,
        Object,
    }

    public static ArrayElementKind ElementKindFromType(VmType type) {
        if (type.IsValueType) {
            // プリミティブ以外の値型 (構造体/構築ジェネリック構造体) は VmStructValue スロットのまま扱う
            if (type is not VmIntrinsicType)
                return ArrayElementKind.Object;
            return type.FullName switch {
                "System.SByte" => ArrayElementKind.SignedByte,
                "System.Byte" => ArrayElementKind.UnsignedByte,
                "System.Char" or "System.UInt16" => ArrayElementKind.UnsignedShort,
                "System.Int16" => ArrayElementKind.SignedShort,
                "System.UInt32" => ArrayElementKind.UnsignedInt32,
                "System.Int64" or "System.UInt64" => ArrayElementKind.Int64,
                "System.IntPtr" or "System.UIntPtr" => ArrayElementKind.NativeInt,
                "System.Single" or "System.Double" => ArrayElementKind.Float,
                _ => ArrayElementKind.Int32, // プリミティブ小整数
            };
        }
        return ArrayElementKind.Object;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VmArray GetArray(in StackSlot slot) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmArray array)
            return array;
        if (slot.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        throw new UnhandledGuestException("System.InvalidProgramException",
            $"配列でないオブジェクトに配列命令を適用しました: {SlotOps.Describe(slot)}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CheckArrayBounds(VmArray array, int index) {
        if ((uint)index >= (uint)array.Length)
            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                $"インデックス {index} は長さ {array.Length} の配列の範囲外です。");
    }

    public static StackSlot ArrayLoad(InterpreterFrame frame, ArrayElementKind kind) {
        var index = frame.Stack.Pop().AsInt32;
        var array = GetArray(frame.Stack.Pop());
        CheckArrayBounds(array, index);
        StackSlot slot;
        lock (array.Elements)
            slot = array.Elements[index];
        return kind switch {
            ArrayElementKind.Int32 => StackSlot.OfInt32((int)slot.Int64Value),
            ArrayElementKind.SignedByte => StackSlot.OfInt32((sbyte)slot.Int64Value),
            ArrayElementKind.UnsignedByte => StackSlot.OfInt32((byte)slot.Int64Value),
            ArrayElementKind.SignedShort => StackSlot.OfInt32((short)slot.Int64Value),
            ArrayElementKind.UnsignedShort => StackSlot.OfInt32((ushort)slot.Int64Value),
            ArrayElementKind.UnsignedInt32 => StackSlot.OfInt32(unchecked((int)(uint)slot.Int64Value)),
            ArrayElementKind.Int64 => StackSlot.OfInt64(slot.Int64Value),
            ArrayElementKind.NativeInt => StackSlot.OfNativeInt(slot.Int64Value),
            ArrayElementKind.Float => StackSlot.OfFloat(array.ArrayType.ElementType.FullName == "System.Single"
                ? (float)slot.DoubleValue : slot.DoubleValue),
            // 構造体要素は読み出し時にコピーする (値型コピー意味論)
            _ => slot.ObjectValue is VmStructValue sv ? StackSlot.OfValueType(sv.Clone()) : slot,
        };
    }

    public static void ArrayStore(InterpreterFrame frame, ArrayElementKind kind, VmType? stringType = null) {
        var value = frame.Stack.Pop();
        var index = frame.Stack.Pop().AsInt32;
        var array = GetArray(frame.Stack.Pop());
        CheckArrayBounds(array, index);

        // 共変配列の書込検査 (stelem.ref)
        if (kind == ArrayElementKind.Object && value.ObjectValue is not null &&
            !TypeChecks.IsAssignableToType(value.ObjectValue, array.ArrayType.ElementType, stringType))
            throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                $"{SlotOps.Describe(value)} を {array.ArrayType.ElementType.FullName}[] に格納できません。");

        lock (array.Elements)
            array.Elements[index] = kind switch {
                ArrayElementKind.Int32 or ArrayElementKind.UnsignedInt32 =>
                    StackSlot.OfInt32(unchecked((int)(uint)value.Int64Value)),
                ArrayElementKind.SignedByte or ArrayElementKind.UnsignedByte =>
                    StackSlot.OfInt32(unchecked((byte)value.Int64Value)),
                ArrayElementKind.SignedShort or ArrayElementKind.UnsignedShort =>
                    StackSlot.OfInt32(unchecked((ushort)value.Int64Value)),
                ArrayElementKind.Int64 => StackSlot.OfInt64(value.Int64Value),
                ArrayElementKind.NativeInt => StackSlot.OfNativeInt(value.Int64Value),
                ArrayElementKind.Float => StackSlot.OfFloat(array.ArrayType.ElementType.FullName == "System.Single"
                    ? (float)value.DoubleValue : value.DoubleValue),
                _ => value.ObjectValue is VmStructValue structValue
                    ? StackSlot.OfValueType(structValue.Clone())
                    : value,
            };
    }

    // ---- 生メモリ系 (cpblk / initblk) ----

    /// <summary>cpblk/initblk の被演算子が指すメモリ位置。VM 内メモリの実体はスロット配列なので、
    /// マネージポインタ (ByRef) が指すスロット配列 + インデックスのみを受け付ける。</summary>
    private static VmByRef MemoryLocation(in StackSlot slot) =>
        slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
            ? byRef
            : throw new UnhandledGuestException("System.InvalidProgramException",
                $"cpblk/initblk はマネージポインタ (&) を要求します: {SlotOps.Describe(slot)}");

    /// <summary>cpblk: unmanaged ポインタ間だけをバイト単位で扱う。
    /// スロット配列は実バイト配置を表していないため、ByRef 間の近似コピーは拒否する。</summary>
    public static void CopyMemoryBlock(in StackSlot dstSlot, in StackSlot srcSlot, int size) {
        if (size < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        if (dstSlot.ObjectValue is VmNativePointer dst && srcSlot.ObjectValue is VmNativePointer src) {
            dst.EnsureWritable();
            if (dst.ByteOffset < 0 || src.ByteOffset < 0 ||
                (long)dst.ByteOffset + size > dst.Bytes.Length || (long)src.ByteOffset + size > src.Bytes.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException",
                    $"cpblk が仮想メモリブロックの範囲外です (size={size}, dst offset={dst.ByteOffset}/{dst.Bytes.Length}, src offset={src.ByteOffset}/{src.Bytes.Length})。");
            src.Bytes.AsSpan(src.ByteOffset, size).CopyTo(dst.Bytes.AsSpan(dst.ByteOffset, size));
            var references = src.Memory.References.Where(entry => entry.Key >= src.ByteOffset && (long)entry.Key + VmPrimitiveTypes.NativeIntSizeBytes <= (long)src.ByteOffset + size).ToArray();
            dst.Memory.ClearReferences(dst.ByteOffset, size);
            foreach (var reference in references) dst.Memory.References[dst.ByteOffset + reference.Key - src.ByteOffset] = reference.Value;
            return;
        }
        // StackSlot の 1 要素は int/参照/値型のいずれにもなり得るため、8 バイト丸めで
        // コピーすると隣接要素や参照を破壊する。正確な managed byte storage が導入される
        // までは、曖昧な入力を fail-closed にする。
        if (dstSlot.ObjectValue is VmByRef || srcSlot.ObjectValue is VmByRef)
            throw new UnhandledGuestException("System.InvalidProgramException",
                "cpblk のマネージ参照間コピーは VM のスロット表現では安全に表現できません。");
        _ = MemoryLocation(dstSlot);
        _ = MemoryLocation(srcSlot);
    }

    /// <summary>initblk: unmanaged ポインタ先だけを実バイト充填する。</summary>
    public static void InitMemoryBlock(in StackSlot dstSlot, in StackSlot value, int size) {
        if (size < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        if (dstSlot.ObjectValue is VmNativePointer dst) {
            dst.EnsureWritable();
            // initblk は value の下位 8 bit だけを使用する (ECMA-335)。
            // ホスト側 checked cast で例外を漏らさない。
            var fill = unchecked((byte)value.Int64Value);
            if (dst.ByteOffset < 0 || (long)dst.ByteOffset + size > dst.Bytes.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException",
                    $"initblk が仮想メモリブロックの範囲外です (size={size}, offset={dst.ByteOffset}/{dst.Bytes.Length})。");
            dst.Bytes.AsSpan(dst.ByteOffset, size).Fill(fill);
            dst.Memory.ClearReferences(dst.ByteOffset, size);
            return;
        }
        if (dstSlot.ObjectValue is VmByRef)
            throw new UnhandledGuestException("System.InvalidProgramException",
                "initblk のマネージ参照先は VM のスロット表現では安全に表現できません。");
        _ = MemoryLocation(dstSlot);
    }

    /// <summary>native-size の byte count をホスト配列長へ変換する。
    /// 符号付き縮小や int wraparound を許さず、確保/コピー前に拒否する。</summary>
    public static int ByteCount(in StackSlot value, string operation) {
        if (value.Kind is not (StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt))
            throw new UnhandledGuestException("System.InvalidProgramException",
                $"{operation} のサイズが整数型ではありません。");
        var count = value.Int64Value;
        if (count < 0 || count > int.MaxValue)
            throw new UnhandledGuestException("System.OverflowException",
                $"{operation} のサイズが VM の上限を超えています。");
        return (int)count;
    }

    /// <summary>ポインタ演算 (add/sub)。C# の p[i] は「要素バイト数 × i + ポインタ」の mul + add に
    /// コンパイルされるため、オフセットは既にバイト単位 (スケール不要)。ptr - ptr はバイト距離。</summary>
    public static StackSlot? TryPointerArithmetic(ILOp op, in StackSlot left, in StackSlot right) {
        if (op is not (ILOp.Add or ILOp.Sub))
            return null;
        var managed = left.ObjectValue as VmByRef;
        var displacement = right;
        if (managed is null && op == ILOp.Add && right.ObjectValue is VmByRef rightReference) {
            managed = rightReference; displacement = left;
        }
        if (managed is not null) {
            if (!managed.IsNullOrOnePast && managed.Read().ObjectValue is VmStructValue inline &&
                inline.StructType is VmClassType definition && FixedBufferStorage(definition) is { } storage)
                managed = new VmByRef(inline.Fields, 0, managed.IsReadOnly, managed.Owner,
                    GenericSubstitutor.Substitute(storage.ElementType, GenericContext.Of(inline.TypeArguments, null)));
            var elementType = managed.Owner is VmArray array ? array.ArrayType.ElementType : managed.ElementType;
            if (elementType is null)
                throw new UnhandledGuestException("System.NotSupportedException", "Pointer arithmetic requires typed array storage.");
            var stride = SizeOfRawType(elementType);
            if (op == ILOp.Sub && displacement.ObjectValue is VmByRef second && ReferenceEquals(managed.Container, second.Container))
                return StackSlot.OfNativeInt(checked((long)(managed.Index - second.Index) * stride));
            if (displacement.Kind is not (StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt) || displacement.Int64Value % stride != 0)
                throw new UnhandledGuestException("System.NotSupportedException", "Pointer displacement must align with array element storage.");
            var index = checked((long)managed.Index + (op == ILOp.Add ? displacement.Int64Value : -displacement.Int64Value) / stride);
            if (index < 0 || index > managed.Container.Length) throw new UnhandledGuestException("System.IndexOutOfRangeException", "Pointer displacement exceeds array storage.");
            return StackSlot.OfByRef(new VmByRef(managed.Container, (int)index, managed.IsReadOnly, managed.Owner, managed.ElementType));
        }
        VmNativePointer? pointer;
        StackSlot other;
        if (left.ObjectValue is VmNativePointer lp) {
            pointer = lp;
            other = right;
        } else if (op == ILOp.Add && right.ObjectValue is VmNativePointer rp) {
            pointer = rp;
            other = left;
        } else {
            return null;
        }
        if (other.Kind is not (StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt)) {
            if (op == ILOp.Sub && other.ObjectValue is VmNativePointer rhs)
                return StackSlot.OfNativeInt((long)pointer.ByteOffset - rhs.ByteOffset); // ポインタ差 = バイト距離
            return null; // 不正な組合せは通常の算術カーネルにフォールバック (そこで fail-closed)
        }
        long offset;
        try {
            offset = op == ILOp.Add
                ? checked((long)pointer.ByteOffset + other.Int64Value)
                : checked((long)pointer.ByteOffset - other.Int64Value);
        } catch (OverflowException) {
            throw new UnhandledGuestException("System.OverflowException", null);
        }
        if (offset < 0 || offset > int.MaxValue)
            throw new UnhandledGuestException("System.OverflowException", null);
        return StackSlot.OfObject(new VmNativePointer {
            Memory = pointer.Memory,
            ByteOffset = (int)offset,
            IsReadOnly = pointer.IsReadOnly,
        });
    }

    // ---- 間接アクセス (ldind / stind) ----

    /// <summary>ldind: アドレスの参照先から読み出す。unmanaged ポインタはバイト列からの
    /// リトルエンディアン読み出し (命令幅どおり)、マネージポインタ (ByRef) はスロット読み出し。</summary>
    public static StackSlot LoadIndirect(ILOp op, in StackSlot address) {
        if (address.ObjectValue is VmNativePointer ptr) {
            if (op is ILOp.Ldind_I or ILOp.Ldind_Ref && ptr.Memory.References.TryGetValue(ptr.ByteOffset, out var reference)) {
                ptr.EnsureBounds(VmPrimitiveTypes.NativeIntSizeBytes);
                return reference;
            }
            return op switch {
                ILOp.Ldind_I1 => ReadInt8(ptr),
                ILOp.Ldind_U1 => ReadUInt8(ptr),
                ILOp.Ldind_I2 => ReadInt16(ptr),
                ILOp.Ldind_U2 => ReadUInt16(ptr),
                ILOp.Ldind_I4 or ILOp.Ldind_U4 => ReadInt32(ptr),
                ILOp.Ldind_I8 or ILOp.Ldind_I => ReadInt64(ptr),
                ILOp.Ldind_R4 => ReadSingle(ptr),
                ILOp.Ldind_R8 => ReadDouble(ptr),
                _ => throw new UnhandledGuestException("System.InvalidProgramException",
                    "unmanaged ポインタからの参照読み出し (ldind.ref) は対応していません。"),
            };
        }
        if (address.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // ボックス実体への直接読み出しの緩和: 値型のインターフェース実装 (EII) は
        // box レシーバを this に受け、本体冒頭の ldind で m_value を読む (ldarg.0; ldind.i4)。
        // CLR は callvirt 時に unbox 済み this ポインタを渡すが、VM は box をそのまま渡すため
        // ここで unbox + 読み出しに落とす (IConvertible EII のプリミティブ / enum 基底型面)
        if (address.ObjectValue is VmBoxedValue boxed) {
            lock (boxed.Fields) {
                var slot = boxed.Fields[0];
                return op switch {
                    ILOp.Ldind_I when slot.ObjectValue is VmByRef or VmNativePointer or VmMethodPointer or VmRuntimeCallback => slot,
                    ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(slot.Int64Value),
                    ILOp.Ldind_R4 => StackSlot.OfFloat((float)slot.DoubleValue),
                    ILOp.Ldind_R8 => StackSlot.OfFloat(slot.DoubleValue),
                    ILOp.Ldind_Ref => slot,
                    _ => StackSlot.OfInt32((int)slot.Int64Value), // I1〜U4 は i4 正規化スロット
                };
            }
        }
        if (address.ObjectValue is VmByRef byRef) {
            var slot = byRef.Read();
            return op switch {
                ILOp.Ldind_I when slot.ObjectValue is VmByRef or VmNativePointer or VmMethodPointer or VmRuntimeCallback => slot,
                ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(slot.Int64Value),
                ILOp.Ldind_R4 => StackSlot.OfFloat((float)slot.DoubleValue),
                ILOp.Ldind_R8 => StackSlot.OfFloat(slot.DoubleValue),
                ILOp.Ldind_Ref => slot,
                _ => StackSlot.OfInt32((int)slot.Int64Value), // I1〜U4 は i4 正規化スロット
            };
        }
        throw new UnhandledGuestException("System.InvalidProgramException",
            $"ldind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
    }

    /// <summary>stind: アドレスの参照先へ書き込む (LoadIndirect の書き込み版)。</summary>
    public static void StoreIndirect(ILOp op, in StackSlot address, in StackSlot value) {
        if (address.ObjectValue is VmNativePointer ptr) {
            ptr.EnsureWritable();
            if (op is ILOp.Stind_I or ILOp.Stind_Ref && value.ObjectValue is not null) {
                ptr.EnsureBounds(VmPrimitiveTypes.NativeIntSizeBytes);
                ptr.Memory.ClearReferences(ptr.ByteOffset, VmPrimitiveTypes.NativeIntSizeBytes);
                ptr.Bytes.AsSpan(ptr.ByteOffset, VmPrimitiveTypes.NativeIntSizeBytes).Clear();
                ptr.Memory.References[ptr.ByteOffset] = value;
                return;
            }
            switch (op) {
                case ILOp.Stind_I1: ptr.EnsureBounds(1); ptr.WriteInt8((int)value.Int64Value); return;
                case ILOp.Stind_I2: ptr.EnsureBounds(2); ptr.WriteInt16((int)value.Int64Value); return;
                case ILOp.Stind_I4: ptr.EnsureBounds(4); ptr.WriteInt32((int)value.Int64Value); return;
                case ILOp.Stind_I8: ptr.EnsureBounds(8); ptr.WriteInt64(value.Int64Value); return;
                case ILOp.Stind_R4: ptr.EnsureBounds(4); ptr.WriteSingle((float)value.DoubleValue); return;
                case ILOp.Stind_R8: ptr.EnsureBounds(8); ptr.WriteDouble(value.DoubleValue); return;
                case ILOp.Stind_I: ptr.EnsureBounds(VmPrimitiveTypes.NativeIntSizeBytes); ptr.WriteInt64(value.Int64Value); return;
                case ILOp.Stind_Ref when value.ObjectValue is null: ptr.EnsureBounds(VmPrimitiveTypes.NativeIntSizeBytes); ptr.WriteInt64(0); return;
                default:
                    throw new UnhandledGuestException("System.InvalidProgramException",
                        "unmanaged ポインタへの参照書き込み (stind.ref) は対応していません。");
            }
        }
        if (address.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // ボックス実体への書き込みの緩和 (LoadIndirect と対。box が指す先 = Fields[0] を更新する)
        if (address.ObjectValue is VmBoxedValue boxed) {
            lock (boxed.Fields)
                boxed.Fields[0] = op switch {
                    ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                    ILOp.Stind_R4 => StackSlot.OfFloat((float)value.DoubleValue),
                    ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                    ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                    _ => StackSlot.OfInt32((int)value.Int64Value),
                };
            return;
        }
        if (address.ObjectValue is VmByRef byRef) {
            byRef.Write(op switch {
                ILOp.Stind_I => value,
                ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                ILOp.Stind_R4 => StackSlot.OfFloat((float)value.DoubleValue),
                ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                _ => StackSlot.OfInt32((int)value.Int64Value),
            });
            return;
        }
        throw new UnhandledGuestException("System.InvalidProgramException",
            $"stind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
    }

    private static StackSlot ReadInt8(VmNativePointer pointer) { pointer.EnsureBounds(1); return StackSlot.OfInt32(pointer.ReadInt8()); }
    private static StackSlot ReadUInt8(VmNativePointer pointer) { pointer.EnsureBounds(1); return StackSlot.OfInt32(pointer.ReadUInt8()); }
    private static StackSlot ReadInt16(VmNativePointer pointer) { pointer.EnsureBounds(2); return StackSlot.OfInt32(pointer.ReadInt16()); }
    private static StackSlot ReadUInt16(VmNativePointer pointer) { pointer.EnsureBounds(2); return StackSlot.OfInt32(pointer.ReadUInt16()); }
    private static StackSlot ReadInt32(VmNativePointer pointer) { pointer.EnsureBounds(4); return StackSlot.OfInt32(pointer.ReadInt32()); }
    private static StackSlot ReadInt64(VmNativePointer pointer) { pointer.EnsureBounds(8); return StackSlot.OfInt64(pointer.ReadInt64()); }
    private static StackSlot ReadSingle(VmNativePointer pointer) { pointer.EnsureBounds(4); return StackSlot.OfFloat(pointer.ReadSingle()); }
    private static StackSlot ReadDouble(VmNativePointer pointer) { pointer.EnsureBounds(8); return StackSlot.OfFloat(pointer.ReadDouble()); }

    // ---- sizeof ----

    /// <summary>sizeof の VM 値。プリミティブは CLR と同じ実際のサイズ。ゲスト値型は
    /// ClassLayout / FieldLayout を適用し、順次配置のフィールド整列は VM の型レイアウト規則で近似する。</summary>
    public static int SizeOfType(VmType type) => SizeOfTypeCore(type, []);

    private static int SizeOfTypeCore(VmType type, HashSet<VmType> visiting) {
        if (!type.IsValueType || type is VmByRefType)
            return VmPrimitiveTypes.NativeIntSizeBytes;
        if (type is VmIntrinsicType intrinsic) {
            if (!intrinsic.IsValue)
                throw new InvalidOperationException($"sizeof は値型にのみ適用できます: {type.FullName}");
            return intrinsic.FullName switch {
                "System.SByte" or "System.Byte" or "System.Boolean" => 1,
                "System.Char" or "System.Int16" or "System.UInt16" => 2,
                "System.Int32" or "System.UInt32" or "System.Single" => 4,
                "System.Int64" or "System.UInt64" or "System.Double"
                    or "System.IntPtr" or "System.UIntPtr" => VmPrimitiveTypes.NativeIntSizeBytes,
                // 列挙ファサード等 (StringSplitOptions 等) は VM 内で i4 スロットに正規化される
                _ => 4,
            };
        }
        var definition = type is VmConstructedType constructed ? constructed.Definition : type;
        if (definition is VmClassType cls && cls.IsValueType) {
            // CoreLib 実型化されたプリミティブ (m_value 単一フィールド) は CLR と同じ
            // 実際のサイズ。フィールドを辿ると m_value → 自身の相互参照になるため先に潰す
            // (sizeof: CoreLib IL の BitConverter 系 / Span 計算から参照される)
            var primitiveSize = cls.FullName switch {
                "System.SByte" or "System.Byte" or "System.Boolean" => 1,
                "System.Char" or "System.Int16" or "System.UInt16" => 2,
                "System.Int32" or "System.UInt32" or "System.Single" => 4,
                "System.Int64" or "System.UInt64" or "System.Double"
                    or "System.IntPtr" or "System.UIntPtr" => VmPrimitiveTypes.NativeIntSizeBytes,
                _ => 0,
            };
            if (primitiveSize > 0)
                return primitiveSize;
            if (!visiting.Add(cls))
                throw new BadImageFormatException($"sizeof: 相互参照する値型レイアウト {cls.FullName} は不正です。");
            try {
                var layout = s_layoutMetadata.GetValue(cls.Image, static image => LayoutMetadata.Read(image));
                var packingSize = EffectivePackingSize(cls, layout);
                var size = 0;
                var maxAlign = 1;
                foreach (var field in cls.Fields) {
                    if ((field.Flags & 0x0010) != 0)
                        continue; // FieldAttributes.Static
                    var fieldType = field.FieldType;
                    if (fieldType is not null && type is VmConstructedType generic)
                        fieldType = GenericSubstitutor.Substitute(fieldType, new GenericContext { ClassArgs = generic.TypeArguments });
                    var fieldSize = fieldType is null ? VmPrimitiveTypes.NativeIntSizeBytes : SizeOfTypeCore(fieldType, visiting);
                    var fieldLayout = GetFieldOffset(field, layout);
                    if (fieldLayout is { } explicitOffset) {
                        size = LayoutSize(Math.Max((long)size, (long)explicitOffset + fieldSize), cls);
                        maxAlign = Math.Max(maxAlign, Math.Min(fieldSize, packingSize));
                    } else {
                        if ((cls.Flags & 0x18) == 0x10)
                            throw new BadImageFormatException(
                                $"sizeof: 明示レイアウト型 {cls.FullName} のフィールド {field.Name} に FieldLayout がありません。");
                        var align = Math.Min(fieldSize, packingSize);
                        size = LayoutSize(((long)size + align - 1) / align * align + fieldSize, cls);
                        maxAlign = Math.Max(maxAlign, align);
                    }
                }
                var declaredSize = DeclaredClassSize(cls, layout);
                size = Math.Max(size, declaredSize);
                size = LayoutSize(((long)size + maxAlign - 1) / maxAlign * maxAlign, cls);
                return Math.Max(size, 1);
            } finally {
                visiting.Remove(cls);
            }
        }
        throw new InvalidOperationException($"sizeof は値型にのみ適用できます: {type.FullName}");
    }

    private static int EffectivePackingSize(VmClassType type, LayoutMetadata layout) {
        if (!layout.Types.TryGetValue(type.TypeDefRid, out var typeLayout) || typeLayout.PackingSize == 0)
            return 8;
        if (typeLayout.PackingSize is 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128)
            return typeLayout.PackingSize;
        throw new BadImageFormatException(
            $"sizeof: {type.FullName} の PackingSize {typeLayout.PackingSize} は不正です。");
    }

    private static int DeclaredClassSize(VmClassType type, LayoutMetadata layout) {
        if (!layout.Types.TryGetValue(type.TypeDefRid, out var typeLayout))
            return 0;
        if (typeLayout.ClassSize > int.MaxValue)
            throw new BadImageFormatException($"sizeof: {type.FullName} の ClassSize が大きすぎます。");
        return (int)typeLayout.ClassSize;
    }

    private static int? GetFieldOffset(VmField field, LayoutMetadata layout) {
        if (field.DeclaringType is not VmClassType || !layout.Fields.TryGetValue(field.FieldRid, out var rawOffset))
            return null;
        if (rawOffset > int.MaxValue)
            throw new BadImageFormatException(
                $"sizeof: {field.DeclaringType.FullName}::{field.Name} の FieldLayout offset が大きすぎます。");
        return (int)rawOffset;
    }

    private static int LayoutSize(long size, VmClassType type) => size is >= 0 and <= int.MaxValue
        ? (int)size
        : throw new BadImageFormatException($"sizeof: {type.FullName} のレイアウトサイズが大きすぎます。");

    /// <summary>IL 値列 (LE バイト) → VM スロットへ展開する補助面。
    /// typeof から構成を取り出し, VM 表現を使って "ldobj 型", "cpobj 型" も走る</summary>
    public static StackSlot ValueFromBytes(ReadOnlySpan<byte> bytes, VmType type, int size)
    {
        if (IsBlittableStruct(type)) {
            var layout = GetByteLayout(type);
            var fields = new StackSlot[layout.Fields.Length == 0 ? 0 : layout.Fields.Max(f => f.Slot) + 1];
            foreach (var field in layout.Fields) fields[field.Slot] = ValueFromBytes(bytes.Slice(field.Offset, field.Size), field.Type, field.Size);
            return StackSlot.OfValueType(new VmStructValue(type, fields, (type as VmConstructedType)?.TypeArguments));
        }
        // プリミティブ値は VM の統合スロットで表す (int/char/bool 等は i4 スロット)
        var typeName = PrimitiveStorageTypeName(type);
        if (bytes.Length == 1)
        {
            return typeName switch
            {
                "System.Byte" => StackSlot.OfInt32(bytes[0]),
                "System.SByte" => StackSlot.OfInt32((sbyte)bytes[0]),
                "System.Boolean" => StackSlot.OfInt32(bytes[0] != 0 ? 1 : 0),
                _ => StackSlot.OfInt32(bytes[0]),
            };
        }
        if (bytes.Length == 2) {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            return StackSlot.OfInt32(typeName == "System.Int16" ? (short)value : value);
        }

        if (bytes.Length == 4)
        {
            var v32 = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (typeName == "System.Single")
                return StackSlot.OfFloat(BitConverter.Int32BitsToSingle(v32));
            return StackSlot.OfInt32(v32);
        }
        if (bytes.Length == 8)
        {
            if (typeName == "System.Double") return StackSlot.OfFloat(BinaryPrimitives.ReadDoubleLittleEndian(bytes));
            return StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        }
        throw new InvalidOperationException($"unmanaged ポインタから {typeName} (要素幅 {size} バイト) の読み取りに対応していません。");
    }

    private static string PrimitiveStorageTypeName(VmType type) {
        if (type.IsEnum && type is VmClassType enumType &&
            enumType.Fields.FirstOrDefault(static field => field.Name == "value__")?.FieldType is { } underlyingType)
            return underlyingType.FullName;
        return type.FullName;
    }

    /// <summary>VM スロット値を LE バイト列へ展開する (stobj/unmanaged ポインタ書き込み用)。</summary>
    public static void BytesOfValue(in StackSlot value, VmType type, int size, Span<byte> destination)
    {
        if (IsBlittableStruct(type)) {
            var layout = GetByteLayout(type);
            var slot = value.ObjectValue is VmByRef byRef ? byRef.Read() : value;
            var fields = slot.ObjectValue switch { VmStructValue sv => sv.Fields, VmBoxedValue box => box.Fields, _ => throw new InvalidOperationException("Expected a struct value.") };
            destination[..layout.Size].Clear();
            foreach (var field in layout.Fields) BytesOfValue(fields[field.Slot], field.Type, field.Size, destination.Slice(field.Offset, field.Size));
            return;
        }
        if (size is not (1 or 2 or 4 or 8))
            throw new InvalidOperationException($"unmanaged ポインタへの {type.FullName} (要素幅 {size} バイト) の書き込みに対応していません。");
        if ((uint)size > (uint)destination.Length)
            throw new ArgumentException("出力バッファが値型のサイズより小さくなっています。", nameof(destination));
        var bytes = destination[..size];
        var typeName = type.FullName;
        if (size == 1)
        {
            bytes[0] = (byte)value.AsInt32;
            return;
        }
        if (size == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value.AsInt32);
            return;
        }
        if (size == 4)
        {
            var v = typeName == "System.Single"
                ? BitConverter.SingleToInt32Bits((float)value.DoubleValue)
                : value.AsInt32;
            BinaryPrimitives.WriteInt32LittleEndian(bytes, v);
            return;
        }
        if (size == 8)
        {
            if (typeName == "System.Double")
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, value.DoubleValue);
                return;
            }
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Int64Value);
            return;
        }
        throw new InvalidOperationException($"unmanaged ポインタへの {typeName} (要素幅 {size} バイト) の書き込みに対応していません。");
    }
}

