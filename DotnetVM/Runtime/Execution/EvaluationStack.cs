using System.Runtime.CompilerServices;

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
    public int Count { get; private set; }
    public readonly int MaxStack;

    public EvaluationStack(int maxStack) {
        if (maxStack < 0)
            throw new ArgumentOutOfRangeException(nameof(maxStack));
        // The verifier proves the required depth before a frame is created.
        // Keep the actual capacity equal to that proof instead of silently
        // rounding hostile metadata up to eight slots.
        MaxStack = maxStack;
        _slots = new StackSlot[this.MaxStack];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in StackSlot slot) {
        if (Count == _slots.Length)
            throw new BadImageFormatException(
                $"評価スタックがオーバーフローしました (Count={Count}, MaxStack={MaxStack})。");
        _slots[Count++] = slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StackSlot Pop() {
        if (Count == 0)
            throw new BadImageFormatException("評価スタックが空です (pop できません)。");
        var slot = _slots[--Count];
        _slots[Count] = default;
        return slot;
    }

    /// <summary>peek (取り出さない)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref StackSlot Peek() {
        if (Count == 0)
            throw new BadImageFormatException("評価スタックが空です (peek できません)。");
        return ref _slots[Count - 1];
    }

    /// <summary>上から depth 番目を参照 (dup や二項演算の両辺参照用)。</summary>
    public ref StackSlot PeekAt(int depth) {
        if (depth < 0 || depth >= Count)
            throw new BadImageFormatException($"評価スタックの深さ {depth} は範囲外です (Count={Count})。");
        return ref _slots[Count - 1 - depth];
    }

    public void Clear() {
        Array.Clear(_slots, 0, Count);
        Count = 0;
    }

    /// <summary>現在有効なスロットのコピー (GC ルート走査用。下から順)。</summary>
    public StackSlot[] CopySlots() {
        var copy = new StackSlot[Count];
        Array.Copy(_slots, copy, Count);
        return copy;
    }
}
