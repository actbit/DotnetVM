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
        EnsureStaticMethodTypeInitialized(method, context);
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
            if (state.Depth == 0)
                FlushPendingAssemblyContextCaches();
        }

    }

    /// <summary>
    /// 静的メソッド呼出しの入口でも CLR の型初期化規約を適用する。
    /// 静的フィールドを直接参照しない .cctor でも、明示的 static constructor は最初の
    /// static method 呼出し前に実行される必要がある。.cctor 自身は再入を避けて除外する。
    /// </summary>
    private void EnsureStaticMethodTypeInitialized(VmMethod method, GenericContext? context) {
        if (!method.IsStatic || method.Name == ".cctor" || method.DeclaringType is not VmClassType definition)
            return;

        var objects = EnginesFor(method).Objects;
        if (definition.GenericParamCount > 0 && context?.ClassArgs is { Length: > 0 } classArgs &&
            classArgs.Length == definition.GenericParamCount) {
            objects.EnsureConstructedInitialized(new VmConstructedType {
                Definition = definition,
                TypeArguments = classArgs,
            });
        } else {
            objects.EnsureInitialized(definition);
        }
    }

    // サービス群からの再帰呼出入口 (循環依存をインターフェースで切る)
    StackSlot IGuestInvoker.Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context) =>
        Invoke(method, arguments, context);
}
