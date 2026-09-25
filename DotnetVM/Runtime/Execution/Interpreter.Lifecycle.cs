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
    /// <summary>アンロード対象 ALC のローダー別エンジンと VM-wide 静的キャッシュを解放する。</summary>
    internal void RemoveAssemblyContextCaches(VmAssemblyContext context) {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        // Unload は guest 命令中の intrinsic から再入できる。実行中に engine の
        // root source を外すと、同じ frame の後続 call が辞書から消えた engine を
        // 参照するため、loader のスナップショットを取って安全な命令境界まで遅延する。
        var loaders = context.Loaders.ToArray();
        lock (_cacheRemovalGate)
            _pendingCacheRemovals[context] = loaders;
        FlushPendingAssemblyContextCaches();
    }

    private void FlushPendingAssemblyContextCaches() {
        if (_coordinator.IsInsideGuestInstruction)
            return;
        KeyValuePair<VmAssemblyContext, TypeLoader[]>[] pending;
        lock (_cacheRemovalGate) {
            if (_pendingCacheRemovals.Count == 0)
                return;
            pending = _pendingCacheRemovals.ToArray();
            _pendingCacheRemovals.Clear();
        }

        using (_coordinator.StopTheWorldAtBoundary()) {
            foreach (var (context, loaders) in pending) {
                _unifiedStaticStorage.RemoveForContext(context);
                lock (_enginesGate) {
                    foreach (var loader in loaders) {
                        // Interpreter の primary engine は VM の公開呼出し面が直接保持している。
                        if (ReferenceEquals(loader, _services.Loader) || !_engines.Remove(loader, out var engines))
                            continue;
                        engines.Jit.Clear();
                        _heap.RemoveRootSlotSource(engines.StaticStorageRoots);
                        _heap.RemoveRootSlotSource(engines.IntrinsicStaticRoots);
                    }
                }
            }
        }
    }

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

    private sealed class ActionLease(Action action) : IDisposable {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    internal void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // 通常の VM.Dispose は別 host thread から来るため、実行中の read lease が
        // 全て抜けるまで write lease で待ってから coordinator を破棄する。
        // guest callback から再入的に Dispose された場合は自分の read lease を
        // 解放できないので、lock を破棄せず root だけ外し、残りの lease に任せる。
        if (_coordinator.IsExecutingOnCurrentThread) {
            _heap.RemoveRootSlotSource(_frameRootSource);
            _executionStates.Clear();
            return;
        }
        using (_coordinator.StopTheWorld())
            _heap.RemoveRootSlotSource(_frameRootSource);
        lock (_enginesGate) {
            foreach (var engines in _engines.Values)
                engines.Jit.Clear();
            _engines.Clear();
        }
        _executionStates.Clear();
        _currentExecution.Dispose();
        _coordinator.Dispose();
    }
}
