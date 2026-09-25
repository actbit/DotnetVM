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

    // ---- 配列要素 ----

    public enum ArrayElementKind { Int32, Int64, Float, Object }

    public static ArrayElementKind ElementKindFromType(VmType type) {
        if (type.IsValueType) {
            // プリミティブ以外の値型 (構造体/構築ジェネリック構造体) は VmStructValue スロットのまま扱う
            if (type is not VmIntrinsicType)
                return ArrayElementKind.Object;
            return type.FullName switch {
                "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr" => ArrayElementKind.Int64,
                "System.Single" or "System.Double" => ArrayElementKind.Float,
                _ => ArrayElementKind.Int32, // プリミティブ小整数
            };
        }
        return ArrayElementKind.Object;
    }

    public static VmArray GetArray(in StackSlot slot) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmArray array)
            return array;
        if (slot.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        throw new InvalidOperationException($"配列でないオブジェクトに配列命令を適用しました: {SlotOps.Describe(slot)}");
    }

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
            ArrayElementKind.Int64 => StackSlot.OfInt64(slot.Int64Value),
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
                ArrayElementKind.Int32 => StackSlot.OfInt32((int)value.Int64Value),
                ArrayElementKind.Int64 => StackSlot.OfInt64(value.Int64Value),
                ArrayElementKind.Float => StackSlot.OfFloat(array.ArrayType.ElementType.FullName == "System.Single"
                    ? (float)value.DoubleValue : value.DoubleValue),
                _ => value,
            };
    }

    // ---- 生メモリ系 (cpblk / initblk) ----

    /// <summary>cpblk/initblk の被演算子が指すメモリ位置。VM 内メモリの実体はスロット配列なので、
    /// マネージポインタ (ByRef) が指すスロット配列 + インデックスのみを受け付ける。</summary>
    private static VmByRef MemoryLocation(in StackSlot slot) =>
        slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
            ? byRef
            : throw new InvalidOperationException($"cpblk/initblk はマネージポインタ (&) を要求します: {SlotOps.Describe(slot)}");

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
            Array.Copy(src.Bytes, src.ByteOffset, dst.Bytes, dst.ByteOffset, size);
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
            Array.Fill(dst.Bytes, fill, dst.ByteOffset, size);
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
        return StackSlot.OfObject(new VmNativePointer { Memory = pointer.Memory, ByteOffset = (int)offset });
    }

    // ---- 間接アクセス (ldind / stind) ----

    /// <summary>ldind: アドレスの参照先から読み出す。unmanaged ポインタはバイト列からの
    /// リトルエンディアン読み出し (命令幅どおり)、マネージポインタ (ByRef) はスロット読み出し。</summary>
    public static StackSlot LoadIndirect(ILOp op, in StackSlot address) {
        if (address.ObjectValue is VmNativePointer ptr) {
            return op switch {
                ILOp.Ldind_I1 => ReadInt8(ptr),
                ILOp.Ldind_U1 => ReadUInt8(ptr),
                ILOp.Ldind_I2 => ReadInt16(ptr),
                ILOp.Ldind_U2 => ReadUInt16(ptr),
                ILOp.Ldind_I4 or ILOp.Ldind_U4 => ReadInt32(ptr),
                ILOp.Ldind_I8 or ILOp.Ldind_I => ReadInt64(ptr),
                ILOp.Ldind_R4 => ReadSingle(ptr),
                ILOp.Ldind_R8 => ReadDouble(ptr),
                _ => throw new NotSupportedException(
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
                ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(slot.Int64Value),
                ILOp.Ldind_R4 => StackSlot.OfFloat((float)slot.DoubleValue),
                ILOp.Ldind_R8 => StackSlot.OfFloat(slot.DoubleValue),
                ILOp.Ldind_Ref => slot,
                _ => StackSlot.OfInt32((int)slot.Int64Value), // I1〜U4 は i4 正規化スロット
            };
        }
        throw new InvalidOperationException($"ldind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
    }

    /// <summary>stind: アドレスの参照先へ書き込む (LoadIndirect の書き込み版)。</summary>
    public static void StoreIndirect(ILOp op, in StackSlot address, in StackSlot value) {
        if (address.ObjectValue is VmNativePointer ptr) {
            switch (op) {
                case ILOp.Stind_I1: ptr.EnsureBounds(1); ptr.WriteInt8((int)value.Int64Value); return;
                case ILOp.Stind_I2: ptr.EnsureBounds(2); ptr.WriteInt16((int)value.Int64Value); return;
                case ILOp.Stind_I4: ptr.EnsureBounds(4); ptr.WriteInt32((int)value.Int64Value); return;
                case ILOp.Stind_I8: ptr.EnsureBounds(8); ptr.WriteInt64(value.Int64Value); return;
                case ILOp.Stind_R4: ptr.EnsureBounds(4); ptr.WriteSingle((float)value.DoubleValue); return;
                case ILOp.Stind_R8: ptr.EnsureBounds(8); ptr.WriteDouble(value.DoubleValue); return;
                default: // Stind_Ref / Stind_I
                    throw new NotSupportedException(
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
                ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                ILOp.Stind_R4 => StackSlot.OfFloat((float)value.DoubleValue),
                ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                _ => StackSlot.OfInt32((int)value.Int64Value),
            });
            return;
        }
        throw new InvalidOperationException($"stind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
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
        if (type is VmIntrinsicType intrinsic) {
            if (!intrinsic.IsValue)
                throw new InvalidOperationException($"sizeof は値型にのみ適用できます: {type.FullName}");
            return intrinsic.FullName switch {
                "System.SByte" or "System.Byte" or "System.Boolean" => 1,
                "System.Char" or "System.Int16" or "System.UInt16" => 2,
                "System.Int32" or "System.UInt32" or "System.Single" => 4,
                "System.Int64" or "System.UInt64" or "System.Double"
                    or "System.IntPtr" or "System.UIntPtr" => 8,
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
                    or "System.IntPtr" or "System.UIntPtr" => 8,
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
                    var fieldSize = field.FieldType is null ? 8 : SizeOfTypeCore(field.FieldType, visiting);
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
    public static StackSlot ValueFromBytes(byte[] bytes, VmType type, int size)
    {
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
    public static byte[] BytesOfValue(in StackSlot value, VmType type, int size)
    {
        var bytes = new byte[size];
        var typeName = type.FullName;
        if (size == 1)
        {
            bytes[0] = (byte)value.AsInt32;
            return bytes;
        }
        if (size == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value.AsInt32);
            return bytes;
        }
        if (size == 4)
        {
            var v = typeName == "System.Single"
                ? BitConverter.ToInt32(BitConverter.GetBytes((float)value.DoubleValue), 0)
                : value.AsInt32;
            BinaryPrimitives.WriteInt32LittleEndian(bytes, v);
            return bytes;
        }
        if (size == 8)
        {
            if (typeName == "System.Double")
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, value.DoubleValue);
                return bytes;
            }
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Int64Value);
            return bytes;
        }
        throw new InvalidOperationException($"unmanaged ポインタへの {typeName} (要素幅 {size} バイト) の書き込みに対応していません。");
    }
}

