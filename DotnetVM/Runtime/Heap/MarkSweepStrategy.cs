using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>
/// 非世代別のマーク &amp; スイープ。ルートから ObjectGraphWalker で到達可能集合を求め、
/// 生存集合を返す (sweep と計上は VmHeap 側)。世代別戦略へは VmObject.Generation を足場に拡張する。
/// </summary>
public sealed class MarkSweepStrategy : IGcStrategy {
    public string Name => "mark-sweep";

    public HashSet<VmObject> Collect(GcCollectionContext context) {
        var live = new HashSet<VmObject>(context.HeapObjects.Count);
        var worklist = new Queue<VmObject>();

        void Mark(VmObject obj) {
            if (live.Add(obj))
                worklist.Enqueue(obj);
        }

        foreach (var root in context.Roots)
            Mark(root);
        while (worklist.Count > 0) {
            ObjectGraphWalker.CollectReferences(worklist.Dequeue(), Mark);
        }
        return live;
    }
}
