using DotnetVM.Devices;
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
    IntrinsicContext intrinsicContext) {
    public TypeLoader Loader { get; } = loader;
    public IntrinsicRegistry Intrinsics { get; } = intrinsics;
    public VmConsole Console { get; } = console;
    public MemoryPolicy Memory { get; } = memory;
    public VmHeap Heap { get; } = heap;
    public VmStringPool Strings { get; } = strings;
    public IntrinsicContext IntrinsicContext { get; } = intrinsicContext;

    /// <summary>オブジェクトモデル (レイアウト/静的ストレージ)。VM インスタンスごとの状態。</summary>
    public ObjectModel Objects { get; } = new();
}
