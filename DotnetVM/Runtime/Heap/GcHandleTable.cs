using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

/// <summary>ホスト側がゲストオブジェクトを強参照で保持するための不透明ハンドル。</summary>
public readonly record struct GcHandle(long Id) {
    /// <summary>割り当て済みのハンドルか (既定値は無効)。</summary>
    public bool IsAllocated => Id != 0;
}

/// <summary>
/// GC ハンドル表。ホストがゲストオブジェクトへの参照を GC をまたいで保持するための面
/// (CLR の GCHandle 強参照相当)。登録されたオブジェクトは GC ルートになる。
/// </summary>
public sealed class GcHandleTable {
    private readonly Dictionary<long, VmObject> _handles = [];
    private readonly object _gate = new();
    private long _nextId;

    /// <summary>オブジェクトへの強参照ハンドルを割り当てる。</summary>
    public GcHandle Register(VmObject obj) {
        ArgumentNullException.ThrowIfNull(obj);
        lock (_gate) {
            var handle = new GcHandle(++_nextId);
            _handles[handle.Id] = obj;
            return handle;
        }
    }

    /// <summary>ハンドルの参照先を取得する (解除済みなら null)。</summary>
    public VmObject? GetTarget(GcHandle handle) {
        lock (_gate)
            return _handles.TryGetValue(handle.Id, out var obj) ? obj : null;
    }

    /// <summary>ハンドルを解除する (参照先が他に参照されていなければ次の GC で回収される)。
    /// 二重解除は許容する (no-op)。</summary>
    public void Free(GcHandle handle) {
        lock (_gate)
            _handles.Remove(handle.Id);
    }

    public int Count { get { lock (_gate) return _handles.Count; } }

    /// <summary>
    /// VM Dispose 時に全ハンドルを無効化する。個別 Free と異なり、既にホストが保持している
    /// GcHandle 値もすべて stale になるため、破棄済み VM のオブジェクトを再び root 化できない。
    /// </summary>
    internal void InvalidateAll() {
        lock (_gate)
            _handles.Clear();
    }

    /// <summary>生存ハンドルの参照先を列挙する (GC ルート用)。</summary>
    public IEnumerable<VmObject> EnumerateRoots() {
        lock (_gate)
            return _handles.Values.ToArray();
    }
}
