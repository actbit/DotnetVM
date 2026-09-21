using DotnetVM.Devices;
using DotnetVM.Diagnostics;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>Interpreter とその実行サービス群 (MethodPreparer/ObjectEngine/CallEngine/
/// ExceptionDispatcher) が共有する依存の束。VM インスタンスごとに 1 つ (VM 間で共有しない)。</summary>
internal sealed class InterpreterServices(
    TypeLoader loader,
    IntrinsicRegistry intrinsics,
    VmConsole console,
    MemoryPolicy memory,
    VmHeap heap,
    VmStringPool strings,
    IntrinsicContext intrinsicContext,
    ExecutionTracer? tracer = null,
    VmCoreLibSurfaces? coreLibSurfaces = null) {
    public TypeLoader Loader { get; } = loader;
    public IntrinsicRegistry Intrinsics { get; } = intrinsics;
    public VmConsole Console { get; } = console;
    public MemoryPolicy Memory { get; } = memory;
    public VmHeap Heap { get; } = heap;
    public VmStringPool Strings { get; } = strings;
    public IntrinsicContext IntrinsicContext { get; } = intrinsicContext;
    public ExecutionTracer? Tracer { get; } = tracer;
    public VmCoreLibSurfaces? CoreLibSurfaces { get; } = coreLibSurfaces;

    /// <summary>オブジェクトモデル (レイアウト/静的ストレージ)。VM インスタンスごとの状態。</summary>
    public ObjectModel Objects { get; } = new();

    /// <summary>System.String の実型 (CoreLib TypeDef)。LoadHostCoreLib = true 時のみ
    /// VirtualMachine が値を持ち、Interpreter がエンジン構築時にここへ載せる。
    /// VmString の型同一性を実型に接続し、VmString → インターフェースの castclass /
    /// インターフェースディスパッチを可能にする (String は IConvertible 等を EII 実装)。
    /// VM 間で共有すると Dispose 競合で他 VM の実行が壊れるため静的には持たない。
    /// 未設定時は従来どおり FullName 緩和のみ。</summary>
    public VmType? StringType { get; set; }
}
