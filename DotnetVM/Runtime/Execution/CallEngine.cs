using DotnetVM.Metadata;
using System.Collections.Concurrent;
using System.Threading;
using DotnetVM.Metadata.Signatures;
using DotnetVM.IL;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>呼出面の実行サービス: 呼出トークンの解決 (MethodDef/MemberRef/MethodSpec)、
/// intrinsic 呼出ゲートの適用、callvirt の実行時型ディスパッチ (VTable 相当)、デリゲート呼出、
/// constrained. レシーバのボックス化。全ての呼出は <see cref="IGuestInvoker"/> と
/// <see cref="IExecutionGate"/> を通るため、IL 実行と intrinsic 実行で制約が等価になる。</summary>
internal sealed partial class CallEngine(
    InterpreterServices services,
    IExecutionGate gate,
    IGuestInvoker invoker,
    ObjectEngine objectEngine) {
    private readonly TypeLoader _loader = services.Loader;
    private readonly IntrinsicRegistry _intrinsics = services.Intrinsics;
    private readonly VmHeap _heap = services.Heap;
    private readonly IntrinsicContext _intrinsicContext = services.IntrinsicContext;
    private readonly ObjectEngine _objectEngine = objectEngine;
    private readonly VmCoreLibSurfaces? _coreLibSurfaces = services.CoreLibSurfaces;
    private readonly InterpreterServices _services = services;
    private readonly AsyncLocal<Dictionary<VmExpressionObject, StackSlot>?> _expressionScope = new();
    // MethodDef tokens are immutable within a loader and do not depend on a
    // generic caller context.  Cache their target object so a hot guest call
    // does not allocate and populate the same CallTarget on every iteration.
    private readonly ConcurrentDictionary<int, CallTarget> _methodDefTargets = new();
}
