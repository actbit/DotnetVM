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
    /// <summary>ホスト側の一時アロケーション (ヒープ管理外) の累計 (HostTemp quota)。</summary>
    private long _hostTempAllocated;
    /// <summary>ホスト側 CPU コストの消費 (HostWorkBudget)。</summary>
    private long _hostWorkSpent;
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
    /// アロケーション予約 (トランザクション)。生成時 (Reserve) に上限検査と計上を先に済ませ、
    /// <see cref="Commit"/> でオブジェクトの登録を確定する。localloc / newarr のように
    /// 「上限検査 → ホスト側の実確保 (new byte[] 等) → ヒープ登録」の順で進めたい箇所で使い、
    /// 実確保が途中で失敗した場合も using 抜けの <see cref="Dispose"/> が予約分の計上を
    /// 自動的に巻き戻す (予約済み会計の取り残しを構造的に防ぐ)。サイズ式は
    /// ObjectModel の概算式と共有されるため、Reserve 経由なら二重計上・計上漏れが起きない。
    /// </summary>
    public sealed class VmReservation : IDisposable {
        private VmHeap? _heap;
        private readonly long _size;
        private bool _committed;

        internal VmReservation(VmHeap heap, long size) {
            _heap = heap;
            _size = size;
        }

        /// <summary>予約を確定してオブジェクトをヒープに登録する (計上は予約時のまま = 二重計上しない)。</summary>
        public T Commit<T>(T obj) where T : VmObject {
            if (_committed || _heap is null)
                throw new InvalidOperationException("アロケーション予約は確定済みか解放済みです (Commit は 1 回だけ呼べます)。");
            _committed = true;
            var heap = _heap;
            _heap = null;
            heap.Register(obj);
            return obj;
        }

        /// <summary>未確定なら予約分の計上を巻き戻す (実確保の途中失敗 → using 抜けで自動実行)。</summary>
        public void Dispose() {
            if (!_committed && _heap is not null) {
                _heap.Rollback(_size);
                _heap = null;
            }
        }
    }

    /// <summary>確保予約を開始する (サイズは概算式を直接指定)。Commit で確定、未確定のまま Dispose すると巻き戻し。</summary>
    public VmReservation Reserve(long size) {
        CheckQuota(size);
        Charge(size);
        return new VmReservation(this, size);
    }

    /// <summary>localloc ブロックの確保予約 (概算式は ObjectModel.EstimateLocallocSize と共有)。</summary>
    public VmReservation ReserveLocalloc(int byteCount) =>
        Reserve(ObjectModel.EstimateLocallocSize(byteCount));

    /// <summary>配列の確保予約 (概算式は ObjectModel.EstimateArraySize と共有)。</summary>
    public VmReservation ReserveArray(int elementCount) =>
        Reserve(ObjectModel.EstimateArraySize(elementCount));

    /// <summary>予約分の計上を巻き戻す (VmReservation.Dispose 専用)。</summary>
    private void Rollback(long size) {
        _totalAllocated -= size;
        _liveBytes -= size;
        _allocatedSinceGc -= size;
        // _collectionDue は据え置き (巻き戻しで GC 要求を取り消さない = 保守的に早めの回収)
    }

    /// <summary>Reserve で計上済みのオブジェクトをヒープに登録する (計上は二重に行わない)。</summary>
    private T Register<T>(T obj) where T : VmObject {
        _objects.Add(obj);
        return obj;
    }

    /// <summary>上限検査のみ (超過は拒否)。</summary>
    private void CheckQuota(long size) {
        if (_totalAllocated + size > _memory.TotalAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"累計アロケーション上限 {_memory.TotalAllocationByteLimit:N0} バイトを超過しました (要求 {size:N0} バイト)。");
        if (_liveBytes + size > _memory.LiveObjectByteLimit)
            throw new MemoryQuotaExceededException(
                $"生存オブジェクト上限 {_memory.LiveObjectByteLimit:N0} バイトを超過しました (生存 {_liveBytes:N0} + 要求 {size:N0} バイト)。GC で回収可能なオブジェクトがない場合はゲストのメモリ使用量を見直してください。");
    }

    /// <summary>上限検査後の計上 (4 カウンタ + GC 要求)。</summary>
    private void Charge(long size) {
        _totalAllocated += size;
        _liveBytes += size;
        _allocatedSinceGc += size;
        if (_allocatedSinceGc >= _memory.GcTriggerAllocationInterval)
            _collectionDue = true; // 実回収はセーフポイントで (ルート整合のため確保の再入では起動しない)
    }

    /// <summary>
    /// ホスト側の一時バッファ (DefaultInterpolatedStringHandler 内部の StringBuilder 等、
    /// VM heap 外のホストメモリ) を概算計上する。GC 管理外で解放されないため
    /// 累計上限への計上のみ (保守的 = 安全側)。ハンドラ等の append 系 intrinsic で呼ぶ。
    /// HostTempAllocationByteLimit (タスク 2: ホスト側一時メモリも quota 対象) を計上する。
    /// </summary>
    public void ChargeHostBuffer(int charCount) {
        var size = 24 + 2L * charCount;
        // HostTemp quota (ゲスト操作の結果で VM ヒープ外に生じるホスト確保分)
        if (_hostTempAllocated + size > _memory.HostTempAllocationByteLimit)
            throw new MemoryQuotaExceededException(
                $"ホスト側一時アロケーション上限 {_memory.HostTempAllocationByteLimit:N0} バイトを超過しました (buffers " +
                $"(+{size:N0}) 内 {charCount:N0} 文字)。\n残 {_hostTempAllocated:N0} / 上限 {_memory.HostTempAllocationByteLimit:N0}。");
        _hostTempAllocated += size;
        CheckQuota(size);
        _totalAllocated += size;
    }

    /// <summary>ホスト側 CPU コストの消費 (HostWorkBudget タスク 2)。bc/intrinsic 毎に
    /// 重い host 処理 (InvariantCulture 比較 / formatting / encoding 等) の発生に対して
    /// 見積ったコストを積み上げる。budget中超過はメモリ系例外で VM に伝播 (ゲスト catch 外)。</summary>
    public void ChargeHostWork(long costUnits) {
        if (_hostWorkSpent + costUnits > _memory.HostWorkBudget)
            throw new MemoryQuotaExceededException(
                $"host 側作業予算 (HostWorkBudget) {_memory.HostWorkBudget:N0} を超えました。" +
                $"+{costUnits:N0} ユニットで累計 {_hostWorkSpent + costUnits:N0}。");
        _hostWorkSpent += costUnits;
    }

    /// <summary>オブジェクトをヒープに登録し、サイズを計上する。上限超過は拒否。</summary>
    public T Allocate<T>(T obj) where T : VmObject {
        var size = ObjectModel.EstimateSize(obj);
        CheckQuota(size);
        Charge(size);
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
