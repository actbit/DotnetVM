using DotnetVM.Runtime.Types;
using System.Collections.Concurrent;

namespace DotnetVM.Runtime.Execution;

/// <summary>Bounded IL operand cache, scoped to one loader engine. Keys retain
/// structured type identity; assembly registration/unload invalidates resolution.</summary>
internal sealed class MetadataResolutionCache<T> where T : class {
    private const int Capacity = 4096;
    private readonly object _gate = new();
    private sealed class CacheState(VmAssemblyContext context, long version) {
        internal readonly VmAssemblyContext Context = context;
        internal readonly long Version = version;
        internal readonly ConcurrentDictionary<Key, T> Entries = new(new KeyComparer());
    }
    private CacheState? _state;

    private readonly record struct Key(int Token, VmType[] ClassArgs, VmType[] MethodArgs, bool Variant);

    private sealed class KeyComparer : IEqualityComparer<Key> {
        private static readonly IEqualityComparer<VmType> Types = GuestTaskRuntime.VmTypeIdentityComparer.Instance;

        public bool Equals(Key left, Key right) => left.Token == right.Token && left.Variant == right.Variant &&
            SameTypes(left.ClassArgs, right.ClassArgs) && SameTypes(left.MethodArgs, right.MethodArgs);

        private static bool SameTypes(VmType[] left, VmType[] right) {
            if (ReferenceEquals(left, right)) return true;
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (!Types.Equals(left[i], right[i])) return false;
            return true;
        }

        public int GetHashCode(Key key) {
            var hash = new HashCode();
            hash.Add(key.Token);
            hash.Add(key.Variant);
            hash.Add(key.ClassArgs.Length);
            foreach (var type in key.ClassArgs) hash.Add(Types.GetHashCode(type));
            foreach (var type in key.MethodArgs) hash.Add(Types.GetHashCode(type));
            return hash.ToHashCode();
        }
    }

    internal void Clear() {
        lock (_gate)
            Volatile.Write(ref _state, null);
    }

    internal bool TryGet(VmAssemblyContext? assemblyContext, int token, GenericContext? genericContext,
        bool variant, out T value, out long version) {
        version = assemblyContext?.ResolutionVersion ?? -1;
        value = null!;
        if (assemblyContext is null) return false;
        var key = new Key(token, genericContext?.ClassArgs ?? [], genericContext?.MethodArgs ?? [], variant);
        var state = Volatile.Read(ref _state);
        if (state is null || !ReferenceEquals(state.Context, assemblyContext) || state.Version != version) {
            lock (_gate) {
                state = _state;
                if (state is null || !ReferenceEquals(state.Context, assemblyContext) || state.Version != version) {
                    state = new CacheState(assemblyContext, version);
                    Volatile.Write(ref _state, state);
                }
            }
        }
        // Entries only grow within one epoch. Readers need no monitor; replacing
        // the state drops all old references without mutating their dictionary.
        return state.Entries.TryGetValue(key, out value!);
    }

    internal void Add(VmAssemblyContext? assemblyContext, long version, int token,
        GenericContext? genericContext, bool variant, T value) {
        if (assemblyContext is null) return;
        lock (_gate) {
            var state = _state;
            // Resolution runs outside the lock: it can lazily load dependencies
            // or recurse. Publish only if its starting epoch still applies.
            if (state is null || state.Entries.Count >= Capacity || !ReferenceEquals(state.Context, assemblyContext) ||
                state.Version != version || assemblyContext.ResolutionVersion != version)
                return;
            var classArgs = genericContext?.ClassArgs ?? [];
            var methodArgs = genericContext?.MethodArgs ?? [];
            var snapshot = new Key(token, classArgs.Length == 0 ? [] : (VmType[])classArgs.Clone(),
                methodArgs.Length == 0 ? [] : (VmType[])methodArgs.Clone(), variant);
            state.Entries.TryAdd(snapshot, value);
        }
    }
}
