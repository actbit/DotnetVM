using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>GC 統計のスナップショット。</summary>
public readonly record struct GcStatistics(long TotalAllocatedBytes, long LiveBytes, int CollectionCount);

/// <summary>
/// VM の唯一のアロケーション入口。全 VM オブジェクトはここを通って登録・計上される。
/// - TotalAllocationByteLimit 超過は即 MemoryQuotaExceededException (管理例外)
/// - GcTriggerAllocationInterval 分の新規確保で回収を要求し、次のセーフポイントで IGcStrategy を起動
///   (セーフポイント間でのみゲスト状態が整合するため、確保の再入で回収しない)
/// - 回収後の生存バイトが LiveObjectByteLimit を超えたら拒否
/// ルートは登録されたソース (実行中フレーム / 静的フィールド / GcHandleTable) から集める。
/// </summary>
public sealed class VmHeap {
    private readonly MemoryPolicy _memory;
    private readonly IGcStrategy _strategy;
    private readonly List<VmObject> _objects = [];
    private readonly List<Func<IEnumerable<VmObject?>>> _rootObjectSources = [];
    private readonly List<Func<IEnumerable<StackSlot[]>>> _rootSlotSources = [];
    private long _totalAllocated;
    private long _liveBytes;
    private long _allocatedSinceGc;
    private int _collectionCount;
    private bool _collectionDue;
    private bool _collecting;

    public VmHeap(MemoryPolicy memory, IGcStrategy? strategy = null) {
        _memory = memory;
        _strategy = strategy ?? new MarkSweepStrategy();
    }

    /// <summary>GC 戦略名。</summary>
    public string GcStrategyName => _strategy.Name;

    /// <summary>オブジェクトを直接ルートとして登録する (GcHandleTable 等)。</summary>
    public void AddRootObjectSource(Func<IEnumerable<VmObject?>> source) =>
        _rootObjectSources.Add(source);

    /// <summary>スロット配列をルートとして登録する (フレームの args/locals/スタック、静的ストレージ等)。</summary>
    public void AddRootSlotSource(Func<IEnumerable<StackSlot[]>> source) =>
        _rootSlotSources.Add(source);

    /// <summary>
    /// ホスト側の実メモリ確保 (<c>new byte[]</c> 等) の**前に**上限を検査し、計上だけ先に進める。
    /// localloc / newarr のように実確保がオブジェクト構築より先に起きる箇所で
    /// 「Reserve(推定サイズ) → 実確保 → Register」の順に使い、巨大確保が上限チェック前に
    /// ホストメモリを圧迫するのを防ぐ。サイズは Allocate 実行時の EstimateSize と同一の式で。
    /// </summary>
    public void Reserve(long size) {
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (要求 {size:N0} バイト)。");
        if (_liveBytes + size > _memory.LiveObjectByteLimit)
            throw new MemoryQuotaExceededException(
                $"生存オブジェクト上限 {_memory.LiveObjectByteLimit:N0} バイトを超過しました (生存 {_liveBytes:N0} + 要求 {size:N0} バイト)。GC で回収可能なオブジェクトがない場合はゲストのメモリ使用量を見直してください。");
        _totalAllocated += size;
        _liveBytes += size;
        _allocatedSinceGc += size;
        if (_allocatedSinceGc >= _memory.GcTriggerAllocationInterval)
            _collectionDue = true; // 実回収はセーフポイントで (ルート整合のため確保の再入では起動しない)
    }

    /// <summary>Reserve で計上済みのオブジェクトをヒープに登録する (計上は二重に行わない)。</summary>
    public T Register<T>(T obj) where T : VmObject {
        _objects.Add(obj);
        return obj;
    }

    /// <summary>
    /// ホスト側の一時バッファ (DefaultInterpolatedStringHandler 内部の StringBuilder 等、
    /// VM heap 外のホストメモリ) を概算計上する。GC 管理外で解放されないため
    /// 累計上限への計上のみ (保守的 = 安全側)。ハンドラ等の append 系 intrinsic で呼ぶ。
    /// </summary>
    public void ChargeHostBuffer(int charCount) {
        var size = 24 + 2L * charCount;
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (ホスト バッファ {charCount} 文字)。");
        _totalAllocated += size;
    }

    /// <summary>オブジェクトをヒープに登録し、サイズを計上する。上限超過は拒否。</summary>
    public T Allocate<T>(T obj) where T : VmObject {
        var size = ObjectModel.EstimateSize(obj);
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (要求 {size:N0} バイト)。");
        if (_liveBytes + size > _memory.LiveObjectByteLimit)
            throw new MemoryQuotaExceededException(
                $"生存オブジェクト上限 {_memory.LiveObjectByteLimit:N0} バイトを超過しました (生存 {_liveBytes:N0} + 要求 {size:N0} バイト)。GC で回収可能なオブジェクトがない場合はゲストのメモリ使用量を見直してください。");
        _totalAllocated += size;
        _liveBytes += size;
        _allocatedSinceGc += size;
        if (_allocatedSinceGc >= _memory.GcTriggerAllocationInterval)
            _collectionDue = true; // 実回収はセーフポイントで (ルート整合のため確保の再入では起動しない)
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

    /// <summary>セーフポイントから呼ぶ。回収要求が溜まっていれば Collect を起動する。</summary>
    public void CollectIfDue() {
        if (_collectionDue && !_collecting)
            Collect();
    }

    /// <summary>GC を起動する (IGcStrategy にルートとヒープを渡し、到達不能オブジェクトを sweep)。</summary>
    public GcStatistics Collect() {
        if (_collecting)
            return Snapshot(); // 再入 (セーフポイントとホスト呼出の同時起動) は 1 回に潰す
        _collecting = true;
        try {
            var roots = new List<VmObject>();
            foreach (var source in _rootObjectSources)
                foreach (var obj in source())
                    if (obj is not null)
                        roots.Add(obj);
            foreach (var source in _rootSlotSources)
                foreach (var slots in source())
                    ObjectGraphWalker.CollectFromSlots(slots, roots.Add);

            var live = _strategy.Collect(new GcCollectionContext {
                HeapObjects = _objects,
                Roots = roots,
            });

            var deadCount = 0;
            for (var i = _objects.Count - 1; i >= 0; i--) {
                if (!live.Contains(_objects[i])) {
                    _objects.RemoveAt(i);
                    deadCount++;
                }
            }
            _liveBytes = 0;
            foreach (var obj in _objects)
                _liveBytes += ObjectModel.EstimateSize(obj);
            _collectionCount++;
            _allocatedSinceGc = 0;
            _collectionDue = false;

            if (_liveBytes > _memory.LiveObjectByteLimit)
                throw new MemoryQuotaExceededException(
                    $"生存オブジェクト上限 {_memory.LiveObjectByteLimit:N0} バイトを超過しました (GC 後の生存 {_liveBytes:N0} バイト)。");
            return new GcStatistics(_totalAllocated, _liveBytes, _collectionCount);
        } finally {
            _collecting = false;
        }
    }

    public GcStatistics Snapshot() => new(_totalAllocated, _liveBytes, _collectionCount);

    /// <summary>ヒープ登録解除 (テスト補助。通常は Collect が担当)。</summary>
    internal void RemoveDead(IEnumerable<VmObject> dead) {
        var deadSet = new HashSet<VmObject>(dead);
        _objects.RemoveAll(o => deadSet.Contains(o));
    }

    internal IReadOnlyList<VmObject> TrackedObjects => _objects;
}
