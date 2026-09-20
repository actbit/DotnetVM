using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>GC 統計のスナップショット。</summary>
public readonly record struct GcStatistics(long TotalAllocatedBytes, long LiveBytes, int CollectionCount);

/// <summary>
/// VM の唯一のアロケーション入口。全 VM オブジェクトはここを通って登録・計上される。
/// クォータ (TotalAllocationByteLimit) 超過は MemoryQuotaExceededException (管理例外)。
/// GC 本体は M6 で IGcStrategy としてここに接続する (現状は計上のみ)。
/// </summary>
public sealed class VmHeap {
    private readonly MemoryPolicy _memory;
    private readonly List<VmObject> _objects = [];
    private long _totalAllocated;
    private int _collectionCount;

    public VmHeap(MemoryPolicy memory) {
        _memory = memory;
    }

    /// <summary>オブジェクトをヒープに登録し、サイズを計上する。上限超過は拒否。</summary>
    public T Allocate<T>(T obj) where T : VmObject {
        var size = ObjectModel.EstimateSize(obj);
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (要求 {size:N0} バイト)。");
        _totalAllocated += size;
        _objects.Add(obj);
        return obj;
    }

    /// <summary>文字列のアロケーション計上 (VmString はプール共有のため概算のみ計上)。</summary>
    public void ChargeString(int charCount) {
        var size = 24 + 2L * charCount;
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (文字列 {charCount} 文字)。");
        _totalAllocated += size;
    }

    public GcStatistics Snapshot() => new(_totalAllocated, _totalAllocated, _collectionCount);

    /// <summary>ヒープ登録解除 (M6 の sweep から呼ばれる想定。現状はテスト補助)。</summary>
    internal void RemoveDead(IEnumerable<VmObject> dead) {
        var deadSet = new HashSet<VmObject>(dead);
        _objects.RemoveAll(o => deadSet.Contains(o));
    }

    internal IReadOnlyList<VmObject> TrackedObjects => _objects;
}
