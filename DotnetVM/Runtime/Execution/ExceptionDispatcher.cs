using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>例外処理 (EH) の実行サービス: catch / finally / fault / filter のディスパッチ、
/// VM 内部例外 (ゼロ除算/境界外/null 参照等) のゲスト例外オブジェクトへの実体化、
/// leave による finally チェーンの通過実行、分岐先 (IL オフセット → 命令インデックス) の解決。
/// ResourceExhaustedException 系 (メモリ/命令クォータ) は捕捉しない (管理例外としてゲスト catch を迂回)。</summary>
internal sealed class ExceptionDispatcher(
    InterpreterServices services,
    MethodPreparer preparer,
    ObjectEngine objectEngine,
    IFrameRunner frameRunner) {
    private readonly TypeLoader _loader = services.Loader;
    private readonly VmHeap _heap = services.Heap;
    private readonly VmStringPool _strings = services.Strings;

    /// <summary>endfinally で伝播を再開することを示す番兵 (FinallyResume スタックの値)。</summary>
    internal const int PropagateSentinel = -1;

    // ---- 実行ループ ----

    /// <summary>
    /// 1 フレームの実行 + EH (例外処理)。ゲスト例外 (VmGuestThrow / VM 内部例外) が上がると
    /// このフレームの EH 句で処理できる限り処理を続け (catch 句へのディスパッチ、finally/fault
    /// の通過実行)、処理できない場合はキャリアごと上位フレームへ伝播する。
    /// </summary>
    public StackSlot RunFrame(InterpreterFrame frame) {
        try {
            return frameRunner.RunFrameCore(frame);
        } catch (VmGuestThrow direct) {
            return UnwindAndContinue(frame, direct);
        } catch (UnhandledGuestException uge) {
            // VM 内部例外 (ゼロ除算/境界外/null 参照等) を例外オブジェクトに実体化してゲスト EH へ
            return UnwindAndContinue(frame, SynthesizeCarrier(uge));
        }
    }

    /// <summary>例外をこのフレームの EH 句で処理し、handler/finally 内の実行を続ける。</summary>
    private StackSlot UnwindAndContinue(InterpreterFrame frame, VmGuestThrow current) {
        while (true) {
            if (!TryDispatchHandler(frame, current))
                throw current; // このフレームでは処理できない → 上位フレームへ (finally は実行済み)
            try {
                return frameRunner.RunFrameCore(frame);
            } catch (VmGuestThrow next) {
                current = next; // handler/filter/finally 内での新たな例外
            } catch (UnhandledGuestException uge) {
                current = SynthesizeCarrier(uge);
            }
        }
    }

    /// <summary>VM 内部例外を例外ファサード型のインスタンスとしてヒープに実体化する。</summary>
    private VmGuestThrow SynthesizeCarrier(UnhandledGuestException uge) {
        var type = _loader.FindIntrinsicType(uge.ExceptionTypeName)
            ?? throw new InvalidOperationException($"例外ファサード型 {uge.ExceptionTypeName} が未登録です。");
        var obj = _heap.Allocate(new VmExceptionObject(type,
            uge.GuestMessage is null ? null : _strings.GetOrNew(uge.GuestMessage)));
        return new VmGuestThrow(obj, type, uge.ExceptionTypeName, uge.GuestMessage);
    }

    /// <summary>
    /// 現在の Ip を含む最内の EH 句から外側へ走査し、ハンドラへのジャンプをセットアップする。
    /// - Catch: 型一致なら handler へ (例外オブジェクトをスタックに積む)
    /// - Filter: フィルタ本体へ (例外オブジェクトを積む。endfilter で判定)
    /// - Finally/Fault: 通過実行してから伝播を再開 (endfinally で再送出)
    /// 処理できなければ false (上位フレームへ)。
    /// </summary>
    public bool TryDispatchHandler(InterpreterFrame frame, VmGuestThrow carrier) {
        frame.UnwindIp = frame.Ip; // フィルタ不採用時に発生位置から探索をやり直せるように
        return TryDispatchHandlerFrom(frame, carrier, 0);
    }

    public bool TryDispatchHandlerFrom(InterpreterFrame frame, VmGuestThrow carrier, int startIndex) {
        var clauses = preparer.Prepare(frame.Method).Clauses;
        if (clauses is null)
            return false;

        for (var i = startIndex; i < clauses.Length; i++) {
            var clause = clauses[i];
            if (!(clause.TryStart <= frame.Ip && frame.Ip < clause.TryEnd))
                continue;
            switch (clause.Kind) {
                case ExceptionClauseKind.Finally or ExceptionClauseKind.Fault:
                    frame.CurrentThrow = carrier;
                    frame.FinallyResume.Add(PropagateSentinel); // finally の後で伝播を再開
                    frame.Stack.Clear();
                    frame.Ip = clause.HandlerStart;
                    return true;
                case ExceptionClauseKind.Catch: {
                    var targetType = objectEngine.ResolveTypeToken(clause.ClassToken, frame.Context);
                    if (!carrier.ExceptionType.IsAssignableTo(targetType))
                        continue; // 型不一致 → 外側の句へ
                    frame.CurrentThrow = carrier;
                    frame.FilterClause = -1;
                    frame.Stack.Clear();
                    frame.Stack.Push(StackSlot.OfObject(carrier.ExceptionObject));
                    frame.Ip = clause.HandlerStart;
                    return true;
                }
                case ExceptionClauseKind.Filter:
                    frame.CurrentThrow = carrier;
                    frame.FilterClause = i;
                    frame.Stack.Clear();
                    frame.Stack.Push(StackSlot.OfObject(carrier.ExceptionObject));
                    frame.Ip = clause.FilterStart;
                    return true;
            }
        }
        return false;
    }

    // ---- 分岐 ----

    public void JumpTo(InterpreterFrame frame, int offset) {
        if (!frame.OffsetMap.TryGetValue(offset, out var index))
            throw new BadImageFormatException($"分岐先 IL_{offset:X4} が命令境界上にありません。");
        frame.Ip = index;
    }

    // ---- 例外送出/leave ----

    /// <summary>throw 命令の被演算子をゲスト例外キャリアに変換する。</summary>
    public VmGuestThrow MakeGuestThrow(in StackSlot value) {
        if (value.Kind == StackKind.Object && value.ObjectValue is null)
            return SynthesizeCarrier(new UnhandledGuestException("System.NullReferenceException", null));
        if (value.ObjectValue is not VmObject vmObject)
            return SynthesizeCarrier(new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} は System.Exception を派生していないため throw できません。"));
        var runtimeType = vmObject switch {
            VmExceptionObject e => (VmType)e.ExceptionType,
            VmClassInstance ci => (VmType)ci.ClassType,
            VmArray arr => (VmType)arr.ArrayType,
            VmBoxedValue boxed => boxed.Type,
            _ => throw new InvalidOperationException($"throw できない VM オブジェクトです: {vmObject.GetType().Name}"),
        };
        // CLR 互換: Exception 派生でないオブジェクトの throw は InvalidCastException
        var exceptionType = _loader.FindIntrinsicType("System.Exception")!;
        if (!runtimeType.IsAssignableTo(exceptionType))
            return SynthesizeCarrier(new UnhandledGuestException("System.InvalidCastException",
                $"{SlotOps.Describe(value)} は System.Exception を派生していないため throw できません。"));
        return new VmGuestThrow(vmObject, runtimeType, runtimeType.FullName, ExceptionMessageOf(vmObject));
    }

    /// <summary>例外オブジェクトのメッセージ (ホスト境界報告用)。</summary>
    private static string? ExceptionMessageOf(object exceptionObject) => exceptionObject switch {
        VmExceptionObject e => e.Message?.Value,
        VmClassInstance ci => IntrinsicContext.GetExceptionMessage(ci)?.Value,
        _ => null,
    };

    /// <summary>
    /// leave: 評価スタックを空にし、現在位置から飛び先までの間で保護している finally/fault を
    /// 内側から順に通過実行してからジャンプする (finally チェーンは FinallyResume スタックで管理)。
    /// </summary>
    public void DoLeave(InterpreterFrame frame, PreparedClause[]? clauses, int targetOffset) {
        if (!frame.OffsetMap.TryGetValue(targetOffset, out var target))
            throw new BadImageFormatException($"leave 先 IL_{targetOffset:X4} が命令境界上にありません。");
        frame.Stack.Clear();
        var chain = CollectFinallys(clauses, frame.Ip, target);
        if (chain.Count == 0) {
            frame.Ip = target;
            return;
        }
        frame.FinallyResume.Clear();
        frame.FinallyResume.Add(target); // 最後に pop される (チェーンの末尾)
        // 外側の finally から順に積む (pop は内側から)
        for (var i = chain.Count - 1; i >= 1; i--)
            frame.FinallyResume.Add(chain[i]);
        frame.Ip = chain[0];
    }

    /// <summary>
    /// ip を保護し target を保護しない finally/fault 句のハンドラ開始インデックスを
    /// 内側から外側の順で集める (leave の finally チェーン)。
    /// </summary>
    private static List<int> CollectFinallys(PreparedClause[]? clauses, int ip, int target) {
        var chain = new List<int>();
        if (clauses is null)
            return chain;
        foreach (var clause in clauses) {
            if (clause.Kind is not (ExceptionClauseKind.Finally or ExceptionClauseKind.Fault))
                continue;
            var protectsIp = clause.TryStart <= ip && ip < clause.TryEnd;
            var protectsTarget = clause.TryStart <= target && target < clause.TryEnd;
            if (protectsIp && !protectsTarget)
                chain.Add(clause.HandlerStart);
        }
        // 内側 (範囲が狭い) から外側の順に実行する
        chain.Sort((a, b) => SpanOf(clauses, a).CompareTo(SpanOf(clauses, b)));
        return chain;

        static int SpanOf(PreparedClause[] cs, int handlerStart) {
            foreach (var c in cs)
                if (c.HandlerStart == handlerStart)
                    return c.TryEnd - c.TryStart;
            return int.MaxValue;
        }
    }
}
