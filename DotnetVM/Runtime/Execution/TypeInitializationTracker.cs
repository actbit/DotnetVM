using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// 状態遷移だけを短いロックで保護し、guest の .cctor 本体はロックの外で実行する。
/// 初期化中の型へ同じ実行フローから再入した場合だけ待たずに通過し、別スレッドは完了を待つ。
/// 失敗は ExceptionDispatchInfo として保存し、後続の呼出元にも同じ失敗を返す。
/// </summary>
internal sealed class TypeInitializationTracker {
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TypeInitializationKey, Entry> _entries = new();

    public void Ensure(VmType type, Action initializer) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(initializer);

        var entry = _entries.GetOrAdd(TypeInitializationKey.For(type), static _ => new Entry());
        lock (entry.Gate) {
            while (entry.Status == TypeInitializationStatus.Initializing) {
                // CLR の型初期化は同一実行フローからの再入を許す。
                if (entry.OwnerThreadId == Environment.CurrentManagedThreadId)
                    return;
                Monitor.Wait(entry.Gate);
            }

            if (entry.Status == TypeInitializationStatus.Completed)
                return;
            if (entry.Status == TypeInitializationStatus.Failed) {
                entry.Failure!.Throw();
                return;
            }

            entry.Status = TypeInitializationStatus.Initializing;
            entry.OwnerThreadId = Environment.CurrentManagedThreadId;
        }

        try {
            // guest IL 実行中は entry.Gate も VM-wide gate も保持しない。
            initializer();
            lock (entry.Gate) {
                entry.Status = TypeInitializationStatus.Completed;
                entry.OwnerThreadId = 0;
                Monitor.PulseAll(entry.Gate);
            }
        } catch (Exception ex) {
            lock (entry.Gate) {
                entry.Status = TypeInitializationStatus.Failed;
                entry.Failure = ExceptionDispatchInfo.Capture(ex);
                entry.OwnerThreadId = 0;
                Monitor.PulseAll(entry.Gate);
            }
            throw;
        }
    }

    internal TypeInitializationStatus GetStatus(VmType type) {
        if (!_entries.TryGetValue(TypeInitializationKey.For(type), out var entry))
            return TypeInitializationStatus.NotStarted;
        lock (entry.Gate)
            return entry.Status;
    }

    private sealed class Entry {
        public readonly object Gate = new();
        public TypeInitializationStatus Status;
        public int OwnerThreadId;
        public ExceptionDispatchInfo? Failure;
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
            hash.Add(RuntimeHelpers.GetHashCode(_definition));
            hash.Add(_arguments.Length);
            foreach (var argument in _arguments)
                hash.Add(RuntimeHelpers.GetHashCode(argument));
            return hash.ToHashCode();
        }
    }
}
