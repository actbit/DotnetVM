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
        var slot = array.Elements[index];
        return kind switch {
            ArrayElementKind.Int32 => StackSlot.OfInt32((int)slot.Int64Value),
            ArrayElementKind.Int64 => StackSlot.OfInt64(slot.Int64Value),
            ArrayElementKind.Float => StackSlot.OfFloat(slot.DoubleValue),
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

        array.Elements[index] = kind switch {
            ArrayElementKind.Int32 => StackSlot.OfInt32((int)value.Int64Value),
            ArrayElementKind.Int64 => StackSlot.OfInt64(value.Int64Value),
            ArrayElementKind.Float => StackSlot.OfFloat(value.DoubleValue),
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

    /// <summary>cpblk: unmanaged ポインタ間は実バイトコピー、マネージポインタ (ByRef) 間は
    /// スロット粒度コピー (バイト数は 8 バイト単位に切り上げ)。</summary>
    public static void CopyMemoryBlock(in StackSlot dstSlot, in StackSlot srcSlot, int size) {
        if (size < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        if (dstSlot.ObjectValue is VmNativePointer dst && srcSlot.ObjectValue is VmNativePointer src) {
            if ((long)dst.ByteOffset + size > dst.Bytes.Length || (long)src.ByteOffset + size > src.Bytes.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException",
                    $"cpblk が仮想メモリブロックの範囲外です (size={size}, dst offset={dst.ByteOffset}/{dst.Bytes.Length}, src offset={src.ByteOffset}/{src.Bytes.Length})。");
            Array.Copy(src.Bytes, src.ByteOffset, dst.Bytes, dst.ByteOffset, size);
            return;
        }
        var dstByRef = MemoryLocation(dstSlot);
        var srcByRef = MemoryLocation(srcSlot);
        var count = (int)Math.Min((size + 7L) / 8, int.MaxValue);
        if ((long)dstByRef.Index + count > dstByRef.Container.Length || (long)srcByRef.Index + count > srcByRef.Container.Length)
            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                $"cpblk が範囲外です (size={size} → {count} スロット, dst 長 {dstByRef.Container.Length}, src 長 {srcByRef.Container.Length})。");
        Array.Copy(srcByRef.Container, srcByRef.Index, dstByRef.Container, dstByRef.Index, count);
    }

    /// <summary>initblk: unmanaged ポインタ先は実バイト充填、マネージポインタ (ByRef) 先は
    /// スロット粒度 (8 バイト単位に切り上げ) の 0 充填のみ。</summary>
    public static void InitMemoryBlock(in StackSlot dstSlot, in StackSlot value, int size) {
        if (size < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        if (dstSlot.ObjectValue is VmNativePointer dst) {
            var fill = checked((byte)value.Int64Value);
            if ((long)dst.ByteOffset + size > dst.Bytes.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException",
                    $"initblk が仮想メモリブロックの範囲外です (size={size}, offset={dst.ByteOffset}/{dst.Bytes.Length})。");
            Array.Fill(dst.Bytes, fill, dst.ByteOffset, size);
            return;
        }
        if (value.Int64Value != 0)
            throw new NotSupportedException("マネージポインタ (ByRef) 先への initblk は 0 以外の充填値に対応していません (スロット粒度のため)。");
        var dstByRef = MemoryLocation(dstSlot);
        var count = (int)Math.Min((size + 7L) / 8, int.MaxValue);
        if ((long)dstByRef.Index + count > dstByRef.Container.Length)
            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                $"initblk が範囲外です (size={size} → {count} スロット, 長 {dstByRef.Container.Length})。");
        Array.Clear(dstByRef.Container, dstByRef.Index, count);
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
        var offset = op == ILOp.Add ? pointer.ByteOffset + other.Int64Value : pointer.ByteOffset - other.Int64Value;
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
                ILOp.Ldind_I1 => StackSlot.OfInt32(ptr.ReadInt8()),
                ILOp.Ldind_U1 => StackSlot.OfInt32(ptr.ReadUInt8()),
                ILOp.Ldind_I2 => StackSlot.OfInt32(ptr.ReadInt16()),
                ILOp.Ldind_U2 => StackSlot.OfInt32(ptr.ReadUInt16()),
                ILOp.Ldind_I4 or ILOp.Ldind_U4 => StackSlot.OfInt32(ptr.ReadInt32()),
                ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(ptr.ReadInt64()),
                ILOp.Ldind_R4 or ILOp.Ldind_R8 => StackSlot.OfFloat(ptr.ReadDouble()),
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
            return op switch {
                ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(boxed.Fields[0].Int64Value),
                ILOp.Ldind_R4 or ILOp.Ldind_R8 => StackSlot.OfFloat(boxed.Fields[0].DoubleValue),
                ILOp.Ldind_Ref => boxed.Fields[0],
                _ => StackSlot.OfInt32((int)boxed.Fields[0].Int64Value), // I1〜U4 は i4 正規化スロット
            };
        }
        if (address.ObjectValue is VmByRef byRef) {
            return op switch {
                ILOp.Ldind_I8 or ILOp.Ldind_I => StackSlot.OfInt64(byRef.Slot.Int64Value),
                ILOp.Ldind_R4 or ILOp.Ldind_R8 => StackSlot.OfFloat(byRef.Slot.DoubleValue),
                ILOp.Ldind_Ref => byRef.Slot,
                _ => StackSlot.OfInt32((int)byRef.Slot.Int64Value), // I1〜U4 は i4 正規化スロット
            };
        }
        throw new InvalidOperationException($"ldind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
    }

    /// <summary>stind: アドレスの参照先へ書き込む (LoadIndirect の書き込み版)。</summary>
    public static void StoreIndirect(ILOp op, in StackSlot address, in StackSlot value) {
        if (address.ObjectValue is VmNativePointer ptr) {
            switch (op) {
                case ILOp.Stind_I1: ptr.WriteInt8((int)value.Int64Value); return;
                case ILOp.Stind_I2: ptr.WriteInt16((int)value.Int64Value); return;
                case ILOp.Stind_I4: ptr.WriteInt32((int)value.Int64Value); return;
                case ILOp.Stind_I8: ptr.WriteInt64(value.Int64Value); return;
                case ILOp.Stind_R4 or ILOp.Stind_R8: ptr.WriteDouble(value.DoubleValue); return;
                default: // Stind_Ref / Stind_I
                    throw new NotSupportedException(
                        "unmanaged ポインタへの参照書き込み (stind.ref) は対応していません。");
            }
        }
        if (address.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // ボックス実体への書き込みの緩和 (LoadIndirect と対。box が指す先 = Fields[0] を更新する)
        if (address.ObjectValue is VmBoxedValue boxed) {
            boxed.Fields[0] = op switch {
                ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                ILOp.Stind_R4 or ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                _ => StackSlot.OfInt32((int)value.Int64Value),
            };
            return;
        }
        if (address.ObjectValue is VmByRef byRef) {
            byRef.Slot = op switch {
                ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                ILOp.Stind_R4 or ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                _ => StackSlot.OfInt32((int)value.Int64Value),
            };
            return;
        }
        throw new InvalidOperationException($"stind のアドレスがポインタではありません: {SlotOps.Describe(address)}");
    }

    // ---- sizeof ----

    /// <summary>sizeof の VM 値。プリミティブは CLR と同じ実際のサイズ、ゲスト値型は
    /// 順次レイアウト近似 (フィールドサイズの和 + アライメント詰め)、参照型は適用不可。</summary>
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
                var size = 0;
                var maxAlign = 1;
                foreach (var field in cls.Fields) {
                    if ((field.Flags & 0x0010) != 0)
                        continue; // FieldAttributes.Static
                    var fieldSize = field.FieldType is null ? 8 : SizeOfTypeCore(field.FieldType, visiting);
                    var align = Math.Min(fieldSize, 8);
                    size = (size + align - 1) / align * align;
                    size += fieldSize;
                    maxAlign = Math.Max(maxAlign, align);
                }
                size = (size + maxAlign - 1) / maxAlign * maxAlign;
                return Math.Max(size, 1);
            } finally {
                visiting.Remove(cls);
            }
        }
        throw new InvalidOperationException($"sizeof は値型にのみ適用できます: {type.FullName}");
    }
}
