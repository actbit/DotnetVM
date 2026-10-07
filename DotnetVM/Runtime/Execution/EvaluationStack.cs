using System.Runtime.CompilerServices;
using System.Buffers;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>評価スタックのスロット型 (ECMA-335 III.1.1 の検証可能スタック型に対応)。</summary>
public enum StackKind : byte {
    /// <summary>参照なし (未使用スロット)。</summary>
    Empty,
    /// <summary>int32 (bool/char/ I1〜U4 はここに正規化)。</summary>
    Int32,
    /// <summary>int64。</summary>
    Int64,
    /// <summary>native int。</summary>
    NativeInt,
    /// <summary>float (F / F64。F32 は読み込み時に double へ正規化)。</summary>
    Float,
    /// <summary>オブジェクト参照 / マネージ参照以外の O。</summary>
    Object,
    /// <summary>マネージポインタ (&amp;、配列要素/フィールド/ローカルへの参照)。</summary>
    ByRef,
    /// <summary>内部ポインタ (native int と同義だが検証区別用)。</summary>
    IntPtr,
    /// <summary>TypedByRef。</summary>
    TypedByRef,
    /// <summary>値型の値 (VmStructValue)。</summary>
    ValueType,
}

/// <summary>
/// 評価スタック上の 1 スロット。IL 実行中の全ての値はここを通る。
/// プリミティブは生のフィールドに、オブジェクトは VM オブジェクトモデル (VmObject) 経由で保持する。
/// </summary>
public struct StackSlot {
    public StackKind Kind;
    // A small set of CoreLib value facades can carry a primitive payload
    // directly in the slot. InlineKind/InlineType are empty for ordinary
    // slots, so the representation remains compatible with existing VM
    // values while avoiding a temporary VmStructValue for scalar wrappers.
    internal StackKind InlineKind;
    internal VmType? InlineType;

    // プリミティブ共用体 (Kind で解釈を決定)
    public long Int64Value;
    public double DoubleValue;
    public object? ObjectValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfInt32(int value) => new() { Kind = StackKind.Int32, Int64Value = value };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfInt64(long value) => new() { Kind = StackKind.Int64, Int64Value = value };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfNativeInt(long value) => new() { Kind = StackKind.NativeInt, Int64Value = value };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfFloat(double value) => new() { Kind = StackKind.Float, DoubleValue = value };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfObject(object? value) => new() { Kind = StackKind.Object, ObjectValue = value };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfByRef(object reference) => new() { Kind = StackKind.ByRef, ObjectValue = reference };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StackSlot OfValueType(object structValue) => new() { Kind = StackKind.ValueType, ObjectValue = structValue };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static StackSlot OfInlineValue(VmType type, in StackSlot value) => new() {
        Kind = StackKind.ValueType,
        InlineKind = value.Kind,
        InlineType = type,
        Int64Value = value.Int64Value,
        DoubleValue = value.DoubleValue,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetInlineValue(out StackSlot value) {
        if (Kind == StackKind.ValueType && InlineType is not null &&
            InlineKind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt or StackKind.IntPtr or StackKind.Float) {
            value = new StackSlot {
                Kind = InlineKind,
                Int64Value = Int64Value,
                DoubleValue = DoubleValue,
            };
            return true;
        }
        value = default;
        return false;
    }
    public static StackSlot Null => new() { Kind = StackKind.Object, ObjectValue = null };

    public int AsInt32 => (int)Int64Value;

    public override string ToString() => Kind switch {
        StackKind.Int32 => $"i4({Int64Value})",
        StackKind.Int64 => $"i8({Int64Value})",
        StackKind.NativeInt or StackKind.IntPtr => $"i({Int64Value})",
        StackKind.Float => $"f({DoubleValue})",
        StackKind.Object => $"obj({ObjectValue?.ToString() ?? "null"})",
        StackKind.ByRef => $"byref({ObjectValue})",
        StackKind.ValueType => $"val({ObjectValue})",
        StackKind.TypedByRef => "typedref",
        _ => "empty",
    };
}

/// <summary>
/// メソッド実行 1 フレーム分の評価スタック。
/// 深いゲスト再帰でもホストスタックを消費しないようヒープ確保とする。
/// </summary>
public sealed class EvaluationStack {
    private StackSlot[] _slots;
    private bool _released;
    public int Count { get; private set; }
    public int MaxStack { get; private set; }
    // Pop/Clear erase unused slots, so scanning this buffer at a stopped-world
    // boundary is equivalent to copying the active prefix, without allocation.
    internal StackSlot[] RootSlots => _slots;
    internal ReadOnlySpan<StackSlot> ActiveSlots => _slots.AsSpan(0, Count);

    public EvaluationStack(int maxStack) {
        if (maxStack < 0)
            throw new ArgumentOutOfRangeException(nameof(maxStack));
        // The verifier proves the required depth before a frame is created.
        // Keep the actual capacity equal to that proof instead of silently
        // rounding hostile metadata up to eight slots.
        MaxStack = maxStack;
        _slots = this.MaxStack == 0 ? [] : ArrayPool<StackSlot>.Shared.Rent(this.MaxStack);
    }

    /// <summary>
    /// Rebind this stack to a recycled interpreter frame. Keeping the backing
    /// storage in the frame pool avoids a rent/return pair and, more
    /// importantly, avoids retaining guest references between invocations.
    /// </summary>
    internal void PrepareForReuse(int maxStack) {
        if (maxStack < 0)
            throw new ArgumentOutOfRangeException(nameof(maxStack));
        if (maxStack > _slots.Length) {
            if (_slots.Length != 0)
                ArrayPool<StackSlot>.Shared.Return(_slots);
            _slots = maxStack == 0 ? [] : ArrayPool<StackSlot>.Shared.Rent(maxStack);
        }
        Array.Clear(_slots);
        MaxStack = maxStack;
        Count = 0;
        _released = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in StackSlot slot) {
        var index = Count;
        if ((uint)index >= (uint)MaxStack)
            throw new BadImageFormatException(
                $"評価スタックがオーバーフローしました (Count={index}, MaxStack={MaxStack})。");
        _slots[index] = slot;
        Count = index + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StackSlot Pop() {
        var index = Count - 1;
        if ((uint)index >= (uint)_slots.Length)
            throw new BadImageFormatException("評価スタックが空です (pop できません)。");
        ref var top = ref _slots[index];
        var slot = top;
        top = default;
        Count = index;
        return slot;
    }

    /// <summary>peek (取り出さない)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref StackSlot Peek() {
        var index = Count - 1;
        if ((uint)index >= (uint)_slots.Length)
            throw new BadImageFormatException("評価スタックが空です (peek できません)。");
        return ref _slots[index];
    }

    /// <summary>上から depth 番目を参照 (dup や二項演算の両辺参照用)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref StackSlot PeekAt(int depth) {
        var count = Count;
        var index = count - 1 - depth;
        if ((uint)depth >= (uint)count || (uint)index >= (uint)_slots.Length)
            throw new BadImageFormatException($"評価スタックの深さ {depth} は範囲外です (Count={count})。");
        return ref _slots[index];
    }

    /// <summary>呼出引数を IL の引数順でコピーし、元のスロットを消去する。</summary>
    internal StackSlot[] PopArguments(int arity) {
        var source = ArgumentSlots(arity);
        var arguments = source.ToArray();
        source.Clear();
        Count -= arity;
        return arguments;
    }

    // A synchronous primitive leaf can borrow these slots. Keep them active
    // until it returns so the caller remains a GC root at every safepoint.
    internal Span<StackSlot> ArgumentSlots(int arity) {
        var count = Count;
        if ((uint)arity > (uint)count)
            throw new BadImageFormatException("評価スタックに呼出引数が足りません。");
        return _slots.AsSpan(count - arity, arity);
    }

    /// <summary>
    /// Copy and remove call arguments without constructing an intermediate
    /// Span or running the general DropArguments path. The one- and two-slot
    /// cases dominate ordinary managed calls, so keep those copies explicit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void TakeArguments(int arity, Span<StackSlot> destination) {
        var count = Count;
        if ((uint)arity > (uint)count || destination.Length < arity)
            throw new BadImageFormatException("評価スタックに呼出引数が足りません。");
        var start = count - arity;
        if (arity == 0) {
            Count = start;
            return;
        }
        if (arity == 1) {
            destination[0] = _slots[start];
            _slots[start] = default;
        } else if (arity == 2) {
            destination[0] = _slots[start];
            destination[1] = _slots[start + 1];
            _slots[start] = default;
            _slots[start + 1] = default;
        } else {
            _slots.AsSpan(start, arity).CopyTo(destination);
            _slots.AsSpan(start, arity).Clear();
        }
        Count = start;
    }


    internal void DropArguments(int arity) {
        ArgumentSlots(arity).Clear();
        Count -= arity;
    }


    /// <summary>
    /// Update the active prefix after a generated JIT frame has manipulated
    /// the backing slots through its verified stack pointer. The JIT keeps
    /// the pointer in a register between safepoints, while the ordinary
    /// interpreter continues to use the checked Push/Pop API.
    /// </summary>
    internal void SetCountForJit(int count) {
        if ((uint)count > (uint)MaxStack)
            throw new BadImageFormatException("JIT 評価スタックの深さが検証済み上限を超えました。");
        Count = count;
    }

    public void Clear() {
        Array.Clear(_slots, 0, Count);
        Count = 0;
    }

    /// <summary>
    /// Return the backing storage after the frame has been removed from the
    /// VM root set. Pooled arrays are cleared completely so a later frame
    /// cannot observe or retain guest references from this execution.
    /// </summary>
    internal void Release() {
        if (_released)
            return;
        _released = true;
        if (_slots.Length == 0)
            return;
        Array.Clear(_slots);
        ArrayPool<StackSlot>.Shared.Return(_slots);
    }

    /// <summary>現在有効なスロットのコピー (GC ルート走査用。下から順)。</summary>
    public StackSlot[] CopySlots() {
        var copy = new StackSlot[Count];
        Array.Copy(_slots, copy, Count);
        return copy;
    }
}
