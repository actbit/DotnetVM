using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>
/// オブジェクトグラフ走査の共通基盤 (GC 戦略から共用)。
/// スロット配列から参照 (VmClassInstance / VmArray / VmBoxedValue) を辿る。
/// ByRef (VmByRef) は参照先コンテナのスロットを展開し、値型 (VmStructValue) は
/// フィールドを展開する — 構造体自体はヒープ識別性を持たないため参照だけを辿る。
/// VmString はプール管理 (VmObject ではない) のため走査対象外。
/// </summary>
public static class ObjectGraphWalker {
    /// <summary>ヒープオブジェクトから直接到達する参照を列挙する。</summary>
    public static void CollectReferences(VmObject obj, Action<VmObject> visit) {
        switch (obj) {
            case VmClassInstance instance:
                CollectFromSlots(instance.Fields, visit);
                break;
            case VmArray array:
                CollectFromSlots(array.Elements, visit);
                break;
            case VmBoxedValue boxed:
                CollectFromSlots(boxed.Fields, visit);
                break;
            case VmIntrinsicInstance intrinsicInstance:
                CollectFromSlots(intrinsicInstance.State, visit);
                break;
            // VmExceptionObject: Message は VmString (ヒープ管理外)。将来の InnerException 追加時にここへ
        }
    }

    /// <summary>スロット配列から到達する参照を列挙する (ByRef コンテナ / ネスト構造体も展開)。</summary>
    public static void CollectFromSlots(StackSlot[]? slots, Action<VmObject> visit) {
        if (slots is null)
            return;
        foreach (var slot in slots)
            CollectFromSlot(slot, visit);
    }

    private static void CollectFromSlot(in StackSlot slot, Action<VmObject> visit) {
        switch (slot.Kind) {
            case StackKind.Object:
                if (slot.ObjectValue is VmObject referenced)
                    visit(referenced);
                break;
            case StackKind.ByRef:
                if (slot.ObjectValue is VmByRef byRef)
                    CollectFromSlots(byRef.Container, visit); // 参照先コンテナ (ローカル/フィールド) を展開
                break;
            case StackKind.ValueType:
                if (slot.ObjectValue is VmStructValue structValue)
                    CollectFromSlots(structValue.Fields, visit);
                break;
        }
    }
}
