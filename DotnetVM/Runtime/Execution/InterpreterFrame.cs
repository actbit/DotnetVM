using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using System.Runtime.CompilerServices;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// マネージ参照 (ldarga/ldloca/ldelema 等の結果)。参照先を StackSlot 配列 + インデックスで表す。
/// ローカル/引数配列を直接指すことで stind 等が正しいスロットに書き込む。
/// VM ヒープ上の storage を指す参照は、参照自身が root になったときにも storage owner を
/// 生存させるため <see cref="Owner"/> を必ず保持する。
/// </summary>
public sealed class VmByRef {
    public readonly StackSlot[] Container;
    public readonly int Index;
    /// <summary>配列/オブジェクトの storage owner。ByRef 自体が GC root になった場合も所有物を保持する。</summary>
    public readonly VmObject? Owner;
    internal readonly VmType? ElementType;
    // Frame/local references are private to the active guest frame. Heap-owned
    // references keep the synchronized path for cross-thread visibility.
    private readonly bool _frameStorage;

    public VmByRef(StackSlot[] container, int index, bool isReadOnly = false, VmObject? owner = null)
        : this(container, index, isReadOnly, owner, null, false) { }

    internal VmByRef(StackSlot[] container, int index, bool isReadOnly, VmObject? owner,
        VmType? elementType, bool frameStorage = false) {
        ArgumentNullException.ThrowIfNull(container);
        if ((uint)index > (uint)container.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        Container = container;
        Index = index;
        IsReadOnly = isReadOnly;
        Owner = owner;
        ElementType = elementType;
        _frameStorage = frameStorage;
    }

    /// <summary>フレームの引数/ローカル storage への参照を作る。</summary>
    public static VmByRef Frame(StackSlot[] container, int index, bool isReadOnly = false) =>
        new(container, index, isReadOnly);

    internal static VmByRef Local(StackSlot[] container, int index, bool isReadOnly = false) =>
        new(container, index, isReadOnly, null, null, true);

    /// <summary>
    /// 配列要素への参照を作る。配列要素の storage は必ず配列本体と同じ寿命を持つため、
    /// owner 付き生成をこの factory に集約する。
    /// </summary>
    public static VmByRef ArrayElement(VmArray array, int index, bool isReadOnly = false) =>
        array.ElementReference(index, isReadOnly);

    /// <summary>ボックス化値の fields への参照を作る。</summary>
    public static VmByRef BoxedValue(VmBoxedValue boxed, int index = 0, bool isReadOnly = false) {
        return new(boxed.Fields, index, isReadOnly, boxed);
    }

    /// <summary>VmObject が所有する任意の field/state storage への参照を作る。</summary>
    public static VmByRef OwnedStorage(VmObject owner, StackSlot[] container, int index,
        bool isReadOnly = false) => new(container, index, isReadOnly, owner);

    public bool IsReadOnly { get; }
    internal bool IsFrameStorage => _frameStorage;
    /// <summary>空コンテナの index 0 は null/one-past 参照の表現として許可する。</summary>
    public bool IsNullOrOnePast => Index == Container.Length;

    /// <summary>参照先スロット。読み書きの境界を必ず VM 例外へ正規化する。</summary>
    public ref StackSlot Slot {
        get {
            EnsureInBounds();
            return ref Container[Index];
        }
    }

    public StackSlot Read() {
        EnsureInBounds();
        if (_frameStorage || VmExecutionCoordinator.IsGuestExecutionActive)
            return Container[Index];
        lock (Container)
            return Container[Index];
    }

    public void Write(in StackSlot value) {
        EnsureWritable();
        EnsureInBounds();
        if (_frameStorage || VmExecutionCoordinator.IsGuestExecutionActive) {
            Container[Index] = value;
            return;
        }
        lock (Container)
            Container[Index] = value;
    }

    public void EnsureWritable() {
        if (IsReadOnly)
            throw new UnhandledGuestException("System.InvalidProgramException",
                "readonly. で作られたマネージ参照には書き込めません。");
    }

    public void EnsureInBounds() {
        if ((uint)Index >= (uint)Container.Length)
            throw new UnhandledGuestException(
                Container.Length == 0
                    ? "System.NullReferenceException"
                    : "System.IndexOutOfRangeException",
                "マネージ参照が有効なスロットを指していません。");
    }
}

/// <summary>
/// インタプリタ 1 フレーム。ヒープ確保 (ゲスト再帰がホストスタックを消費しない設計への布石)。
/// ただし呼出解決・戻り値伝播は当面ホスト再帰 (Interpreter.Invoke の呼び戻し) で行い、
/// 深さは MemoryPolicy.MaxRecursionDepth で事前拒否する。
/// </summary>
public sealed class InterpreterFrame {
    private List<StackSlot[]>? _callArgumentBuffers;
    private VmByRef?[]? _argumentByRefs;
    private VmByRef?[]? _localByRefs;
    private int _callTargetIp0 = -1;
    private int _callTargetIp1 = -1;
    private int _callTargetIp2 = -1;
    private int _callTargetIp3 = -1;
    private int _callTargetToken0;
    private int _callTargetToken1;
    private int _callTargetToken2;
    private int _callTargetToken3;
    private CallTarget? _callTarget0;
    private CallTarget? _callTarget1;
    private CallTarget? _callTarget2;
    private CallTarget? _callTarget3;
    // Methods with many call sites (collection and LINQ helpers are typical)
    // outgrow the four inline entries. Keep an overflow map so those sites
    // still resolve once instead of continuously evicting one another.
    private Dictionary<int, CallTarget>? _callTargetOverflow;
    private int _callTargetCursor;
    private readonly int[] _fieldCacheTokens = new int[4];
    private readonly VmMethod?[] _fieldCacheMethods = new VmMethod?[4];
    private readonly GenericContext?[] _fieldCacheContexts = new GenericContext?[4];
    private readonly VmField?[] _fieldCacheFields = new VmField?[4];
    private int _fieldCacheCount;
    private int _fieldCacheCursor;
    private readonly VmClassType?[] _fieldIndexTypes = new VmClassType?[4];
    private readonly VmField?[] _fieldIndexFields = new VmField?[4];
    private readonly int[] _fieldIndices = new int[4];
    private int _fieldIndexCount;
    private int _fieldIndexCursor;
    private int _virtualTargetIp0 = -1;
    private int _virtualTargetIp1 = -1;
    private int _virtualTargetIp2 = -1;
    private int _virtualTargetIp3 = -1;
    private int _virtualTargetToken0;
    private int _virtualTargetToken1;
    private int _virtualTargetToken2;
    private int _virtualTargetToken3;
    private VmType? _virtualReceiverType0;
    private VmType? _virtualReceiverType1;
    private VmType? _virtualReceiverType2;
    private VmType? _virtualReceiverType3;
    private VmMethod? _virtualTarget0;
    private VmMethod? _virtualTarget1;
    private VmMethod? _virtualTarget2;
    private VmMethod? _virtualTarget3;
    private int _virtualTargetCursor;
    private VmMethod? _leafMethod0;
    private VmMethod? _leafMethod1;
    private VmMethod? _leafMethod2;
    private VmMethod? _leafMethod3;
    private JitCompiledMethod? _leafCompiled0;
    private JitCompiledMethod? _leafCompiled1;
    private JitCompiledMethod? _leafCompiled2;
    private JitCompiledMethod? _leafCompiled3;
    private bool _leafKnown0;
    private bool _leafKnown1;
    private bool _leafKnown2;
    private bool _leafKnown3;
    private int _leafCursor;
    private VmMethod? _compiledMethod0;
    private VmMethod? _compiledMethod1;
    private VmMethod? _compiledMethod2;
    private VmMethod? _compiledMethod3;
    private JitCompiledMethod? _compiled0;
    private JitCompiledMethod? _compiled1;
    private JitCompiledMethod? _compiled2;
    private JitCompiledMethod? _compiled3;
    private bool _compiledKnown0;
    private bool _compiledKnown1;
    private bool _compiledKnown2;
    private bool _compiledKnown3;
    private Dictionary<VmMethod, JitCompiledMethod?>? _compiledOverflow;
    private int _compiledCursor;
    private CallTarget? _contextTarget0;
    private CallTarget? _contextTarget1;
    private CallTarget? _contextTarget2;
    private CallTarget? _contextTarget3;
    private VmMethod? _contextMethod0;
    private VmMethod? _contextMethod1;
    private VmMethod? _contextMethod2;
    private VmMethod? _contextMethod3;
    private VmType? _contextReceiverType0;
    private VmType? _contextReceiverType1;
    private VmType? _contextReceiverType2;
    private VmType? _contextReceiverType3;
    private GenericContext? _context0;
    private GenericContext? _context1;
    private GenericContext? _context2;
    private GenericContext? _context3;
    private bool _contextKnown0;
    private bool _contextKnown1;
    private bool _contextKnown2;
    private bool _contextKnown3;
    private int _contextCursor;
    private int _callArgumentDepth;
    private int _argumentOffset;
    private int _argumentCount;
    private VmMethod? _localResolutionMethod;
    private GenericContext? _localResolutionContext;
    private VmType?[]? _localResolutionTypes;
    public required VmMethod Method { get; set; }
    public required DecodedInstruction[] Code { get; set; }
    /// <summary>IL オフセット → 命令インデックス (分岐ジャンプ用)。</summary>
    public required Dictionary<int, int> OffsetMap { get; set; }
    /// <summary>引数スロット (インスタンスメソッドは this が先頭)。</summary>
    public required StackSlot[] Arguments { get; set; }
    /// <summary>ローカル変数スロット (既定値で初期化済み)。</summary>
    public required StackSlot[] Locals { get; set; }
    /// <summary>ローカル変数の署名型 (既定値/検証に使用)。</summary>
    public required SigType[] LocalTypes { get; set; }
    public required EvaluationStack Stack { get; set; }

    internal int ArgumentCount => _argumentCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StackSlot ArgumentAt(int index) => Arguments[_argumentOffset + index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetArgument(int index, in StackSlot value) =>
        Arguments[_argumentOffset + index] = value;

    internal StackSlot[] CopyArguments() {
        var copy = new StackSlot[_argumentCount];
        Array.Copy(Arguments, _argumentOffset, copy, 0, _argumentCount);
        return copy;
    }

    internal bool TryGetCachedLocalResolution(VmMethod method, GenericContext? context,
        out VmType?[] types) {
        if (ReferenceEquals(_localResolutionMethod, method) &&
            ReferenceEquals(_localResolutionContext, context) &&
            _localResolutionTypes is { } cached) {
            types = cached;
            return true;
        }
        types = null!;
        return false;
    }

    internal void CacheLocalResolution(VmMethod method, GenericContext? context,
        VmType?[] types) {
        _localResolutionMethod = method;
        _localResolutionContext = context;
        _localResolutionTypes = types;
    }

    /// <summary>次に実行する命令のインデックス (Code 内)。</summary>
    public int Ip;

    // ---- EH (例外処理) 状態 ----

    /// <summary>
    /// finally 連鎖の再開先スタック (LIFO)。leave/例外伝播で finally を実行する際に積み、
    /// endfinally で pop する。値は命令インデックス、PropagateSentinel は「実行後に例外の伝播を再開」。
    /// </summary>
    public readonly List<int> FinallyResume = [];

    /// <summary>実行中のフィルタ句のインデックス (PreparedMethod.Clauses 内。endfilter 待ち。-1 = なし)。</summary>
    public int FilterClause = -1;

    /// <summary>例外の発生位置 (命令インデックス)。フィルタ不採用時に探索をここから再開する。</summary>
    internal int UnwindIp;

    /// <summary>このフレームで現在処理中の例外 (rethrow / finally 連鎖の伝播再開に使用)。</summary>
    internal VmGuestThrow? CurrentThrow;

    // ---- ジェネリック (M5) 状態 ----

    /// <summary>
    /// ジェネリックパラメータの置換コンテキスト (!n / !!n → 実引数)。
    /// 呼出ごとに Call で構築され、ローカル変数初期化・トークン解決 (TypeSpec/constrained. 等) で使う。
    /// </summary>
    public GenericContext? Context;

    /// <summary>直前の constrained. プレフィックスの型トークン (0 = なし)。次の call/callvirt で消費する。</summary>
    public int PendingConstrained;

    /// <summary>volatile. プレフィックスが続くメモリ命令に適用されるか。</summary>
    public bool PendingVolatile;

    /// <summary>tail. プレフィックスが次の call/callvirt/calli に適用されるか。</summary>
    public bool PendingTail;

    /// <summary>tail. プレフィックスが protected region 内にあったか。</summary>
    public bool PendingTailInProtectedRegion;

    /// <summary>readonly. プレフィックスが次の ldelema に適用されるか。</summary>
    public bool PendingReadonly;

    /// <summary>
    /// 一時的な call 引数バッファ。IL 呼出しは戻りまでしか引数配列を
    /// 必要としないため、フレーム単位で再利用してホットな intrinsic/IL
    /// 呼出しごとの配列確保を避ける。再入時は深さ別バッファを使う。
    /// </summary>
    internal IEnumerable<StackSlot[]> ActiveCallArgumentRoots {
        get {
            if (_callArgumentBuffers is null)
                yield break;
            for (var i = 0; i < _callArgumentDepth; i++)
                yield return _callArgumentBuffers[i];
        }
    }

    internal CallArgumentLease BorrowCallArguments(int arity) {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));
        _callArgumentBuffers ??= [];
        if (_callArgumentDepth == _callArgumentBuffers.Count)
            _callArgumentBuffers.Add(new StackSlot[arity]);
        // The array is passed across the VM/host call boundary, so its
        // Length is observable (delegate and intrinsic binders use it as the
        // argument count). Reusing a larger buffer would turn a two-argument
        // call into a three-argument call. Keep one exact-length buffer per
        // active depth; different arities pay only the first allocation for
        // that depth.
        else if (_callArgumentBuffers[_callArgumentDepth].Length != arity)
            _callArgumentBuffers[_callArgumentDepth] = new StackSlot[arity];
        var buffer = _callArgumentBuffers[_callArgumentDepth++];
        Stack.TakeArguments(arity, buffer);
        return new CallArgumentLease(this, buffer);
    }

    /// <summary>
    /// Borrow a constructor argument buffer.  The receiver occupies slot zero;
    /// constructor operands are copied from the evaluation stack into the
    /// remaining slots.  This keeps the leaf-constructor path allocation-free
    /// after the first use while preserving the exact argument-array shape
    /// required by the guest call boundary.
    /// </summary>
    internal CallArgumentLease BorrowConstructorArguments(int arity, in StackSlot receiver) {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));
        _callArgumentBuffers ??= [];
        var length = arity + 1;
        if (_callArgumentDepth == _callArgumentBuffers.Count)
            _callArgumentBuffers.Add(new StackSlot[length]);
        else if (_callArgumentBuffers[_callArgumentDepth].Length != length)
            _callArgumentBuffers[_callArgumentDepth] = new StackSlot[length];
        var buffer = _callArgumentBuffers[_callArgumentDepth++];
        buffer[0] = receiver;
        Stack.TakeArguments(arity, buffer.AsSpan(1));
        return new CallArgumentLease(this, buffer);
    }

    internal readonly struct CallArgumentLease : IDisposable {
        private readonly InterpreterFrame _frame;
        public readonly StackSlot[] Arguments;

        internal CallArgumentLease(InterpreterFrame frame, StackSlot[] arguments) {
            _frame = frame;
            Arguments = arguments;
        }

        public void Dispose() {
            // Only buffers below _callArgumentDepth are reported as VM roots.
            // Every borrow copies the complete active argument span before it
            // can be observed, so clearing this inactive buffer only adds a
            // write barrier/scan to every call without changing semantics.
            _frame._callArgumentDepth--;
        }
    }

    internal VmByRef ArgumentByRef(int index) {
        var cache = _argumentByRefs ??= new VmByRef?[_argumentCount];
        return cache[index] ??= VmByRef.Local(Arguments, _argumentOffset + index);
    }

    internal VmByRef LocalByRef(int index) {
        var cache = _localByRefs ??= new VmByRef?[Locals.Length];
        return cache[index] ??= VmByRef.Local(Locals, index);
    }

    internal bool TryGetCachedCallTarget(int token, out CallTarget target) {
        if (_callTargetIp0 == Ip && _callTargetToken0 == token && _callTarget0 is { } cached0) { target = cached0; return true; }
        if (_callTargetIp1 == Ip && _callTargetToken1 == token && _callTarget1 is { } cached1) { target = cached1; return true; }
        if (_callTargetIp2 == Ip && _callTargetToken2 == token && _callTarget2 is { } cached2) { target = cached2; return true; }
        if (_callTargetIp3 == Ip && _callTargetToken3 == token && _callTarget3 is { } cached3) { target = cached3; return true; }
        if (_callTargetOverflow is not null && _callTargetOverflow.TryGetValue(token, out var overflow)) { target = overflow; return true; }
        target = null!;
        return false;
    }

    internal void CacheCallTarget(int token, CallTarget target) {
        if (_callTargetCursor >= 4) {
            (_callTargetOverflow ??= new Dictionary<int, CallTarget>(8))[token] = target;
            _callTargetCursor++;
            return;
        }
        switch (_callTargetCursor++ & 3) {
            case 0: _callTargetIp0 = Ip; _callTargetToken0 = token; _callTarget0 = target; break;
            case 1: _callTargetIp1 = Ip; _callTargetToken1 = token; _callTarget1 = target; break;
            case 2: _callTargetIp2 = Ip; _callTargetToken2 = token; _callTarget2 = target; break;
            default: _callTargetIp3 = Ip; _callTargetToken3 = token; _callTarget3 = target; break;
        }
    }

    internal bool TryGetCachedField(int token, GenericContext? context, out VmField field) {
        var method = Method;
        for (var i = 0; i < _fieldCacheCount; i++) {
            if (_fieldCacheTokens[i] == token &&
                ReferenceEquals(_fieldCacheMethods[i], method) &&
                ReferenceEquals(_fieldCacheContexts[i], context) &&
                _fieldCacheFields[i] is { } cached) {
                field = cached;
                return true;
            }
        }
        field = null!;
        return false;
    }

    internal void CacheField(int token, GenericContext? context, VmField field) {
        var index = _fieldCacheCount < _fieldCacheTokens.Length
            ? _fieldCacheCount++
            : _fieldCacheCursor++ & 3;
        _fieldCacheTokens[index] = token;
        _fieldCacheMethods[index] = Method;
        _fieldCacheContexts[index] = context;
        _fieldCacheFields[index] = field;
    }

    internal bool TryGetCachedFieldIndex(VmClassType type, VmField field, out int index) {
        for (var i = 0; i < _fieldIndexCount; i++) {
            if (ReferenceEquals(_fieldIndexTypes[i], type) &&
                ReferenceEquals(_fieldIndexFields[i], field)) {
                index = _fieldIndices[i];
                return true;
            }
        }
        index = 0;
        return false;
    }

    internal void CacheFieldIndex(VmClassType type, VmField field, int index) {
        var slot = _fieldIndexCount < _fieldIndices.Length
            ? _fieldIndexCount++
            : _fieldIndexCursor++ & 3;
        _fieldIndexTypes[slot] = type;
        _fieldIndexFields[slot] = field;
        _fieldIndices[slot] = index;
    }

    internal bool TryGetCachedVirtualTarget(int token, VmType receiverType, out VmMethod target) {
        if (_virtualTargetIp0 == Ip && _virtualTargetToken0 == token &&
            ReferenceEquals(_virtualReceiverType0, receiverType) && _virtualTarget0 is { } target0) { target = target0; return true; }
        if (_virtualTargetIp1 == Ip && _virtualTargetToken1 == token &&
            ReferenceEquals(_virtualReceiverType1, receiverType) && _virtualTarget1 is { } target1) { target = target1; return true; }
        if (_virtualTargetIp2 == Ip && _virtualTargetToken2 == token &&
            ReferenceEquals(_virtualReceiverType2, receiverType) && _virtualTarget2 is { } target2) { target = target2; return true; }
        if (_virtualTargetIp3 == Ip && _virtualTargetToken3 == token &&
            ReferenceEquals(_virtualReceiverType3, receiverType) && _virtualTarget3 is { } target3) { target = target3; return true; }
        target = null!;
        return false;
    }

    internal void CacheVirtualTarget(int token, VmType receiverType, VmMethod target) {
        switch (_virtualTargetCursor++ & 3) {
            case 0:
                _virtualTargetIp0 = Ip; _virtualTargetToken0 = token;
                _virtualReceiverType0 = receiverType; _virtualTarget0 = target; break;
            case 1:
                _virtualTargetIp1 = Ip; _virtualTargetToken1 = token;
                _virtualReceiverType1 = receiverType; _virtualTarget1 = target; break;
            case 2:
                _virtualTargetIp2 = Ip; _virtualTargetToken2 = token;
                _virtualReceiverType2 = receiverType; _virtualTarget2 = target; break;
            default:
                _virtualTargetIp3 = Ip; _virtualTargetToken3 = token;
                _virtualReceiverType3 = receiverType; _virtualTarget3 = target; break;
        }
    }

    internal bool TryGetCachedLeaf(VmMethod method, out JitCompiledMethod? compiled) {
        if (_leafKnown0 && ReferenceEquals(_leafMethod0, method) && _leafCompiled0 is not null) { compiled = _leafCompiled0; return true; }
        if (_leafKnown1 && ReferenceEquals(_leafMethod1, method) && _leafCompiled1 is not null) { compiled = _leafCompiled1; return true; }
        if (_leafKnown2 && ReferenceEquals(_leafMethod2, method) && _leafCompiled2 is not null) { compiled = _leafCompiled2; return true; }
        if (_leafKnown3 && ReferenceEquals(_leafMethod3, method) && _leafCompiled3 is not null) { compiled = _leafCompiled3; return true; }
        compiled = null;
        return false;
    }

    internal bool TryGetCachedCallContext(CallTarget target, VmMethod method,
        VmType? receiverType, out GenericContext? context) {
        if (_contextKnown0 && ReferenceEquals(_contextTarget0, target) &&
            ReferenceEquals(_contextMethod0, method) && ReferenceEquals(_contextReceiverType0, receiverType)) {
            context = _context0; return true;
        }
        if (_contextKnown1 && ReferenceEquals(_contextTarget1, target) &&
            ReferenceEquals(_contextMethod1, method) && ReferenceEquals(_contextReceiverType1, receiverType)) {
            context = _context1; return true;
        }
        if (_contextKnown2 && ReferenceEquals(_contextTarget2, target) &&
            ReferenceEquals(_contextMethod2, method) && ReferenceEquals(_contextReceiverType2, receiverType)) {
            context = _context2; return true;
        }
        if (_contextKnown3 && ReferenceEquals(_contextTarget3, target) &&
            ReferenceEquals(_contextMethod3, method) && ReferenceEquals(_contextReceiverType3, receiverType)) {
            context = _context3; return true;
        }
        context = null;
        return false;
    }

    internal void CacheCallContext(CallTarget target, VmMethod method,
        VmType? receiverType, GenericContext? context) {
        switch (_contextCursor++ & 3) {
            case 0:
                _contextTarget0 = target; _contextMethod0 = method;
                _contextReceiverType0 = receiverType; _context0 = context; _contextKnown0 = true; break;
            case 1:
                _contextTarget1 = target; _contextMethod1 = method;
                _contextReceiverType1 = receiverType; _context1 = context; _contextKnown1 = true; break;
            case 2:
                _contextTarget2 = target; _contextMethod2 = method;
                _contextReceiverType2 = receiverType; _context2 = context; _contextKnown2 = true; break;
            default:
                _contextTarget3 = target; _contextMethod3 = method;
                _contextReceiverType3 = receiverType; _context3 = context; _contextKnown3 = true; break;
        }
    }

    internal void CacheLeaf(VmMethod method, JitCompiledMethod? compiled) {
        if (compiled is null)
            return;
        switch (_leafCursor++ & 3) {
            case 0: _leafMethod0 = method; _leafCompiled0 = compiled; _leafKnown0 = true; break;
            case 1: _leafMethod1 = method; _leafCompiled1 = compiled; _leafKnown1 = true; break;
            case 2: _leafMethod2 = method; _leafCompiled2 = compiled; _leafKnown2 = true; break;
            default: _leafMethod3 = method; _leafCompiled3 = compiled; _leafKnown3 = true; break;
        }
    }

    internal bool TryGetCachedCompiled(VmMethod method, out JitCompiledMethod? compiled) {
        if (_compiledKnown0 && ReferenceEquals(_compiledMethod0, method) && _compiled0 is not null) { compiled = _compiled0; return true; }
        if (_compiledKnown1 && ReferenceEquals(_compiledMethod1, method) && _compiled1 is not null) { compiled = _compiled1; return true; }
        if (_compiledKnown2 && ReferenceEquals(_compiledMethod2, method) && _compiled2 is not null) { compiled = _compiled2; return true; }
        if (_compiledKnown3 && ReferenceEquals(_compiledMethod3, method) && _compiled3 is not null) { compiled = _compiled3; return true; }
        if (_compiledOverflow is not null && _compiledOverflow.TryGetValue(method, out compiled) && compiled is not null)
            return true;
        compiled = null;
        return false;
    }

    internal void CacheCompiled(VmMethod method, JitCompiledMethod? compiled) {
        if (compiled is null)
            return;
        if (_compiledCursor >= 4) {
            (_compiledOverflow ??= new Dictionary<VmMethod, JitCompiledMethod?>(8))[method] = compiled;
            _compiledCursor++;
            return;
        }
        switch (_compiledCursor++ & 3) {
            case 0: _compiledMethod0 = method; _compiled0 = compiled; _compiledKnown0 = true; break;
            case 1: _compiledMethod1 = method; _compiled1 = compiled; _compiledKnown1 = true; break;
            case 2: _compiledMethod2 = method; _compiled2 = compiled; _compiledKnown2 = true; break;
            default: _compiledMethod3 = method; _compiled3 = compiled; _compiledKnown3 = true; break;
        }
    }

    internal void Reinitialize(VmMethod method, StackSlot[] arguments, PreparedMethod prepared, int maxStack) {
        Method = method;
        Code = prepared.Code;
        OffsetMap = prepared.OffsetMap;
        Arguments = arguments;
        _argumentOffset = 0;
        _argumentCount = arguments.Length;
        if (Locals.Length != prepared.InitialLocals.Length)
            Locals = new StackSlot[prepared.InitialLocals.Length];
        // Prepared locals are a small, fixed-shape vector for each method.
        // Copying them element-wise is faster than Array.Copy for the tiny
        // arities that dominate nested CoreLib calls, and overwrites every
        // slot so a separate clear is unnecessary.
        for (var i = 0; i < prepared.InitialLocals.Length; i++)
            Locals[i] = prepared.InitialLocals[i];
        LocalTypes = prepared.LocalTypes;
        Stack.PrepareForReuse(maxStack);
        ResetTransientState();
    }

    internal void ReinitializeAliased(VmMethod method, StackSlot[] argumentStorage,
        int argumentOffset, int argumentCount, PreparedMethod prepared, int maxStack) {
        Method = method;
        Code = prepared.Code;
        OffsetMap = prepared.OffsetMap;
        Arguments = argumentStorage;
        _argumentOffset = argumentOffset;
        _argumentCount = argumentCount;
        if (Locals.Length != prepared.InitialLocals.Length)
            Locals = new StackSlot[prepared.InitialLocals.Length];
        for (var i = 0; i < prepared.InitialLocals.Length; i++)
            Locals[i] = prepared.InitialLocals[i];
        LocalTypes = prepared.LocalTypes;
        Stack.PrepareForReuse(maxStack);
        _argumentByRefs = null;
        _localByRefs = null;
        ResetTransientState();
    }

    /// <summary>Drop all frame-owned roots before putting the frame on the local pool.</summary>
    internal void ResetForPool() {
        if (_callArgumentBuffers is { } buffers) {
            foreach (var buffer in buffers)
                Array.Clear(buffer);
        }
        _callArgumentDepth = 0;
        _argumentByRefs = null;
        _localByRefs = null;
        _argumentOffset = 0;
        _argumentCount = 0;
        Array.Clear(Locals);
        Stack.Clear();
        Arguments = [];
        ResetTransientState();
    }

    internal void ResetForAliasedPool() {
        if (_callArgumentBuffers is { } buffers) {
            foreach (var buffer in buffers)
                Array.Clear(buffer);
        }
        _callArgumentDepth = 0;
        _argumentByRefs = null;
        _localByRefs = null;
        _argumentOffset = 0;
        _argumentCount = 0;
        Array.Clear(Locals);
        Stack.Clear();
        Arguments = [];
        ResetTransientState();
    }

    private void ResetTransientState() {
        Ip = 0;
        FilterClause = -1;
        UnwindIp = 0;
        CurrentThrow = null;
        Context = null;
        PendingConstrained = 0;
        PendingVolatile = false;
        PendingTail = false;
        PendingTailInProtectedRegion = false;
        PendingReadonly = false;
        FinallyResume.Clear();
        _callTargetIp0 = _callTargetIp1 = _callTargetIp2 = _callTargetIp3 = -1;
        _callTarget0 = _callTarget1 = _callTarget2 = _callTarget3 = null;
        _callTargetOverflow?.Clear();
        _callTargetCursor = 0;
        _virtualTargetIp0 = _virtualTargetIp1 = _virtualTargetIp2 = _virtualTargetIp3 = -1;
        _virtualReceiverType0 = _virtualReceiverType1 = _virtualReceiverType2 = _virtualReceiverType3 = null;
        _virtualTarget0 = _virtualTarget1 = _virtualTarget2 = _virtualTarget3 = null;
        _virtualTargetCursor = 0;
        _leafKnown0 = _leafKnown1 = _leafKnown2 = _leafKnown3 = false;
        _leafMethod0 = _leafMethod1 = _leafMethod2 = _leafMethod3 = null;
        _leafCompiled0 = _leafCompiled1 = _leafCompiled2 = _leafCompiled3 = null;
        _leafCursor = 0;
        _compiledKnown0 = _compiledKnown1 = _compiledKnown2 = _compiledKnown3 = false;
        _compiledMethod0 = _compiledMethod1 = _compiledMethod2 = _compiledMethod3 = null;
        _compiled0 = _compiled1 = _compiled2 = _compiled3 = null;
        _compiledOverflow?.Clear();
        _compiledCursor = 0;
        _contextKnown0 = _contextKnown1 = _contextKnown2 = _contextKnown3 = false;
        _contextTarget0 = _contextTarget1 = _contextTarget2 = _contextTarget3 = null;
        _contextMethod0 = _contextMethod1 = _contextMethod2 = _contextMethod3 = null;
        _contextReceiverType0 = _contextReceiverType1 = _contextReceiverType2 = _contextReceiverType3 = null;
        _context0 = _context1 = _context2 = _context3 = null;
        _contextCursor = 0;
    }

    internal void Release() => Stack.Release();


    public static InterpreterFrame Create(VmMethod method, StackSlot[] arguments, SigType[] localTypes, int maxStack,
        DecodedInstruction[]? preparedCode = null) {
        var code = preparedCode ?? method.DecodeIl();
        var offsetMap = new Dictionary<int, int>(code.Length * 2);
        for (var i = 0; i < code.Length; i++)
            offsetMap[code[i].Offset] = i;

        var locals = new StackSlot[localTypes.Length];
        for (var i = 0; i < localTypes.Length; i++)
            locals[i] = DefaultValue(localTypes[i]);

        return new InterpreterFrame {
            Method = method,
            Code = code,
            OffsetMap = offsetMap,
            Arguments = arguments,
            _argumentCount = arguments.Length,
            Locals = locals,
            LocalTypes = localTypes,
            // Preparation has already checked the declared maxstack. Keep
            // the runtime storage exact; a larger floor would allocate eight
            // slots for every tiny helper method and those frames are often
            // created millions of times by managed BCL IL.
            Stack = new EvaluationStack(maxStack),
        };
    }

    internal static InterpreterFrame Create(VmMethod method, StackSlot[] arguments,
        PreparedMethod prepared, int maxStack) => new() {
            Method = method,
            Code = prepared.Code,
            OffsetMap = prepared.OffsetMap,
            Arguments = arguments,
            _argumentCount = arguments.Length,
            Locals = prepared.InitialLocals.Length == 0 ? [] : (StackSlot[])prepared.InitialLocals.Clone(),
            LocalTypes = prepared.LocalTypes,
            Stack = new EvaluationStack(maxStack),
        };

    /// <summary>署名型に対する既定値スロット (ローカル変数のゼロ初期化)。</summary>
    public static StackSlot DefaultValue(SigType type) => type.Kind switch {
        SigKind.I4 or SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1
            or SigKind.I2 or SigKind.U2 or SigKind.U4 => StackSlot.OfInt32(0),
        SigKind.I8 or SigKind.U8 => StackSlot.OfInt64(0),
        SigKind.R4 or SigKind.R8 => StackSlot.OfFloat(0),
        SigKind.I or SigKind.U => StackSlot.OfNativeInt(0),
        SigKind.ByRef => StackSlot.OfByRef(new VmByRef([], 0)), // null byref 相当 (扱いは M3+)
        SigKind.Pointer => StackSlot.OfNativeInt(0),
        // オブジェクト参照/値型/未確定型は null で開始 (値型実体は M3 の VmStructValue)
        _ => StackSlot.Null,
    };
}
