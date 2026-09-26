using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

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

    public VmByRef(StackSlot[] container, int index, bool isReadOnly = false, VmObject? owner = null) {
        ArgumentNullException.ThrowIfNull(container);
        if ((uint)index > (uint)container.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        Container = container;
        Index = index;
        IsReadOnly = isReadOnly;
        Owner = owner;
    }

    /// <summary>フレームの引数/ローカル storage への参照を作る。</summary>
    public static VmByRef Frame(StackSlot[] container, int index, bool isReadOnly = false) =>
        new(container, index, isReadOnly);

    /// <summary>
    /// 配列要素への参照を作る。配列要素の storage は必ず配列本体と同じ寿命を持つため、
    /// owner 付き生成をこの factory に集約する。
    /// </summary>
    public static VmByRef ArrayElement(VmArray array, int index, bool isReadOnly = false) =>
        new(array.Elements, index, isReadOnly, array);

    /// <summary>ボックス化値の fields への参照を作る。</summary>
    public static VmByRef BoxedValue(VmBoxedValue boxed, int index = 0, bool isReadOnly = false) =>
        new(boxed.Fields, index, isReadOnly, boxed);

    /// <summary>VmObject が所有する任意の field/state storage への参照を作る。</summary>
    public static VmByRef OwnedStorage(VmObject owner, StackSlot[] container, int index,
        bool isReadOnly = false) => new(container, index, isReadOnly, owner);

    public bool IsReadOnly { get; }
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
        lock (Container)
            return Container[Index];
    }

    public void Write(in StackSlot value) {
        EnsureWritable();
        EnsureInBounds();
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
    public required VmMethod Method { get; init; }
    public required DecodedInstruction[] Code { get; init; }
    /// <summary>IL オフセット → 命令インデックス (分岐ジャンプ用)。</summary>
    public required Dictionary<int, int> OffsetMap { get; init; }
    /// <summary>引数スロット (インスタンスメソッドは this が先頭)。</summary>
    public required StackSlot[] Arguments { get; init; }
    /// <summary>ローカル変数スロット (既定値で初期化済み)。</summary>
    public required StackSlot[] Locals { get; init; }
    /// <summary>ローカル変数の署名型 (既定値/検証に使用)。</summary>
    public required SigType[] LocalTypes { get; init; }
    public required EvaluationStack Stack { get; init; }

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
            Locals = locals,
            LocalTypes = localTypes,
            // Preparation has already checked the declared maxstack.  Keep a
            // small runtime floor for intrinsic bridge calls whose host-side
            // implementation can transiently retain an extra result slot.
            Stack = new EvaluationStack(Math.Max(maxStack, 8)),
        };
    }

    internal static InterpreterFrame Create(VmMethod method, StackSlot[] arguments,
        PreparedMethod prepared, int maxStack) => new() {
            Method = method,
            Code = prepared.Code,
            OffsetMap = prepared.OffsetMap,
            Arguments = arguments,
            Locals = (StackSlot[])prepared.InitialLocals.Clone(),
            LocalTypes = prepared.LocalTypes,
            Stack = new EvaluationStack(Math.Max(maxStack, 8)),
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
