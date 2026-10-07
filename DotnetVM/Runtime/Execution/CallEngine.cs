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
    // VM-local cache for promoted instance targets reached from compiled call
    // sites. It avoids repeating the loader/cache lookup on every helper call.
    private readonly ConcurrentDictionary<VmMethod, JitCompiledMethod> _nestedCompiledMethods = new();
    // Generic receiver contexts are immutable loader-owned identities once a
    // call target has been resolved. Reuse them across short-lived JIT frames
    // so generic CoreLib calls do not allocate a new context and redo
    // inheritance argument resolution on every helper invocation.
    private readonly ConcurrentDictionary<CallContextKey, GenericContext> _callContexts = new();
    // Only successful intrinsic MemberRefs are cached, after registry sealing.
    // Entries are loader-local, bounded by the image's MemberRef rows, and
    // invalidated whenever the root context's assembly set changes.
    private readonly ConcurrentDictionary<int, CachedIntrinsicTarget> _memberRefIntrinsicTargets = new();
    // Binding resolution includes signature token substitution and can be much
    // more expensive than the actual host callback. Cache only positive,
    // sealed-registry results, keyed by the exact generic argument arrays and
    // caller security domain.
    private readonly ConcurrentDictionary<BindingCacheKey, BindingResolution> _bindingResolutions = new();
    private sealed record CachedIntrinsicTarget(VmAssemblyContext Context, long Version, CallTarget Target);

    private readonly record struct BindingCacheKey(
        VmMethod Method, VmType[]? MethodArgs, VmType[]? ClassArgs, BindingDomain CallerDomain);
}

internal readonly record struct CallContextKey(CallTarget Target, VmMethod Method,
    VmType ReceiverType);
