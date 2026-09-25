using DotnetVM.Metadata;
using System.Collections.Concurrent;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>オブジェクトモデル面の実行サービス: newobj による実体化 (ファサード/デリゲート/
/// 構造体/クラス/例外)、フィールド・静的ストレージの位置解決、型初期化子 (.cctor) の起動。
/// .cctor・.ctor の起動は <see cref="IGuestInvoker"/>、intrinsic .ctor の呼出は
/// <see cref="IExecutionGate"/> 経由で行い、IL 実行と同一の制約 (クォータ/セーフポイント/
/// ヒープ計上) を適用する。</summary>
internal sealed partial class ObjectEngine(
    InterpreterServices services,
    IExecutionGate gate,
    IGuestInvoker invoker,
    UnifiedStaticStorage? unifiedStaticStorage = null,
    TypeInitializationTracker? typeInitialization = null) {
    private readonly TypeLoader _loader = services.Loader;
    private readonly IntrinsicRegistry _intrinsics = services.Intrinsics;
    private readonly VmHeap _heap = services.Heap;
    private readonly IntrinsicContext _intrinsicContext = services.IntrinsicContext;
    private readonly ObjectModel _objects = services.Objects;
    /// <summary>VM 単位で共有する静的ストレージ (ユニフィケーションされた実型の静的フィールドは CLR と同じく 1 つ)。
    /// null = 単一画像実行 (既定動作の ObjectModel ローカル辞書に統一)。</summary>
    private readonly UnifiedStaticStorage? _unifiedStaticStorage = unifiedStaticStorage;
    private readonly TypeInitializationTracker _typeInitialization = typeInitialization ?? new();
    /// <summary>intrinsic 型の静的フィールドのストレージ (トークンごとに 1 スロット。例: String.Empty)。GC ルート源。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, StackSlot[]> _intrinsicStaticFields = new();
    /// <summary>静的 FieldRVA データフィールドのアドレス (トークンごとに 1 つ。例: Char.Latin1CharInfo の
    /// &lt;PrivateImplementationDetails&gt; 初期化データ)。GC グラフ源 (Interpreter が到達可能性に使う)。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, VmNativePointer> _rvaFieldAddresses = new();
    // MethodDef/Field tokens are immutable within this loader.  Keep their
    // resolved objects beside the object engine so hot newobj/ldfld paths do
    // not re-enter TypeLoader.MetadataGate on every iteration.
    private readonly ConcurrentDictionary<int, VmMethod> _methodDefConstructors = new();
    private readonly ConcurrentDictionary<int, VmField> _fieldTokens = new();

    /// <summary>intrinsic 静的フィールドのストレージ一覧 (GC ルート源として Interpreter が登録する)。</summary>
    public IEnumerable<StackSlot[]> IntrinsicStaticFields => _intrinsicStaticFields.Values;

    /// <summary>intrinsic 静的フィールド / FieldRVA データアドレスの実体一覧 (GC グラフ源)。</summary>
    public IEnumerable<VmNativePointer> RvaFieldAddresses => _rvaFieldAddresses.Values;

    private void InvokeGuest(VmMethod method, StackSlot[] arguments, GenericContext? context = null) {
        if (invoker is Interpreter interpreter &&
            interpreter.TryInvokeCompiled(method, arguments, context, out _))
            return;
        invoker.Invoke(method, arguments, context);
    }

}
