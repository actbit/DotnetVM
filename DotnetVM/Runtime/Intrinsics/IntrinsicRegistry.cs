using System.Runtime.CompilerServices;
using DotnetVM.Devices;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

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
    private sealed class InvocationMetadata {
        public string[] Parameters = [];
        public string[] MethodArguments = [];
        public string[] ClassArguments = [];
        public VmType[] MethodTypes = [];
        public VmType[] ClassTypes = [];
    }
    private readonly ThreadLocal<InvocationMetadata> _metadata = new(() => new InvocationMetadata());
    private readonly object _arrayTypeGate = new();

    /// <summary>仮想コンソールデバイス (ホスト物理 I/O ではなくここへ出る)。</summary>
    public required VmConsole Console { get; init; }

    /// <summary>VM の文字列プール (文字列生成は必ずここ経由。アロケーション計上済み)。</summary>
    public required VmStringPool Strings { get; init; }

    /// <summary>VM ヒープ (intrinsic がオブジェクトを作る場合は必ずここ経由で計上される)。</summary>
    public required VmHeap Heap { get; init; }

    /// <summary>型解決面 (ファサード型の取得等)。ゲスト型のロードには使わない。</summary>
    public required TypeLoader Types { get; init; }

    /// <summary>ネットワークゲートウェイ (ポリシー検証済みの通信のみ可。null = 全拒否)。</summary>
    public NetworkGateway? Network { get; init; }

    /// <summary>ストレージゲートウェイ (ポリシー検証済みの I/O のみ可。null = 全拒否)。</summary>
    public StorageGateway? Storage { get; init; }

    /// <summary>VM ごとの共有状態 (仮想環境変数ストア / last system error)。
    /// static 共有にしない (VM ごとに分離する)。</summary>
    public required DotnetVM.Runtime.Execution.VmSharedState Shared { get; init; }

    /// <summary>ゲスト Assembly.Load(byte[]) の内容を VM ローダーへ登録するフック。</summary>
    internal Func<ReadOnlyMemory<byte>, TypeLoader>? LoadAssemblyFromBytes { get; init; }

    /// <summary>ゲスト Assembly.Load(byte[]) の PE 入力上限に使うポリシー。</summary>
    internal MemoryPolicy? MemoryPolicy { get; init; }

    /// <summary>ゲスト AssemblyLoadContext.Default の VM ハンドル。</summary>
    internal VmAssemblyLoadContext? DefaultAssemblyLoadContext { get; init; }

    /// <summary>ゲストが名前付き AssemblyLoadContext を生成するフック。</summary>
    internal Func<string?, bool, VmAssemblyLoadContext>? CreateAssemblyLoadContext { get; init; }

    /// <summary>指定 AssemblyLoadContext に byte[] を登録するフック。</summary>
    internal Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? LoadAssemblyInContext { get; init; }

    /// <summary>指定 AssemblyLoadContext にストレージ経由でパスを登録するフック。</summary>
    internal Func<VmAssemblyLoadContext, string, TypeLoader>? LoadAssemblyFromPath { get; init; }

    /// <summary>
    /// 呼出ゲート (Interpreter.Call) が設定する「今回の呼出の宣言上のパラメータ型名」。
    /// i4 スロットに統合される char / bool / int 等のオーバーロードを intrinsic 側で
    /// 判別するために使う (例: Console.Write(char) と Console.Write(int) は同一キー)。
    /// </summary>
    public string[] ParameterTypeNames { get => _metadata.Value!.Parameters; internal set => _metadata.Value!.Parameters = value; }

    /// <summary>呼出ゲート (CallEngine.TryInvokeBinding) が設定する「今回の呼出のメソッド型実引数名」。
    /// ジェネリック メソッドのバインド (IsReferenceOrContainsReferences&lt;T&gt;() 等の値パラメータ
    /// 0 個の面) で T を判別するために使う。</summary>
    public string[] MethodTypeArgumentNames { get => _metadata.Value!.MethodArguments; internal set => _metadata.Value!.MethodArguments = value; }

    /// <summary>呼出ゲートが設定する「今回の呼出のクラス型実引数名」 (構築型の !0 等)。
    /// EqualityComparer&lt;T&gt;.get_Default 等の値パラメータ 0 個のクラスジェネリック面で
    /// T を判別するために使う (MethodSpec の !!n とは別軸)。</summary>
    public string[] ClassTypeArgumentNames { get => _metadata.Value!.ClassArguments; internal set => _metadata.Value!.ClassArguments = value; }

    internal VmType[] MethodTypeArguments { get => _metadata.Value!.MethodTypes; set => _metadata.Value!.MethodTypes = value; }
    internal VmType[] ClassTypeArguments { get => _metadata.Value!.ClassTypes; set => _metadata.Value!.ClassTypes = value; }

    /// <summary>クラス型実引数のインデックス名 (範囲外は空文字列)。</summary>
    public string ClassTypeArgAt(int i) =>
        i >= 0 && i < ClassTypeArgumentNames.Length ? ClassTypeArgumentNames[i] : "";

    /// <summary>メソッド型実引数のインデックス名 (範囲外は空文字列)。</summary>
    public string MethodTypeArgAt(int i) =>
        i >= 0 && i < MethodTypeArgumentNames.Length ? MethodTypeArgumentNames[i] : "";

    /// <summary>インデックスのパラメータ型名 (範囲外は空文字列)。</summary>
    public string ParamAt(int i) =>
        i >= 0 && i < ParameterTypeNames.Length ? ParameterTypeNames[i] : "";

    /// <summary>ゲストオブジェクトの ToString 仮想呼出 (Interpreter が設定するフック)。
    /// Console.Write(object) / String.Concat(object) 等、 CLR で暗黙に ToString が走る面で使う。
    /// ゲスト実装がなければ null (intrinsic 側の既定書式にフォールバック)。</summary>
    internal Func<StackSlot, VmString?>? ToStringHook { get; set; }

    /// <summary>MethodBase.GetCurrentMethod() 用の現在メソッド取得フック (Interpreter が設定)。</summary>
    internal Func<VmMethod?>? CurrentMethodHook { get; set; }

    /// <summary>Guest Thread が delegate を VM の呼出ゲート経由で実行するためのフック。</summary>
    internal Action<VmDelegate, StackSlot, bool>? RunGuestThreadDelegate { get; set; }

    internal Func<VmDelegate, StackSlot[], StackSlot?>? InvokeGuestDelegate { get; set; }

    internal Action<StackSlot>? RunGuestStateMachine { get; set; }

    /// <summary>Blocking host waits release VM execution leases so other guest threads can run.</summary>
    internal Action<Action>? SuspendExecution { get; set; }

    /// <summary>Temporarily roots intrinsic arguments while the guest execution lease is yielded.</summary>
    internal Func<StackSlot[], IDisposable>? RegisterTransientRoots { get; set; }

    /// <summary>インスタンス生成フック (Activator.CreateInstance 等が .ctor を実行する用)。
    /// 定義型 + 実行する .ctor + 引数 + ジェネリック文脈を受け、確保＋初期化＋.ctor 実行済みの
    /// インスタンスを返す。ゲートは呼出側 intrinsic が既に通過済みで、.ctor 本体は
    /// Interpreter.Invoke 経由 (クォータ/再帰深度の対象) で実行される。</summary>
    internal Func<VmType, VmMethod, StackSlot[], DotnetVM.Runtime.Types.GenericContext?, DotnetVM.Runtime.Objects.VmClassInstance>? NewInstanceHook { get; set; }

    /// <summary>オブジェクトの CLR 互換文字列化 (ゲストの ToString override を仮想ディスパッチ)。</summary>
    public VmString? InvokeToString(in StackSlot slot) =>
        ToStringHook is { } hook ? hook(slot) : null;

    /// <summary>文字列を VM オブジェクトに正規化して作る。</summary>
    public VmString MakeString(string value) => Strings.GetOrNew(value);

    private VmArrayType? _byteArrayType;
    private VmArrayType? _stringArrayType;
    private VmArrayType? _objectArrayType;

    private VmArrayType ArrayTypeOf(string intrinsicTypeFullName, ref VmArrayType? cache) {
        lock (_arrayTypeGate)
            return cache ??= new VmArrayType { ElementType = Types.FindIntrinsicType(intrinsicTypeFullName)
                ?? throw new InvalidOperationException($"ファサード型 {intrinsicTypeFullName} が未登録です。") };
    }

    /// <summary>byte 配列を VM オブジェクト (VmArray) に正規化して作る (ヒープ計上済み)。</summary>
    public VmArray MakeByteArray(ReadOnlySpan<byte> data) {
        lock (_arrayTypeGate)
            _byteArrayType ??= new VmArrayType { ElementType = Types.FindIntrinsicType("System.Byte")! };
        var elements = new StackSlot[data.Length];
        for (var i = 0; i < data.Length; i++)
            elements[i] = StackSlot.OfInt32(data[i]);
        return Heap.Allocate(new VmArray(_byteArrayType, elements));
    }

    /// <summary>string 配列を VM オブジェクトに正規化して作る (String.Split 等の戻り値用。ヒープ計上済み)。</summary>
    public VmArray MakeStringArray(IReadOnlyList<string> values) {
        var arrayType = ArrayTypeOf("System.String", ref _stringArrayType);
        var elements = new StackSlot[values.Count];
        for (var i = 0; i < values.Count; i++)
            elements[i] = StackSlot.OfObject(MakeString(values[i]));
        return Heap.Allocate(new VmArray(arrayType, elements));
    }

    /// <summary>object 配列 (params object[]) を VM オブジェクトに正規化して作る (ヒープ計上済み)。</summary>
    public VmArray MakeObjectArray(IReadOnlyList<StackSlot> values) {
        var arrayType = ArrayTypeOf("System.Object", ref _objectArrayType);
        return Heap.Allocate(new VmArray(arrayType, values.ToArray()));
    }

    /// <summary>VM オブジェクト (VmArray) を byte 配列として読み取る。</summary>
    public byte[] ReadByteArray(in StackSlot slot) {
        var array = RequireByteArray(slot);
        var data = new byte[array.Length];
        for (var i = 0; i < array.Length; i++) {
            var element = array.Elements[i];
            if (element.Kind != StackKind.Int32 || element.Int64Value is < 0 or > 255)
                throw new InvalidOperationException($"byte 配列の要素 {i} が不正です (Kind={element.Kind})。");
            data[i] = (byte)element.Int64Value;
        }
        return data;
    }

    /// <summary>byte[] の長さをコピーせずに取得する。</summary>
    public int GetByteArrayLength(in StackSlot slot) => RequireByteArray(slot).Length;

    private static VmArray RequireByteArray(in StackSlot slot) =>
        slot.Kind == StackKind.Object && slot.ObjectValue is VmArray array
            ? array
            : throw new InvalidOperationException($"byte[] を期待しましたが {slot.Kind} が来ました。");

    /// <summary>オブジェクト同一性ハッシュ (Object.GetHashCode 相当)。生存中は対象を弱参照で保持する。</summary>
    public int IdentityHash(object? value) {
        if (value is null)
            return 0;
        var box = IdentityHashes.GetOrCreateValue(value);
        lock (box) {
            if (!box.Assigned) {
                box.Value = System.HashCode.Combine(RuntimeHelpers.GetHashCode(value));
                box.Assigned = true;
            }
            return box.Value;
        }
    }

    private static readonly ConditionalWeakTable<VmClassInstance, StrongBox<VmString?>> ExceptionMessages = [];

    /// <summary>
    /// Exception 派生のゲストクラスのインスタンスにメッセージを記録する
    /// (Exception::.ctor(string) intrinsic から呼ばれる。生存中のみ弱参照で保持)。
    /// </summary>
    public static void SetExceptionMessage(VmClassInstance instance, VmString? message) {
        var box = ExceptionMessages.GetOrCreateValue(instance);
        lock (box)
            box.Value = message;
    }

    /// <summary>
    /// Exception 派生のゲストクラスのインスタンスからメッセージを取り出す
    /// (get_Message intrinsic 用)。未記録なら null。
    /// </summary>
    public static VmString? GetExceptionMessage(VmClassInstance instance) {
        if (!ExceptionMessages.TryGetValue(instance, out var box))
            return null;
        lock (box)
            return box.Value;
    }

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
    private readonly Dictionary<BindingKey, (IntrinsicImpl Impl, BindingOrigin Origin)> _bindings = [];
    private readonly Dictionary<(string TypeFullName, string FieldName), Func<IntrinsicContext, StackSlot>> _staticFields = [];
    private bool _sealed;

    /// <summary>intrinsic を登録する (起動時のみ。Seal 後は拒否)。重複登録は例外。</summary>
    public void Register(IntrinsicKey key, IntrinsicImpl impl) {
        if (_sealed)
            throw new OperationNotAllowedException("VM 実行開始後の intrinsic 登録は許可されていません。");
        if (!_impls.TryAdd(key, impl))
            throw new InvalidOperationException($"intrinsic {key.TypeFullName}::{key.MethodName} (arity {key.Arity}) は既に登録されています。");
    }

    /// <summary>
    /// ランタイムバインドを署名キー + 由来付きで登録する (起動時のみ。Seal 後は拒否)。重複登録は例外。
    /// 実装デリゲート形状は IntrinsicImpl と同じで、呼出は必ず Interpreter の intrinsic 呼出ゲート
    /// (命令クォータ消費・セーフポイント・VM オブジェクトモデル正規化) を経由する。
    /// </summary>
    public void RegisterBinding(BindingKey key, IntrinsicImpl impl, BindingOrigin origin) {
        if (_sealed)
            throw new OperationNotAllowedException("VM 実行開始後のランタイムバインド登録は許可されていません。");
        if (!_bindings.TryAdd(key, (impl, origin)))
            throw new InvalidOperationException($"ランタイムバインド {key} は既に登録されています。");
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

    /// <summary>
    /// 署名キーでランタイムバインドを解決する (完全一致 → 全引数一致面の順)。由来も返す。
    /// 呼出 domain の制約: 完全一致キーの domain が TrustedCoreLib で callerDomain が
    /// TrustedCoreLib でない (trusted CoreLib 画像の IL 外からの呼出) 場合は照合しない
    /// (特権面の呼出元限定、タスク 2 hardening)。
    /// </summary>
    public bool TryGetBinding(BindingKey key, out IntrinsicImpl impl, out BindingOrigin origin,
        BindingDomain callerDomain = BindingDomain.Guest) {
        // 完全一致 (domain も含めて照合)
        if (_bindings.TryGetValue(key, out var entry) &&
            (key.Domain != BindingDomain.TrustedCoreLib || callerDomain == BindingDomain.TrustedCoreLib ||
             key.Domain == callerDomain)) {
            impl = entry.Impl;
            origin = entry.Origin;
            return true;
        }
        // 型名を実行時に確定できる面でも、宣言引数個数だけを固定した登録を許可する。
        // AnyParams (= 引数個数も無制限) とは別キーなので、監査上の無制限 wildcard にはならない。
        if (!key.IsAnyParams && _bindings.TryGetValue(key.WithAnyParamTypes(), out entry) &&
            key.Domain != BindingDomain.TrustedCoreLib) {
            impl = entry.Impl;
            origin = entry.Origin;
            return true;
        }
        // 全引数一致面 (AnyParams) へのフォールバック: 同一 domain の面のみ照合する
        if (!key.IsAnyParams && _bindings.TryGetValue(key.WithAnyParams(), out entry) &&
            key.Domain != BindingDomain.TrustedCoreLib) {
            impl = entry.Impl;
            origin = entry.Origin;
            return true;
        }
        impl = null!;
        origin = default;
        return false;
    }

    /// <summary>登録済みバインドの監査面 (キーと由来の列挙。監査テスト / デバッグ用)。
    /// domain は BindingKey 側に保持されるためここでは origin のみを返す。</summary>
    public IReadOnlyList<(BindingKey Key, BindingOrigin Origin)> Bindings =>
        [.. _bindings.Select(kv => (kv.Key, kv.Value.Origin))];

    /// <summary>intrinsic 型の静的フィールド値を取得する (ldsfld の TypeRef 親用)。</summary>
    public bool TryGetStaticField(string typeFullName, string fieldName, out Func<IntrinsicContext, StackSlot> value) =>
        _staticFields.TryGetValue((typeFullName, fieldName), out value!);

    public int Count => _impls.Count;

    public IEnumerable<IntrinsicKey> Keys => _impls.Keys;
}
