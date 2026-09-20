using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// マネージ参照 (ldarga/ldloca/ldelema 結果)。参照先を StackSlot 配列 + インデックスで表す。
/// ローカル/引数配列を直接指すことで stind 等が正しいスロットに書き込む。
/// フィールド/配列要素への参照はオブジェクトモデル (M3) で拡張する。
/// </summary>
public sealed class VmByRef {
    public readonly StackSlot[] Container;
    public readonly int Index;

    public VmByRef(StackSlot[] container, int index) {
        Container = container;
        Index = index;
    }

    public ref StackSlot Slot => ref Container[Index];
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

    public static InterpreterFrame Create(VmMethod method, StackSlot[] arguments, SigType[] localTypes, int maxStack) {
        var code = method.DecodeIl();
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
            Stack = new EvaluationStack(maxStack),
        };
    }

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
