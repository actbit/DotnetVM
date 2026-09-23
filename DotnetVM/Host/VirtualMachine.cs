using DotnetVM.Devices;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Host;

/// <summary>
/// VM の組み込みファサード。アセンブリロード → メソッド明示指定実行 → 結果/仮想I/O取得の一連を提供する。
/// </summary>
public sealed class VirtualMachine : IDisposable {
    private readonly VmHostOptions _options;
    private readonly IntrinsicRegistry _intrinsics = new();
    private readonly VmConsole _console = new();
    private readonly VmHeap _heap;
    private readonly GcHandleTable _handles = new();
    private readonly NetworkGateway _network;
    private readonly StorageGateway _storage;
    private readonly List<TypeLoader> _loaders = [];
    private readonly object _assemblyGate = new();
    private readonly VmAssemblyContext _context;
    private readonly VmAssemblyLoadContext _defaultAssemblyLoadContext;
    private readonly VmSharedState _sharedState;
    private readonly object _interpreterGate = new();
    private readonly object _lifetimeGate = new();
    private Interpreter? _interpreter;
    private VmCoreLibSurfaces? _coreLibSurfaces;
    private VmClassType? _stringType;
    private readonly Func<IEnumerable<VmObject?>> _handleRoots;
    private readonly Func<IEnumerable<VmObject?>> _typeFacadeRoots;
    private readonly Func<IEnumerable<VmObject?>> _guestThreadRoots;
    private readonly Func<IEnumerable<StackSlot[]>> _guestTaskRoots;
    private int _disposed;

    public VirtualMachine(VmHostOptions? options = null) {
        _options = options ?? new VmHostOptions();
        _options.Memory.Validate();
        if (_options.MaxGuestThreads < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxGuestThreads は 1 以上である必要があります。");
        if (_options.MaxTaskWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTaskWorkers は 1 以上である必要があります。");
        if (_options.MaxGuestWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxGuestWorkers は 1 以上である必要があります。");
        if (_options.MaxPendingTaskTimers < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPendingTaskTimers は 1 以上である必要があります。");
        if (_options.ShutdownTimeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeoutMilliseconds は 0 以上である必要があります。");
        _sharedState = new VmSharedState(_options.MaxGuestThreads, _options.MaxTaskWorkers,
            _options.MaxGuestWorkers, _options.MaxPendingTaskTimers, _options.ShutdownTimeoutMilliseconds);
        _heap = new VmHeap(_options.Memory, _options.Gc);
        _handleRoots = _handles.EnumerateRoots;
        _heap.AddRootObjectSource(_handleRoots); // ホスト保持参照 (GCHandle 相当) をルートに
        _typeFacadeRoots = _sharedState.EnumerateRoots;
        _guestThreadRoots = _sharedState.GuestThreads.EnumerateRoots;
        _guestTaskRoots = _sharedState.GuestTasks.EnumerateRoots;
        _heap.AddRootObjectSource(_typeFacadeRoots);
        _heap.AddRootObjectSource(_guestThreadRoots);
        _heap.AddRootSlotSource(_guestTaskRoots);
        _network = new NetworkGateway(_options.Network, _options.NetworkBridge);
        _storage = new StorageGateway(_options.Storage, _options.StorageBridge);
        _context = new VmAssemblyContext(LoadDependencyAssembly);
        _defaultAssemblyLoadContext = new VmAssemblyLoadContext {
            Context = _context,
            Name = "Default",
            IsCollectible = false,
            IsDefault = true,
        };
        DefaultIntrinsics.RegisterAll(_intrinsics);
        if (_options.UseDefaultCoreLibBindings)
            new BuiltInCoreLibBindingProvider().RegisterBindings(_intrinsics); // 外部プロバイダーと同じ登録契約を使う
        foreach (var provider in _options.CoreLibBindingProviders) {
            if (provider is null)
                throw new ArgumentException("CoreLibBindingProviders に null は指定できません。", nameof(options));
            provider.RegisterBindings(_intrinsics);
        }
        if (_options.LoadHostCoreLib)
            LoadHostCoreLib();
    }

    /// <summary>ホスト実行環境の本物の System.Private.CoreLib.dll をロードする
    /// (VmHostOptions.LoadHostCoreLib = true 時に VM 構築時に呼ぶ)。</summary>
    private void LoadHostCoreLib() {
        var coreLibPath = typeof(object).Assembly.Location;
        if (string.IsNullOrEmpty(coreLibPath))
            throw new InvalidOperationException("ホストの System.Private.CoreLib.dll の場所を特定できません (Single-file 発行等)。");
        LoadAssembly(coreLibPath);
        TypeLoader coreLibLoader;
        lock (_assemblyGate)
            coreLibLoader = _loaders.First(l => l.Image.Identity.Name == "System.Private.CoreLib");
        // VM CoreLib (置換面の managed IL 実装) を DotnetVM.dll と同じディレクトリからロードし、
        // 実在 CoreLib の面 → DotnetVM.CoreLib IL の置換辞書を構築する (欠面は fail-closed)
        var vmCoreLibPath = Path.Combine(
            Path.GetDirectoryName(typeof(VirtualMachine).Assembly.Location)!, "DotnetVM.CoreLib.dll");
        if (!File.Exists(vmCoreLibPath))
            throw new InvalidOperationException(
                $"VM CoreLib ({vmCoreLibPath}) が見つかりません。DotnetVM.CoreLib.dll を DotnetVM.dll と同じディレクトリに配置してください。");
        // trusted CoreLib の identity は LoadHostCoreLib が取得した loader 参照そのもの:
        // タスク 2 hardening (trusted identity) — ファイル名照合でなく trusted marker。
        // CallEngine はこの marker (TypeLoader.IsTrustedCoreLib) で caller domain を判定し、
        // TrustedCoreLib domain の特権 binding (Kernel32 / Marshal 等) の呼出を CoreLib IL
        // に限定する。ゲストが同名 DLL を偽配置しても trusted にならない。
        // identity (Name / PublicKeyToken) が想定の System.Private.CoreLib であることを
        // 検証してから mark する (防御深度)。
        var coreLibIdentity = coreLibLoader.Image.Identity;
        if (!coreLibIdentity.Name.Equals("System.Private.CoreLib", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ホスト CoreLib の identity が想定外です: {coreLibIdentity} (System.Private.CoreLib を期待)。");
        coreLibLoader.IsTrustedCoreLib = true;
        LoadAssembly(vmCoreLibPath);
        // 置換対象 (Substitute の呼出元) を trusted System.Private.CoreLib 画像に限定する
        // (タスク 2 hardening: ゲスト画像や依存画像が同名面を宣言しても置換されない)。
        // ファイル名照合でなく loader 参照 (identity) でマークする
        TypeLoader vmCoreLibLoader;
        lock (_assemblyGate)
            vmCoreLibLoader = _loaders[^1];
        var surfaces = VmCoreLibSurfaces.Create(vmCoreLibLoader);
        surfaces.MarkTrustedCoreLib(coreLibLoader);
        _coreLibSurfaces = surfaces;
        // VmString の型同一性を System.String 実型 (CoreLib TypeDef) に接続する
        // (castclass IConvertible / インターフェースディスパッチ / String IL 面)。
        // VM 単位の状態として Interpreter へ渡す (静的に持つと並列実行する VM 間で
        // Dispose 競合が起きるため)
        _stringType = _loaders
            .Select(l => l.FindTypeByFullName("System.String"))
            .OfType<VmClassType>()
            .FirstOrDefault();
    }

    /// <summary>VmAssemblyContext が依存アセンブリの同一ディレクトリ探索で見つけた DLL をロードする。</summary>
    private TypeLoader LoadDependencyAssembly(string path) {
        using var stream = File.OpenRead(path);
        return LoadAssemblyLoader(stream, Path.GetFullPath(path));
    }

    /// <summary>ゲスト AssemblyLoadContext 用の名前付き VM ローダーを生成する。</summary>
    private VmAssemblyLoadContext CreateAssemblyLoadContext(string? name, bool isCollectible) {
        VmAssemblyContext? context = null;
        context = new VmAssemblyContext(
            path => LoadDependencyAssemblyFromStorage(path, context!),
            _context,
            path => _storage.IsEnabled && _storage.Exists(path));
        var loadContext = new VmAssemblyLoadContext {
            Context = context,
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            IsCollectible = isCollectible,
            IsDefault = false,
            UnloadAction = () => UnloadAssemblyLoadContext(context),
        };
        return loadContext;
    }

    /// <summary>ゲスト ALC の依存アセンブリをストレージブリッジから読み込む。</summary>
    private TypeLoader LoadDependencyAssemblyFromStorage(string path, VmAssemblyContext context) {
        var bytes = _storage.Read(path, _options.Memory.MaxAssemblyBytes);
        if (bytes.Length > _options.Memory.MaxAssemblyBytes)
            throw new OperationNotAllowedException(
                $"AssemblyLoadContext の入力が上限を超えています (上限 {_options.Memory.MaxAssemblyBytes:N0} バイト)。");
        _heap.ChargeHostBuffer(bytes.Length);
        var image = AssemblyImage.Parse(bytes, limits: _options.Memory);
        image.SourcePath = Path.GetFullPath(path);
        return RegisterAssemblyImage(image, context);
    }

    /// <summary>ゲスト ALC に byte[] を VM アセンブリとして解析・登録する。入力コピーの quota は intrinsic 側で事前計上済み。</summary>
    private TypeLoader LoadAssemblyBytesInContext(VmAssemblyLoadContext loadContext, ReadOnlyMemory<byte> bytes) {
        ThrowIfDisposed();
        lock (loadContext.LifetimeGate) {
            if (loadContext.IsUnloaded)
                throw new ObjectDisposedException(nameof(VmAssemblyLoadContext));
            if (bytes.Length > _options.Memory.MaxAssemblyBytes)
                throw new OperationNotAllowedException(
                    $"AssemblyLoadContext の入力が上限を超えています (上限 {_options.Memory.MaxAssemblyBytes:N0} バイト)。");
            var image = AssemblyImage.Parse(bytes, limits: _options.Memory);
            return RegisterAssemblyImage(image, loadContext.Context);
        }
    }

    /// <summary>ゲスト ALC のパスロード。ホストファイル API ではなくストレージブリッジを使う。</summary>
    private TypeLoader LoadAssemblyPathInContext(VmAssemblyLoadContext loadContext, string path) {
        ThrowIfDisposed();
        lock (loadContext.LifetimeGate) {
            if (loadContext.IsUnloaded)
                throw new ObjectDisposedException(nameof(VmAssemblyLoadContext));
            if (!_storage.IsEnabled)
                throw new OperationNotAllowedException(
                    "AssemblyLoadContext.LoadFromAssemblyPath はストレージブリッジが設定されている場合のみ利用できます。");
            var fullPath = Path.GetFullPath(path);
            var bytes = _storage.Read(fullPath, _options.Memory.MaxAssemblyBytes);
            if (bytes.Length > _options.Memory.MaxAssemblyBytes)
                throw new OperationNotAllowedException(
                    $"AssemblyLoadContext の入力が上限を超えています (上限 {_options.Memory.MaxAssemblyBytes:N0} バイト)。");
            _heap.ChargeHostBuffer(bytes.Length);
            var image = AssemblyImage.Parse(bytes, limits: _options.Memory);
            image.SourcePath = fullPath;
            return RegisterAssemblyImage(image, loadContext.Context);
        }
    }

    private void UnloadAssemblyLoadContext(VmAssemblyContext context) {
        // VmType のキャッシュキーから TypeLoader に到達できるため、Unregister で所属情報を外す前に除去する。
        _sharedState.RemoveAssemblyContextCaches(context);
        lock (_interpreterGate)
            _interpreter?.RemoveAssemblyContextCaches(context);
        lock (_assemblyGate) {
            foreach (var loader in context.Loaders)
                _loaders.Remove(loader);
            foreach (var loader in context.Loaders)
                context.Unregister(loader);
        }
    }

    /// <summary>仮想コンソールデバイス (出力購読/入力バインド/実装差し替え)。</summary>
    public VmConsole Console => _console;

    /// <summary>GC ハンドル表 (ホストがゲストオブジェクトを GC をまたいで強参照保持する)。</summary>
    public GcHandleTable Handles => _handles;

    /// <summary>VM ヒープ (テスト/診断用)。</summary>
    internal VmHeap Heap => _heap;

    /// <summary>ネットワークゲートウェイ (累計転送バイトの診断用)。</summary>
    internal NetworkGateway Network => _network;

    /// <summary>ストレージゲートウェイ (累計転送バイトの診断用)。</summary>
    internal StorageGateway Storage => _storage;

    /// <summary>GC を起動し統計を返す (通常はアロケーション間隔で自動起動。明示起動はホスト用)。</summary>
    public GcStatistics CollectGarbage() {
        ThrowIfDisposed();
        return RunGuest(() => GetInterpreter().CollectGarbage());
    }

    /// <summary>ロード済みアセンブリの型ローダ。</summary>
    public IReadOnlyList<TypeLoader> Loaders { get { lock (_assemblyGate) return _loaders.ToArray(); } }

    /// <summary>多アセンブリ ロード コンテキスト (AssemblyRef 依存解決)。</summary>
    public VmAssemblyContext Context => _context;

    /// <summary>累積実行命令数。</summary>
    public long InstructionCount => _interpreter?.InstructionCount ?? 0;

    /// <summary>intrinsic を起動前に追加登録する (実行開始後は不可)。</summary>
    public void RegisterIntrinsic(IntrinsicKey key, IntrinsicImpl impl) {
        ThrowIfDisposed();
        _intrinsics.Register(key, impl);
    }

    /// <summary>ランタイムバインドを起動前に追加登録する (実行開始後は不可。P/Invoke 代替等)。</summary>
    public void RegisterBinding(BindingKey key, IntrinsicImpl impl, BindingOrigin origin) {
        ThrowIfDisposed();
        _intrinsics.RegisterBinding(key, impl, origin);
    }

    /// <summary>登録済みランタイムバインドの監査面 (キーと由来。監査テスト / 診断用)。</summary>
    public IReadOnlyList<(BindingKey Key, BindingOrigin Origin)> Bindings => _intrinsics.Bindings;

    /// <summary>登録済み legacy intrinsic キーの列挙 (監査テスト / 診断用)。</summary>
    public IEnumerable<IntrinsicKey> IntrinsicKeys => _intrinsics.Keys;

    /// <summary>DLL アセンブリをファイルからロードする (EXE は不要/非対応)。
    /// AssemblyRef による依存アセンブリは、参照元と同一ディレクトリの同名 DLL から自動解決される。</summary>
    public AssemblyImage LoadAssembly(string path) {
        ThrowIfDisposed();
        using var stream = File.OpenRead(path);
        return LoadAssemblyLoader(stream, Path.GetFullPath(path)).Image;
    }

    /// <summary>DLL アセンブリをストリームからロードする。
    /// Stream ロードは依存アセンブリの同一ディレクトリ探索を行わない (SourcePath を設定
    /// しない = 探索ヒントなし): ゲストが提供する stream の依存関係は、ホストが明示的に
    /// LoadAssembly(path) / LoadDependencyAssembly で解決するか、resolver を登録する。
    /// host current directory への暗黙フォールバック (SourcePath ?? ".") を廃止した (タスク 2)。</summary>
    public AssemblyImage LoadAssembly(Stream peStream, string? sourcePath = null) {
        return LoadAssemblyLoader(peStream, sourcePath).Image;
    }

    private TypeLoader LoadAssemblyLoader(Stream peStream, string? sourcePath) =>
        LoadAssemblyLoader(peStream, sourcePath, _context);

    private TypeLoader LoadAssemblyLoader(Stream peStream, string? sourcePath, VmAssemblyContext context) {
        ThrowIfDisposed();
        // 入力サイズ上限 (loader hardening): 読み込み途中で強制する (非 seekable な入力も含め、
        // 上限を超えた時点で打ち切って拒否する。巨大 stream を丸ごと buffer してから判定しない)
        var maxBytes = _options.Memory.MaxAssemblyBytes;
        using var buffered = new MemoryStream();
        // maxBytes が小さい場合は上限を検査するために必要な 1 バイトだけを
        // 余分に読む。固定 80 KiB の host 一時配列を先に確保しない。
        var copyBufferSize = maxBytes >= 81920
            ? 81920
            : checked((int)Math.Max(1, maxBytes + 1));
        var copyBuffer = new byte[copyBufferSize];
        while (true) {
            var n = peStream.Read(copyBuffer, 0, copyBuffer.Length);
            if (n == 0)
                break; // EOF
            if (n > maxBytes - buffered.Position)
                throw new OperationNotAllowedException(
                    $"LoadAssembly の入力が上限を超えています (上限 {maxBytes:N0} バイト。読込途中で打ち切りました)。");
            buffered.Write(copyBuffer, 0, n);
        }
        var image = AssemblyImage.Parse(buffered.ToArray(), limits: _options.Memory);
        image.SourcePath = sourcePath;
        return RegisterAssemblyImage(image, context);
    }

    /// <summary>Assembly.Load(byte[]) 用の画像登録。入力コピーの quota は intrinsic 側で事前計上し、ここでは VM loader で解析する。</summary>
    private TypeLoader LoadAssemblyBytes(ReadOnlyMemory<byte> bytes) {
        ThrowIfDisposed();
        if (bytes.Length > _options.Memory.MaxAssemblyBytes)
            throw new OperationNotAllowedException(
                $"Assembly.Load の入力が上限を超えています (上限 {_options.Memory.MaxAssemblyBytes:N0} バイト)。");
        var image = AssemblyImage.Parse(bytes, limits: _options.Memory);
        return RegisterAssemblyImage(image, _context);
    }

    private TypeLoader RegisterAssemblyImage(AssemblyImage image, VmAssemblyContext? context = null) {
        ThrowIfDisposed();
        context ??= _context;
        var loader = new TypeLoader(image);
        // 界面の再現制御: ブリッジが設定されている場合のみ対応する I/O ファサード型を合成する。
        // 未設定ならゲストはその型を解決できず、ロード/呼出の時点で fail-closed になる
        if (_options.NetworkBridge is not null)
            loader.AddIoFacade("WebClient");
        if (_options.StorageBridge is not null)
            loader.AddIoFacade("File");
        lock (_assemblyGate) {
            context.Register(loader);
            _loaders.Add(loader);
        }
        try {
            loader.CompletePendingTypes();
        } catch {
            lock (_assemblyGate) {
                context.Unregister(loader);
                _loaders.Remove(loader);
            }
            throw;
        }
        return loader;
    }

    /// <summary>
    /// 型とメソッド名を明示指定してゲスト関数を呼び出す。
    /// 対応しているのは静的メソッド (インスタンスメソッドは CreateInstance + CallInstance を使用)。
    /// </summary>
    public object? Invoke(string typeFullName, string methodName, params object?[] args) {
        ThrowIfDisposed();
        var method = FindMethod(typeFullName, methodName, args);
        return RunGuest(() => Execute(method, args).ReturnValue);
    }

    /// <summary>ホスト境界: 未処理のゲスト例外 (内部キャリア) を UnhandledGuestException に変換する。</summary>
    private static T RunGuest<T>(Func<T> action) {
        try {
            return action();
        } catch (VmGuestThrow guest) {
            throw new UnhandledGuestException(guest.ExceptionTypeName, guest.MessageText);
        }
    }

    /// <summary>
    /// 型のインスタンスを生成する (newobj 相当: 既定値ストレージ生成 + .ctor 実行)。
    /// 戻り値は VM オブジェクト (CallInstance のレシーバや Invoke の引数に使える)。
    /// </summary>
    public VmClassInstance CreateInstance(string typeFullName, params object?[] args) {
        ThrowIfDisposed();
        var type = FindType(typeFullName);
        var ctor = FindConstructor(type, args.Length);
        var interpreter = GetInterpreter();
        using var operation = interpreter.EnterHostOperation();
        var ctorArgs = new StackSlot[args.Length];
        for (var i = 0; i < args.Length; i++)
            ctorArgs[i] = ToSlot(args[i], ctor.Signature.ParamTypes[i], interpreter);
        return RunGuest(() => interpreter.CreateInstance(type, ctorArgs));
    }

    /// <summary>インスタンスメソッドを明示指定して呼び出す (仮想メソッドは最派生実装を実行)。</summary>
    public object? CallInstance(VmClassInstance instance, string methodName, params object?[] args) {
        ThrowIfDisposed();
        var method = FindInstanceMethod(instance.ClassType, methodName, args.Length);
        var interpreter = GetInterpreter();
        using var operation = interpreter.EnterHostOperation();
        var slots = new StackSlot[args.Length + 1];
        slots[0] = StackSlot.OfObject(instance);
        for (var i = 0; i < args.Length; i++)
            slots[i + 1] = ToSlot(args[i], method.Signature.ParamTypes[i], interpreter);
        return RunGuest(() => {
            var result = interpreter.Invoke(method, slots);
            return FromSlot(result, method.Signature.ReturnType);
        });
    }

    /// <summary>メソッドを実行し、戻り値・コンソール出力スナップショット・命令数を返す。</summary>
    public ExecutionResult Execute(VmMethod method, params object?[] args) {
        ThrowIfDisposed();
        if (!method.IsStatic)
            throw new NotSupportedException($"インスタンスメソッド {method} はオブジェクトモデル (M3) 以降に対応します。静的メソッドを指定してください。");
        var signature = method.Signature;
        if (signature.ParamTypes.Length != args.Length)
            throw new ArgumentException($"引数個数が一致しません ({method}: 期待 {signature.ParamTypes.Length}, 実際 {args.Length})。");

        var interpreter = GetInterpreter();
        using var operation = interpreter.EnterHostOperation();
        var slots = new StackSlot[args.Length];
        for (var i = 0; i < args.Length; i++)
            slots[i] = ToSlot(args[i], signature.ParamTypes[i], interpreter);

        var countBefore = interpreter.CurrentThreadInstructionCount;
        return RunGuest(() => {
            var result = interpreter.Invoke(method, slots);
            return new ExecutionResult(
                FromSlot(result, method.Signature.ReturnType),
                _console.OutputLog,
                interpreter.CurrentThreadInstructionCount - countBefore);
        });
    }

    /// <summary>静的メソッドを解決する (名前 + 引数個数 + 変換可能性で最良の候補を選ぶ)。</summary>
    private VmMethod FindMethod(string typeFullName, string methodName, object?[] args) {
        var type = FindType(typeFullName);

        var candidates = type.Methods
            .Where(m => m.Name == methodName && m.IsStatic && m.Body is not null)
            .Where(m => m.Signature.ParamTypes.Length == args.Length)
            .Where(m => Enumerable.Range(0, args.Length).All(i => CanConvert(args[i], m.Signature.ParamTypes[i])))
            .OrderByDescending(m => Enumerable.Range(0, args.Length).Sum(i => ExactnessScore(args[i], m.Signature.ParamTypes[i])))
            .ToList();

        if (candidates.Count == 0)
            throw new ArgumentException(
                $"メソッド '{typeFullName}::{methodName}' (引数 {args.Length} 個) の静的な候補が見つかりません。");
        return candidates[0];
    }

    private VmClassType FindType(string typeFullName) {
        ThrowIfDisposed();
        lock (_assemblyGate) {
            if (_loaders.Count == 0)
                throw new InvalidOperationException("アセンブリがロードされていません。先に LoadAssembly を呼んでください。");
            return _loaders
                .Select(l => l.FindTypeByFullName(typeFullName) ?? l.FindTypeByName(typeFullName))
                .FirstOrDefault(t => t is not null)
                ?? throw new ArgumentException($"型 '{typeFullName}' がロード済みアセンブリに見つかりません。");
        }
    }

    /// <summary>コンストラクタを解決する (引数個数 + 変換可能性で最良候補)。</summary>
    private VmMethod FindConstructor(VmClassType type, int argCount) =>
        type.Methods
            .Where(m => m.Name == ".ctor" && !m.IsStatic && m.Body is not null)
            .Where(m => m.Signature.ParamTypes.Length == argCount)
            .OrderByDescending(m => Enumerable.Range(0, argCount).Sum(i => ExactnessScore(null, m.Signature.ParamTypes[i])))
            .FirstOrDefault()
            ?? throw new ArgumentException($"型 '{type.FullName}' に引数 {argCount} 個の .ctor が見つかりません。");

    /// <summary>インスタンスメソッドを解決する (基底連鎖を辿り最派生の実装を返す = VTable 相当)。</summary>
    private VmMethod FindInstanceMethod(VmClassType type, string methodName, int paramCount) {
        for (var t = (VmType?)type; t is VmClassType cls; t = cls.BaseType) {
            var method = cls.Methods.FirstOrDefault(m =>
                m.Name == methodName && !m.IsStatic &&
                m.Signature.ParamTypes.Length == paramCount && m.Body is not null);
            if (method is not null)
                return method;
        }
        throw new ArgumentException($"型 '{type.FullName}' にインスタンスメソッド '{methodName}' (引数 {paramCount} 個) が見つかりません。");
    }

    /// <summary>VM ごとの共有状態 (仮想環境変数ストア / last system error)。
    /// static 共有にしない (VM ごとに分離する)。</summary>
    public VmSharedState SharedState => _sharedState;

    /// <summary>仮想環境変数を設定する (Kernel32.GetEnvironmentVariable 面が読む VM ごとのストア)。
    /// value が null なら削除する。host の実環境変数には触れない。</summary>
    public void SetVirtualEnvironmentVariable(string name, string? value) {
        ThrowIfDisposed();
        if (value is null)
            _sharedState.VirtualEnvironment.TryRemove(name, out _);
        else
            _sharedState.VirtualEnvironment[name] = value;
    }

    /// <summary>仮想環境変数を取得する (未定義なら null)。</summary>
    public string? GetVirtualEnvironmentVariable(string name) {
        ThrowIfDisposed();
        return _sharedState.VirtualEnvironment.TryGetValue(name, out var value) ? value : null;
    }

    private Interpreter GetInterpreter() {
        ThrowIfDisposed();
        lock (_interpreterGate) {
            ThrowIfDisposed();
            return _interpreter ??= new Interpreter(GetPrimaryLoader(), _intrinsics, _console, _options.Memory, _heap,
                _network, _storage, Tracer, _coreLibSurfaces, _stringType, _sharedState, LoadAssemblyBytes,
                _defaultAssemblyLoadContext, CreateAssemblyLoadContext, LoadAssemblyBytesInContext,
                LoadAssemblyPathInContext);
        }
    }

    /// <summary>実行トレース (どのアセンブリ/メソッドの IL フレームが実行されたか)。
    /// Tracer.Start() で記録を有効化してから Invoke する (常時記録はしない)。</summary>
    public Diagnostics.ExecutionTracer Tracer { get; } = new();

    private TypeLoader GetPrimaryLoader() {
        lock (_assemblyGate) {
            if (_loaders.Count == 0)
                throw new InvalidOperationException("アセンブリがロードされていません。");
            return _loaders[^1];
        }
    }

    // ---- ホスト値 ↔ VM スロット変換 ----

    private static bool CanConvert(object? value, SigType type) => type.Kind switch {
        SigKind.I4 or SigKind.I1 or SigKind.U1 or SigKind.I2 or SigKind.U2 or SigKind.U4
            or SigKind.Boolean or SigKind.Char => value is int or uint or byte or sbyte or short or ushort or bool or char or long,
        SigKind.I8 or SigKind.U8 => value is long or int or uint or ulong,
        SigKind.R4 or SigKind.R8 => value is double or float or int or long,
        SigKind.String => value is string or null,
        // object パラメータはゲスト側で box されるためプリミティブも許容
        SigKind.Object => value is null or string or VmObject
            or int or uint or byte or sbyte or short or ushort or long or ulong or bool or char or float or double,
        SigKind.SzArray => value is null or VmObject or System.Array,
        SigKind.TypeToken or SigKind.GenericInst => value is null or VmObject or decimal,
        SigKind.Void => false,
        _ => value is null,
    };

    private static int ExactnessScore(object? value, SigType type) {
        if (value is null)
            return 1;
        return (type.Kind, value) switch {
            (SigKind.I4, int) or (SigKind.Boolean, bool) or (SigKind.Char, char) => 2,
            (SigKind.I8, long) or (SigKind.R8, double) => 2,
            (SigKind.R4, float) => 2,
            (SigKind.String, string) => 2,
            (SigKind.TypeToken, decimal) => 3,
            (SigKind.Object, string) => 1,
            _ => 0,
        };
    }

    private static StackSlot ToSlot(object? value, SigType type, Interpreter interpreter) {
        if (value is null)
            return type.Kind is SigKind.String or SigKind.Object or SigKind.TypeToken
                or SigKind.GenericInst or SigKind.SzArray
                ? StackSlot.Null
                : InterpreterFrame.DefaultValue(type);
        // object パラメータはゲスト側でボックス化済み参照として扱われるため、
        // プリミティブはホスト境界で box する (IL の callvirt Object::ToString 等が正しく動くように)
        return value switch {
            bool b => Prim(type, interpreter, "System.Boolean", StackSlot.OfInt32(b ? 1 : 0)),
            char c => Prim(type, interpreter, "System.Char", StackSlot.OfInt32(c)),
            int i => Prim(type, interpreter, "System.Int32", StackSlot.OfInt32(i)),
            uint u => Prim(type, interpreter, "System.UInt32", StackSlot.OfInt32((int)u)),
            byte b => Prim(type, interpreter, "System.Byte", StackSlot.OfInt32(b)),
            sbyte b => Prim(type, interpreter, "System.SByte", StackSlot.OfInt32(b)),
            short s => Prim(type, interpreter, "System.Int16", StackSlot.OfInt32(s)),
            ushort s => Prim(type, interpreter, "System.UInt16", StackSlot.OfInt32(s)),
            long l => Prim(type, interpreter, "System.Int64", StackSlot.OfInt64(l)),
            ulong ul => Prim(type, interpreter, "System.UInt64", StackSlot.OfInt64((long)ul)),
            float f => Prim(type, interpreter, "System.Single", StackSlot.OfFloat(f)),
            double d => Prim(type, interpreter, "System.Double", StackSlot.OfFloat(d)),
            // decimal は VM 側一般为構造体値 (System.Decimal 統合型) で表現されるため、
            // フィールドスロット (_flags / _hi32 / _lo64。.NET 10 CoreLib レイアウト) を
            // 構築する (相互の型同一性は C2 統合辞書の実型 = CoreLib TypeDef)
            decimal d => VMDecimalToSlot(d, type, interpreter),
            string s => StackSlot.OfObject(interpreter.Strings.GetOrNew(s)),
            // ホスト配列 → VM 配列 (SzArray パラメータ。要素はホスト境界で再帰変換)
            System.Array array when type.Kind == SigKind.SzArray => HostArrayToSlot(array, type, interpreter),
            // VM オブジェクト (CreateInstance の戻り値等) はそのまま参照渡し
            VmObject vmObject => StackSlot.OfObject(vmObject),
            _ => throw new ArgumentException($"ホスト値 {value.GetType().Name} は VM 引数に変換できません。"),
        };
    }

    /// <summary>ホスト配列を SzArray パラメータ用の VmArray に変換する (ヒープ計上済み)。</summary>
    private static StackSlot HostArrayToSlot(System.Array array, SigType type, Interpreter interpreter) {
        var elementSig = type.Inner ?? new SigType(SigKind.Object);
        var elementType = interpreter.Loader.ResolveToken(elementSig);
        var elements = new StackSlot[array.Length];
        for (var i = 0; i < array.Length; i++)
            elements[i] = ToSlot(array.GetValue(i), elementSig, interpreter);
        return StackSlot.OfObject(interpreter.Heap.Allocate(
            new VmArray(new VmArrayType { ElementType = elementType }, elements)));
    }

    /// <summary>宣言型が object の場合はプリミティブをボックス化する。
    /// 型は CoreLib ロード時は実型に統一する (ゲスト側の typeof/キャストと同一視される)。</summary>
    private static StackSlot Prim(SigType declared, Interpreter interpreter, string intrinsicTypeName, StackSlot slot) =>
        declared.Kind is SigKind.Object or SigKind.TypeToken
            ? interpreter.Box(interpreter.Loader.ResolveWellKnownType(intrinsicTypeName), slot)
            : slot;

    /// <summary>戻り値スロットをホスト値へ変換する (宣言型があれば正確な .NET 型へ)。</summary>
    private static object? FromSlot(in StackSlot slot, SigType? declared) {
        if (declared is not null && slot.Kind == StackKind.Int32) {
            return declared.Kind switch {
                SigKind.Boolean => slot.Int64Value != 0,
                SigKind.Char => (char)slot.Int64Value,
                SigKind.U1 => (byte)slot.Int64Value,
                SigKind.U2 => (ushort)slot.Int64Value,
                SigKind.U4 => (uint)slot.Int64Value,
                SigKind.I1 => (sbyte)slot.Int64Value,
                SigKind.I2 => (short)slot.Int64Value,
                _ => FromSlotCore(slot),
            };
        }
        if (declared is not null && slot.Kind == StackKind.Int64 && declared.Kind == SigKind.U8)
            return (ulong)slot.Int64Value;
        return FromSlotCore(slot);
    }

    private static object? FromSlotCore(in StackSlot slot) => slot.Kind switch {
        StackKind.Empty => null,
        StackKind.Int32 => (int)slot.Int64Value,
        StackKind.Int64 or StackKind.NativeInt => slot.Int64Value,
        StackKind.Float => slot.DoubleValue,
        StackKind.Object => slot.ObjectValue switch {
            null => null,
            VmString s => s.Value,
            // System.Decimal box (object 経由の戻り値) → ホスト decimal
            VmBoxedValue boxed when boxed.Type.FullName == "System.Decimal" =>
                DecodeDecimal(new VmStructValue(boxed.Type, boxed.Fields)),
            var other => other, // VmClassInstance/VmArray/VmBoxedValue は VM オブジェクトのまま返す
        },
        // System.Decimal (統合実型) 構造体値 → ホスト decimal
        StackKind.ValueType when slot.ObjectValue is VmStructValue sv && sv.StructType.FullName == "System.Decimal" =>
            DecodeDecimal(sv),
        StackKind.ValueType => slot.ObjectValue, // その他の構造体値は VM オブジェクトのまま返す
        _ => throw new InvalidOperationException($"戻り値スロット {slot.Kind} はホスト値に変換できません。"),
    };

    /// <summary>ホスト decimal → VM 構造体値 (System.Decimal 実型。CoreLib ロード時に統合される)。
    /// フィールドは .NET 10 CoreLib の宣言順 (_flags int32 / _hi32 uint32 / _lo64 uint64)。</summary>
    private static StackSlot VMDecimalToSlot(decimal value, SigType declared, Interpreter interpreter) {
        if (declared.Kind != SigKind.TypeToken)
            throw new ArgumentException("decimal 引数は ValueType 署名 (TypeToken) での渡しのみ対応しています。");
        var cls = FindDecimalType(interpreter.Loader)
            ?? throw new InvalidOperationException("System.Decimal (CoreLib 実型) がロードされていません。");
        var bits = decimal.GetBits(value);
        var lo64 = (ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        var fields = new StackSlot[cls.Fields.Count(f => !f.IsStatic && !f.IsLiteral)];
        var index = 0;
        foreach (var field in cls.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            fields[index++] = field.Name switch {
                "_flags" => StackSlot.OfInt32(bits[3]),
                "_hi32" => StackSlot.OfInt32(bits[2]),
                "_lo64" => StackSlot.OfInt64((long)lo64),
                _ => StackSlot.OfInt32(0),
            };
        }
        return StackSlot.OfValueType(new VmStructValue(cls, fields));
    }

    /// <summary>CoreLib 実型の System.Decimal (統合された VmClassType) を探す。
    /// trusted CoreLib 画像に限定する (fake System.Decimal への誤結合防止)。</summary>
    private static VmClassType? FindDecimalType(TypeLoader loader) {
        foreach (var candidate in loader.Context?.Loaders ?? []) {
            if (!candidate.IsTrustedCoreLib)
                continue;
            if (candidate.FindTypeByFullName("System.Decimal") is not VmClassType cls)
                continue;
            if (candidate.Image.SourcePath?.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) == true)
                return cls;
            return cls;
        }
        return null;
    }

    private static decimal DecodeDecimal(VmStructValue sv) {
        int iFlags = -1, iHi = -1, iLo = -1, index = 0;
        foreach (var field in ((VmClassType)sv.StructType).Fields) {
            if (field.IsStatic || field.IsLiteral) continue;
            switch (field.Name) {
                case "_flags": iFlags = index; break;
                case "_hi32": iHi = index; break;
                case "_lo64": iLo = index; break;
            }
            index++;
        }
        if (iFlags < 0 || iHi < 0 || iLo < 0)
            throw new InvalidOperationException("System.Decimal 構造体のフィールドレイアウトを解決できません。");
        var flags = (int)sv.Fields[iFlags].Int64Value;
        var hi = (uint)sv.Fields[iHi].Int64Value;
        var lo64 = (ulong)sv.Fields[iLo].Int64Value;
        return new decimal((int)(uint)lo64, (int)(uint)(lo64 >> 32), (int)hi, flags < 0, (byte)((flags >> 16) & 0xFF));
    }

    public void Dispose() {
        lock (_lifetimeGate) {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _sharedState.Dispose();
            lock (_interpreterGate) {
                _interpreter?.Dispose();
                _interpreter = null;
            }
            _heap.RemoveRootObjectSource(_handleRoots);
            _heap.RemoveRootObjectSource(_typeFacadeRoots);
            _heap.RemoveRootObjectSource(_guestThreadRoots);
            _heap.RemoveRootSlotSource(_guestTaskRoots);
            lock (_assemblyGate) {
                foreach (var loader in _loaders.ToArray())
                    _context.Unregister(loader);
                _loaders.Clear();
            }
        }
    }

    private void ThrowIfDisposed() {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VirtualMachine));
    }
}
