using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Heap;

// Ephemerons: a secondary is live only when its primary is independently live.
// Strong-rooting both halves makes ConditionalWeakTable leak every guest key.
internal sealed class DependentHandleTable {
    private readonly object _gate = new();
    private readonly Dictionary<long, (StackSlot Target, StackSlot Dependent)> _entries = [];
    private long _next;
    private readonly HashSet<long> _strong = [];
    internal long Allocate(StackSlot target, StackSlot dependent, bool strong = false) { lock (_gate) { var id = checked(++_next * 8); _entries.Add(id, (target, dependent)); if (strong) _strong.Add(id); return id; } }
    internal VmObject[] StrongRoots() { lock (_gate) return _strong.Select(id => _entries[id].Target.ObjectValue).OfType<VmObject>().ToArray(); }
    internal void SetTarget(long id, StackSlot value) { lock (_gate) if (_entries.TryGetValue(id, out var pair)) _entries[id] = (value, pair.Dependent); }
    internal StackSlot CompareExchange(long id, StackSlot value, StackSlot comparand) { lock (_gate) {
        var current = Get(id).Target;
        if (ReferenceEquals(current.ObjectValue, comparand.ObjectValue)) SetTarget(id, value);
        return current;
    } }
    internal (StackSlot Target, StackSlot Dependent) Get(long id) { lock (_gate) return _entries.TryGetValue(id, out var pair) ? pair : (StackSlot.Null, StackSlot.Null); }
    internal void SetDependent(long id, StackSlot value) { lock (_gate) if (_entries.TryGetValue(id, out var pair)) _entries[id] = (pair.Target, value); }
    internal void ClearTarget(long id) { lock (_gate) if (_entries.ContainsKey(id)) _entries[id] = (StackSlot.Null, StackSlot.Null); }
    internal bool Free(long id) { lock (_gate) { _strong.Remove(id); return _entries.Remove(id); } }
    internal VmObject[] ConditionalRoots(HashSet<VmObject> live) {
        lock (_gate) {
            var roots = new List<VmObject>();
            foreach (var pair in _entries.Values)
                if (pair.Target.ObjectValue is VmObject target && live.Contains(target)) ObjectGraphWalker.CollectFromSlots([pair.Dependent], roots.Add);
            return roots.ToArray();
        }
    }
    internal void Sweep(HashSet<VmObject> live) {
        lock (_gate) foreach (var (id, pair) in _entries.ToArray())
            if (pair.Target.ObjectValue is VmObject target && !live.Contains(target)) _entries[id] = (StackSlot.Null, StackSlot.Null);
    }
}
