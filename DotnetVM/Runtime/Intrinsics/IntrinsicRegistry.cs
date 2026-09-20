using System.Runtime.CompilerServices;
using DotnetVM.Devices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics;

/// <summary>
/// intrinsic の呼び出しキー。(型フルネーム, メソッド名, 引数個数, this 有無) で一意。
/// Arity はインスタンスメソッドの場合 this を含む。
/// </summary>
public readonly record struct IntrinsicKey(string TypeFullName, string MethodName, int Arity, bool HasThis) {
    public static IntrinsicKey Static(string type, string method, int paramCount) =>
        new(type, method, paramCount, HasThis: false);
    public static IntrinsicKey Instance(string type, string method, int paramCount) =>
        new(type, method, paramCount + 1, HasThis: true); // this 分
}

/// <summary>
/// intrinsic 実装に渡される実行コンテキスト。ゲートを通った後でのみ得られる。
/// intrinsic 実装はこの経由でしか VM 状態 (コンソール/文字列プール) に触れない。
/// </summary>
public sealed class IntrinsicContext {
    private static readonly ConditionalWeakTable<object, Box> IdentityHashes = [];

    /// <summary>仮想コンソールデバイス (ホスト物理 I/O ではなくここへ出る)。</summary>
    public required VmConsole Console { get; init; }

    /// <summary>VM の文字列プール (文字列生成は必ずここ経由)。</summary>
    public required VmStringPool Strings { get; init; }

    /// <summary>文字列を VM オブジェクトに正規化して作る。</summary>
    public VmString MakeString(string value) => Strings.GetOrNew(value);

    /// <summary>オブジェクト同一性ハッシュ (Object.GetHashCode 相当)。生存中は対象を弱参照で保持する。</summary>
    public int IdentityHash(object? value) {
        if (value is null)
            return 0;
        var box = IdentityHashes.GetOrCreateValue(value);
        if (!box.Assigned) {
            box.Value = System.HashCode.Combine(RuntimeHelpers.GetHashCode(value));
            box.Assigned = true;
        }
        return box.Value;
    }

    private static readonly ConditionalWeakTable<VmClassInstance, StrongBox<VmString?>> ExceptionMessages = [];

    /// <summary>
    /// Exception 派生のゲストクラスのインスタンスにメッセージを記録する
    /// (Exception::.ctor(string) intrinsic から呼ばれる。生存中のみ弱参照で保持)。
    /// </summary>
    public static void SetExceptionMessage(VmClassInstance instance, VmString? message) {
        var box = ExceptionMessages.GetOrCreateValue(instance);
        box.Value = message;
    }

    /// <summary>
    /// Exception 派生のゲストクラスのインスタンスからメッセージを取り出す
    /// (get_Message intrinsic 用)。未記録なら null。
    /// </summary>
    public static VmString? GetExceptionMessage(VmClassInstance instance) =>
        ExceptionMessages.TryGetValue(instance, out var box) ? box.Value : null;

    private sealed class StrongBox<T> {
        public T? Value;
    }

    private sealed class Box {
        public int Value;
        public bool Assigned;
    }
}

/// <summary>
/// intrinsic 実装デリゲート。戻り値 null は void 返却を意味する。
/// 値は必ず StackSlot (VM オブジェクトモデル) に正規化して返す。
/// </summary>
public delegate StackSlot? IntrinsicImpl(IntrinsicContext context, StackSlot[] args);

/// <summary>
/// intrinsic レジストリ。(型, メソッド, arity) → 実装の対応表。
/// 登録は VM 起動時のみ許可し、Seal 呼出後は OperationNotAllowedException。
/// 実行中の intrinsic 呼出は必ず Interpreter の InvokeIntrinsic ゲートを経由する
/// (命令クォータ消費・セーフポイント・VM オブジェクトモデル正規化はゲート側で強制)。
/// </summary>
public sealed class IntrinsicRegistry {
    private readonly Dictionary<IntrinsicKey, IntrinsicImpl> _impls = [];
    private readonly Dictionary<(string TypeFullName, string FieldName), Func<IntrinsicContext, StackSlot>> _staticFields = [];
    private bool _sealed;

    /// <summary>intrinsic を登録する (起動時のみ。Seal 後は拒否)。重複登録は例外。</summary>
    public void Register(IntrinsicKey key, IntrinsicImpl impl) {
        if (_sealed)
            throw new OperationNotAllowedException("VM 実行開始後の intrinsic 登録は許可されていません。");
        if (!_impls.TryAdd(key, impl))
            throw new InvalidOperationException($"intrinsic {key.TypeFullName}::{key.MethodName} (arity {key.Arity}) は既に登録されています。");
    }

    /// <summary>intrinsic 型の静的フィールド値を登録する (例: String.Empty)。遅延評価 (実行時に文字列プールから実体化等)。起動時のみ。</summary>
    public void RegisterStaticField(string typeFullName, string fieldName, Func<IntrinsicContext, StackSlot> value) {
        if (_sealed)
            throw new OperationNotAllowedException("VM 実行開始後の intrinsic 静的フィールド登録は許可されていません。");
        if (!_staticFields.TryAdd((typeFullName, fieldName), value))
            throw new InvalidOperationException($"静的フィールド {typeFullName}::{fieldName} は既に登録されています。");
    }

    /// <summary>実行開始前に呼ぶ。以降の登録を拒否する。</summary>
    public void Seal() => _sealed = true;

    public bool IsSealed => _sealed;

    public bool TryGet(IntrinsicKey key, out IntrinsicImpl impl) =>
        _impls.TryGetValue(key, out impl!);

    /// <summary>intrinsic 型の静的フィールド値を取得する (ldsfld の TypeRef 親用)。</summary>
    public bool TryGetStaticField(string typeFullName, string fieldName, out Func<IntrinsicContext, StackSlot> value) =>
        _staticFields.TryGetValue((typeFullName, fieldName), out value!);

    public int Count => _impls.Count;

    public IEnumerable<IntrinsicKey> Keys => _impls.Keys;
}
