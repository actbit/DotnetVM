using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>ゲスト メソッド呼出の入口 (Interpreter.Invoke への再帰)。
/// ObjectEngine (.ctor/.cctor 起動) と CallEngine (デリゲート/ゲスト実装の呼出) が使う。
/// インタープリタ本体とサービス群の循環依存をこのインターフェースで切る。</summary>
internal interface IGuestInvoker {
    StackSlot Invoke(VmMethod method, StackSlot[] arguments, GenericContext? context);
    bool TryCreateTailCall(InterpreterFrame caller, VmMethod method, StackSlot[] arguments,
        GenericContext? context, out TailCallRequest? request);
}

internal sealed record TailCallRequest(VmMethod Method, StackSlot[] Arguments, GenericContext? Context);

/// <summary>実行ゲート: intrinsic 呼出の直前に ① 命令クォータの消費 ② セーフポイント検査
/// (GC 起動) を強制する。IL 実行と intrinsic 実行で制約適用を等価にするための構造的入口。
/// intrinsic をゲート外で呼ぶ経路は存在しない (サービスは必ずこのインターフェース経由)。</summary>
internal interface IExecutionGate {
    void ConsumeInstruction();
    void CheckSafepoint();
}

/// <summary>EH 付き実行ループの再入口。ExceptionDispatcher が catch/finally ハンドラ内での
/// 実行再開 (RunFrameCore) に使う。Interpreter が実装する。</summary>
internal interface IFrameRunner {
    StackSlot RunFrameCore(InterpreterFrame frame);
}
