using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
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
    private readonly MetadataResolutionCache<VmField> _fieldTokens = new();
    private readonly ConcurrentDictionary<int, VmField> _fieldDefTokens = new();
    private readonly MetadataResolutionCache<VmType> _typeTokens = new();
    private readonly ConcurrentDictionary<int, VmTypeHandle> _typeHandles = new();
    private readonly ConcurrentDictionary<GenericContext, ConcurrentDictionary<int, VmTypeHandle>> _genericTypeHandles = new();
    // Resolved type identity is the semantic key for RuntimeTypeHandle. A
    // generic call can produce equivalent short-lived contexts/constructed
    // type objects, so caching by those object references misses repeatedly.
    private readonly ConcurrentDictionary<string, VmTypeHandle> _resolvedTypeHandles = new();

    internal VmTypeHandle GetCachedTypeHandle(int token, GenericContext? context,
        IReadOnlyDictionary<uint, object>? dynamicTokens) {
        if (dynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true) {
            if (dynamicReference is not VmType dynamicType)
                throw new NotSupportedException("動的 ldtoken の型ハンドルが不正です。");
            return GetCachedResolvedTypeHandle(dynamicType);
        }

        var type = ResolveTypeToken(token, context, dynamicTokens);
        return GetCachedResolvedTypeHandle(type);
    }

    internal VmTypeHandle GetCachedDynamicTypeHandle(VmType type) =>
        GetCachedResolvedTypeHandle(type);

    private VmTypeHandle GetCachedResolvedTypeHandle(VmType type) {
        return _resolvedTypeHandles.GetOrAdd(type.FullName,
            static (_, state) => state.Heap.Allocate(new VmTypeHandle { Target = state.Type }),
            (Heap: _heap, Type: type));
    }

    internal IEnumerable<VmObject?> TypeHandleRoots =>
        _typeHandles.Values.Cast<VmObject?>().Concat(
            _genericTypeHandles.Values.SelectMany(static cache => cache.Values))
            .Concat(_resolvedTypeHandles.Values);

    internal void ClearOperandCaches() {
        _fieldTokens.Clear();
        _fieldDefTokens.Clear();
        _typeTokens.Clear();
        _typeHandles.Clear();
        _genericTypeHandles.Clear();
        _resolvedTypeHandles.Clear();
        _arrayBackedValueTypes.Clear();
    }
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
    private readonly ConcurrentDictionary<int, SigType[]> _stringConstructorSignatures = new();
    private readonly ConcurrentDictionary<VmMethod, int> _constructorInstructionCosts = new();
    private readonly ConcurrentDictionary<VmMethod, SimpleFieldConstructor> _simpleFieldConstructors = new();
    private readonly ConcurrentDictionary<VmMethod, byte> _nonSimpleFieldConstructors = new();
    private readonly ConcurrentDictionary<VmMethod, VmField> _simpleFieldGetters = new();
    private readonly ConcurrentDictionary<VmMethod, byte> _nonSimpleFieldGetters = new();
    private readonly ConcurrentDictionary<VmClassType, bool> _typesWithStaticConstructor = new();

    private sealed record SimpleFieldConstructor(
        VmField Field,
        int FieldIndex,
        bool CanSkipDefaultStorage,
        int InstructionCost);

    // Array-backed CoreLib value wrappers (Span<T>, ReadOnlySpan<T>, etc.) have
    // immutable layout/constructor metadata. Cache only successful structural
    // matches; misses continue through the normal IL path.
    private readonly ConcurrentDictionary<int, ArrayBackedValueTypeInfo> _arrayBackedValueTypes = new();

    private sealed record ArrayBackedValueTypeInfo(
        VmConstructedType Constructed,
        VmClassType Definition,
        VmType ElementType,
        VmMethod Constructor,
        int ReferenceIndex,
        int LengthIndex,
        int InstructionCost);

    private void EnsureInitializedForAllocation(VmClassType type) {
        if (_typesWithStaticConstructor.GetOrAdd(type,
                static candidate => candidate.Methods.Any(method =>
                    method.Name == ".cctor" && method.Body is not null)))
            EnsureInitialized(type);
    }

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
