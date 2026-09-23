using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>型初期化の状態。</summary>
internal enum TypeInitializationStatus : byte {
    NotStarted,
    Initializing,
    Completed,
    Failed,
}

/// <summary>
/// VM 単位の型初期化状態表。
///
/// 状態の遷移だけを短いロックで保護し、guest の .cctor 本体はロックの外で実行する。
/// これにより、ある型の .cctor が別の guest Thread/Task を起動して待機しても、別の型の
/// 初期化が VM-wide gate に阻まれることはない。失敗は状態に保存し、後続の呼出元へ共有する。
/// </summary>
internal sealed class TypeInitializationTracker {
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TypeInitializationKey, Entry> _entries = new();

    public void Ensure(VmType type, Action initializer) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(initializer);

        var key = TypeInitializationKey.For(type);
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        var runInitializer = false;

        lock (entry.Gate) {
            while (entry.Status == TypeInitializationStatus.Initializing) {
                // CLR の型初期化は同一実行フローからの再入を許す。ここで自分自身を
                // 待つと、.cctor 内の静的メンバ参照が自己 deadlock になる。
                if (entry.OwnerThreadId == Environment.CurrentManagedThreadId)
                    return;
                Monitor.Wait(entry.Gate);
            }

            if (entry.Status == TypeInitializationStatus.Completed)
                return;
            if (entry.Status == TypeInitializationStatus.Failed)
                throw entry.Failure!;

            entry.Status = TypeInitializationStatus.Initializing;
            entry.OwnerThreadId = Environment.CurrentManagedThreadId;
            runInitializer = true;
        }

        if (!runInitializer)
            return;

        try {
            // 重要: guest IL を実行している間は entry.Gate も VM-wide gate も保持しない。
            initializer();
            lock (entry.Gate) {
                entry.Status = TypeInitializationStatus.Completed;
                entry.OwnerThreadId = 0;
                Monitor.PulseAll(entry.Gate);
            }
        } catch (Exception ex) {
            lock (entry.Gate) {
                entry.Status = TypeInitializationStatus.Failed;
                entry.Failure = ex;
                entry.OwnerThreadId = 0;
                Monitor.PulseAll(entry.Gate);
            }
            throw;
        }
    }

    internal TypeInitializationStatus GetStatus(VmType type) {
        var key = TypeInitializationKey.For(type);
        return _entries.TryGetValue(key, out var entry)
            ? entry.Status
            : TypeInitializationStatus.NotStarted;
    }

    private sealed class Entry {
        public readonly object Gate = new();
        public TypeInitializationStatus Status;
        public int OwnerThreadId;
        public Exception? Failure;
    }

    /// <summary>非構築型は定義参照、構築型は定義参照 + 型引数参照列で識別する。</summary>
    private readonly struct TypeInitializationKey : IEquatable<TypeInitializationKey> {
        private readonly VmType _definition;
        private readonly VmType[] _arguments;

        private TypeInitializationKey(VmType definition, VmType[] arguments) {
            _definition = definition;
            _arguments = arguments;
        }

        public static TypeInitializationKey For(VmType type) => type switch {
            VmConstructedType constructed => new(constructed.Definition, [.. constructed.TypeArguments]),
            _ => new(type, []),
        };

        public bool Equals(TypeInitializationKey other) {
            if (!ReferenceEquals(_definition, other._definition) || _arguments.Length != other._arguments.Length)
                return false;
            for (var i = 0; i < _arguments.Length; i++)
                if (!ReferenceEquals(_arguments[i], other._arguments[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is TypeInitializationKey other && Equals(other);

        public override int GetHashCode() {
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_definition));
            hash.Add(_arguments.Length);
            foreach (var argument in _arguments)
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(argument));
            return hash.ToHashCode();
        }
    }
}
