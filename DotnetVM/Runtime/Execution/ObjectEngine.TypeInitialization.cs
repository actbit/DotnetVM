using DotnetVM.Runtime.Types;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class ObjectEngine {
    // ---- 型初期化 (.cctor) ----

    /// <summary>型初期化子 (.cctor) の起動規約: 静的フィールド初回アクセス/newobj 前に 1 回だけ実行。</summary>
    public void EnsureInitialized(VmClassType type) {
        EnsureInitializationOutsideExecutionLease(() => _typeInitialization.Ensure(type, () => {
            var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor?.Body is not null)
                invoker.Invoke(cctor, [], null);
        }));
    }

    /// <summary>構築ジェネリック型の .cctor 起動 (CLR と同じく型実引数ごとに 1 回。
    /// 定義参照 + 型引数参照列で鍵化し、FullName 文字列は使わない)。</summary>
    public void EnsureConstructedInitialized(VmConstructedType type) {
        EnsureInitializationOutsideExecutionLease(() => _typeInitialization.Ensure(type, () => {
            var definition = (VmClassType)type.Definition;
            var cctor = definition.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor?.Body is not null)
                invoker.Invoke(cctor, [], new GenericContext { ClassArgs = type.TypeArguments });
        }));
    }

    /// <summary>
    /// 型初期化は別スレッドが同じ型を待つ可能性がある。待機中の呼出元が実行 coordinator
    /// の read lease を保持したままだと、cctor の最初のセーフポイントが stop-the-world
    /// write lease を取得できず循環待ちになるため、状態待ちと cctor 本体を lease の外で実行する。
    /// </summary>
    private void EnsureInitializationOutsideExecutionLease(Action ensure) {
        if (_intrinsicContext.SuspendExecution is { } suspend)
            suspend(ensure);
        else
            ensure();
    }

    /// <summary>intrinsic からのインスタンス生成 (Activator.CreateInstance 用フック実体)。
    /// 確保＋型初期化＋指定 .ctor 実行まで行う (.ctor 本体は invoker 経由で IL 実行)。
    /// type が構築型の場合は実引数を記録し、フィールド型の !0 はそれで解決する。</summary>
    public VmClassInstance CreateInstanceByCtor(VmType type, VmMethod ctor, StackSlot[] ctorArgs, GenericContext? context) {
        var definition = type is VmConstructedType constructed ? (VmClassType)constructed.Definition
            : (VmClassType)type;
        var typeArgs = type is VmConstructedType ct ? ct.TypeArguments : null;
        var effectiveContext = context ?? (typeArgs is { Length: > 0 } ? new GenericContext { ClassArgs = typeArgs } : null);
        if (type is VmConstructedType ctype)
            EnsureConstructedInitialized(ctype);
        else
            EnsureInitialized(definition);
        var instance = _heap.Allocate(new VmClassInstance(definition,
            _objects.CreateInstanceStorage(definition, _loader, effectiveContext), typeArgs ?? []));
        var args = new StackSlot[ctorArgs.Length + 1];
        args[0] = StackSlot.OfObject(instance);
        ctorArgs.CopyTo(args, 1);
        if (ctor.Body is not null)
            invoker.Invoke(ctor, args, effectiveContext);
        return instance;
    }
}
