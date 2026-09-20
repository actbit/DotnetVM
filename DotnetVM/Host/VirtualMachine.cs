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
    private readonly VmAssemblyContext _context;
    private Interpreter? _interpreter;

    public VirtualMachine(VmHostOptions? options = null) {
        _options = options ?? new VmHostOptions();
        _heap = new VmHeap(_options.Memory, _options.Gc);
        _heap.AddRootObjectSource(_handles.EnumerateRoots); // ホスト保持参照 (GCHandle 相当) をルートに
        _network = new NetworkGateway(_options.Network, _options.NetworkBridge);
        _storage = new StorageGateway(_options.Storage, _options.StorageBridge);
        _context = new VmAssemblyContext(LoadDependencyAssembly);
        DefaultIntrinsics.RegisterAll(_intrinsics);
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
    }

    /// <summary>VmAssemblyContext が依存アセンブリの同一ディレクトリ探索で見つけた DLL をロードする。</summary>
    private TypeLoader LoadDependencyAssembly(string path) {
        LoadAssembly(path);
        return _loaders[^1];
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
    public GcStatistics CollectGarbage() => RunGuest(_heap.Collect);

    /// <summary>ロード済みアセンブリの型ローダ。</summary>
    public IReadOnlyList<TypeLoader> Loaders => _loaders;

    /// <summary>多アセンブリ ロード コンテキスト (AssemblyRef 依存解決)。</summary>
    public VmAssemblyContext Context => _context;

    /// <summary>累積実行命令数。</summary>
    public long InstructionCount => _interpreter?.InstructionCount ?? 0;

    /// <summary>intrinsic を起動前に追加登録する (実行開始後は不可)。</summary>
    public void RegisterIntrinsic(IntrinsicKey key, IntrinsicImpl impl) =>
        _intrinsics.Register(key, impl);

    /// <summary>DLL アセンブリをファイルからロードする (EXE は不要/非対応)。
    /// AssemblyRef による依存アセンブリは、参照元と同一ディレクトリの同名 DLL から自動解決される。</summary>
    public AssemblyImage LoadAssembly(string path) {
        using var stream = File.OpenRead(path);
        return LoadAssembly(stream, Path.GetFullPath(path));
    }

    /// <summary>DLL アセンブリをストリームからロードする。</summary>
    public AssemblyImage LoadAssembly(Stream peStream, string? sourcePath = null) {
        using var buffered = new MemoryStream();
        peStream.CopyTo(buffered);
        var image = AssemblyImage.Parse(buffered.ToArray());
        image.SourcePath = sourcePath;
        var loader = new TypeLoader(image);
        // 界面の再現制御: ブリッジが設定されている場合のみ対応する I/O ファサード型を合成する。
        // 未設定ならゲストはその型を解決できず、ロード/呼出の時点で fail-closed になる
        if (_options.NetworkBridge is not null)
            loader.AddIoFacade("WebClient");
        if (_options.StorageBridge is not null)
            loader.AddIoFacade("File");
        loader.CompletePendingTypes();
        _context.Register(loader);
        _loaders.Add(loader);
        return image;
    }

    /// <summary>
    /// 型とメソッド名を明示指定してゲスト関数を呼び出す。
    /// 対応しているのは静的メソッド (インスタンスメソッドは CreateInstance + CallInstance を使用)。
    /// </summary>
    public object? Invoke(string typeFullName, string methodName, params object?[] args) {
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
        var type = FindType(typeFullName);
        var ctor = FindConstructor(type, args.Length);
        var interpreter = GetInterpreter();
        var ctorArgs = new StackSlot[args.Length];
        for (var i = 0; i < args.Length; i++)
            ctorArgs[i] = ToSlot(args[i], ctor.Signature.ParamTypes[i], interpreter);
        return RunGuest(() => interpreter.CreateInstance(type, ctorArgs));
    }

    /// <summary>インスタンスメソッドを明示指定して呼び出す (仮想メソッドは最派生実装を実行)。</summary>
    public object? CallInstance(VmClassInstance instance, string methodName, params object?[] args) {
        var method = FindInstanceMethod(instance.ClassType, methodName, args.Length);
        var interpreter = GetInterpreter();
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
        if (!method.IsStatic)
            throw new NotSupportedException($"インスタンスメソッド {method} はオブジェクトモデル (M3) 以降に対応します。静的メソッドを指定してください。");
        var signature = method.Signature;
        if (signature.ParamTypes.Length != args.Length)
            throw new ArgumentException($"引数個数が一致しません ({method}: 期待 {signature.ParamTypes.Length}, 実際 {args.Length})。");

        var interpreter = GetInterpreter();
        var slots = new StackSlot[args.Length];
        for (var i = 0; i < args.Length; i++)
            slots[i] = ToSlot(args[i], signature.ParamTypes[i], interpreter);

        var countBefore = interpreter.InstructionCount;
        return RunGuest(() => {
            var result = interpreter.Invoke(method, slots);
            return new ExecutionResult(
                FromSlot(result, method.Signature.ReturnType),
                _console.OutputLog,
                interpreter.InstructionCount - countBefore);
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
        if (_loaders.Count == 0)
            throw new InvalidOperationException("アセンブリがロードされていません。先に LoadAssembly を呼んでください。");
        return _loaders
            .Select(l => l.FindTypeByFullName(typeFullName) ?? l.FindTypeByName(typeFullName))
            .FirstOrDefault(t => t is not null)
            ?? throw new ArgumentException($"型 '{typeFullName}' がロード済みアセンブリに見つかりません。");
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

    private Interpreter GetInterpreter() =>
        _interpreter ??= new Interpreter(GetPrimaryLoader(), _intrinsics, _console, _options.Memory, _heap,
            _network, _storage);

    private TypeLoader GetPrimaryLoader() {
        if (_loaders.Count == 0)
            throw new InvalidOperationException("アセンブリがロードされていません。");
        return _loaders[^1];
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
        SigKind.TypeToken or SigKind.GenericInst => value is null or VmObject,
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
            var other => other, // VmClassInstance/VmArray/VmBoxedValue は VM オブジェクトのまま返す
        },
        _ => throw new InvalidOperationException($"戻り値スロット {slot.Kind} はホスト値に変換できません。"),
    };

    public void Dispose() {
        _loaders.Clear();
        _interpreter = null;
    }
}
