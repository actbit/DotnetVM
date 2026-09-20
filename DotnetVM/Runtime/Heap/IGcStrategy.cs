using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>1 回の GC 起動に渡されるコンテキスト (ヒープ全オブジェクト + ルート集合)。</summary>
public sealed class GcCollectionContext {
    public required IReadOnlyList<VmObject> HeapObjects { get; init; }
    /// <summary>再帰的に到達可能とみなすルート (実行中フレーム/静的フィールド/ハンドル表)。</summary>
    public required IReadOnlyList<VmObject> Roots { get; init; }
}

/// <summary>
/// GC 戦略インターフェース。Collect は「生存オブジェクトの集合」を返し、
/// ヒープ側は返されなかったオブジェクトを sweep する (計上・統計は VmHeap の責務)。
/// 戦略を差し替えることで世代別 GC 等に拡張できる (VmObject.Generation を利用)。
/// </summary>
public interface IGcStrategy {
    string Name { get; }

    /// <summary>生存オブジェクトの集合を返す。Roots から到達できないオブジェクトを含めてはならない。</summary>
    HashSet<VmObject> Collect(GcCollectionContext context);
}
