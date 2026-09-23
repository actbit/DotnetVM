using DotnetVM.Devices;
using DotnetVM.IL;
using DotnetVM.Host;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// IL インタプリタ本体。事前デコード済み命令列をループ実行する。
/// - 命令ごとにクォータを消費し、1024 命令毎にセーフポイント検査を行う
/// - intrinsic 呼出は必ず実行ゲート (IExecutionGate) 経由 (クォータ消費・セーフポイント・正規化を強制)
/// - ゲスト呼出はフレームを積み、深さは MaxRecursionDepth で事前拒否
/// 実装は責務ごとの独立クラスに分割 (Interpreter が IGuestInvoker/IExecutionGate/IFrameRunner を実装し注入):
///   SlotOps               スロット演算 (算術/比較/変換/コピー) — static
///   MemoryOps             配列/生メモリ/間接アクセス/sizeof — static
///   TypeChecks            型代入可否・デリゲート/例外ファサード判定 — static
///   MethodPreparer        メソッド事前準備キャッシュ (ローカル署名/EH 句)
///   ObjectEngine          newobj/フィールド/静的ストレージ/型初期化
///   CallEngine            呼出解決/仮想ディスパッチ/デリゲート
///   ExceptionDispatcher   EH (catch/finally/fault/filter) と分岐制御
/// </summary>
public sealed class Interpreter : IGuestInvoker, IExecutionGate, IFrameRunner {

    private const int SafepointInterval = 1024;

    private readonly MemoryPolicy _memory;
    private readonly IntrinsicRegistry _intrinsics;
    private readonly VmConsole _console;
    private readonly VmHeap _heap;
    private readonly NetworkGateway? _network;
    private readonly StorageGateway? _storage;
    private readonly Diagnostics.ExecutionTracer? _tracer;
    private readonly VmCoreLibSurfaces? _coreLibSurfaces;
    private readonly VmType? _stringType;
    private readonly VmSharedState _shared;
    private readonly Func<ReadOnlyMemory<byte>, TypeLoader>? _loadAssemblyFromBytes;
    private readonly VmAssemblyLoadContext? _defaultAssemblyLoadContext;
    private readonly Func<string?, bool, VmAssemblyLoadContext>? _createAssemblyLoadContext;
    private readonly Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? _loadAssemblyInContext;
    private readonly Func<VmAssemblyLoadContext, string, TypeLoader>? _loadAssemblyFromPath;
    private readonly InterpreterServices _services;
    private readonly MethodPreparer _preparer;
    private readonly ObjectEngine _objectEngine;
    private readonly CallEngine _callEngine;
    private readonly ExceptionDispatcher _exceptionDispatcher;
    /// <summary>loader ごとのエンジンセット (多アセンブリ実行: メソッドの所属画像で token 解決する)。</summary>
    private readonly Dictionary<TypeLoader, LoaderEngines> _engines = [];
    private readonly object _enginesGate = new();
    /// <summary>VM 単位で共有する静的ストレージ (ユニフィケーションされた実型の静的フィールドは 1 つ)。</summary>
    private readonly UnifiedStaticStorage _unifiedStaticStorage = new();
    private bool? _enginesRegisteredRootKey;
    // 実行状態はホストスレッドごとに分離する。フレーム一覧は stop-the-world GC が
    // 全ゲスト命令を停止した状態で走査し、独立した呼出しのルートをまとめて返す。
    private sealed class ExecutionState {
        public readonly object Gate = new();
        public readonly List<InterpreterFrame> Frames = [];
        public readonly List<StackSlot[]> TemporaryRoots = [];
        public int Depth;
        public long InstructionCount;
    }
    private readonly ThreadLocal<ExecutionState> _currentExecution = new(() => new ExecutionState());
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ExecutionState, byte> _executionStates = new();
    private readonly VmExecutionCoordinator _coordinator = new();
    private long _instructionCount;
    private int _running;
    private int _disposed;
    private readonly Func<IEnumerable<StackSlot[]>> _frameRootSource;

    public long InstructionCount => Interlocked.Read(ref _instructionCount);

    internal long CurrentThreadInstructionCount => CurrentState.InstructionCount;

    internal IDisposable EnterHostOperation() => _coordinator.EnterRead();

    private ExecutionState CurrentState {
        get {
            var state = _currentExecution.Value!;
            _executionStates.TryAdd(state, 0);
            return state;
        }
    }

    /// <summary>1 アセンブリ (TypeLoader) 分の実行エンジン。token 解決はすべてこの loader の画像に対して行う。</summary>
    private sealed class LoaderEngines {
        public required InterpreterServices Services { get; init; }
        public required MethodPreparer Preparer { get; init; }
        public required ObjectEngine Objects { get; init; }
        public required CallEngine Calls { get; init; }
        public required ExceptionDispatcher Exceptions { get; init; }
    }

    internal Interpreter(TypeLoader loader, IntrinsicRegistry intrinsics, VmConsole console, MemoryPolicy memory, VmHeap heap,
        NetworkGateway? network = null, StorageGateway? storage = null, Diagnostics.ExecutionTracer? tracer = null,
        VmCoreLibSurfaces? coreLibSurfaces = null, VmType? stringType = null, VmSharedState? shared = null,
        Func<ReadOnlyMemory<byte>, TypeLoader>? loadAssemblyFromBytes = null,
        VmAssemblyLoadContext? defaultAssemblyLoadContext = null,
        Func<string?, bool, VmAssemblyLoadContext>? createAssemblyLoadContext = null,
        Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? loadAssemblyInContext = null,
        Func<VmAssemblyLoadContext, string, TypeLoader>? loadAssemblyFromPath = null) {
        _memory = memory;
        _intrinsics = intrinsics;
        _console = console;
        _heap = heap;
        _network = network;
        _storage = storage;
        _tracer = tracer;
        _coreLibSurfaces = coreLibSurfaces;
        _stringType = stringType;
        _shared = shared ?? new VmSharedState();
        _loadAssemblyFromBytes = loadAssemblyFromBytes;
        _defaultAssemblyLoadContext = defaultAssemblyLoadContext;
        _createAssemblyLoadContext = createAssemblyLoadContext;
        _loadAssemblyInContext = loadAssemblyInContext;
        _loadAssemblyFromPath = loadAssemblyFromPath;
        var strings = new VmStringPool(heap);
        var primary = CreateEngines(loader, strings);
        _services = primary.Services;
        _preparer = primary.Preparer;
        _objectEngine = primary.Objects;
        _callEngine = primary.Calls;
        _exceptionDispatcher = primary.Exceptions;
        _engines[loader] = primary;
        // 実行中フレームのルート源は Interpreter 単位で 1 回登録する
        _frameRootSource = EnumerateFrameRoots;
        heap.AddRootSlotSource(_frameRootSource);
    }

    /// <summary>指定 loader のエンジンセットを取得 (無ければ遅延生成して GC ルート源も登録する)。</summary>
    private LoaderEngines EnginesFor(VmMethod method) {
        var loader = method.Loader;
        lock (_enginesGate) {
            if (loader is null || ReferenceEquals(loader, _services.Loader))
                return _engines[_services.Loader];
            return _engines.TryGetValue(loader, out var engines)
                ? engines
                : _engines[loader] = CreateEngines(loader, _services.Strings);
        }
    }

    /// <summary>loader のエンジンセットを構築する (文字列プール・静的ストレージは VM 単位で共有)。</summary>
    private LoaderEngines CreateEngines(TypeLoader loader, VmStringPool strings) {
        var intrinsicContext = new IntrinsicContext {
            Console = _console,
            Strings = strings,
            Heap = _heap,
            Types = loader,
            Network = _network,
            Storage = _storage,
            Shared = _shared,
            LoadAssemblyFromBytes = _loadAssemblyFromBytes,
            DefaultAssemblyLoadContext = _defaultAssemblyLoadContext,
            CreateAssemblyLoadContext = _createAssemblyLoadContext,
            LoadAssemblyInContext = _loadAssemblyInContext,
            LoadAssemblyFromPath = _loadAssemblyFromPath,
            MemoryPolicy = _memory,
        };
        var services = new InterpreterServices(loader, _intrinsics, _console, _memory, _heap, strings,
            intrinsicContext, _tracer, _coreLibSurfaces) {
            // VmString の型同一性を接続する System.String 実型 (VM 単位。LoadHostCoreLib = true 時のみ非 null)
            StringType = _stringType,
        };
        var preparer = new MethodPreparer(loader);
        var objects = new ObjectEngine(services, this, this, _unifiedStaticStorage, _shared.TypeInitialization);
        var calls = new CallEngine(services, this, this, objects);
        var exceptions = new ExceptionDispatcher(services, preparer, objects, this);
        intrinsicContext.RunGuestThreadDelegate = (guestDelegate, state, hasState) => {
            var arguments = hasState
                ? new[] { StackSlot.OfObject(guestDelegate), state }
                : new[] { StackSlot.OfObject(guestDelegate) };
            calls.InvokeDelegate(guestDelegate, arguments);
        };
        intrinsicContext.InvokeGuestDelegate = (guestDelegate, arguments) => calls.InvokeDelegate(guestDelegate, arguments);
        intrinsicContext.RunGuestStateMachine = stateMachine => {
            var byRef = stateMachine.Kind == StackKind.ByRef && stateMachine.ObjectValue is VmByRef reference
                ? reference
                : null;
            var value = byRef?.Read() ?? stateMachine;
            VmType stateType;
            VmStructValue? structMachine = null;
            if (value.ObjectValue is VmStructValue valueMachine) {
                structMachine = valueMachine;
                stateType = valueMachine.StructType is VmConstructedType constructed
                    ? constructed.Definition : valueMachine.StructType;
            } else if (value.ObjectValue is VmClassInstance classMachine) {
                stateType = classMachine.RuntimeType is VmConstructedType constructed
                    ? constructed.Definition : classMachine.RuntimeType;
            } else {
                throw new InvalidOperationException("async state machine は VM の構造体またはクラス値である必要があります。");
            }
            if (stateType is not VmClassType stateClass)
                throw new InvalidOperationException($"async state machine 型 {stateType.FullName} に IL 本体がありません。");
            var moveNext = stateClass.Methods.FirstOrDefault(method => method.Name == "MoveNext" && !method.IsStatic)
                ?? throw new InvalidOperationException($"async state machine {stateType.FullName} に MoveNext がありません。");
            if (structMachine is null) {
                Invoke(moveNext, [value], GenericContext.Of([], null));
                return;
            }
            var container = byRef?.Container ?? [value];
            var index = byRef?.Index ?? 0;
            Invoke(moveNext, [StackSlot.OfByRef(new VmByRef(container, index))],
                GenericContext.Of(structMachine.TypeArguments, null));
        };
        // Activator.CreateInstance 等が .ctor を実行するためのフック
        intrinsicContext.NewInstanceHook = objects.CreateInstanceByCtor;
        // ゲストオブジェクトの暗黙 ToString (Console.Write(object) / String.Concat(object) 用)
        intrinsicContext.ToStringHook = calls.InvokeToStringSlot;
        // MethodBase.GetCurrentMethod() 用の現在メソッドフック
        intrinsicContext.CurrentMethodHook = () => {
            var state = CurrentState;
            lock (state.Gate)
                return state.Frames.Count > 0 ? state.Frames[^1].Method : null;
        };
        intrinsicContext.SuspendExecution = action => {
            using (_coordinator.SuspendExecution())
                action();
        };
        intrinsicContext.RegisterTransientRoots = slots => {
            var state = CurrentState;
            lock (state.Gate)
                state.TemporaryRoots.Add(slots);
            return new ActionLease(() => {
                lock (state.Gate)
                    state.TemporaryRoots.Remove(slots);
            });
        };
        // GC ルート源の登録: 静的ストレージ / intrinsic 静的フィールド (フレームは Interpreter 単位で登録済み)
        // 共有静的ストレージ (UnifiedStaticStorage) は全画像で共通の 1 件として VM 単位で 1 回登録する
        if (_enginesRegisteredRootKey is null) {
            _heap.AddRootSlotSource(() => _unifiedStaticStorage?.EnumerateRoots().ToArray() ?? []);
            _enginesRegisteredRootKey = true;
        }
        _heap.AddRootSlotSource(services.Objects.EnumerateStaticStorage);
        _heap.AddRootSlotSource(() => objects.IntrinsicStaticFields.ToArray());
        return new LoaderEngines { Services = services, Preparer = preparer, Objects = objects, Calls = calls, Exceptions = exceptions };
    }
    /// <summary>文字列プール (VM ファサードから参照用)。</summary>
    public VmStringPool Strings => _services.Strings;

    /// <summary>型ローダ (ホスト API が intrinsic ファサード型を解決するのに使う)。</summary>
    public TypeLoader Loader => _services.Loader;

    /// <summary>VM ヒープ (アロケーション計上の唯一の入口。ホスト API のインスタンス生成からも使う)。</summary>
    public VmHeap Heap => _services.Heap;

    internal GcStatistics CollectGarbage() {
        _shared.ThrowIfDisposed();
        using (_coordinator.StopTheWorld())
            return _heap.Collect();
    }

    /// <summary>値を指定の型としてボックス化する (box 命令と同じセマンティクス)。</summary>
    public StackSlot Box(VmType type, in StackSlot value) {
        var fields = value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue sv
            ? sv.Clone().Fields
            : [value];
        return StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields)));
    }

    /// <summary>インスタンスを生成して .ctor を実行する (newobj 相当。VM ホスト API 用)。</summary>
    public VmClassInstance CreateInstance(VmClassType type, StackSlot[] constructorArgs) {
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && !m.IsStatic &&
                m.Signature.ParamTypes.Length == constructorArgs.Length && m.Body is not null)
            ?? throw new ArgumentException($"型 {type.FullName} に引数 {constructorArgs.Length} 個の .ctor がありません。");
        _objectEngine.EnsureInitialized(type);
        var instance = _services.Heap.Allocate(new VmClassInstance(type,
            _services.Objects.CreateInstanceStorage(type, _services.Loader)));
        var args = new StackSlot[constructorArgs.Length + 1];
        args[0] = StackSlot.OfObject(instance);
        constructorArgs.CopyTo(args, 1);
        Invoke(ctor, args);
        return instance;
    }

    /// <summary>メソッドを実行し戻り値を得る (void は Kind=Empty)。</summary>
    public StackSlot Invoke(VmMethod method, StackSlot[] arguments) => Invoke(method, arguments, null);

    /// <summary>メソッドを実行し戻り値を得る (void は Kind=Empty)。context は呼出元のジェネリック実引数。</summary>
    public StackSlot Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context) {
        _shared.ThrowIfDisposed();
        if (Interlocked.Exchange(ref _running, 1) == 0) {
            _services.Intrinsics.Seal(); // 実行開始後の intrinsic 登録を禁止
        }
        // 置換面 (C5): 実在 CoreLib 由来のメソッドのうち DotnetVM.CoreLib の managed IL が
        // 面を置換するものは、ここ (唯一の IL 実行入口) で本体を差し替える。MemberRef 解決
        // でも仮想ディスパッチでも最終的にここを通るため、CoreLib IL 内の boxed int の
        // callvirt ToString も同じ面で置換される
        if (_services.CoreLibSurfaces is { } surfaces && surfaces.Substitute(method) is { } substituted) {
            // instance 面 → static 実装への差し替えでは受信者 (this) を生スロットへ正規化する:
            // constrained callvirt (直接の int.ToString() 等) では VmByRef、CoreLib IL 内の
            // box 済み値の callvirt (String.Concat など) では VmBoxedValue 参照で渡るため、
            // どちらも保持している値スロットへ読み替えてから渡す
            if (method.Signature.HasThis && !substituted.Signature.HasThis && arguments.Length > 0)
                arguments[0] = arguments[0].ObjectValue switch {
                    VmByRef byRef => byRef.Slot,
                    VmBoxedValue boxed => boxed.Fields[0],
                    _ => arguments[0],
                };
            method = substituted;
        }
        if (method.Body is null)
            ThrowNoBody(method);
        var state = CurrentState;
        if (state.Depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");
        state.Depth++;
        try {
            var engines = EnginesFor(method);
            CloneStructArgs(method, arguments);
            var frame = InterpreterFrame.Create(method, arguments,
                engines.Preparer.Prepare(method).LocalTypes, method.Body.MaxStack);
            frame.Context = context; // FixupStructLocals が !n ローカルを実引数で初期化する
            using (_coordinator.EnterRead()) {
                lock (state.Gate)
                    state.Frames.Add(frame);
            }
            // 実行トレース: IL 本体を実行したフレームのみ記録する
            // (intrinsic / ランタイムバインドへの委譲は IL フレームを持たないため記録されない)
            if (_tracer is { } tracer)
                tracer.Record(method.Loader?.Image.Name ?? "", method.DeclaringType.FullName, method.Name);
            try {
                using (_coordinator.EnterRead())
                    FixupStructLocals(frame);
                return engines.Exceptions.RunFrame(frame);
            } finally {
                using (_coordinator.EnterRead()) {
                    lock (state.Gate)
                        state.Frames.Remove(frame);
                }
            }
        } finally {
            state.Depth--;
        }

    }

    // サービス群からの再帰呼出入口 (循環依存をインターフェースで切る)
    StackSlot IGuestInvoker.Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context) =>
        Invoke(method, arguments, context);

    void IExecutionGate.ConsumeInstruction() => ConsumeInstruction();

    void IExecutionGate.CheckSafepoint() => CheckSafepoint();

    StackSlot IFrameRunner.RunFrameCore(InterpreterFrame frame) => RunFrameCore(frame);

    /// <summary>実行中フレームが保持する全スロット (引数/ローカル/評価スタック/送出中例外) をルートとして列挙する。</summary>
    private IEnumerable<StackSlot[]> EnumerateFrameRoots() {
        foreach (var state in _executionStates.Keys) {
            InterpreterFrame[] frames;
            lock (state.Gate)
                frames = [.. state.Frames];
            StackSlot[][] temporaryRoots;
            lock (state.Gate)
                temporaryRoots = [.. state.TemporaryRoots];
            foreach (var roots in temporaryRoots)
                yield return roots;
            foreach (var frame in frames) {
                yield return frame.Arguments;
                yield return frame.Locals;
                if (frame.Stack.Count > 0)
                    yield return frame.Stack.CopySlots();
                if (frame.CurrentThrow is { } throwing)
                    yield return [StackSlot.OfObject(throwing.ExceptionObject)];
            }
        }
    }

    /// <summary>値型引数は呼出境界でコピーする (this はポインタ意味論のため除く)。</summary>
    private static void CloneStructArgs(VmMethod method, StackSlot[] arguments) {
        var start = method.Signature.HasThis ? 1 : 0;
        for (var i = start; i < arguments.Length; i++)
            if (arguments[i].Kind == StackKind.ValueType && arguments[i].ObjectValue is VmStructValue sv)
                arguments[i] = StackSlot.OfValueType(sv.Clone());
    }

    /// <summary>値型ローカルの既定値を VmStructValue で実体化する (InterpreterFrame はローダ無しで null を置くため)。
    /// !n / !!n ローカルは frame.Context の実引数で置換してから判定する。</summary>
    private void FixupStructLocals(InterpreterFrame frame) {
        var engines = EnginesFor(frame.Method);
        for (var i = 0; i < frame.Locals.Length; i++) {
            ref var slot = ref frame.Locals[i];
            if (slot.Kind != StackKind.Object || slot.ObjectValue is not null)
                continue;
            var sigType = frame.LocalTypes[i];
            if (sigType.Kind is not (SigKind.TypeToken or SigKind.GenericInst
                or SigKind.GenericVar or SigKind.GenericMethodVar))
                continue;
            var type = engines.Services.Loader.ResolveToken(sigType, frame.Context);
            if (type.IsValueType)
                slot = engines.Services.Objects.DefaultForType(type, engines.Services.Loader);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void ThrowNoBody(VmMethod method) {
        // abstract は未実装面、native (P/Invoke) はセキュリティポリシーで拒否。
        // ランタイムバインド (CallEngine) の照合漏れの最終拒否点でもある (InternalCall と
        // P/Invoke の区別は監査性のため例外型を分ける)
        if (method.IsAbstract)
            throw new NotSupportedException(
                $"抽象メソッド {method} には実装がありません (継承解決は M3 以降)。");
        // pinvokeimpl (MethodAttributes 0x2000) または native (ImplFlags) の両方を検出する
        // (P/Invoke の宣言方法によってビットの付き方が異なるため)
        if ((method.Flags & 0x2000) != 0 || (method.ImplFlags & 0x0003) == 0x0003)
            throw new OperationNotAllowedException(
                $"メソッド {method} はネイティブ実行 (P/Invoke) を要求しますが、VM はネイティブ依存を許可しません。");
        throw new NotSupportedException($"メソッド {method} には実行可能な本体がありません (InternalCall 面はランタイムバインドの登録が必要です)。");
    }

    // ---- クォータ/セーフポイント ----

    private void ConsumeInstruction() {
        _shared.ThrowIfDisposed();
        long count;
        while (true) {
            var current = Volatile.Read(ref _instructionCount);
            if (current >= _memory.InstructionQuota)
                throw new InstructionQuotaExceededException(
                    $"命令数クォータ {_memory.InstructionQuota:N0} を超過しました (実行命令数: {current:N0})。");
            if (Interlocked.CompareExchange(ref _instructionCount, current + 1, current) == current) {
                count = current + 1;
                break;
            }
        }
        var state = CurrentState;
        state.InstructionCount++;
        if (count % SafepointInterval == 0)
            CheckSafepoint();
    }

    /// <summary>セーフポイント。命令境界 = 全ゲスト状態がフレームに含まれる時点なので、ここでのみ GC を起動してよい
    /// (newobj 処理中のオブジェクトがホストローカルにのみ保持される瞬間があり、そこで回収すると誤 sweep する)。</summary>
    private void CheckSafepoint() {
        _shared.ThrowIfDisposed();
        // IL 命令またはその intrinsic 呼出中は共有 read lease を保持している。
        // その場で GC せず、RunFrameCore の次の命令境界で stop-the-world 回収する。
        if (_coordinator.IsInsideGuestInstruction)
            return;
        using (_coordinator.StopTheWorldAtBoundary())
            _services.Heap.CollectIfDue();
    }

    // ---- 命令ディスパッチ ループ ----

    private StackSlot RunFrameCore(InterpreterFrame frame) {
        // メソッドの所属画像に対応するエンジンセットで実行する (多アセンブリ: dep の IL は
        // dep の loader で ldstr/ldtoken/呼出を解決する)
        var engines = EnginesFor(frame.Method);
        var loader = engines.Services.Loader;
        var prepared = engines.Preparer.Prepare(frame.Method);
        var eh = engines.Exceptions;
        var calls = engines.Calls;
        var objects = engines.Objects;
        while (true) {
            CheckSafepoint();
            using var instructionLease = _coordinator.EnterInstruction();
            ConsumeInstruction();
            var instruction = frame.Code[frame.Ip];
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                case ILOp.Volatile or ILOp.Unaligned or ILOp.Readonly or ILOp.Tail: // プレフィックス (効果なし)
                    break;
                case ILOp.Constrained:
                    // constrained. <type> は次の call/callvirt で解決する。値型レシーバは
                    // ValueType/ByRef スロットでそのまま仮想ディスパッチでき、参照型レシーバは
                    // 通常の callvirt と等価なため、トークンを記録して次の呼出に渡すだけでよい
                    frame.PendingConstrained = instruction.IntOperand;
                    break;

                // ---- 引数 ----
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    frame.Stack.Push(frame.Arguments[instruction.Op - ILOp.Ldarg_0]);
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(frame.Arguments[instruction.IntOperand]);
                    break;
                case ILOp.Ldarga_S or ILOp.Ldarga:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(frame.Arguments, instruction.IntOperand)));
                    break;
                case ILOp.Starg_S or ILOp.Starg:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Arguments[instruction.IntOperand] = frame.Stack.Pop();
                    break;

                // ---- ローカル変数 ----
                case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                    CheckLocalIndex(frame, instruction.Op - ILOp.Ldloc_0);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(frame.Locals[instruction.Op - ILOp.Ldloc_0]));
                    break;
                case ILOp.Ldloc_S or ILOp.Ldloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(frame.Locals[instruction.IntOperand]));
                    break;
                case ILOp.Ldloca_S or ILOp.Ldloca:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(frame.Locals, instruction.IntOperand)));
                    break;
                case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                    CheckLocalIndex(frame, instruction.Op - ILOp.Stloc_0);
                    frame.Locals[instruction.Op - ILOp.Stloc_0] = SlotOps.StoreCopyOfValue(frame.Stack.Pop());
                    break;
                case ILOp.Stloc_S or ILOp.Stloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Locals[instruction.IntOperand] = SlotOps.StoreCopyOfValue(frame.Stack.Pop());
                    break;

                // ---- 即値 ----
                case ILOp.Ldnull:
                    frame.Stack.Push(StackSlot.Null);
                    break;
                case ILOp.Ldc_I4_M1:
                    frame.Stack.Push(StackSlot.OfInt32(-1));
                    break;
                case ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or ILOp.Ldc_I4_3
                    or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.Op - ILOp.Ldc_I4_0));
                    break;
                case ILOp.Ldc_I4_S:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.IntOperand));
                    break;
                case ILOp.Ldc_I4:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.IntOperand));
                    break;
                case ILOp.Ldc_I8:
                    frame.Stack.Push(StackSlot.OfInt64(instruction.LongOperand));
                    break;
                case ILOp.Ldc_R4 or ILOp.Ldc_R8:
                    frame.Stack.Push(StackSlot.OfFloat(instruction.DoubleOperand));
                    break;
                case ILOp.Dup:
                    frame.Stack.Push(frame.Stack.Peek());
                    break;
                case ILOp.Pop:
                    _ = frame.Stack.Pop();
                    break;

                // ---- 分岐 ----
                case ILOp.Br or ILOp.Br_S:
                    eh.JumpTo(frame, instruction.IntOperand);
                    continue;
                case ILOp.BrFalse or ILOp.BrFalse_S:
                    if (!SlotOps.IsTrue(frame.Stack.Pop()))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.BrTrue or ILOp.BrTrue_S:
                    if (SlotOps.IsTrue(frame.Stack.Pop()))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S
                    or ILOp.Bge or ILOp.Bge_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S
                    or ILOp.Blt or ILOp.Blt_S or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S
                    or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    if (SlotOps.CompareBranch(instruction.Op, left, right))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                }
                case ILOp.Switch: {
                    var index = frame.Stack.Pop().AsInt32;
                    var targets = instruction.SwitchTargets!;
                    if ((uint)index < (uint)targets.Length) {
                        eh.JumpTo(frame, targets[index]);
                        continue;
                    }
                    break;
                }

                // ---- 比較 (スタックに 0/1 を積む) ----
                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    frame.Stack.Push(StackSlot.OfInt32(SlotOps.Compare(instruction.Op, left, right) ? 1 : 0));
                    break;
                }

                // ---- 算術 ----
                case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un
                    or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor
                    or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
                    or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un
                    or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    frame.Stack.Push(MemoryOps.TryPointerArithmetic(instruction.Op, left, right)
                        ?? SlotOps.BinaryArithmetic(instruction.Op, left, right));
                    break;
                }
                case ILOp.Neg or ILOp.Not: {
                    var value = frame.Stack.Pop();
                    frame.Stack.Push(SlotOps.UnaryArithmetic(instruction.Op, value));
                    break;
                }

                // ---- 変換 ----
                case >= ILOp.Conv_I1 and <= ILOp.Conv_R_Un
                    when instruction.Op is ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8
                        or ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U4 or ILOp.Conv_U8 or ILOp.Conv_R_Un:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Conv_U2 or ILOp.Conv_U1 or ILOp.Conv_I or ILOp.Conv_U:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un
                    or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un
                    or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un
                    or ILOp.Conv_Ovf_I1 or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or ILOp.Conv_Ovf_U2
                    or ILOp.Conv_Ovf_I4 or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I8 or ILOp.Conv_Ovf_U8
                    or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;

                // ---- 文字列 ----
                case ILOp.Ldstr:
                    frame.Stack.Push(StackSlot.OfObject(_services.Strings.GetOrNew(
                        frame.Method.DynamicStrings is { } dynamicStrings &&
                        dynamicStrings.TryGetValue(unchecked((uint)instruction.IntOperand), out var dynamicString)
                            ? dynamicString
                            : loader.Image.GetUserString(instruction.IntOperand & 0xFFFFFF))));
                    break;

                // ---- 呼出 ----
                case ILOp.Call or ILOp.Callvirt: {
                    var result = calls.Call(instruction.IntOperand, frame,
                        instruction.Op == ILOp.Callvirt, frame.PendingConstrained);
                    frame.PendingConstrained = 0; // constrained. は直後の 1 呼出でのみ有効
                    if (result is { } value)
                        frame.Stack.Push(value);
                    break;
                }
                case ILOp.Ret:
                    return SlotOps.SignatureReturnsValue(frame.Method.Signature) ? frame.Stack.Pop() : default;

                // ---- オブジェクト生成 ----
                case ILOp.Newobj: {
                    var value = objects.NewObject(instruction.IntOperand, frame);
                    if (value is { } pushed)
                        frame.Stack.Push(pushed);
                    break;
                }
                case ILOp.Newarr: {
                    var count = frame.Stack.Pop().AsInt32;
                    if (count < 0)
                        throw new UnhandledGuestException("System.OverflowException", null);
                    var elementType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    // localloc と同じく予約トランザクション (Reserve → 実確保 → Commit、失敗時は自動巻き戻し)
                    using var reservation = _services.Heap.ReserveArray(count);
                    var elements = new StackSlot[count];
                    for (var i = 0; i < count; i++)
                        elements[i] = engines.Services.Objects.DefaultForType(elementType, loader);
                    frame.Stack.Push(StackSlot.OfObject(reservation.Commit(
                        new VmArray(new VmArrayType { ElementType = elementType }, elements))));
                    break;
                }
                case ILOp.Ldlen:
                    frame.Stack.Push(StackSlot.OfNativeInt(MemoryOps.GetArray(frame.Stack.Pop()).Length));
                    break;

                // ---- 配列要素 ----
                case ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2
                    or ILOp.Ldelem_I4 or ILOp.Ldelem_U4:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Int32));
                    break;
                case ILOp.Ldelem_I8 or ILOp.Ldelem_I:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Int64));
                    break;
                case ILOp.Ldelem_R4 or ILOp.Ldelem_R8:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Float));
                    break;
                case ILOp.Ldelem_Ref:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Object));
                    break;
                case ILOp.Ldelem:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame,
                        MemoryOps.ElementKindFromType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens))));
                    break;
                case ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2 or ILOp.Stelem_I4:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Int32);
                    break;
                case ILOp.Stelem_I8:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Int64);
                    break;
                case ILOp.Stelem_R4 or ILOp.Stelem_R8:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Float);
                    break;
                case ILOp.Stelem_Ref:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Object, _services.StringType);
                    break;
                case ILOp.Stelem:
                    MemoryOps.ArrayStore(frame,
                        MemoryOps.ElementKindFromType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens)));
                    break;
                case ILOp.Ldelema: {
                    var index = frame.Stack.Pop().AsInt32;
                    var array = MemoryOps.GetArray(frame.Stack.Pop());
                    MemoryOps.CheckArrayBounds(array, index);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(array.Elements, index)));
                    break;
                }

                // ---- フィールド ----
                case ILOp.Ldfld or ILOp.Ldflda: {
                    var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var objSlot = frame.Stack.Pop();
                    if (instruction.Op == ILOp.Ldflda) {
                        // VnString (可変 char バッファ) はバイト実体への unmanaged ポインタ、
                        // それ以外は ByRef (CoreLib IL が Unsafe.Add / Buffer.Memmove に渡す形)
                        frame.Stack.Push(objects.FieldAddress(objSlot, field));
                        break;
                    }
                    var location = objects.FieldLocation(objSlot, field);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(location.Read()));
                    break;
                }
                case ILOp.Stfld: {
                    var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    var objSlot = frame.Stack.Pop();
                    // VnString レシーバはバイト実体 (真実源) に直接書く
                    if (objects.TryStoreStringField(objSlot, field, value))
                        break;
                    objects.FieldLocation(objSlot, field).Write(SlotOps.StoreCopyOfValue(value));
                    break;
                }
                case ILOp.Ldsfld: {
                    var slot = objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(slot.Read()));
                    break;
                }
                case ILOp.Ldsflda: {
                    // 静的 FieldRVA データ (<PrivateImplementationDetails> 初期化データ) は
                    // バイト実体への unmanaged ポインタ (CoreLib IL が ReadOnlySpan(void*) ctor に渡す形)、
                    // それ以外は通常の静的ストレージへの ByRef
                    if (objects.TryGetStaticFieldRvaAddress(instruction.IntOperand) is { } rvaSlot)
                        frame.Stack.Push(rvaSlot);
                    else
                        frame.Stack.Push(StackSlot.OfByRef(objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens)));
                    break;
                }
                case ILOp.Stsfld: {
                    var value = frame.Stack.Pop();
                    objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens).Write(value);
                    break;
                }

                // ---- 間接アクセス (ldind/stind。マネージポインタ ByRef と unmanaged ポインタの両対応) ----
                case ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2
                    or ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I
                    or ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref:
                    frame.Stack.Push(MemoryOps.LoadIndirect(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or ILOp.Stind_I2
                    or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8: {
                    var value = frame.Stack.Pop();
                    MemoryOps.StoreIndirect(instruction.Op, frame.Stack.Pop(), value);
                    break;
                }

                // ---- オブジェクト/値型のコピー ----
                case ILOp.Ldobj: {
                    var address = frame.Stack.Pop();
                    if (address.ObjectValue is VmNativePointer ldNative) {
                        // unmanaged ポインタ先からの読み出し (Guid 解析等の RawData 経路)。
                        // 型のバイト幅で LE 読みし VM スロットへ展開する
                        var ldType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var ldSize = MemoryOps.SizeOfType(ldType);
                        if (ldNative.ByteOffset < 0 || (long)ldNative.ByteOffset + ldSize > ldNative.Bytes.Length)
                            throw new InvalidOperationException(
                                $"ldobj がブロック外を参照します (offset={ldNative.ByteOffset}, {ldSize} バイト)。");
                        frame.Stack.Push(MemoryOps.ValueFromBytes(
                            ldNative.Bytes.AsSpan(ldNative.ByteOffset, ldSize).ToArray(), ldType, ldSize));
                        break;
                    }
                    var byref = (VmByRef)address.ObjectValue!;
                    frame.Stack.Push(SlotOps.PushCopyOfValue(byref.Slot));
                    break;
                }
                case ILOp.Stobj: {
                    var value = frame.Stack.Pop();
                    var stAddress = frame.Stack.Pop();
                    if (stAddress.ObjectValue is VmNativePointer stNative) {
                        var stType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var stSize = MemoryOps.SizeOfType(stType);
                        if (stNative.ByteOffset < 0 || (long)stNative.ByteOffset + stSize > stNative.Bytes.Length)
                            throw new InvalidOperationException(
                                $"stobj がブロック外を参照します (offset={stNative.ByteOffset}, {stSize} バイト)。");
                        var stBytes = MemoryOps.BytesOfValue(value, stType, stSize);
                        Array.Copy(stBytes, 0, stNative.Bytes, stNative.ByteOffset, stSize);
                        break;
                    }
                    var byref = (VmByRef)stAddress.ObjectValue!;
                    byref.Write(SlotOps.StoreCopyOfValue(value));
                    break;
                }
                case ILOp.Cpobj: {
                    var src = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    var cpType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var cpSize = MemoryOps.SizeOfType(cpType);
                    if (src.ObjectValue is VmNativePointer srcNative && dst.ObjectValue is VmNativePointer dstNative) {
                        if ((long)srcNative.ByteOffset + cpSize > srcNative.Bytes.Length ||
                            (long)dstNative.ByteOffset + cpSize > dstNative.Bytes.Length)
                            throw new InvalidOperationException("cpobj がブロック外を参照します。");
                        Array.Copy(srcNative.Bytes, srcNative.ByteOffset, dstNative.Bytes, dstNative.ByteOffset, cpSize);
                        break;
                    }
                    if (src.ObjectValue is VmNativePointer srcOnly) {
                        if (srcOnly.ByteOffset < 0 || (long)srcOnly.ByteOffset + cpSize > srcOnly.Bytes.Length)
                            throw new InvalidOperationException("cpobj がブロック外を参照します。");
                        var value = MemoryOps.ValueFromBytes(
                            srcOnly.Bytes.AsSpan(srcOnly.ByteOffset, cpSize).ToArray(), cpType, cpSize);
                        ((VmByRef)dst.ObjectValue!).Write(SlotOps.StoreCopyOfValue(value));
                        break;
                    }
                    if (dst.ObjectValue is VmNativePointer dstOnly) {
                        var srcValue = ((VmByRef)src.ObjectValue!).Read();
                        var dstBytes = MemoryOps.BytesOfValue(srcValue, cpType, cpSize);
                        if ((long)dstOnly.ByteOffset + cpSize > dstOnly.Bytes.Length)
                            throw new InvalidOperationException("cpobj がブロック外を参照します。");
                        Array.Copy(dstBytes, 0, dstOnly.Bytes, dstOnly.ByteOffset, cpSize);
                        break;
                    }
                    var srcRef = (VmByRef)src.ObjectValue!;
                    var dstRef = (VmByRef)dst.ObjectValue!;
                    dstRef.Write(SlotOps.StoreCopyOfValue(srcRef.Read()));
                    break;
                }
                case ILOp.Initobj: {
                    var initAddress = frame.Stack.Pop();
                    if (initAddress.ObjectValue is VmNativePointer initNative) {
                        var initType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var initSize = MemoryOps.SizeOfType(initType);
                        if (initNative.ByteOffset < 0 || (long)initNative.ByteOffset + initSize > initNative.Bytes.Length)
                            throw new InvalidOperationException("initobj がブロック外を参照します。");
                        Array.Clear(initNative.Bytes, initNative.ByteOffset, initSize);
                        break;
                    }
                    var byref = (VmByRef)initAddress.ObjectValue!;
                    byref.Write(engines.Services.Objects.DefaultForType(
                        objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens), loader));
                    break;
                }

                // ---- ボックス化 ----
                case ILOp.Box: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    var fields = value.Kind == StackKind.ValueType
                        ? ((VmStructValue)value.ObjectValue!).Clone().Fields
                        : [value];
                    frame.Stack.Push(StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields))));
                    break;
                }
                case ILOp.Unbox: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{SlotOps.Describe(value)} を {target.FullName} として unbox できません。");
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(boxed.Fields, 0)));
                    break;
                }
                case ILOp.Unbox_Any: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (target.IsValueType) {
                        // 値型への unbox.any はボックス化実体からコピーを取り出す
                        if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{SlotOps.Describe(value)} を {target.FullName} に unbox.any できません。");
                        if (VmPrimitiveTypes.IsSlotPrimitive(target.FullName)) {
                            // プリミティブ (実型 / intrinsic ファサードとも) はスロットをそのまま取り出す
                            frame.Stack.Push(boxed.Fields[0]);
                        } else if (target is VmClassType or VmConstructedType) {
                            // 構造体 (構築ジェネリック構造体を含む) は実体をコピーして取り出す
                            var args = boxed.Type is VmConstructedType ct ? ct.TypeArguments : null;
                            var sv = new VmStructValue(boxed.Type, (StackSlot[])boxed.Fields.Clone(), args);
                            frame.Stack.Push(StackSlot.OfValueType(sv));
                        } else {
                            // その他の intrinsic 値型 (TypedReference 等) もスロットで保持される
                            frame.Stack.Push(boxed.Fields[0]);
                        }
                    } else {
                        // 参照型への unbox.any は castclass と同等
                        var ok = value.ObjectValue is null ||
                            TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
                        if (!ok)
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{SlotOps.Describe(value)} を {target.FullName} に変換できません。");
                        frame.Stack.Push(value);
                    }
                    break;
                }

                // ---- キャスト ----
                case ILOp.Castclass or ILOp.Isinst: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    var ok = value.ObjectValue is null ||
                        TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
                    if (!ok && instruction.Op == ILOp.Castclass)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{SlotOps.Describe(value)} を {target.FullName} にキャストできません。");
                    if (ok)
                        frame.Stack.Push(value);
                    else
                        frame.Stack.Push(StackSlot.Null);
                    break;
                }

                // ---- 例外処理 ----
                case ILOp.Throw: {
                    var value = frame.Stack.Pop();
                    throw eh.MakeGuestThrow(value);
                }
                case ILOp.Rethrow:
                    // 処理中の例外を元の情報のまま再送出 (現在の catch コンテキストの外へ)
                    throw frame.CurrentThrow
                        ?? throw new BadImageFormatException("rethrow が catch ハンドラの外で実行されました。");
                case ILOp.Leave or ILOp.Leave_S:
                    eh.DoLeave(frame, prepared.Clauses, instruction.IntOperand);
                    continue;
                case ILOp.Endfinally: {
                    if (frame.FinallyResume.Count == 0)
                        throw new BadImageFormatException("endfinally が finally/fault の外で実行されました。");
                    var next = frame.FinallyResume[^1];
                    frame.FinallyResume.RemoveAt(frame.FinallyResume.Count - 1);
                    if (next == ExceptionDispatcher.PropagateSentinel)
                        throw frame.CurrentThrow!; // finally/fault 通過後の伝播再開
                    frame.Ip = next;
                    continue;
                }
                case ILOp.Endfilter: {
                    if (frame.FilterClause < 0)
                        throw new BadImageFormatException("endfilter がフィルタの外で実行されました。");
                    var clauseIndex = frame.FilterClause;
                    var clause = prepared.Clauses![clauseIndex];
                    frame.FilterClause = -1;
                    if (frame.Stack.Pop().AsInt32 != 0) {
                        // フィルタ採用 → catch ハンドラへ (例外オブジェクトを積み直す)
                        frame.Stack.Clear();
                        frame.Stack.Push(StackSlot.OfObject(frame.CurrentThrow!.ExceptionObject));
                        frame.Ip = clause.HandlerStart;
                    } else {
                        // フィルタ不採用 → 発生位置から後続の句の探索を継続する
                        frame.Stack.Clear();
                        frame.Ip = frame.UnwindIp;
                        if (!eh.TryDispatchHandlerFrom(frame, frame.CurrentThrow!, clauseIndex + 1))
                            throw frame.CurrentThrow!;
                    }
                    continue;
                }

                // ---- 関数ポインタ / デリゲート / 特殊命令 ----
                case ILOp.Ldftn: {
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    if (target.Method is null)
                        throw new OperationNotAllowedException(
                            $"ldftn: intrinsic 面 {target.DeclaringType}::{target.Name} への関数ポインタ取得は対応していません。");
                    frame.Stack.Push(StackSlot.OfObject(new VmMethodPointer { Target = target.Method }));
                    break;
                }
                case ILOp.Ldvirtftn: {
                    // レシーバを実行時型で最派生実装に解決してから関数ポインタ化する (仮想束縛の確定)
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    var receiver = frame.Stack.Pop();
                    if (SlotOps.IsNullReference(receiver))
                        throw new UnhandledGuestException("System.NullReferenceException", null);
                    var resolved = target.Method is { } method
                        ? calls.DispatchVirtual(method, receiver)
                        : calls.TryDispatchVirtual(target.Name!, target.ParamCount, receiver)
                          ?? throw new OperationNotAllowedException(
                              $"ldvirtftn: {target.DeclaringType}::{target.Name} への関数ポインタ取得は対応していません。");
                    frame.Stack.Push(StackSlot.OfObject(new VmMethodPointer { Target = resolved }));
                    break;
                }
                case ILOp.Calli: {
                    // 間接呼出。スタック: fnptr, argN...arg1。オペランドは StandAloneSig (呼出規約 + 署名)。
                    // 関数ポインタはゲスト実装のみ (ldftn が intrinsic 面を拒否するため)
                    var signature = calls.DecodeStandAloneSignature(instruction.IntOperand);
                    var argCount = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                    var fnptr = frame.Stack.Pop();
                    var args = new StackSlot[argCount];
                    for (var i = argCount - 1; i >= 0; i--)
                        args[i] = frame.Stack.Pop();
                    ConsumeInstruction(); // 呼出ゲート: クォータ + セーフポイント
                    CheckSafepoint();
                    StackSlot? result = fnptr.ObjectValue switch {
                        VmMethodPointer pointer => Invoke(pointer.Target, args),
                        VmDelegate @delegate => calls.InvokeDelegate(@delegate, args),
                        _ => throw new UnhandledGuestException("System.ArgumentException",
                            "calli の関数ポインタが無効です (ldftn/ldvirtftn の結果を指定してください)。"),
                    };
                    if (SlotOps.SignatureReturnsValue(signature))
                        frame.Stack.Push(result ?? default);
                    break;
                }
                case ILOp.Ckfinite: {
                    var value = frame.Stack.Pop();
                    if (value.Kind == StackKind.Float &&
                        (double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue)))
                        throw new UnhandledGuestException("System.ArithmeticException", null);
                    frame.Stack.Push(value);
                    break;
                }
                case ILOp.Jmp: {
                    // 尾呼び移行: 現フレームの残りを実行せず、呼出先の戻り値をこのメソッドの戻り値とする
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    if (target.Method is null)
                        throw new OperationNotAllowedException(
                            $"jmp: intrinsic 面 {target.DeclaringType}::{target.Name} への尾呼び移行は対応していません。");
                    var args = new StackSlot[target.Arity];
                    for (var i = target.Arity - 1; i >= 0; i--)
                        args[i] = frame.Stack.Pop();
                    var method = target.Method!;
                    var context = calls.BuildCallContext(target, method, method.Signature.HasThis ? args[0] : default);
                    return Invoke(method, args, context);
                }

                // ---- TypedReference / varargs ----
                case ILOp.Mkrefany: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.Kind != StackKind.ByRef)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"mkrefany はマネージポインタ (&) を要求します: {SlotOps.Describe(value)}");
                    frame.Stack.Push(StackSlot.OfObject(new VmTypedReference { Slot = value, RefType = type }));
                    break;
                }
                case ILOp.Refanyval: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmTypedReference typed || !typed.RefType.IsAssignableTo(type))
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"refanyval: TypedReference の型 {SlotOps.Describe(value)} を {type.FullName} として取り出せません。");
                    frame.Stack.Push(typed.Slot);
                    break;
                }
                case ILOp.Refanytype: {
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmTypedReference typed)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"refanytype の被演算子が TypedReference ではありません: {SlotOps.Describe(value)}");
                    frame.Stack.Push(StackSlot.OfObject(_services.Heap.Allocate(new VmTypeHandle { Target = typed.RefType })));
                    break;
                }
                case ILOp.Arglist:
                    // varargs 呼出そのものは fail-closed (C# 産の IL では生成されない)。ハンドルの一貫性のみ提供
                    frame.Stack.Push(StackSlot.OfObject(new VmArgList { Args = [] }));
                    break;

                // ---- 生メモリ系 ----
                case ILOp.Localloc: {
                    var bytes = frame.Stack.Pop().AsInt32;
                    if (bytes < 0)
                        throw new UnhandledGuestException("System.OverflowException", null);
                    // VM 内表現: 実バイト列の仮想メモリブロック (ヒープ確保・実バイト数を計上) を作り、
                    // 先頭バイトへの unmanaged ポインタを返す。実 CLR と異なり初期化は 0 (安全側の
                    // 上限動作)、フレーム終了でも解放されない (GC 管理) = 脱出 stackalloc も安全側に動く
                    // 上限検査と計上はホスト側の実確保 (new byte[]) より先に行う — 巨大確保が
                    // チェック前にホストメモリを圧迫しないよう Reserve → 実確保 → Commit の順。
                    // 実確保が失敗したら予約トランザクションの Dispose が計上を巻き戻す
                    using var reservation = _services.Heap.ReserveLocalloc(bytes);
                    var memory = reservation.Commit(new VmLocallocMemory { Bytes = new byte[bytes] });
                    frame.Stack.Push(StackSlot.OfObject(new VmNativePointer { Memory = memory, ByteOffset = 0 }));
                    break;
                }
                case ILOp.Cpblk: {
                    // スタック: dst, src, size (逆順に pop)
                    var size = frame.Stack.Pop().AsInt32;
                    var src = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    MemoryOps.CopyMemoryBlock(dst, src, size);
                    break;
                }
                case ILOp.Initblk: {
                    var size = frame.Stack.Pop().AsInt32;
                    var value = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    MemoryOps.InitMemoryBlock(dst, value, size);
                    break;
                }
                case ILOp.Sizeof:
                    // ECMA-335 III.4.14: 結果は unsigned int32 として積む (C# の sizeof(T) の結果型は int)
                    frame.Stack.Push(StackSlot.OfInt32(
                        MemoryOps.SizeOfType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens))));
                    break;

                // ---- M5 以降の命令 ----
                case ILOp.Ldtoken: {
                    // ldtoken Field は FieldRVA 初期データのハンドル (RuntimeHelpers::InitializeArray 用)、
                // Type は typeof() 用の RuntimeTypeHandle、Method は MethodBase::GetMethodFromHandle 用
                    if (frame.Method.DynamicTokens?.TryGetValue(unchecked((uint)instruction.IntOperand), out var dynamicReference) == true) {
                        switch (dynamicReference) {
                            case VmType dynamicType:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmTypeHandle { Target = dynamicType })));
                                break;
                            case VmMethod dynamicMethod:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmMethodHandle { Target = dynamicMethod })));
                                break;
                            case VmField dynamicField:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmFieldHandle { Target = dynamicField })));
                                break;
                            default:
                                throw new NotSupportedException("動的 ldtoken の参照種別は未対応です。");
                        }
                        break;
                    }
                    var tokenTable = (TableKind)((uint)instruction.IntOperand >> 24);
                    var tokenRid = (int)((uint)instruction.IntOperand & 0xFFFFFF);
                    switch (tokenTable) {
                        case TableKind.Field: {
                            var rva = loader.Image.GetFieldRva(tokenRid);
                            if (rva == 0)
                                throw new BadImageFormatException($"Field rid {tokenRid} に FieldRVA エントリがありません。");
                            var handle = _services.Heap.Allocate(new VmFieldRvaData { Data = loader.Image.GetRvaDataToEnd(rva) });
                            frame.Stack.Push(StackSlot.OfObject(handle));
                            break;
                        }
                        case TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec: {
                            var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                            frame.Stack.Push(StackSlot.OfObject(
                                _services.Heap.Allocate(new VmTypeHandle { Target = type })));
                            break;
                        }
                        case TableKind.MethodDef: {
                            var method = loader.GetMethodByToken((uint)instruction.IntOperand)
                                ?? throw new BadImageFormatException($"MethodDef rid {tokenRid} を解決できません。");
                            frame.Stack.Push(StackSlot.OfObject(
                                _services.Heap.Allocate(new VmMethodHandle { Target = method })));
                            break;
                        }
                        default:
                            throw new NotSupportedException(
                                $"ldtoken は Field/Type/Method トークンのみ対応しています (要求: {tokenTable})。");
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"IL 命令 {IlOpcodeTable.Get(instruction.Op)?.Name ?? instruction.Op.ToString()} は未対応です (ジェネリック/JIT は今後のフェーズで実装)。");
            }
            frame.Ip++;
        }
    }

    private static void CheckArgIndex(InterpreterFrame frame, int index) {
        if ((uint)index >= (uint)frame.Arguments.Length)
            throw new BadImageFormatException(
                $"引数インデックス {index} が範囲外です ({frame.Method} は引数 {frame.Arguments.Length} 個)。");
    }

    private static void CheckLocalIndex(InterpreterFrame frame, int index) {
        if ((uint)index >= (uint)frame.Locals.Length)
            throw new BadImageFormatException(
                $"ローカル変数インデックス {index} が範囲外です ({frame.Method} はローカル {frame.Locals.Length} 個)。");
    }

    private sealed class ActionLease(Action action) : IDisposable {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    internal void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _heap.RemoveRootSlotSource(_frameRootSource);
        _coordinator.Dispose();
    }
}
