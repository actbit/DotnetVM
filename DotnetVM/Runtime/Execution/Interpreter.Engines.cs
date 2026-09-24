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

public sealed partial class Interpreter {
    /// <summary>1 アセンブリ (TypeLoader) 分の実行エンジン。token 解決はすべてこの loader の画像に対して行う。</summary>
    private sealed class LoaderEngines {
        public required InterpreterServices Services { get; init; }
        public required MethodPreparer Preparer { get; init; }
        public required ObjectEngine Objects { get; init; }
        public required CallEngine Calls { get; init; }
        public required ExceptionDispatcher Exceptions { get; init; }
        public required JitCodeCache Jit { get; init; }
        public required Func<IEnumerable<StackSlot[]>> StaticStorageRoots { get; init; }
        public required Func<IEnumerable<StackSlot[]>> IntrinsicStaticRoots { get; init; }
    }

    internal Interpreter(TypeLoader loader, IntrinsicRegistry intrinsics, VmConsole console, MemoryPolicy memory, VmHeap heap,
        bool enableJit = false, int jitPromotionThreshold = 1000,
        NetworkGateway? network = null, StorageGateway? storage = null, Diagnostics.ExecutionTracer? tracer = null,
        VmCoreLibSurfaces? coreLibSurfaces = null, VmType? stringType = null, VmSharedState? shared = null,
        Func<ReadOnlyMemory<byte>, TypeLoader>? loadAssemblyFromBytes = null,
        VmAssemblyLoadContext? defaultAssemblyLoadContext = null,
        Func<string?, bool, VmAssemblyLoadContext>? createAssemblyLoadContext = null,
        Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? loadAssemblyInContext = null,
        Func<VmAssemblyLoadContext, string, TypeLoader>? loadAssemblyFromPath = null) {
        _memory = memory;
        _enableJit = enableJit;
        _jitPromotionThreshold = jitPromotionThreshold;
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
        if (loader is not null && loader.Context is null && !ReferenceEquals(loader, _services.Loader))
            throw new ObjectDisposedException(nameof(VmAssemblyLoadContext),
                $"メソッド {method} のアセンブリロードコンテキストは既にアンロードされています。");
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
        intrinsicContext.InvokeGuestInstanceMethod = (receiver, name, arguments) =>
            calls.InvokeGuestInstanceMethod(receiver, name, arguments);
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
        Func<IEnumerable<StackSlot[]>> staticStorageRoots = services.Objects.EnumerateStaticStorage;
        Func<IEnumerable<StackSlot[]>> intrinsicStaticRoots = () => objects.IntrinsicStaticFields.ToArray();
        _heap.AddRootSlotSource(staticStorageRoots);
        _heap.AddRootSlotSource(intrinsicStaticRoots);
        return new LoaderEngines {
            Services = services,
            Preparer = preparer,
            Objects = objects,
            Calls = calls,
            Exceptions = exceptions,
            Jit = new JitCodeCache(_enableJit, _jitPromotionThreshold),
            StaticStorageRoots = staticStorageRoots,
            IntrinsicStaticRoots = intrinsicStaticRoots,
        };
    }
}
