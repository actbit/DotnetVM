using DotnetVM.Metadata;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class CallEngine {
    private readonly MetadataResolutionCache<CallTarget> _targetCache = new();

    internal void ClearCallTargetCache() {
        _targetCache.Clear();
        _methodDefTargets.Clear();
        _memberRefIntrinsicTargets.Clear();
        _callContexts.Clear();
        lock (_virtualTargetGate) {
            Interlocked.Increment(ref _virtualTargetVersion);
            _virtualTargets.Clear();
        }
    }

    private CallTarget ResolveCachedCallTarget(int token, GenericContext? context, bool throwOnMissingIntrinsic) {
        // Keep registration mutable until first execution. Dynamic tokens and
        // standalone loaders take the original resolution path.
        _loader.EnsureLive();
        // A MethodDef always identifies this loader's immutable declaration.
        // Reuse the existing integer-key cache without hashing generic context.
        if ((TableKind)(token >> 24) == TableKind.MethodDef)
            return ResolveCallTargetCore(token, context, throwOnMissingIntrinsic);
        var assemblyContext = _intrinsics.IsSealed && _loader.Context is { HasParent: false } root ? root : null;
        var keyContext = (TableKind)(token >> 24) == TableKind.MethodDef ? null : context;
        if (_targetCache.TryGet(assemblyContext, token, keyContext, throwOnMissingIntrinsic,
                out var cached, out var version))
            return cached;
        var target = ResolveCallTargetCore(token, context, throwOnMissingIntrinsic);
        if (target.Method is not null || target.Intrinsic is not null)
            _targetCache.Add(assemblyContext, version, token, keyContext, throwOnMissingIntrinsic, target);
        // Virtual dispatch and caller-domain checks remain per invocation.
        return target;
    }
}
