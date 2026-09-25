using System.Runtime.CompilerServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Objects;

/// <summary>VM ヒープ上のオブジェクトの基底。世代別 GC 拡張用の Generation を初段から保持する。</summary>
public abstract class VmObject {
    /// <summary>GC 世代 (0 = 新世代)。世代別戦略 (M6 以降) で利用。</summary>
    public byte Generation { get; internal set; }

    public abstract VmType Type { get; }
}

/// <summary>クラスのインスタンス。フィールドは宣言順 (基底型フィールドが先頭) のスロット配列。</summary>
public sealed class VmClassInstance : VmObject {
    private readonly VmClassType _classType;
    public readonly StackSlot[] Fields;
    /// <summary>AssemblyLoadContext 派生ゲストクラスの base .ctor が接続する VM ハンドル。</summary>
    internal VmAssemblyLoadContext? AssemblyLoadContextHandle { get; set; }

    public VmClassInstance(VmClassType classType, StackSlot[] fields, VmType[]? typeArguments = null) {
        _classType = classType;
        Fields = fields;
        TypeArguments = typeArguments ?? [];
    }

    public override VmType Type => _classType;
    public VmClassType ClassType => _classType;

    /// <summary>ジェネリック型の実引数 (非ジェネリック型は空)。実行時型の構築型を再構成するのに使う。</summary>
    public VmType[] TypeArguments { get; }

    /// <summary>実行時型 (ジェネリック型なら構築型、それ以外は ClassType)。</summary>
    public VmType RuntimeType =>
        TypeArguments.Length > 0
            ? new VmConstructedType { Definition = _classType, TypeArguments = TypeArguments }
            : _classType;
}

/// <summary>ボックス化された値 (ヒープオブジェクト)。Fields[0] に値を保持 (構造体は展開済みフィールド列)。</summary>
public sealed class VmBoxedValue : VmObject {
    private readonly VmType _valueType;
    public readonly StackSlot[] Fields;

    public VmBoxedValue(VmType valueType, StackSlot[] fields) {
        _valueType = valueType;
        Fields = fields;
    }

    public override VmType Type => _valueType;
}

/// <summary>
/// intrinsic ファサード型のインスタンス (例: System.Net.WebClient)。例外ファサード以外で
/// .ctor intrinsic が登録された型の実体。状態は State スロット配列に保持し、
/// instance メソッドの intrinsic は this (= args[0]) 経由で読み書きする。
/// </summary>
public sealed class VmIntrinsicInstance : VmObject {
    public VmIntrinsicInstance(VmIntrinsicType instanceType, int stateSlots = 4, VmType[]? typeArguments = null) {
        ArgumentNullException.ThrowIfNull(instanceType);
        InstanceType = instanceType;
        State = new StackSlot[stateSlots];
        TypeArguments = typeArguments ?? [];
    }

    public VmIntrinsicType InstanceType { get; }
    public readonly StackSlot[] State;
    public VmType[] TypeArguments { get; }
    public VmType RuntimeType => TypeArguments.Length == 0
        ? InstanceType
        : new VmConstructedType { Definition = InstanceType, TypeArguments = TypeArguments };

    public override VmType Type => InstanceType;
}

/// <summary>ゲスト Task / Task&lt;T&gt;。完了値と例外は VM スロットで保持し、待機はイベントで行う。</summary>
public sealed class VmTaskObject : VmObject {
    private readonly VmType _type;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _completed = new(false);
    private StackSlot _result;
    private StackSlot _guestException;
    private Exception? _hostException;
    private bool _isCanceled;
    private bool _isCompleted;
    private List<Action>? _completionCallbacks;

    public VmTaskObject(VmType type) => _type = type;

    public override VmType Type => _type;
    public bool IsCompleted { get { lock (_gate) return _isCompleted; } }

    public void SetResult(StackSlot result = default) {
        Action[] callbacks;
        lock (_gate) {
            if (_isCompleted) return;
            _result = result;
            _isCompleted = true;
            _completed.Set();
            callbacks = TakeCompletionCallbacks();
        }
        InvokeCompletionCallbacks(callbacks);
    }

    public void SetGuestException(StackSlot exception) {
        Action[] callbacks;
        lock (_gate) {
            if (_isCompleted) return;
            _guestException = exception;
            _isCompleted = true;
            _completed.Set();
            callbacks = TakeCompletionCallbacks();
        }
        InvokeCompletionCallbacks(callbacks);
    }

    public void SetHostException(Exception exception) {
        Action[] callbacks;
        lock (_gate) {
            if (_isCompleted) return;
            _hostException = exception;
            _isCompleted = true;
            _completed.Set();
            callbacks = TakeCompletionCallbacks();
        }
        InvokeCompletionCallbacks(callbacks);
    }

    public void SetCanceled() {
        Action[] callbacks;
        lock (_gate) {
            if (_isCompleted) return;
            _isCanceled = true;
            _isCompleted = true;
            _completed.Set();
            callbacks = TakeCompletionCallbacks();
        }
        InvokeCompletionCallbacks(callbacks);
    }

    public void Wait() => _completed.Wait();

    public bool Wait(int millisecondsTimeout) => _completed.Wait(millisecondsTimeout);

    internal WaitHandle CompletionWaitHandle => _completed.WaitHandle;

    public bool IsCanceled { get { lock (_gate) return _isCanceled; } }

    internal void Wait(CancellationToken cancellationToken) => _completed.Wait(cancellationToken);

    /// <summary>
    /// WaitAll が Task ごとに順番に待つための待機 primitive。CompletionWaitHandle を取得せず、
    /// 大量の入力 Task に対して ManualResetEventSlim の kernel handle を実体化しない。
    /// </summary>
    internal bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) =>
        _completed.Wait(millisecondsTimeout, cancellationToken);

    public (StackSlot Result, StackSlot GuestException, Exception? HostException) Snapshot() {
        lock (_gate)
            return (_result, _guestException, _hostException);
    }

    /// <summary>
    /// Registers a callback which is invoked exactly once when this task completes.  Unlike a
    /// WaitHandle this scales to an arbitrary number of tasks and does not consume an OS wait
    /// handle slot.  The callback is invoked synchronously when the task is already complete,
    /// matching Task continuation registration's eager-completion behavior.
    /// </summary>
    internal IDisposable RegisterCompletion(Action callback) {
        ArgumentNullException.ThrowIfNull(callback);
        var invokeNow = false;
        lock (_gate) {
            if (_isCompleted)
                invokeNow = true;
            else
                (_completionCallbacks ??= []).Add(callback);
        }
        if (invokeNow) {
            callback();
            return EmptyCompletionRegistration.Instance;
        }
        return new CompletionRegistration(this, callback);
    }

    private Action[] TakeCompletionCallbacks() {
        if (_completionCallbacks is not { Count: > 0 } callbacks)
            return [];
        _completionCallbacks = null;
        return [.. callbacks];
    }

    private static void InvokeCompletionCallbacks(Action[] callbacks) {
        foreach (var callback in callbacks) {
            try { callback(); }
            catch { /* completion observers must not break task publication */ }
        }
    }

    private void RemoveCompletionCallback(Action callback) {
        lock (_gate)
            _completionCallbacks?.Remove(callback);
    }

    private sealed class CompletionRegistration(VmTaskObject owner, Action callback) : IDisposable {
        private VmTaskObject? _owner = owner;
        private readonly Action _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveCompletionCallback(_callback);
    }

    private sealed class EmptyCompletionRegistration : IDisposable {
        public static readonly EmptyCompletionRegistration Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// VM 内の CancellationTokenSource。CLR の token を guest に露出せず、VM ヒープ上の
/// source identity と host cancellation primitive の組だけを保持する。
/// </summary>
public sealed class VmCancellationState : VmObject {
    private readonly VmType _type;
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationToken _token;
    private int _disposed;

    /// <summary>GuestTaskRuntime removes any CancelAfter timer through this callback.</summary>
    internal Action<VmCancellationState>? DisposeTimer { get; set; }
    /// <summary>When the state belongs to a VM, CancelAfter is routed through its quota owner.</summary>
    internal Action<VmCancellationState, int>? ScheduleTimer { get; set; }

    public VmCancellationState(VmType type) {
        _type = type;
        _token = _source.Token;
    }
    public override VmType Type => _type;
    /// <summary>The token remains usable after the source itself has been disposed.</summary>
    public CancellationToken Token => _token;
    internal CancellationToken SourceToken {
        get {
            ThrowIfDisposed();
            return _token;
        }
    }
    public bool IsCancellationRequested => _source.IsCancellationRequested;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public void Cancel() {
        ThrowIfDisposed();
        try {
            _source.Cancel();
        } finally {
            // CancelAfter の timer は cancellation callback の実行中も quota slot と root を
            // 保持し続けるため、callback が例外を返す場合を含めて必ず解除する。
            DisposeTimer?.Invoke(this);
        }
    }
    public void CancelAfter(int millisecondsDelay) {
        ThrowIfDisposed();
        if (millisecondsDelay < Timeout.Infinite)
            throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
        // VM-owned states use the quota-tracked timer. Standalone states retain the public
        // object API's original host-CTS behavior for compatibility.
        if (ScheduleTimer is { } schedule)
            schedule(this, millisecondsDelay);
        else
            _source.CancelAfter(millisecondsDelay);
    }
    internal void CancelFromTimer() {
        if (Volatile.Read(ref _disposed) == 0)
            _source.Cancel();
    }
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        DisposeTimer?.Invoke(this);
        _source.Dispose();
    }

    private void ThrowIfDisposed() {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CancellationTokenSource));
    }
}

/// <summary>VM SynchronizationContext の identity。キュー自体は VM runtime が管理する。</summary>
public sealed class VmSynchronizationContextState : VmObject {
    private readonly VmType _type;
    public VmSynchronizationContextState(VmType type) => _type = type;
    public override VmType Type => _type;
}

/// <summary>
/// 例外ファサード型のインスタンス (VM 内部例外の合成 / `new System.NullReferenceException()` 等)。
/// ゲストクラスが Exception 派生の例外は VmClassInstance として生成され、メッセージは
/// IntrinsicContext の例外メッセージ表で保持する。こちらはファサード型そのものの実体。
/// </summary>
public sealed class VmExceptionObject : VmObject {
    private readonly VmType _exceptionType;

    public VmExceptionObject(VmType exceptionType, VmString? message) {
        _exceptionType = exceptionType;
        Message = message;
    }

    public override VmType Type => _exceptionType;
    public VmType ExceptionType => _exceptionType;
    public VmString? Message { get; internal set; }
}

/// <summary>配列。要素はゼロ初期化済みスロット配列。</summary>
public sealed class VmArray : VmObject {
    private readonly VmArrayType _arrayType;
    public readonly StackSlot[] Elements;

    public VmArray(VmArrayType arrayType, StackSlot[] elements) {
        _arrayType = arrayType;
        Elements = elements;
    }

    public override VmType Type => _arrayType;
    public VmArrayType ArrayType => _arrayType;
    public int Length => Elements.Length;
}

/// <summary>
/// 値型の値 (スタック/ローカル/フィールド上の実体。ヒープ識別性なし、代入時にコピー)。
/// </summary>
public sealed class VmStructValue {
    public VmType StructType { get; }
    public readonly StackSlot[] Fields;

    /// <summary>ジェネリック構造体の実引数 (非ジェネリック型は空)。Clone 時に引き継ぐ。</summary>
    public VmType[] TypeArguments { get; }

    public VmStructValue(VmType structType, StackSlot[] fields, VmType[]? typeArguments = null) {
        StructType = structType;
        Fields = fields;
        TypeArguments = typeArguments ?? [];
    }

    /// <summary>値型コピー意味論: フィールドを深コピーする (ネストした構造体は再帰コピー、参照は共有)。</summary>
    public VmStructValue Clone() {
        var copy = new StackSlot[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
            copy[i] = Fields[i].ObjectValue is VmStructValue nested ? StackSlot.OfValueType(nested.Clone()) : Fields[i];
        return new VmStructValue(StructType, copy, TypeArguments);
    }

    /// <summary>実行時型 (ジェネリック構造体なら構築型、それ以外は StructType)。</summary>
    public VmType RuntimeType =>
        TypeArguments.Length > 0
            ? new VmConstructedType { Definition = StructType, TypeArguments = TypeArguments }
            : StructType;
}

/// <summary>
/// ldtoken Field の結果 (FieldRVA 初期データのハンドル)。System.RuntimeFieldHandle の VM 内表現で、
/// RuntimeHelpers::InitializeArray 専用。ハンドル自体はゲストから観測可能な状態を持たない。
/// </summary>
public sealed class VmFieldRvaData : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeFieldHandle", IsValue = true };

    public required ReadOnlyMemory<byte> Data { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>ldtoken Field の一般形。動的 IL の FieldInfo 参照も VM 内で保持する。</summary>
public sealed class VmFieldHandle : VmObject {
    public static readonly VmIntrinsicType HandleType = VmFieldRvaData.HandleType;
    public required VmField Target { get; init; }
    public override VmType Type => HandleType;
}

/// <summary>
/// ldtoken Type の結果ハンドル (System.RuntimeTypeHandle の VM 内表現)。
/// System.Type::GetTypeFromHandle intrinsic が System.Type ファサードの実体へ変換する。
/// </summary>
public sealed class VmTypeHandle : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeTypeHandle", IsValue = true };

    public required VmType Target { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>
/// ldtoken Method の結果ハンドル (System.RuntimeMethodHandle の VM 内表現)。
/// MethodBase::GetMethodFromHandle / GetCurrentMethod intrinsic が変換する。
/// </summary>
public sealed class VmMethodHandle : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeMethodHandle", IsValue = true };

    public required VmMethod Target { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>
/// typeof(X) / Object.GetType() の結果 (System.Type ファサードの実体)。
/// CLR では内部型 System.RuntimeType のインスタンス。ゲストからは get_Name / get_FullName /
/// ToString / op_Equality intrinsic 面のみ観測できる。Target は VM 型系 (GC 管理外)。
/// </summary>
public sealed class VmRuntimeObject : VmObject {
    public static readonly VmIntrinsicType RuntimeTypeFacade =
        new() { Namespace = "System", Name = "RuntimeType", IsValue = false };

    public required VmType Target { get; init; }

    public override VmType Type => RuntimeTypeFacade;
}

/// <summary>MethodBase.GetCurrentMethod() 等の結果 (System.Reflection.MethodBase ファサードの実体)。</summary>
public sealed class VmRuntimeMethod : VmObject {
    public static readonly VmIntrinsicType MethodBaseFacade =
        new() { Namespace = "System.Reflection", Name = "RuntimeMethodInfo", IsValue = false };

    public required VmMethod Target { get; init; }

    public override VmType Type => MethodBaseFacade;
}

/// <summary>式木や Reflection.Emit から参照される VM の FieldInfo 相当。</summary>
public sealed class VmRuntimeField : VmObject {
    public static readonly VmIntrinsicType FieldInfoFacade =
        new() { Namespace = "System.Reflection", Name = "RuntimeFieldInfo", IsValue = false };

    public required VmField Target { get; init; }

    public override VmType Type => FieldInfoFacade;
}

public sealed class VmRuntimeProperty : VmObject {
    public static readonly VmIntrinsicType PropertyInfoFacade =
        new() { Namespace = "System.Reflection", Name = "RuntimePropertyInfo", IsValue = false };
    public required string Name { get; init; }
    public required VmMethod Getter { get; init; }
    public override VmType Type => PropertyInfoFacade;
}

/// <summary>ゲストの System.Reflection.Assembly を表す VM 側ハンドル。
/// 中身は CLR Assembly ではなく、VM の TypeLoader だけを参照する。</summary>
public sealed class VmAssemblyObject : VmObject {
    public static readonly VmIntrinsicType AssemblyFacade =
        new() { Namespace = "System.Reflection", Name = "Assembly", IsValue = false };

    public required TypeLoader Loader { get; init; }

    public override VmType Type => (VmType?)Loader.FindTypeByFullName("System.Reflection.Assembly") ?? AssemblyFacade;
}

/// <summary>
/// ゲストの System.Runtime.Loader.AssemblyLoadContext を表す VM 側ハンドル。
/// CLR の AssemblyLoadContext は公開せず、VM の AssemblyContext にロードを委譲する。
/// </summary>
public sealed class VmAssemblyLoadContext : VmObject {
    public static readonly VmIntrinsicType LoadContextFacade =
        new() { Namespace = "System.Runtime.Loader", Name = "AssemblyLoadContext", IsValue = false };

    public required VmAssemblyContext Context { get; init; }
    public required string? Name { get; init; }
    public bool IsCollectible { get; init; }
    public bool IsDefault { get; init; }
    public VmType? DeclaredType { get; init; }
    internal Action? UnloadAction { get; init; }
    private readonly object _unloadGate = new();
    private int _unloaded;
    internal bool IsUnloaded => Volatile.Read(ref _unloaded) != 0;
    /// <summary>ロード操作と Unload を直列化する lifetime gate。</summary>
    internal object LifetimeGate => _unloadGate;

    internal void Unload() {
        if (IsDefault)
            throw new InvalidOperationException("AssemblyLoadContext.Default はアンロードできません。");
        if (!IsCollectible)
            throw new InvalidOperationException("collectible ではない AssemblyLoadContext はアンロードできません。");
        lock (_unloadGate) {
            if (Volatile.Read(ref _unloaded) != 0)
                return;
            // これ以降のロードを直ちに拒否し、UnloadAction のキャッシュ掃除と
            // 並行するロードが新しい強参照を作らないようにする。
            Volatile.Write(ref _unloaded, 1);
            UnloadAction?.Invoke();
        }
    }

    public override VmType Type => DeclaredType ?? LoadContextFacade;
}

/// <summary>ゲストの System.Reflection.AssemblyName を表す VM 側ハンドル。</summary>
public sealed class VmAssemblyNameObject : VmObject {
    public static readonly VmIntrinsicType AssemblyNameFacade =
        new() { Namespace = "System.Reflection", Name = "AssemblyName", IsValue = false };

    public string FullName { get; internal set; } = "";
    public VmType? DeclaredType { get; init; }

    /// <summary>AssemblyName.Name 相当の単純名。</summary>
    public string Name {
        get {
            var comma = FullName.IndexOf(',');
            return (comma < 0 ? FullName : FullName[..comma]).Trim();
        }
    }

    public override VmType Type => DeclaredType ?? AssemblyNameFacade;
}

/// <summary>ゲストの MemoryStream を表す小さな VM ストリーム。AssemblyLoadContext.LoadFromStream 用。</summary>
public sealed class VmMemoryStreamObject : VmObject {
    public static readonly VmIntrinsicType MemoryStreamFacade =
        new() { Namespace = "System.IO", Name = "MemoryStream", IsValue = false };
    public static readonly VmIntrinsicType StreamFacade =
        new() { Namespace = "System.IO", Name = "Stream", IsValue = false };

    private byte[] _bytes = [];
    public required byte[] Bytes { get => _bytes; init => _bytes = value; }
    public int Position { get; internal set; }
    public VmType? DeclaredType { get; init; }

    internal void ReplaceBytes(byte[] bytes) => _bytes = bytes;

    public override VmType Type => DeclaredType ?? MemoryStreamFacade;
}

/// <summary>VM 内で評価する式木ノード。Compile は CLR delegate を作らず VmDelegate を返す。</summary>
public enum VmExpressionKind : byte {
    Constant,
    Parameter,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Negate,
    Not,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    And,
    Or,
    ExclusiveOr,
    AndAlso,
    OrElse,
    Convert,
    TypeIs,
    TypeAs,
    Conditional,
    Block,
    Assign,
    NewArray,
    Default,
    Quote,
    Call,
    Invoke,
    New,
    MemberAccess,
    ArrayIndex,
    ArrayLength,
    Lambda,
}

public sealed class VmExpressionObject : VmObject {
    public static readonly VmIntrinsicType ExpressionFacade =
        new() { Namespace = "System.Linq.Expressions", Name = "Expression", IsValue = false };
    public static readonly VmIntrinsicType LambdaExpressionFacade =
        new() { Namespace = "System.Linq.Expressions", Name = "LambdaExpression", IsValue = false, Parent = ExpressionFacade };
    public static readonly VmIntrinsicType ParameterExpressionFacade =
        new() { Namespace = "System.Linq.Expressions", Name = "ParameterExpression", IsValue = false, Parent = ExpressionFacade };
    public static readonly VmIntrinsicType BinaryExpressionFacade =
        new() { Namespace = "System.Linq.Expressions", Name = "BinaryExpression", IsValue = false, Parent = ExpressionFacade };
    public static readonly VmIntrinsicType ConstantExpressionFacade =
        new() { Namespace = "System.Linq.Expressions", Name = "ConstantExpression", IsValue = false, Parent = ExpressionFacade };

    public required VmExpressionKind Kind { get; init; }
    public StackSlot Constant { get; init; }
    public VmType? ResultType { get; init; }
    public VmExpressionObject? Left { get; init; }
    public VmExpressionObject? Right { get; init; }
    public VmExpressionObject? Body { get; init; }
    public VmExpressionObject? Object { get; init; }
    public VmExpressionObject? IfTrue { get; init; }
    public VmExpressionObject? IfFalse { get; init; }
    public VmExpressionObject[] Arguments { get; init; } = [];
    public VmExpressionObject[] Expressions { get; init; } = [];
    public bool NewArrayBounds { get; init; }
    public VmExpressionObject[] Parameters { get; init; } = [];
    public VmType? DelegateType { get; init; }
    public VmMethod? Method { get; init; }
    public VmField? Field { get; init; }
    public VmRuntimeProperty? Property { get; init; }
    public VmType? NewType { get; init; }
    public int ParameterIndex { get; set; }
    public bool IsVariable { get; init; }

    public override VmType Type => Kind switch {
        VmExpressionKind.Lambda => LambdaExpressionFacade,
        VmExpressionKind.Parameter => ParameterExpressionFacade,
        VmExpressionKind.Constant => ConstantExpressionFacade,
        _ => BinaryExpressionFacade,
    };
}

/// <summary>
/// intrinsic が VM スロット上に内部状態 (ホスト側バッファ等) を保持するための搬送体。
/// DefaultInterpolatedStringHandler 等の「構造体ファサードのローカルスロットに実体を置く」
/// intrinsic が使う。ゲストに渡されることはなく、Payload はホストメモリ (GC/クォータ計上外)。
/// </summary>
public sealed class VmIntrinsicCarrier : VmObject {
    public static readonly VmIntrinsicType CarrierType =
        new() { Namespace = "DotnetVM", Name = "IntrinsicCarrier", IsValue = false };

    public required object? Payload { get; set; }

    public override VmType Type => CarrierType;
}

/// <summary>Reflection.Emit の Label/LocalBuilder を VM 側で保持する搬送体。</summary>
public sealed class VmEmitLabel : VmObject {
    public static readonly VmIntrinsicType LabelType =
        new() { Namespace = "System.Reflection.Emit", Name = "Label", IsValue = true };
    public required int Id { get; init; }
    public override VmType Type => LabelType;
}

public sealed class VmLocalBuilder : VmObject {
    public static readonly VmIntrinsicType LocalBuilderType =
        new() { Namespace = "System.Reflection.Emit", Name = "LocalBuilder", IsValue = false };
    public required int Index { get; init; }
    public required VmType LocalType { get; init; }
    public override VmType Type => LocalBuilderType;
}

/// <summary>デリゲートの 1 呼出エントリ (レシーバ + 束縛先メソッド)。</summary>
public readonly record struct DelegateInvocation(StackSlot Target, VmMethod Method);

/// <summary>
/// ldftn / ldvirtftn の結果 (オープンな関数ポインタ)。レシーバは束縛せずメソッドのみ参照する。
/// newobj デリゲート生成時にレシーバと束ねて VmDelegate になる。Target は VM 型系 (GC 管理外)。
/// </summary>
public sealed class VmMethodPointer : VmObject {
    public static readonly VmIntrinsicType PointerType =
        new() { Namespace = "DotnetVM", Name = "MethodPointer", IsValue = false };

    public required VmMethod Target { get; init; }

    public override VmType Type => PointerType;
}

/// <summary>
/// ゲスト デリゲートインスタンス (System.Delegate / MulticastDelegate ファサード型の実体)。
/// マルチキャストは呼出エントリのリストで表現する (CLR と同じく Combine/Remove は新しい
/// インスタンスを返す = 実質イミュータブル)。呼出は Interpreter.InvokeDelegate ゲート経由。
/// </summary>
public sealed class VmDelegate : VmObject {
    private readonly List<DelegateInvocation> _invocations = [];

    /// <summary>デリゲート宣言型 (Func&lt;int&gt; 等の構築型 / ゲストのカスタム delegate 型)。</summary>
    public required VmType DeclaredType { get; init; }

    /// <summary>式木 delegate の場合、呼出リストの代わりに評価する LambdaExpression。</summary>
    public VmExpressionObject? ExpressionLambda { get; init; }

    /// <summary>
    /// VM の callback を必要とする runtime surface (IValueTaskSource / SynchronizationContext 等)
    /// 用の host callback。通常の guest delegate には設定されない。
    /// </summary>
    internal Func<StackSlot[], StackSlot?>? HostCallback { get; init; }

    public IReadOnlyList<DelegateInvocation> Invocations => _invocations;

    public override VmType Type => DeclaredType;

    public void AddInvocation(in DelegateInvocation invocation) => _invocations.Add(invocation);

    /// <summary>呼出リストの複製を作る (デリゲート→デリゲート生成 / Combine の複製元)。</summary>
    public DelegateInvocation[] CopyInvocations() => [.. _invocations];
}

/// <summary>mkrefany の結果 (__makeref / TypedReference の VM 内表現)。</summary>
public sealed class VmTypedReference : VmObject {
    public static readonly VmIntrinsicType TypedRefType =
        new() { Namespace = "System", Name = "TypedReference", IsValue = true };

    /// <summary>参照先スロット (ByRef スロット)。GC 走査は ObjectGraphWalker が Container を展開する。</summary>
    public required StackSlot Slot { get; init; }

    /// <summary>mkrefany で指定された型。</summary>
    public required VmType RefType { get; init; }

    public override VmType Type => TypedRefType;
}

/// <summary>arglist 命令の結果 (varargs の引数リストハンドル)。呼出側の残余引数は保持しない
/// (varargs 呼出自体は fail-closed。ハンドルの一貫性だけを提供する)。</summary>
public sealed class VmArgList : VmObject {
    public static readonly VmIntrinsicType ArgListType =
        new() { Namespace = "DotnetVM", Name = "ArgList", IsValue = false };

    public required StackSlot[] Args { get; init; }

    public override VmType Type => ArgListType;
}

/// <summary>
/// localloc (stackalloc) の仮想メモリブロック。実バイト列を保持し、アロケーションは
/// 実バイト数で計上する (EstimateSize → MemoryPolicy の対象)。ブロック自体は GC 管理。
/// ポインタ演算はブロック内のバイトオフセットとして解決され、ブロック外アクセスは境界検査で
/// 拒否する (実 CLR では未定義動作 = アドレス空間破壊。VM では安全側に置き換える)。
/// </summary>
public sealed class VmLocallocMemory : VmObject {
    public static readonly VmIntrinsicType MemoryType =
        new() { Namespace = "DotnetVM", Name = "LocallocMemory", IsValue = false };

    public required byte[] Bytes { get; init; }

    public override VmType Type => MemoryType;
}

/// <summary>
/// unmanaged ポインタ (int* / byte* 等)。localloc ブロック (VmLocallocMemory) + バイトオフセットを指す。
/// byte[] を直接共有せず VmLocallocMemory を参照して保持する — GC グラフ上でポインタから
/// ブロックが到達可能であることを保証し、参照中ブロックの回収とメモリ会計の消失を防ぐ。
/// ポインタ演算 (p + n / p - q) はオフセット演算、ldind/stind はリトルエンディアンの
/// バイト読み書きとして実現する。実 CLR と異なり初期化は 0 (安全側の上限動作)。
/// </summary>
public sealed class VmNativePointer : VmObject {
    public static readonly VmIntrinsicType PointerType =
        new() { Namespace = "DotnetVM", Name = "NativePointer", IsValue = false };

    public required VmLocallocMemory Memory { get; init; }

    public int ByteOffset { get; init; }

    /// <summary>FieldRVA/readonly span 由来のポインタは書き込み不可。</summary>
    public bool IsReadOnly { get; init; }

    public byte[] Bytes => Memory.Bytes;

    public override VmType Type => PointerType;

    // ---- バイト読み書き (境界検査付き) ----

    private void CheckBounds(int byteCount) {
        if (byteCount < 0)
            throw new InvalidOperationException("ポインタのアクセス幅が負です。");
        if (ByteOffset < 0 || (long)ByteOffset + byteCount > Bytes.Length)
            throw new InvalidOperationException(
                $"unmanaged ポインタがブロック外を参照します (offset={ByteOffset}, 要求 {byteCount} バイト, ブロック {Bytes.Length} バイト)。");
    }

    /// <summary>guest 命令経路用の境界検査。公開した低レベル API の従来例外型は維持しつつ、
    /// ゲストへはホスト例外を漏らさない。</summary>
    public void EnsureBounds(int byteCount) {
        try {
            CheckBounds(byteCount);
        } catch (InvalidOperationException ex) {
            throw new UnhandledGuestException("System.IndexOutOfRangeException", ex.Message);
        }
    }

    public void EnsureWritable() {
        if (IsReadOnly)
            throw new UnhandledGuestException("System.InvalidProgramException",
                "読み取り専用の仮想メモリへ書き込めません。");
    }

    public int ReadInt8() {
        CheckBounds(1);
        return (sbyte)Bytes[ByteOffset];
    }

    public int ReadUInt8() {
        CheckBounds(1);
        return Bytes[ByteOffset];
    }

    public int ReadInt16() {
        CheckBounds(2);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(ByteOffset, 2));
    }

    public int ReadUInt16() {
        CheckBounds(2);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(ByteOffset, 2));
    }

    public int ReadInt32() {
        CheckBounds(4);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(ByteOffset, 4));
    }

    public long ReadInt64() {
        CheckBounds(8);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(Bytes.AsSpan(ByteOffset, 8));
    }

    public double ReadDouble() {
        CheckBounds(8);
        return System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(Bytes.AsSpan(ByteOffset, 8));
    }

    public float ReadSingle() {
        CheckBounds(4);
        return System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(ByteOffset, 4));
    }

    public void WriteInt8(int value) {
        EnsureWritable();
        CheckBounds(1);
        Bytes[ByteOffset] = (byte)value;
    }

    public void WriteInt16(int value) {
        EnsureWritable();
        CheckBounds(2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(ByteOffset, 2), (short)value);
    }

    public void WriteInt32(int value) {
        EnsureWritable();
        CheckBounds(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(ByteOffset, 4), value);
    }

    public void WriteInt64(long value) {
        EnsureWritable();
        CheckBounds(8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(Bytes.AsSpan(ByteOffset, 8), value);
    }

    public void WriteDouble(double value) {
        EnsureWritable();
        CheckBounds(8);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(Bytes.AsSpan(ByteOffset, 8), value);
    }

    public void WriteSingle(float value) {
        EnsureWritable();
        CheckBounds(4);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(ByteOffset, 4), value);
    }
}

/// <summary>
/// オブジェクトモデルの共通処理: インスタンス/静的フィールドのレイアウト (基底型フィールドが先頭)、
/// 型ごとの既定値生成。
/// </summary>
/// <remarks>
/// 互換性上の重要点: このクラスの状態は <strong>VM インスタンスごと</strong>に保持される
/// (かつて static だったため、複数 VM が静的フィールドを共有する分離バグだった)。
/// Interpreter が 1 つ所有する。
/// </remarks>
public sealed class ObjectModel {
    private readonly ConditionalWeakTable<VmType, Dictionary<VmField, int>> Layouts = new();
    private readonly object _gate = new();
    // 静的ストレージは「正準型キー (構築型なら FullName)」で保持する。CLR と同じく
    // 構築ジェネリック型 (C<int> と C<string> 等) は静的フィールドを共有しない。
    private readonly Dictionary<string, StackSlot[]> StaticStorage = [];
    private readonly List<StackSlot[]> StaticStorageList = [];

    /// <summary>インスタンスフィールドのスロット配置 (基底型のフィールドが先頭、同一型内は宣言順)。
    /// 基底が構築ジェネリック型 (例: Sub`1 : Base`1&lt;!0&gt;) の場合は定義型に解いて収集する。</summary>
    public Dictionary<VmField, int> GetLayout(VmClassType type) {
        lock (_gate) {
        if (Layouts.TryGetValue(type, out var cached))
            return cached;
        var layout = new Dictionary<VmField, int>();
        var index = 0;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed) {
                t = constructed.Definition;
                continue;
            }
            if (t is not VmClassType cls)
                break;
            foreach (var field in cls.Fields) {
                if (!field.IsStatic && !field.IsLiteral)
                    layout[field] = index++;
            }
            t = cls.BaseType;
        }
        Layouts.Add(type, layout);
        return layout;
        }
    }

    /// <summary>インスタンスフィールド既定値のストレージを生成する。</summary>
    public StackSlot[] CreateInstanceStorage(VmClassType type, TypeLoader loader) =>
        CreateInstanceStorage(type, loader, null);

    /// <summary>インスタンスフィールド既定値のストレージを生成する (ジェネリック型は型引数でフィールド型を解決)。</summary>
    public StackSlot[] CreateInstanceStorage(VmClassType type, TypeLoader loader, GenericContext? context) {
        var layout = GetLayout(type);
        var fields = new StackSlot[layout.Values.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        return fields;
    }

    /// <summary>静的フィールドのストレージ。world を渡すと VM 単位の共有テーブル
    /// (UnifiedStaticStorage) に「定義 VmType 参照 + 型引数 VmType identity 列」で置く —
    /// 同一 FullName の別アセンブリ型 (identity が別) や同一 FullName でも別 identity の
    /// 型引数を持つ構築型ではストレージを共有しない。FullName 文字列はキーに使わない。
    /// ローカル (world null) 経路は従来どおり画像内辞書 (FullName) を使う
    /// (画像内では FullName が一意のため)。</summary>
    public StackSlot[] GetOrCreateStaticStorage(string storageKey, VmClassType type, TypeLoader loader,
        GenericContext? context = null, UnifiedStaticStorage? world = null, VmType[]? typeArguments = null) {
        if (world is not null)
            return world.GetOrCreate(type, typeArguments, () => BuildStaticStorage(type, loader, context));
        lock (_gate) {
        if (StaticStorage.TryGetValue(storageKey, out var existing))
            return existing;
        var storage = BuildStaticStorage(type, loader, context);
        StaticStorage[storageKey] = storage;
        StaticStorageList.Add(storage);
        return storage;
        }
    }

    private StackSlot[] BuildStaticStorage(VmClassType type, TypeLoader loader, GenericContext? context) {
        var storage = new StackSlot[type.Fields.Count(f => f.IsStatic && !f.IsLiteral)];
        var index = 0;
        foreach (var field in type.Fields)
            if (field.IsStatic && !field.IsLiteral)
                storage[index++] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        return storage;
    }

    /// <summary>生成済みの全静的ストレージを列挙する (GC ルート源)。</summary>
    internal IEnumerable<StackSlot[]> EnumerateStaticStorage() {
        lock (_gate)
            return StaticStorageList.ToArray();
    }

    public static int StaticFieldIndex(VmClassType type, VmField field) {
        var index = 0;
        foreach (var f in type.Fields) {
            if (!f.IsStatic || f.IsLiteral)
                continue;
            if (f == field)
                return index;
            index++;
        }
        throw new InvalidOperationException($"静的フィールド {field} が見つかりません。");
    }

    /// <summary>型に対するゼロ既定値。</summary>
    public StackSlot DefaultForType(VmType? type, TypeLoader loader) => DefaultForType(type, loader, null);

    /// <summary>型に対するゼロ既定値 (ジェネリックパラメータは context の実引数で置換してから判定)。
    /// プリミティブは CoreLib 実型 / intrinsic ファサードのどちらで解決されても単一スロット表現を維持する。</summary>
    public StackSlot DefaultForType(VmType? type, TypeLoader loader, GenericContext? context) {
        if (type is null)
            return StackSlot.Null;
        var substituted = GenericSubstitutor.Substitute(type, context);
        if (substituted is VmIntrinsicType intrinsic) {
            if (!intrinsic.IsValue)
                return StackSlot.Null;
            return intrinsic.FullName switch {
                "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte"
                    or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => StackSlot.OfInt32(0),
                "System.Int64" or "System.UInt64" => StackSlot.OfInt64(0),
                "System.Single" or "System.Double" => StackSlot.OfFloat(0),
                "System.IntPtr" or "System.UIntPtr" => StackSlot.OfNativeInt(0),
                _ => StackSlot.OfValueType(new VmStructValue(intrinsic, [default])),
            };
        }
        if (substituted.IsValueType && !VmPrimitiveTypes.IsSlotPrimitive(substituted.FullName)) {
            if (substituted is VmConstructedType { Definition: VmIntrinsicType } constructedIntrinsic)
                return StackSlot.OfValueType(new VmStructValue(
                    constructedIntrinsic, [default], constructedIntrinsic.TypeArguments));
            var structType = substituted switch {
                VmClassType cls => cls,
                VmConstructedType constructed => (VmClassType)constructed.Definition,
                _ => throw new InvalidOperationException($"値型 {substituted.FullName} の実体を生成できません。"),
            };
            var args = substituted is VmConstructedType ct ? ct.TypeArguments : null;
            // フィールド型の !n は構造体自身の実引数で解決する
            var fieldContext = args is null ? null : new GenericContext { ClassArgs = args };
            return StackSlot.OfValueType(DefaultStruct(structType, loader, fieldContext, args));
        }
        if (substituted.IsValueType) {
            // CoreLib 実型に解決されたプリミティブ (System.Int32 等の TypeDef)。ファサードと
            // 同一の単一スロット表現で既定値を与える (型同一性は統合済み、表現は変わらない)
            return substituted.FullName switch {
                "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte"
                    or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => StackSlot.OfInt32(0),
                "System.Int64" or "System.UInt64" => StackSlot.OfInt64(0),
                "System.Single" or "System.Double" => StackSlot.OfFloat(0),
                "System.IntPtr" or "System.UIntPtr" => StackSlot.OfNativeInt(0),
                _ => throw new InvalidOperationException($"値型 {substituted.FullName} の既定値を生成できません。"),
            };
        }
        return StackSlot.Null;
    }

    /// <summary>構造体の既定値 (全フィールドを再帰的にゼロ初期化)。</summary>
    public VmStructValue DefaultStruct(VmClassType structType, TypeLoader loader) =>
        DefaultStruct(structType, loader, null, null);

    /// <summary>構造体の既定値。context はフィールド型のジェネリックパラメータ解決に使う
    /// (構造体自身の実引数は typeArguments として VmStructValue に記録する)。</summary>
    public VmStructValue DefaultStruct(VmClassType structType, TypeLoader loader,
        GenericContext? context, VmType[]? typeArguments) {
        var layout = GetLayout(structType);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        return new VmStructValue(structType, fields, typeArguments);
    }

    /// <summary>アロケーションサイズの概算 (バイト)。クォータ計上用。</summary>
    public static long EstimateSize(VmObject obj) => obj switch {
        VmArray array => EstimateArraySize(array.Elements.Length),
        VmClassInstance instance => EstimateFieldStorageSize(instance.Fields.Length),
        VmBoxedValue boxed => EstimateFieldStorageSize(boxed.Fields.Length),
        VmIntrinsicInstance intrinsic => EstimateFieldStorageSize(intrinsic.State.Length),
        VmTaskObject => 48,
        VmExpressionObject => 64,
        VmDelegate @delegate => 48 + 24L * @delegate.Invocations.Count,
        // localloc の仮想メモリブロックは実バイト数を計上する (メモリポリシーの対象)
        VmLocallocMemory memory => EstimateLocallocSize(memory.Bytes.Length),
        _ => 24,
    };

    // ---- 概算サイズの共有式 (確保前の事前計上 = Reserve 系と EstimateSize の両方から使う。
    //      呼び出し側にサイズ式を散らさず、ここに集約する) ----

    /// <summary>配列の概算サイズ (オブジェクト ヘッダ概算 + 要素 16 バイト)。</summary>
    public static long EstimateArraySize(long elementCount) {
        if (elementCount < 0)
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        return checked(24 + 16L * elementCount);
    }

    /// <summary>クラス実体 / ボックス化実体の概算サイズ (オブジェクト ヘッダ概算 + フィールド 16 バイト)。</summary>
    public static long EstimateFieldStorageSize(long fieldCount) {
        if (fieldCount < 0)
            throw new ArgumentOutOfRangeException(nameof(fieldCount));
        return checked(24 + 16L * fieldCount);
    }

    /// <summary>localloc ブロックの概算サイズ (実バイト数 + オブジェクト ヘッダ概算)。</summary>
    public static long EstimateLocallocSize(long byteCount) {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        return checked(24 + byteCount);
    }
}
