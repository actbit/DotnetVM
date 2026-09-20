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

/// <summary>
/// IL インタプリタ本体。事前デコード済み命令列をループ実行する。
/// - 命令ごとにクォータを消費し、1024 命令毎にセーフポイント検査を行う
/// - intrinsic 呼出は必ず InvokeIntrinsic ゲート経由 (クォータ消費・セーフポイント・正規化を強制)
/// - ゲスト呼出はフレームを積み、深さは MaxRecursionDepth で事前拒否
/// </summary>
public sealed class Interpreter {
    private const int SafepointInterval = 1024;

    private readonly TypeLoader _loader;
    private readonly IntrinsicRegistry _intrinsics;
    private readonly VmConsole _console;
    private readonly VmHeap _heap;
    private readonly VmStringPool _strings = new();
    private readonly IntrinsicContext _intrinsicContext;
    private readonly MemoryPolicy _memory;
    private readonly Dictionary<VmMethod, PreparedMethod> _prepared = [];
    private readonly HashSet<VmType> _initializedTypes = [];
    private long _instructionCount;
    private int _depth;
    private bool _running;
    // 実行中フレームの一覧 (GC ルート源。Invoke の呼出チェーン = フレームチェーン)
    private readonly List<InterpreterFrame> _liveFrames = [];

    public long InstructionCount => _instructionCount;

    public Interpreter(TypeLoader loader, IntrinsicRegistry intrinsics, VmConsole console, MemoryPolicy memory, VmHeap heap) {
        _loader = loader;
        _intrinsics = intrinsics;
        _console = console;
        _memory = memory;
        _heap = heap;
        _intrinsicContext = new IntrinsicContext { Console = console, Strings = _strings };
        // GC ルート源の登録: 実行中フレーム / 静的ストレージ / intrinsic 静的フィールド
        _heap.AddRootSlotSource(EnumerateFrameRoots);
        _heap.AddRootSlotSource(ObjectModel.EnumerateStaticStorage);
        _heap.AddRootSlotSource(() => _intrinsicStaticFields.Values);
    }

    /// <summary>文字列プール (VM ファサードから参照用)。</summary>
    public VmStringPool Strings => _strings;

    /// <summary>型ローダ (ホスト API が intrinsic ファサード型を解決するのに使う)。</summary>
    public TypeLoader Loader => _loader;

    /// <summary>値を指定の型としてボックス化する (box 命令と同じセマンティクス)。</summary>
    public StackSlot Box(VmType type, in StackSlot value) {
        var fields = value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue sv
            ? sv.Clone().Fields
            : [value];
        return StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(type, fields)));
    }

    /// <summary>VM ヒープ (アロケーション計上の唯一の入口。ホスト API のインスタンス生成からも使う)。</summary>
    public VmHeap Heap => _heap;

    /// <summary>インスタンスを生成して .ctor を実行する (newobj 相当。VM ホスト API 用)。</summary>
    public VmClassInstance CreateInstance(VmClassType type, StackSlot[] constructorArgs) {
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && !m.IsStatic &&
                m.Signature.ParamTypes.Length == constructorArgs.Length && m.Body is not null)
            ?? throw new ArgumentException($"型 {type.FullName} に引数 {constructorArgs.Length} 個の .ctor がありません。");
        EnsureInitialized(type);
        var instance = _heap.Allocate(new VmClassInstance(type, ObjectModel.CreateInstanceStorage(type, _loader)));
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
        if (!_running) {
            _running = true;
            _intrinsics.Seal(); // 実行開始後の intrinsic 登録を禁止
        }
        if (method.Body is null)
            ThrowNoBody(method);
        if (_depth >= _memory.MaxRecursionDepth)
            throw new UnhandledGuestException("System.StackOverflowException",
                $"再帰深さが上限 {_memory.MaxRecursionDepth} を超えました。");
        _depth++;
        try {
            CloneStructArgs(method, arguments);
            var frame = InterpreterFrame.Create(method, arguments,
                Prepare(method).LocalTypes, method.Body.MaxStack);
            frame.Context = context; // FixupStructLocals が !n ローカルを実引数で初期化する
            _liveFrames.Add(frame);
            try {
                FixupStructLocals(frame);
                return RunFrame(frame);
            } finally {
                _liveFrames.RemoveAt(_liveFrames.Count - 1);
            }
        } finally {
            _depth--;
        }
    }

    /// <summary>実行中フレームが保持する全スロット (引数/ローカル/評価スタック/送出中例外) をルートとして列挙する。</summary>
    private IEnumerable<StackSlot[]> EnumerateFrameRoots() {
        foreach (var frame in _liveFrames) {
            yield return frame.Arguments;
            yield return frame.Locals;
            if (frame.Stack.Count > 0)
                yield return frame.Stack.CopySlots();
            if (frame.CurrentThrow is { } throwing)
                yield return [StackSlot.OfObject(throwing.ExceptionObject)];
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
        for (var i = 0; i < frame.Locals.Length; i++) {
            ref var slot = ref frame.Locals[i];
            if (slot.Kind != StackKind.Object || slot.ObjectValue is not null)
                continue;
            var sigType = frame.LocalTypes[i];
            if (sigType.Kind is not (SigKind.TypeToken or SigKind.GenericInst
                or SigKind.GenericVar or SigKind.GenericMethodVar))
                continue;
            var type = _loader.ResolveToken(sigType, frame.Context);
            if (type.IsValueType)
                slot = ObjectModel.DefaultForType(type, _loader);
        }
    }

    private static void ThrowNoBody(VmMethod method) {
        // abstract は未実装面、native (P/Invoke) はセキュリティポリシーで拒否
        if (method.IsAbstract)
            throw new NotSupportedException(
                $"抽象メソッド {method} には実装がありません (継承解決は M3 以降)。");
        if ((method.ImplFlags & 0x0003) == 0x0003)
            throw new OperationNotAllowedException(
                $"メソッド {method} はネイティブ実行 (P/Invoke) を要求しますが、VM はネイティブ依存を許可しません。");
        throw new NotSupportedException($"メソッド {method} には実行可能な本体がありません。");
    }

    // ---- 実行ループ ----

    /// <summary>
    /// 1 フレームの実行 + EH (例外処理)。ゲスト例外 (VmGuestThrow / VM 内部例外) が上がると
    /// このフレームの EH 句で処理できる限り処理を続け (catch 句へのディスパッチ、finally/fault
    /// の通過実行)、処理できない場合はキャリアごと上位フレームへ伝播する。
    /// ResourceExhaustedException 系 (メモリ/命令クォータ) はここでは捕捉しない (管理例外)。
    /// </summary>
    private StackSlot RunFrame(InterpreterFrame frame) {
        try {
            return RunFrameCore(frame);
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
                return RunFrameCore(frame);
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
    private bool TryDispatchHandler(InterpreterFrame frame, VmGuestThrow carrier) {
        frame.UnwindIp = frame.Ip; // フィルタ不採用時に発生位置から探索をやり直せるように
        return TryDispatchHandlerFrom(frame, carrier, 0);
    }

    private bool TryDispatchHandlerFrom(InterpreterFrame frame, VmGuestThrow carrier, int startIndex) {
        var clauses = Prepare(frame.Method).Clauses;
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
                    var targetType = ResolveTypeToken(clause.ClassToken, frame.Context);
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

    /// <summary>endfinally で伝播を再開することを示す番兵 (FinallyResume スタックの値)。</summary>
    private const int PropagateSentinel = -1;

    private StackSlot RunFrameCore(InterpreterFrame frame) {
        var prepared = Prepare(frame.Method);
        while (true) {
            ConsumeInstruction();
            var instruction = frame.Code[frame.Ip];
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                case ILOp.Volatile or ILOp.Unaligned or ILOp.Readonly or ILOp.Tail: // プレフィックス (効果なし)
                    break;
                case ILOp.Constrained:
                    // constrained. <type> は次の call/callvirt で解決する。値型レシーバは
                    // ValueType/ByRef スロットでそのまま仮想ディスパッチでき、参照型レシーバは
                    // 通常の callvirt と等価なため、トークンを記録して次の呼出に渡すだけでよい
                    frame.PendingConstrained = instruction.IntOperand;
                    break;

                // ---- 引数 ----
                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    frame.Stack.Push(frame.Arguments[instruction.Op - ILOp.Ldarg_0]);
                    break;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(frame.Arguments[instruction.IntOperand]);
                    break;
                case ILOp.Ldarga_S or ILOp.Ldarga:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(frame.Arguments, instruction.IntOperand)));
                    break;
                case ILOp.Starg_S or ILOp.Starg:
                    CheckArgIndex(frame, instruction.IntOperand);
                    frame.Arguments[instruction.IntOperand] = frame.Stack.Pop();
                    break;

                // ---- ローカル変数 ----
                case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                    CheckLocalIndex(frame, instruction.Op - ILOp.Ldloc_0);
                    frame.Stack.Push(PushCopyOfValue(frame.Locals[instruction.Op - ILOp.Ldloc_0]));
                    break;
                case ILOp.Ldloc_S or ILOp.Ldloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(PushCopyOfValue(frame.Locals[instruction.IntOperand]));
                    break;
                case ILOp.Ldloca_S or ILOp.Ldloca:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(frame.Locals, instruction.IntOperand)));
                    break;
                case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                    CheckLocalIndex(frame, instruction.Op - ILOp.Stloc_0);
                    frame.Locals[instruction.Op - ILOp.Stloc_0] = StoreCopyOfValue(frame.Stack.Pop());
                    break;
                case ILOp.Stloc_S or ILOp.Stloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Locals[instruction.IntOperand] = StoreCopyOfValue(frame.Stack.Pop());
                    break;

                // ---- 即値 ----
                case ILOp.Ldnull:
                    frame.Stack.Push(StackSlot.Null);
                    break;
                case ILOp.Ldc_I4_M1:
                    frame.Stack.Push(StackSlot.OfInt32(-1));
                    break;
                case ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or ILOp.Ldc_I4_3
                    or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.Op - ILOp.Ldc_I4_0));
                    break;
                case ILOp.Ldc_I4_S:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.IntOperand));
                    break;
                case ILOp.Ldc_I4:
                    frame.Stack.Push(StackSlot.OfInt32(instruction.IntOperand));
                    break;
                case ILOp.Ldc_I8:
                    frame.Stack.Push(StackSlot.OfInt64(instruction.LongOperand));
                    break;
                case ILOp.Ldc_R4 or ILOp.Ldc_R8:
                    frame.Stack.Push(StackSlot.OfFloat(instruction.DoubleOperand));
                    break;
                case ILOp.Dup:
                    frame.Stack.Push(frame.Stack.Peek());
                    break;
                case ILOp.Pop:
                    _ = frame.Stack.Pop();
                    break;

                // ---- 分岐 ----
                case ILOp.Br or ILOp.Br_S:
                    JumpTo(frame, instruction.IntOperand);
                    continue;
                case ILOp.BrFalse or ILOp.BrFalse_S:
                    if (!IsTrue(frame.Stack.Pop()))
                        { JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.BrTrue or ILOp.BrTrue_S:
                    if (IsTrue(frame.Stack.Pop()))
                        { JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S
                    or ILOp.Bge or ILOp.Bge_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S
                    or ILOp.Blt or ILOp.Blt_S or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S
                    or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    if (CompareBranch(instruction.Op, left, right))
                        { JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                }
                case ILOp.Switch: {
                    var index = frame.Stack.Pop().AsInt32;
                    var targets = instruction.SwitchTargets!;
                    if ((uint)index < (uint)targets.Length) {
                        JumpTo(frame, targets[index]);
                        continue;
                    }
                    break;
                }

                // ---- 比較 (スタックに 0/1 を積む) ----
                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    frame.Stack.Push(StackSlot.OfInt32(Compare(instruction.Op, left, right) ? 1 : 0));
                    break;
                }

                // ---- 算術 ----
                case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un
                    or ILOp.Rem or ILOp.Rem_Un or ILOp.And or ILOp.Or or ILOp.Xor
                    or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
                    or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un
                    or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    frame.Stack.Push(BinaryArithmetic(instruction.Op, left, right));
                    break;
                }
                case ILOp.Neg or ILOp.Not: {
                    var value = frame.Stack.Pop();
                    frame.Stack.Push(UnaryArithmetic(instruction.Op, value));
                    break;
                }

                // ---- 変換 ----
                case >= ILOp.Conv_I1 and <= ILOp.Conv_R_Un
                    when instruction.Op is ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8
                        or ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U4 or ILOp.Conv_U8 or ILOp.Conv_R_Un:
                    frame.Stack.Push(ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Conv_U2 or ILOp.Conv_U1 or ILOp.Conv_I or ILOp.Conv_U:
                    frame.Stack.Push(ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case >= ILOp.Conv_Ovf_I1_Un and <= ILOp.Conv_Ovf_U_Un:
                    frame.Stack.Push(ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case >= ILOp.Conv_Ovf_I1 and <= ILOp.Conv_Ovf_U8:
                    frame.Stack.Push(ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;

                // ---- 文字列 ----
                case ILOp.Ldstr:
                    frame.Stack.Push(StackSlot.OfObject(
                        _strings.GetOrNew(_loader.Image.GetUserString(instruction.IntOperand & 0xFFFFFF))));
                    break;

                // ---- 呼出 ----
                case ILOp.Call or ILOp.Callvirt: {
                    var result = Call(instruction.IntOperand, frame,
                        instruction.Op == ILOp.Callvirt, frame.PendingConstrained);
                    frame.PendingConstrained = 0; // constrained. は直後の 1 呼出でのみ有効
                    if (result is { } value)
                        frame.Stack.Push(value);
                    break;
                }
                case ILOp.Ret:
                    return SignatureReturnsValue(frame.Method.Signature) ? frame.Stack.Pop() : default;

                // ---- オブジェクト生成 ----
                case ILOp.Newobj: {
                    var value = NewObject(instruction.IntOperand, frame);
                    if (value is { } pushed)
                        frame.Stack.Push(pushed);
                    break;
                }
                case ILOp.Newarr: {
                    var count = frame.Stack.Pop().AsInt32;
                    if (count < 0)
                        throw new UnhandledGuestException("System.OverflowException", null);
                    var elementType = ResolveTypeToken(instruction.IntOperand, frame.Context);
                    var elements = new StackSlot[count];
                    for (var i = 0; i < count; i++)
                        elements[i] = ObjectModel.DefaultForType(elementType, _loader);
                    frame.Stack.Push(StackSlot.OfObject(_heap.Allocate(
                        new VmArray(new VmArrayType { ElementType = elementType }, elements))));
                    break;
                }
                case ILOp.Ldlen:
                    frame.Stack.Push(StackSlot.OfNativeInt(GetArray(frame.Stack.Pop()).Length));
                    break;

                // ---- 配列要素 ----
                case ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2
                    or ILOp.Ldelem_I4 or ILOp.Ldelem_U4:
                    frame.Stack.Push(ArrayLoad(frame, ArrayElementKind.Int32));
                    break;
                case ILOp.Ldelem_I8 or ILOp.Ldelem_I:
                    frame.Stack.Push(ArrayLoad(frame, ArrayElementKind.Int64));
                    break;
                case ILOp.Ldelem_R4 or ILOp.Ldelem_R8:
                    frame.Stack.Push(ArrayLoad(frame, ArrayElementKind.Float));
                    break;
                case ILOp.Ldelem_Ref:
                    frame.Stack.Push(ArrayLoad(frame, ArrayElementKind.Object));
                    break;
                case ILOp.Ldelem:
                    frame.Stack.Push(ArrayLoad(frame, ElementKindFromType(ResolveTypeToken(instruction.IntOperand, frame.Context))));
                    break;
                case ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2 or ILOp.Stelem_I4:
                    ArrayStore(frame, ArrayElementKind.Int32);
                    break;
                case ILOp.Stelem_I8:
                    ArrayStore(frame, ArrayElementKind.Int64);
                    break;
                case ILOp.Stelem_R4 or ILOp.Stelem_R8:
                    ArrayStore(frame, ArrayElementKind.Float);
                    break;
                case ILOp.Stelem_Ref:
                    ArrayStore(frame, ArrayElementKind.Object);
                    break;
                case ILOp.Stelem:
                    ArrayStore(frame, ElementKindFromType(ResolveTypeToken(instruction.IntOperand, frame.Context)));
                    break;
                case ILOp.Ldelema: {
                    var index = frame.Stack.Pop().AsInt32;
                    var array = GetArray(frame.Stack.Pop());
                    CheckArrayBounds(array, index);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(array.Elements, index)));
                    break;
                }

                // ---- フィールド ----
                case ILOp.Ldfld or ILOp.Ldflda: {
                    var field = ResolveFieldToken(instruction.IntOperand, frame.Context);
                    var objSlot = frame.Stack.Pop();
                    var location = FieldLocation(objSlot, field);
                    if (instruction.Op == ILOp.Ldflda) {
                        frame.Stack.Push(StackSlot.OfByRef(location));
                        break;
                    }
                    frame.Stack.Push(PushCopyOfValue(location.Slot));
                    break;
                }
                case ILOp.Stfld: {
                    var field = ResolveFieldToken(instruction.IntOperand, frame.Context);
                    var value = frame.Stack.Pop();
                    var objSlot = frame.Stack.Pop();
                    FieldLocation(objSlot, field).Slot = StoreCopyOfValue(value);
                    break;
                }
                case ILOp.Ldsfld: {
                    var slot = StaticFieldLocation(instruction.IntOperand, frame.Context);
                    frame.Stack.Push(PushCopyOfValue(slot.Slot));
                    break;
                }
                case ILOp.Ldsflda:
                    frame.Stack.Push(StackSlot.OfByRef(StaticFieldLocation(instruction.IntOperand, frame.Context)));
                    break;
                case ILOp.Stsfld: {
                    var value = frame.Stack.Pop();
                    StaticFieldLocation(instruction.IntOperand, frame.Context).Slot = value;
                    break;
                }

                // ---- 間接アクセス (ldind/stind) ----
                case ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2
                    or ILOp.Ldind_I4 or ILOp.Ldind_U4:
                    frame.Stack.Push(StackSlot.OfInt32((int)((VmByRef)frame.Stack.Pop().ObjectValue!).Slot.Int64Value));
                    break;
                case ILOp.Ldind_I8 or ILOp.Ldind_I:
                    frame.Stack.Push(StackSlot.OfInt64(((VmByRef)frame.Stack.Pop().ObjectValue!).Slot.Int64Value));
                    break;
                case ILOp.Ldind_R4 or ILOp.Ldind_R8:
                    frame.Stack.Push(StackSlot.OfFloat(((VmByRef)frame.Stack.Pop().ObjectValue!).Slot.DoubleValue));
                    break;
                case ILOp.Ldind_Ref:
                    frame.Stack.Push(((VmByRef)frame.Stack.Pop().ObjectValue!).Slot);
                    break;
                case ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or ILOp.Stind_I2
                    or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8: {
                    var value = frame.Stack.Pop();
                    var byref = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    byref.Slot = instruction.Op switch {
                        ILOp.Stind_I8 => StackSlot.OfInt64(value.Int64Value),
                        ILOp.Stind_R4 or ILOp.Stind_R8 => StackSlot.OfFloat(value.DoubleValue),
                        ILOp.Stind_Ref => StackSlot.OfObject(value.ObjectValue),
                        _ => StackSlot.OfInt32((int)value.Int64Value),
                    };
                    break;
                }

                // ---- オブジェクト/値型のコピー ----
                case ILOp.Ldobj: {
                    var byref = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    frame.Stack.Push(PushCopyOfValue(byref.Slot));
                    break;
                }
                case ILOp.Stobj: {
                    var value = frame.Stack.Pop();
                    var byref = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    byref.Slot = StoreCopyOfValue(value);
                    break;
                }
                case ILOp.Cpobj: {
                    var src = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    var dst = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    dst.Slot = StoreCopyOfValue(src.Slot);
                    break;
                }
                case ILOp.Initobj: {
                    var byref = (VmByRef)frame.Stack.Pop().ObjectValue!;
                    byref.Slot = ObjectModel.DefaultForType(ResolveTypeToken(instruction.IntOperand, frame.Context), _loader);
                    break;
                }

                // ---- ボックス化 ----
                case ILOp.Box: {
                    var type = ResolveTypeToken(instruction.IntOperand, frame.Context);
                    var value = frame.Stack.Pop();
                    var fields = value.Kind == StackKind.ValueType
                        ? ((VmStructValue)value.ObjectValue!).Clone().Fields
                        : [value];
                    frame.Stack.Push(StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(type, fields))));
                    break;
                }
                case ILOp.Unbox: {
                    var target = ResolveTypeToken(instruction.IntOperand, frame.Context);
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{Describe(value)} を {target.FullName} として unbox できません。");
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(boxed.Fields, 0)));
                    break;
                }
                case ILOp.Unbox_Any: {
                    var target = ResolveTypeToken(instruction.IntOperand, frame.Context);
                    var value = frame.Stack.Pop();
                    if (target.IsValueType) {
                        // 値型への unbox.any はボックス化実体からコピーを取り出す
                        if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{Describe(value)} を {target.FullName} に unbox.any できません。");
                        if (target is VmClassType or VmConstructedType) {
                            // 構造体 (構築ジェネリック構造体を含む) は実体をコピーして取り出す
                            var args = boxed.Type is VmConstructedType ct ? ct.TypeArguments : null;
                            var sv = new VmStructValue(boxed.Type, (StackSlot[])boxed.Fields.Clone(), args);
                            frame.Stack.Push(StackSlot.OfValueType(sv));
                        } else {
                            // プリミティブ (intrinsic 値型) はスロットをそのまま取り出す
                            frame.Stack.Push(boxed.Fields[0]);
                        }
                    } else {
                        // 参照型への unbox.any は castclass と同等
                        var ok = value.ObjectValue is null || IsAssignableToType(value.ObjectValue, target);
                        if (!ok)
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{Describe(value)} を {target.FullName} に変換できません。");
                        frame.Stack.Push(value);
                    }
                    break;
                }

                // ---- キャスト ----
                case ILOp.Castclass or ILOp.Isinst: {
                    var target = ResolveTypeToken(instruction.IntOperand, frame.Context);
                    var value = frame.Stack.Pop();
                    var ok = value.ObjectValue is null || IsAssignableToType(value.ObjectValue, target);
                    if (!ok && instruction.Op == ILOp.Castclass)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{Describe(value)} を {target.FullName} にキャストできません。");
                    if (ok)
                        frame.Stack.Push(value);
                    else
                        frame.Stack.Push(StackSlot.Null);
                    break;
                }

                // ---- 例外処理 ----
                case ILOp.Throw: {
                    var value = frame.Stack.Pop();
                    throw MakeGuestThrow(value);
                }
                case ILOp.Rethrow:
                    // 処理中の例外を元の情報のまま再送出 (現在の catch コンテキストの外へ)
                    throw frame.CurrentThrow
                        ?? throw new BadImageFormatException("rethrow が catch ハンドラの外で実行されました。");
                case ILOp.Leave or ILOp.Leave_S:
                    DoLeave(frame, prepared.Clauses, instruction.IntOperand);
                    continue;
                case ILOp.Endfinally: {
                    if (frame.FinallyResume.Count == 0)
                        throw new BadImageFormatException("endfinally が finally/fault の外で実行されました。");
                    var next = frame.FinallyResume[^1];
                    frame.FinallyResume.RemoveAt(frame.FinallyResume.Count - 1);
                    if (next == PropagateSentinel)
                        throw frame.CurrentThrow!; // finally/fault 通過後の伝播再開
                    frame.Ip = next;
                    continue;
                }
                case ILOp.Endfilter: {
                    if (frame.FilterClause < 0)
                        throw new BadImageFormatException("endfilter がフィルタの外で実行されました。");
                    var clauseIndex = frame.FilterClause;
                    var clause = prepared.Clauses![clauseIndex];
                    frame.FilterClause = -1;
                    if (frame.Stack.Pop().AsInt32 != 0) {
                        // フィルタ採用 → catch ハンドラへ (例外オブジェクトを積み直す)
                        frame.Stack.Clear();
                        frame.Stack.Push(StackSlot.OfObject(frame.CurrentThrow!.ExceptionObject));
                        frame.Ip = clause.HandlerStart;
                    } else {
                        // フィルタ不採用 → 発生位置から後続の句の探索を継続する
                        frame.Stack.Clear();
                        frame.Ip = frame.UnwindIp;
                        if (!TryDispatchHandlerFrom(frame, frame.CurrentThrow!, clauseIndex + 1))
                            throw frame.CurrentThrow!;
                    }
                    continue;
                }

                // ---- M5 以降の命令 ----
                case ILOp.Ldtoken: {
                    // ldtoken Field は FieldRVA 初期データのハンドルを積む
                    // (RuntimeHelpers::InitializeArray 専用の消費を想定)。
                    // Type/Method トークン (typeof 等) は未対応
                    var tokenTable = (TableKind)((uint)instruction.IntOperand >> 24);
                    var tokenRid = (int)((uint)instruction.IntOperand & 0xFFFFFF);
                    if (tokenTable != TableKind.Field)
                        throw new NotSupportedException(
                            $"ldtoken は Field トークンのみ対応しています (要求: {tokenTable})。");
                    var rva = _loader.Image.GetFieldRva(tokenRid);
                    if (rva == 0)
                        throw new BadImageFormatException($"Field rid {tokenRid} に FieldRVA エントリがありません。");
                    var handle = _heap.Allocate(new VmFieldRvaData { Data = _loader.Image.GetRvaDataToEnd(rva) });
                    frame.Stack.Push(StackSlot.OfObject(handle));
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"IL 命令 {IlOpcodeTable.Get(instruction.Op)?.Name ?? instruction.Op.ToString()} は未対応です (ジェネリック/JIT は今後のフェーズで実装)。");
            }
            frame.Ip++;
        }
    }

    private enum ArrayElementKind { Int32, Int64, Float, Object }

    private static ArrayElementKind ElementKindFromType(VmType type) {
        if (type.IsValueType) {
            // プリミティブ以外の値型 (構造体/構築ジェネリック構造体) は VmStructValue スロットのまま扱う
            if (type is not VmIntrinsicType)
                return ArrayElementKind.Object;
            return type.FullName switch {
                "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr" => ArrayElementKind.Int64,
                "System.Single" or "System.Double" => ArrayElementKind.Float,
                _ => ArrayElementKind.Int32, // プリミティブ小整数
            };
        }
        return ArrayElementKind.Object;
    }

    private VmArray GetArray(in StackSlot slot) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmArray array)
            return array;
        if (slot.ObjectValue is null)
            throw new UnhandledGuestException("System.NullReferenceException", null);
        throw new InvalidOperationException($"配列でないオブジェクトに配列命令を適用しました: {Describe(slot)}");
    }

    private static void CheckArrayBounds(VmArray array, int index) {
        if ((uint)index >= (uint)array.Length)
            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                $"インデックス {index} は長さ {array.Length} の配列の範囲外です。");
    }

    private StackSlot ArrayLoad(InterpreterFrame frame, ArrayElementKind kind) {
        var index = frame.Stack.Pop().AsInt32;
        var array = GetArray(frame.Stack.Pop());
        CheckArrayBounds(array, index);
        var slot = array.Elements[index];
        return kind switch {
            ArrayElementKind.Int32 => StackSlot.OfInt32((int)slot.Int64Value),
            ArrayElementKind.Int64 => StackSlot.OfInt64(slot.Int64Value),
            ArrayElementKind.Float => StackSlot.OfFloat(slot.DoubleValue),
            // 構造体要素は読み出し時にコピーする (値型コピー意味論)
            _ => slot.ObjectValue is VmStructValue sv ? StackSlot.OfValueType(sv.Clone()) : slot,
        };
    }

    private void ArrayStore(InterpreterFrame frame, ArrayElementKind kind) {
        var value = frame.Stack.Pop();
        var index = frame.Stack.Pop().AsInt32;
        var array = GetArray(frame.Stack.Pop());
        CheckArrayBounds(array, index);

        // 共変配列の書込検査 (stelem.ref)
        if (kind == ArrayElementKind.Object && value.ObjectValue is not null &&
            !IsAssignableToType(value.ObjectValue, array.ArrayType.ElementType))
            throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                $"{Describe(value)} を {array.ArrayType.ElementType.FullName}[] に格納できません。");

        array.Elements[index] = kind switch {
            ArrayElementKind.Int32 => StackSlot.OfInt32((int)value.Int64Value),
            ArrayElementKind.Int64 => StackSlot.OfInt64(value.Int64Value),
            ArrayElementKind.Float => StackSlot.OfFloat(value.DoubleValue),
            _ => value,
        };
    }

    /// <summary>値の読み出し/書き込みコピー (値型は Clone、参照はそのまま)。</summary>
    private static StackSlot PushCopyOfValue(in StackSlot slot) {
        if (slot.Kind == StackKind.ValueType && slot.ObjectValue is VmStructValue sv)
            return StackSlot.OfValueType(sv.Clone());
        return slot;
    }

    private static StackSlot StoreCopyOfValue(in StackSlot value) => PushCopyOfValue(value);

    /// <summary>VM オブジェクトがターゲット型に代入可能か (castclass/isinst/配列共変/例外 catch の共通判定)。
    /// ジェネリック型のインスタンスは実引数を記録した構築型を作って判定する (変性込み・M5)。</summary>
    private bool IsAssignableToType(object vmValue, VmType target) => vmValue switch {
        VmClassInstance ci => ci.RuntimeType.IsAssignableTo(target),
        VmStructValue sv => sv.RuntimeType.IsAssignableTo(target),
        VmExceptionObject e => e.ExceptionType.IsAssignableTo(target),
        VmString => target.FullName is "System.String" or "System.Object",
        VmArray array => target switch {
            VmArrayType other => array.ArrayType.ElementType.IsAssignableTo(other.ElementType),
            _ => target.FullName is "System.Array" or "System.Object" or "System.ICloneable"
                or "System.Collections.IList" or "System.Collections.ICollection",
        },
        VmBoxedValue boxed => boxed.Type.IsAssignableTo(target) || target.FullName is "System.Object" or "System.ValueType",
        _ => false,
    };

    private static string Describe(in StackSlot slot) => slot.ObjectValue switch {
        null => "null",
        VmClassInstance ci => ci.ClassType.FullName,
        VmExceptionObject e => e.Type.FullName,
        VmString => "System.String",
        VmArray a => a.ArrayType.FullName,
        VmBoxedValue b => b.Type.FullName,
        VmStructValue sv => sv.StructType.FullName,
        _ => slot.ObjectValue.GetType().Name,
    };

    // ---- フィールドアクセス ----

    /// <summary>フィールドトークン (Field / MemberRef) を解決する。TypeSpec 親 (構築型のフィールド) も解決する。</summary>
    private VmField ResolveFieldToken(int token, GenericContext? context = null) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        switch (table) {
            case TableKind.Field:
                return _loader.GetFieldByToken((uint)token)
                    ?? throw new BadImageFormatException($"Field トークン 0x{token:X8} を解決できません。");
            case TableKind.MemberRef: {
                var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                var fieldName = _loader.GetMemberRefFieldName(rid);
                if (parent.Table == TableKind.TypeDef) {
                    var owner = _loader.GetTypeDef(parent.Rid);
                    return owner.Fields.FirstOrDefault(f => f.Name == fieldName)
                        ?? throw new BadImageFormatException(
                            $"MemberRef 0x{token:X8} の解決先フィールド {owner.FullName}::{fieldName} が見つかりません。");
                }
                if (parent.Table == TableKind.TypeSpec) {
                    // 構築型のフィールド参照 (例: ldfld !0 class List`1<int32>::_items)
                    // 親 TypeSpec が呼出元のパラメータ (!0) を含む場合は context で置換する
                    var constructed = ResolveConstructedParent(parent.Rid, context);
                    var definition = (VmClassType)constructed.Definition;
                    return definition.Fields.FirstOrDefault(f => f.Name == fieldName)
                        ?? throw new BadImageFormatException(
                            $"MemberRef 0x{token:X8} の解決先フィールド {definition.FullName}::{fieldName} が見つかりません。");
                }
                throw new NotSupportedException($"フィールド MemberRef 親テーブル {parent.Table} は未対応です。");
            }
            default:
                throw new BadImageFormatException($"フィールドトークン 0x{token:X8} のテーブル 0x{(int)table:X2} が不正です。");
        }
    }

    /// <summary>MemberRef の TypeSpec 親を構築型として解決する。VAR/MVAR を含む場合は context で置換する。</summary>
    private VmConstructedType ResolveConstructedParent(int typeSpecRid, GenericContext? context) =>
        _loader.ResolveTypeSpec(typeSpecRid, context) as VmConstructedType
            ?? throw new BadImageFormatException($"TypeSpec 0x02{typeSpecRid:X6} は構築ジェネリック型ではありません。");

    /// <summary>レシーバ (インスタンス/ByRef/構造体値) からフィールドスロットへの書き込み可能参照を得る。</summary>
    private VmByRef FieldLocation(in StackSlot objSlot, VmField field) {
        switch (objSlot.Kind) {
            case StackKind.Object when objSlot.ObjectValue is null:
                throw new UnhandledGuestException("System.NullReferenceException", null);
            case StackKind.Object when objSlot.ObjectValue is VmClassInstance instance:
                return new VmByRef(instance.Fields, GetInstanceFieldIndex(instance.ClassType, field));
            case StackKind.Object when objSlot.ObjectValue is VmBoxedValue boxed: {
                // ボックス化ジェネリック構造体 (構築型) は定義型に解いてレイアウトを取る
                VmClassType? bt = boxed.Type switch {
                    VmClassType cls => cls,
                    VmConstructedType constructed => constructed.Definition as VmClassType,
                    _ => null,
                };
                return bt is not null
                    ? new VmByRef(boxed.Fields, GetInstanceFieldIndex(bt, field))
                    : new VmByRef(boxed.Fields, 0);
            }
            case StackKind.ByRef when objSlot.ObjectValue is VmByRef outer: {
                // 構造体ローカル/引数へのフィールド書込 (ldloca → ldfld/stfld)
                var target = outer.Slot;
                if (target.Kind == StackKind.ValueType && target.ObjectValue is VmStructValue sv &&
                    sv.StructType is VmClassType st)
                    return new VmByRef(sv.Fields, GetInstanceFieldIndex(st, field));
                if (target.ObjectValue is VmClassInstance nested)
                    return new VmByRef(nested.Fields, GetInstanceFieldIndex(nested.ClassType, field));
                break;
            }
            case StackKind.ValueType when objSlot.ObjectValue is VmStructValue direct &&
                direct.StructType is VmClassType dt:
                return new VmByRef(direct.Fields, GetInstanceFieldIndex(dt, field));
        }
        throw new InvalidOperationException($"フィールド {field.DeclaringType.FullName}::{field.Name} のレシーバが不正です: {Describe(objSlot)}");
    }

    private static int GetInstanceFieldIndex(VmClassType type, VmField field) {
        var layout = ObjectModel.GetLayout(type);
        if (layout.TryGetValue(field, out var index))
            return index;
        // 継承チェーン上の基底型で宣言されたフィールド (GetLayout は既に基底を含むが、
        // MemberRef 経由で別 VmField インスタンスになる場合は名前でフォールバック)
        foreach (var (candidate, idx) in layout) {
            if (candidate.Name == field.Name && candidate.DeclaringType.FullName == field.DeclaringType.FullName)
                return idx;
        }
        throw new BadImageFormatException($"フィールド {field.DeclaringType.FullName}::{field.Name} が {type.FullName} のレイアウトにありません。");
    }

    /// <summary>intrinsic 型の静的フィールドのストレージ (トークンごとに 1 スロット。例: String.Empty)。</summary>
    private readonly Dictionary<int, StackSlot[]> _intrinsicStaticFields = [];

    /// <summary>静的フィールドの位置を解決する (.cctor 起動を含む)。intrinsic 型 (TypeRef 親) の静的フィールドも解決する。</summary>
    private VmByRef StaticFieldLocation(int token, GenericContext? context = null) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeRef) {
                var typeName = _loader.GetMemberRefParentTypeName(rid)!;
                var fieldName = _loader.GetMemberRefFieldName(rid);
                if (!_intrinsics.TryGetStaticField(typeName, fieldName, out var value))
                    throw new OperationNotAllowedException(
                        $"intrinsic 型 {typeName} の静的フィールド {fieldName} は未登録です。");
                if (!_intrinsicStaticFields.TryGetValue(token, out var storage)) {
                    storage = [value(_intrinsicContext)];
                    _intrinsicStaticFields[token] = storage;
                }
                return new VmByRef(storage, 0);
            }
            if (parent.Table == TableKind.TypeSpec) {
                // 構築型の静的フィールド。ストレージは定義型に紐付く
                // (CLR では値型実引数ごとに別ストレージだが、VM は共有する — BCL 面では等価)
                var constructed = ResolveConstructedParent(parent.Rid, context);
                var definition = (VmClassType)constructed.Definition;
                EnsureInitialized(definition);
                var storage = ObjectModel.GetOrCreateStaticStorage(definition, _loader);
                return new VmByRef(storage, ObjectModel.StaticFieldIndex(definition,
                    ResolveFieldToken(token, context)));
            }
        }
        var field = ResolveFieldToken(token);
        var owner = (VmClassType)field.DeclaringType;
        EnsureInitialized(owner);
        var staticStorage = ObjectModel.GetOrCreateStaticStorage(owner, _loader);
        return new VmByRef(staticStorage, ObjectModel.StaticFieldIndex(owner, field));
    }

    /// <summary>型初期化子 (.cctor) の起動規約: 静的フィールド初回アクセス/newobj 前に 1 回だけ実行。</summary>
    private void EnsureInitialized(VmClassType type) {
        if (!_initializedTypes.Add(type))
            return;
        var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor?.Body is not null)
            Invoke(cctor, []);
    }

    // ---- オブジェクト生成 ----

    private StackSlot? NewObject(int token, InterpreterFrame caller) {
        VmMethod ctor;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MethodDef) {
            ctor = _loader.GetMethodByToken((uint)token)
                ?? throw new BadImageFormatException($"newobj トークン 0x{token:X8} を解決できません。");
        } else if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeSpec)
                return NewConstructedObject(token, rid, parent.Rid, caller);
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(rid).ToArray());
            var facadeParamCount = signature.ParamTypes.Length;
            var name = _loader.GetMemberRefName(rid);
            var typeName = _loader.GetMemberRefParentTypeName(rid);
            if (typeName is not null) {
                var facadeType = _loader.FindIntrinsicType(typeName);
                if (facadeType is not null) {
                    // 例外ファサード型のみ newobj を許可 (throw new XxxException(...) 用)。
                    // それ以外の intrinsic 型の実体化は BCL 不実装の面として拒否し続ける
                    if (!IsExceptionFacade(facadeType))
                        throw new NotSupportedException(
                            $"intrinsic 型 {typeName} のインスタンス生成は未対応です (例外ファサード型のみ)。");
                    var ctorArgs = new StackSlot[facadeParamCount + 1];
                    for (var i = facadeParamCount; i >= 1; i--)
                        ctorArgs[i] = caller.Stack.Pop();
                    var exception = _heap.Allocate(new VmExceptionObject(facadeType, null));
                    ctorArgs[0] = StackSlot.OfObject(exception);
                    // .ctor は基底ファサード連鎖からも解決する (Exception::.ctor を派生型で使う)
                    if (TryGetIntrinsicThroughHierarchy(typeName, name, facadeParamCount + 1, hasThis: true, out var intrinsicCtor)) {
                        ConsumeInstruction();
                        CheckSafepoint();
                        intrinsicCtor(_intrinsicContext, ctorArgs);
                    } else if (facadeParamCount != 0) {
                        throw new OperationNotAllowedException(
                            $"intrinsic {typeName}::{name} (引数 {facadeParamCount} 個) は未登録です。");
                    }
                    return StackSlot.OfObject(exception);
                }
            }
            throw new NotSupportedException(
                $"newobj の MemberRef 0x{token:X8} ({typeName ?? "?"}::{name}) を解決できません。");
        } else {
            throw new BadImageFormatException($"newobj トークン 0x{token:X8} のテーブルが不正です。");
        }

        var owner = (VmClassType)ctor.DeclaringType;
        EnsureInitialized(owner);
        var paramCount = ctor.Signature.ParamTypes.Length;
        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (owner.IsValueType) {
            // 構造体の newobj: this (既定値) を作り、.ctor があればミューテートして this を返す
            var structValue = ObjectModel.DefaultStruct(owner, _loader);
            args[0] = StackSlot.OfValueType(structValue);
            if (ctor.Body is not null)
                Invoke(ctor, args);
            return StackSlot.OfValueType(structValue);
        }

        var instance = _heap.Allocate(new VmClassInstance(owner, ObjectModel.CreateInstanceStorage(owner, _loader)));
        args[0] = StackSlot.OfObject(instance);
        if (ctor.Body is not null)
            Invoke(ctor, args);
        return StackSlot.OfObject(instance);
    }

    private VmType ResolveTypeToken(int token, GenericContext? context = null) =>
        _loader.ResolveToken(new SigType(SigKind.TypeToken, Token: (uint)token), context);

    /// <summary>
    /// 構築ジェネリック型 (TypeSpec 親の MemberRef) の newobj。
    /// 例: newobj instance void class List`1&lt;int32&gt;::.ctor() — 実引数を VmClassInstance/VmStructValue に
    /// 記録し、.ctor はその型引数の GenericContext で実行する (フィールドの !0 等が正しく解決される)。
    /// </summary>
    private StackSlot NewConstructedObject(int token, int memberRefRid, int typeSpecRid, InterpreterFrame caller) {
        var constructed = ResolveConstructedParent(typeSpecRid, caller.Context);
        var definition = (VmClassType)constructed.Definition;
        var signature = SignatureDecoder.DecodeMethodSignature(
            _loader.Image.GetMemberRefSignature(memberRefRid).ToArray());
        var paramCount = signature.ParamTypes.Length;
        var ctorName = _loader.GetMemberRefName(memberRefRid);
        var context = new GenericContext { ClassArgs = constructed.TypeArguments };

        // .ctor は宣言型 (継承チェーン上の基底ジェネリック定義も含む) から探す
        VmMethod? ctor = null;
        for (VmType? t = definition; t is not null && ctor is null; t = t.BaseType) {
            if (t is VmConstructedType ct)
                t = ct.Definition;
            if (t is not VmClassType cls)
                break;
            ctor = cls.Methods.FirstOrDefault(m =>
                m.Name == ctorName && !m.IsStatic && m.Signature.ParamTypes.Length == paramCount);
        }
        EnsureInitialized(definition);
        if (ctor is null)
            throw new BadImageFormatException(
                $"構築型 {constructed.FullName} に引数 {paramCount} 個の .ctor ({ctorName}) が見つかりません。");

        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (definition.IsValueType) {
            var structValue = ObjectModel.DefaultStruct(definition, _loader, context, constructed.TypeArguments);
            args[0] = StackSlot.OfValueType(structValue);
            if (ctor?.Body is not null)
                Invoke(ctor, args, context);
            return StackSlot.OfValueType(structValue);
        }

        var instance = _heap.Allocate(new VmClassInstance(definition,
            ObjectModel.CreateInstanceStorage(definition, _loader, context), constructed.TypeArguments));
        args[0] = StackSlot.OfObject(instance);
        if (ctor?.Body is not null)
            Invoke(ctor, args, context);
        return StackSlot.OfObject(instance);
    }

    /// <summary>例外ファサード型か (System.Exception 自身と XxxException)。</summary>
    private static bool IsExceptionFacade(VmType type) =>
        type.FullName == "System.Exception" || type.FullName.EndsWith("Exception", StringComparison.Ordinal);

    /// <summary>intrinsic を派生ファサード型から基底連鎖まで辿って解決する (継承面のフォールバック)。</summary>
    private bool TryGetIntrinsicThroughHierarchy(string typeName, string name, int arity, bool hasThis, out IntrinsicImpl impl) {
        for (VmType? t = _loader.FindIntrinsicType(typeName); t is not null; t = t.BaseType) {
            if (_intrinsics.TryGet(new IntrinsicKey(t.FullName, name, arity, hasThis), out impl!))
                return true;
        }
        impl = null!;
        return false;
    }

    private void JumpTo(InterpreterFrame frame, int offset) {
        if (!frame.OffsetMap.TryGetValue(offset, out var index))
            throw new BadImageFormatException($"分岐先 IL_{offset:X4} が命令境界上にありません。");
        frame.Ip = index;
    }

    // ---- 例外送出/leave ----

    /// <summary>throw 命令の被演算子をゲスト例外キャリアに変換する。</summary>
    private VmGuestThrow MakeGuestThrow(in StackSlot value) {
        if (value.Kind == StackKind.Object && value.ObjectValue is null)
            return SynthesizeCarrier(new UnhandledGuestException("System.NullReferenceException", null));
        if (value.ObjectValue is not VmObject vmObject)
            return SynthesizeCarrier(new UnhandledGuestException("System.InvalidCastException",
                $"{Describe(value)} は System.Exception を派生していないため throw できません。"));
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
                $"{Describe(value)} は System.Exception を派生していないため throw できません。"));
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
    private void DoLeave(InterpreterFrame frame, PreparedClause[]? clauses, int targetOffset) {
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

    // ---- クォータ/セーフポイント ----

    private void ConsumeInstruction() {
        _instructionCount++;
        if (_instructionCount > _memory.InstructionQuota)
            throw new InstructionQuotaExceededException(
                $"命令数クォータ {_memory.InstructionQuota:N0} を超過しました (実行命令数: {_instructionCount:N0})。");
        if (_instructionCount % SafepointInterval == 0)
            CheckSafepoint();
    }

    /// <summary>セーフポイント。命令境界 = 全ゲスト状態がフレームに含まれる時点なので、ここでのみ GC を起動してよい
    /// (newobj 処理中のオブジェクトがホストローカルにのみ保持される瞬間があり、そこで回収すると誤 sweep する)。</summary>
    private void CheckSafepoint() => _heap.CollectIfDue();

    // ---- 呼出 ----

    private StackSlot? Call(int token, InterpreterFrame caller, bool isCallvirt, int constrainedToken) {
        // 未登録 intrinsic はこの時点では例外にしない (callvirt ならレシーバのゲスト実装を
        // 引数ポップ後に試すため。旧来の即時例外は最後のフォールバックで再現する)
        var target = ResolveCallTarget(token, caller.Context, throwOnMissingIntrinsic: false);

        // 引数はスタック上では逆順
        var args = new StackSlot[target.Arity];
        for (var i = target.Arity - 1; i >= 0; i--)
            args[i] = caller.Stack.Pop();

        if (target.Intrinsic is { } intrinsic) {
            // プリミティブの instance メソッド (int.ToString() 等) は ldloca 経由の
            // ByRef レシーバで来るため、値に読み替えてから渡す (constrained. 値型レシーバも同様)
            if (target.HasThis && args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef thisByRef)
                args[0] = thisByRef.Slot;
            // callvirt で intrinsic 宣言型 (System.Object 等) をターゲットにする場合、
            // レシーバの実行時型にゲスト側 override があればそちらを優先する (仮想ディスパッチ)
            if (isCallvirt && target.HasThis) {
                if (IsNullReference(args[0]))
                    throw new UnhandledGuestException("System.NullReferenceException",
                        $"null レシーバで {target.DeclaringType}::{target.Name} を呼び出しました。");
                if (TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
                    var context = BuildCallContext(target, guestOverride, args[0]);
                    var guestRet = Invoke(guestOverride, args, context);
                    return SignatureReturnsValue(guestOverride.Signature) ? guestRet : null;
                }
            }
            // constrained. 値型レシーバが intrinsic 宣言型 (System.Object 等) に着地した場合、
            // ECMA-335 規約に従い値をボックス化してから渡す (ゲスト実装は上の仮想ディスパッチで優先済み)
            if (constrainedToken != 0 && target.HasThis &&
                args[0].Kind is not (StackKind.Object or StackKind.ByRef)) {
                var constrainedType = ResolveTypeToken(constrainedToken, caller.Context);
                if (constrainedType.IsValueType) {
                    var fields = args[0].Kind == StackKind.ValueType
                        ? ((VmStructValue)args[0].ObjectValue!).Clone().Fields
                        : [args[0]];
                    args[0] = StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(constrainedType, fields)));
                }
            }
            // intrinsic 呼出ゲート: ① 追加クォータ消費 ② セーフポイント検査
            // ③ I/O はデバイス経由・値は VM オブジェクトモデル正規化 (実装側契約)
            ConsumeInstruction();
            CheckSafepoint();
            return intrinsic(_intrinsicContext, args);
        }

        // 解決未了 (未登録 intrinsic): レシーバへの仮想ディスパッチを最終試行してから拒否
        if (target.Method is null)
            return FailOrDispatchLate(target, caller, isCallvirt, args);

        // ゲスト呼出。callvirt はレシーバの実行時型で仮想解決 (VTable 相当)。
        // constrained. 値型レシーバは ByRef/ValueType スロットで来るためディスパッチがそのまま適用される
        var method = target.Method!;
        if (isCallvirt && method.Signature.HasThis) {
            if (IsNullReference(args[0]))
                throw new UnhandledGuestException("System.NullReferenceException",
                    $"null レシーバで {method.DeclaringType.FullName}::{method.Name} を呼び出しました。");
            method = DispatchVirtual(method, args[0]);
        }
        var context2 = BuildCallContext(target, method, method.Signature.HasThis ? args[0] : default);
        var ret = Invoke(method, args, context2);
        return SignatureReturnsValue(method.Signature) ? ret : null;
    }

    /// <summary>解決未了の呼出 (未登録 intrinsic) の最終処理。callvirt ならレシーバの実行時型に
    /// ゲスト実装があればそれを呼び (constrained callvirt による構造体の interface 実装呼出等)、
    /// 無ければ未登録 intrinsic として拒否する。</summary>
    private StackSlot? FailOrDispatchLate(CallTarget target, InterpreterFrame caller, bool isCallvirt, StackSlot[] args) {
        if (isCallvirt && target.HasThis &&
            TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
            var context = BuildCallContext(target, guestOverride, args[0]);
            var ret = Invoke(guestOverride, args, context);
            return SignatureReturnsValue(guestOverride.Signature) ? ret : null;
        }
        throw new OperationNotAllowedException(
            $"intrinsic {target.DeclaringType}::{target.Name} (引数 {target.Arity} 個) は未登録です。BCL 面は VM 起動時に登録された intrinsic のみ提供されます。");
    }

    /// <summary>
    /// 呼出先メソッド用の GenericContext を構築する。クラス型引数は MemberRef/TypeSpec 親の構築型引数だが、
    /// レシーバの実行時型が実引数を持つ場合はそちらを優先する (仮想ディスパッチで派生/実装側の
    /// ジェネリック定義に着地した場合、その !0 はレシーバ自身の実引数を指すため)。
    /// </summary>
    private GenericContext? BuildCallContext(CallTarget target, VmMethod method, in StackSlot receiver) {
        var classArgs = target.ClassArgs;
        var declaringParamCount = method.DeclaringType.GenericParamCount;
        if (declaringParamCount > 0 &&
            TryGetReceiverTypeArguments(receiver, declaringParamCount, out var receiverArgs))
            classArgs = receiverArgs;
        return GenericContext.Of(classArgs, target.MethodArgs);
    }

    /// <summary>レシーバ (インスタンス/ボックス/構造体値、ByRef レシーバも可) が持つジェネリック実引数を得る。</summary>
    private static bool TryGetReceiverTypeArguments(in StackSlot receiver, int count, out VmType[] args) {
        args = [];
        if (count == 0)
            return false;
        var value = receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef
            ? byRef.Slot
            : receiver;
        VmType[]? found = value.Kind switch {
            StackKind.Object => value.ObjectValue switch {
                VmClassInstance ci => ci.TypeArguments,
                VmBoxedValue bv => bv.Type is VmConstructedType boxedCt ? boxedCt.TypeArguments : null,
                _ => null,
            },
            StackKind.ValueType => value.ObjectValue is VmStructValue sv ? sv.TypeArguments : null,
            _ => null,
        };
        if (found is { Length: > 0 } && found.Length == count) {
            args = found;
            return true;
        }
        return false;
    }

    private static bool IsNullReference(in StackSlot slot) =>
        slot.Kind is StackKind.Object or StackKind.ByRef && slot.ObjectValue is null;

    /// <summary>callvirt の実行時型ディスパッチ。名前+引数個数+実装本体で基底連鎖を辿る (VTable 相当)。</summary>
    private VmMethod DispatchVirtual(VmMethod declared, in StackSlot receiver) =>
        TryDispatchVirtual(declared.Name, declared.Signature.ParamTypes.Length, receiver) ?? declared;

    /// <summary>実行時型から最派生のゲスト実装を探す。見つからなければ null (intrinsic 宣装にフォールバック)。
    /// 構築ジェネリック型のインスタンス (VmBoxedValue の VmConstructedType 型 等) も定義側に解いて探索する。</summary>
    private VmMethod? TryDispatchVirtual(string name, int paramCount, in StackSlot receiver) {
        // 構造体の instance メソッドは ByRef レシーバで来ることがある
        var receiverValue = receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef
            ? byRef.Slot
            : receiver;
        var runtimeType = receiverValue.Kind switch {
            StackKind.ValueType => receiverValue.ObjectValue is VmStructValue sv ? (VmType)sv.StructType : null,
            StackKind.Object => receiverValue.ObjectValue switch {
                VmClassInstance ci => (VmType)ci.ClassType,
                VmBoxedValue bv => bv.Type,
                _ => null,
            },
            _ => null,
        };

        for (VmType? t = runtimeType; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition; // 構築型 → ジェネリック定義に解いて探索を続ける
            if (t is not VmClassType cls)
                break;
            var found = cls.Methods.FirstOrDefault(m =>
                m.Name == name &&
                m.Signature.ParamTypes.Length == paramCount &&
                !m.IsAbstract && m.Body is not null);
            if (found is not null)
                return found;
            t = cls.BaseType;
        }
        return null;
    }

    private sealed class CallTarget {
        public int Arity;
        public VmMethod? Method;
        public IntrinsicImpl? Intrinsic;
        /// <summary>intrinsic ターゲットの宣言型名/メソッド名 (callvirt の仮想ディスパッチ用)。</summary>
        public string? DeclaringType;
        public string? Name;
        public int ParamCount;
        public bool HasThis;
        /// <summary>構築型経由 (TypeSpec 親) で解決された場合の型引数 (MemberRef の !0 置換に使う)。</summary>
        public VmType[]? ClassArgs;
        /// <summary>MethodSpec の Instantiation (ジェネリックメソッドの !!0 置換に使う)。</summary>
        public VmType[]? MethodArgs;
    }

    /// <summary>呼出トークンを解決する (Arity = 引数個数、インスタンスは this 込み)。
    /// context は呼出元メソッドのジェネリック実引数 (MemberRef の TypeSpec 親が !0 を含む場合の置換に使う)。
    /// throwOnMissingIntrinsic = false の場合、TypeRef 親の未登録 intrinsic は即例外にせず
    /// Intrinsic = null の CallTarget を返す (Call 側でレシーバの仮想ディスパッチを試してから判定する)。</summary>
    private CallTarget ResolveCallTarget(int token, GenericContext? context, bool throwOnMissingIntrinsic = true) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        switch (table) {
            case TableKind.MethodDef: {
                var method = _loader.GetMethodByToken((uint)token)
                    ?? throw new BadImageFormatException($"MethodDef トークン 0x{token:X8} を解決できません。");
                return new CallTarget {
                    Arity = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                };
            }
            case TableKind.MemberRef: {
                // MemberRef 署名から hasThis/引数個数を得る
                var signature = SignatureDecoder.DecodeMethodSignature(
                    _loader.Image.GetMemberRefSignature(rid).ToArray());
                var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                var name = _loader.GetMemberRefName(rid);

                var parent = _loader.Image.Tables.DecodeCoded(
                    TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                if (parent.Table == TableKind.TypeRef) {
                    var typeName = _loader.GetMemberRefParentTypeName(rid)!;
                    if (_intrinsics.TryGet(new IntrinsicKey(typeName, name, arity, signature.HasThis), out var impl))
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = impl,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                        };
                    // 継承面のフォールバック: 派生ファサード型から基底連鎖を辿って解決する
                    // (例: InvalidOperationException::get_Message → System.Exception に登録された面)
                    if (TryGetIntrinsicThroughHierarchy(typeName, name, arity, signature.HasThis, out var inherited)) {
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = inherited,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                        };
                    }
                    // 未登録 intrinsic: 即例外にせず解決未了の CallTarget を返す
                    // (constrained callvirt ではレシーバの実行時型にゲスト実装があるため。
                    //  Call 側で最終ディスパッチが失敗した時点で改めて例外にする)
                    if (!throwOnMissingIntrinsic)
                        return new CallTarget {
                            Arity = arity,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                        };
                    throw new OperationNotAllowedException(
                        $"intrinsic {typeName}::{name} (引数 {arity} 個) は未登録です。BCL 面は VM 起動時に登録された intrinsic のみ提供されます。");
                }
                if (parent.Table == TableKind.TypeDef) {
                    var owner = _loader.GetTypeDef(parent.Rid);
                    var method = owner.Methods.FirstOrDefault(m => m.Name == name)
                        ?? throw new BadImageFormatException($"MemberRef 0x{token:X8} の解決先メソッド {owner.FullName}::{name} が見つかりません。");
                    return new CallTarget {
                        Arity = arity,
                        Method = method,
                        Name = method.Name,
                        ParamCount = method.Signature.ParamTypes.Length,
                        HasThis = method.Signature.HasThis,
                    };
                }
                if (parent.Table == TableKind.TypeSpec)
                    return ResolveConstructedMethodTarget(token, rid, parent.Rid, signature, name, context, throwOnMissingIntrinsic);
                throw new NotSupportedException($"MemberRef 親テーブル {parent.Table} は未対応です。");
            }
            case TableKind.MethodSpec:
                return ResolveMethodSpecTarget(token, rid, context);
            default:
                throw new BadImageFormatException($"呼出トークン 0x{token:X8} のテーブル 0x{(int)table:X2} が不正です。");
        }
    }

    /// <summary>TypeSpec 親 (構築型) の MemberRef を解決する。例: callvirt int32 class List`1&lt;int32&gt;::get_Item(int32)。
    /// 定義がファサード型 (IEnumerator`1&lt;int&gt; 等の BCL インターフェース) なら intrinsic 面を解決し、
    /// 実呼出は Call でレシーバの実行時型に仮想ディスパッチされる。</summary>
    private CallTarget ResolveConstructedMethodTarget(int token, int memberRefRid, int typeSpecRid,
        MethodSignature signature, string name, GenericContext? context, bool throwOnMissingIntrinsic = true) {
        var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
        var constructed = ResolveConstructedParent(typeSpecRid, context);

        // 構築ファサード型 (BCL 汎用インターフェース等) → intrinsic 面のみ
        if (constructed.Definition is VmIntrinsicType facade) {
            if (_intrinsics.TryGet(new IntrinsicKey(facade.FullName, name, arity, signature.HasThis), out var impl) ||
                TryGetIntrinsicThroughHierarchy(facade.FullName, name, arity, signature.HasThis, out impl)) {
                return new CallTarget {
                    Arity = arity,
                    Intrinsic = impl,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                };
            }
            // 未登録 intrinsic: 即例外にせず解決未了の CallTarget を返す
            // (constrained callvirt ではレシーバの実行時型にゲスト実装があるため)
            if (!throwOnMissingIntrinsic)
                return new CallTarget {
                    Arity = arity,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ClassArgs = constructed.TypeArguments,
                };
            throw new OperationNotAllowedException(
                $"intrinsic {facade.FullName}::{name} (引数 {arity} 個) は未登録です。" +
                "構築ファサード型のメソッドは intrinsic に登録された面のみ解決できます。");
        }

        var definition = (VmClassType)constructed.Definition;
        var method = FindMethodThroughChain(definition, name, signature.ParamTypes.Length)
            ?? throw new BadImageFormatException(
                $"MemberRef 0x{token:X8} の解決先メソッド {definition.FullName}::{name} が見つかりません。");
        return new CallTarget {
            Arity = arity,
            Method = method,
            Name = method.Name,
            ParamCount = method.Signature.ParamTypes.Length,
            HasThis = method.Signature.HasThis,
            ClassArgs = constructed.TypeArguments,
        };
    }

    /// <summary>ジェネリックメソッド (MethodSpec) を解決する。Instantiation blob からメソッド型引数を取り出す。
    /// 例: call !!0 class Generics::First&lt;!!0&gt;(!!0[])</summary>
    private CallTarget ResolveMethodSpecTarget(int token, int methodSpecRid, GenericContext? context) {
        var underlying = _loader.Image.Tables.DecodeCoded(
            TableKind.MethodSpec, methodSpecRid, 0, CodedIndexKind.MethodDefOrRef);
        var instantiationBlobIndex = _loader.Image.Tables.GetRowIndex(TableKind.MethodSpec, methodSpecRid, 1);
        var methodArgs = SignatureDecoder.DecodeMethodSpecInstantiation(
            _loader.Image.GetBlob(instantiationBlobIndex).ToArray())
            .Select(t => _loader.ResolveToken(t, context))
            .ToArray();

        if (underlying.Table == TableKind.MethodDef) {
            var method = _loader.GetMethodByToken(Token.From(underlying.Table, underlying.Rid).Value)
                ?? throw new BadImageFormatException($"MethodSpec 0x{token:X8} の解決先メソッドが見つかりません。");
            if (method.Signature.GenericParamCount != methodArgs.Length)
                throw new BadImageFormatException(
                    $"MethodSpec 0x{token:X8} の型引数は {methodArgs.Length} 個ですが、{method} は {method.Signature.GenericParamCount} 個を要求します。");
            return new CallTarget {
                Arity = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0),
                Method = method,
                Name = method.Name,
                ParamCount = method.Signature.ParamTypes.Length,
                HasThis = method.Signature.HasThis,
                MethodArgs = methodArgs,
            };
        }
        if (underlying.Table == TableKind.MemberRef) {
            var memberRefRid = underlying.Rid;
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(memberRefRid).ToArray());
            var name = _loader.GetMemberRefName(memberRefRid);
            var parent = _loader.Image.Tables.DecodeCoded(
                TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
            if (signature.GenericParamCount != methodArgs.Length)
                throw new BadImageFormatException(
                    $"MethodSpec 0x{token:X8} の型引数は {methodArgs.Length} 個ですが、{name} は {signature.GenericParamCount} 個を要求します。");
            if (parent.Table == TableKind.TypeDef) {
                // 同アセンブリのジェネリックメソッド (Roslyn は MethodDef でも MemberRef 形式で出す)
                var owner = _loader.GetTypeDef(parent.Rid);
                var method = owner.Methods.FirstOrDefault(m =>
                        m.Name == name && m.Signature.ParamTypes.Length == signature.ParamTypes.Length)
                    ?? throw new BadImageFormatException(
                        $"MethodSpec 0x{token:X8} の解決先メソッド {owner.FullName}::{name} が見つかりません。");
                return new CallTarget {
                    Arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                    MethodArgs = methodArgs,
                };
            }
            if (parent.Table == TableKind.TypeSpec) {
                var constructed = ResolveConstructedParent(parent.Rid, context);
                var definition = (VmClassType)constructed.Definition;
                var method = FindMethodThroughChain(definition, name, signature.ParamTypes.Length)
                    ?? throw new BadImageFormatException(
                        $"MethodSpec 0x{token:X8} の解決先メソッド {definition.FullName}::{name} が見つかりません。");
                return new CallTarget {
                    Arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                    ClassArgs = constructed.TypeArguments,
                    MethodArgs = methodArgs,
                };
            }
            throw new NotSupportedException(
                $"MethodSpec の解決先 MemberRef の親テーブル {parent.Table} は未対応です (intrinsic ジェネリックメソッドは今後のフェーズ)。");
        }
        throw new BadImageFormatException($"MethodSpec 0x{token:X8} の解決先テーブル {underlying.Table} が不正です。");
    }

    /// <summary>名前+パラメータ数でメソッドを探す (継承チェーンを辿る。抽象宣言も解決対象)。</summary>
    private static VmMethod? FindMethodThroughChain(VmClassType type, string name, int paramCount) {
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (t is not VmClassType cls)
                break;
            var found = cls.Methods.FirstOrDefault(m =>
                m.Name == name && m.Signature.ParamTypes.Length == paramCount);
            if (found is not null)
                return found;
            t = cls.BaseType;
        }
        return null;
    }

    private static bool SignatureReturnsValue(MethodSignature signature) =>
        signature.ReturnType.Kind != SigKind.Void;

    // ---- 分岐/比較の共通評価 ----

    private static bool IsTrue(in StackSlot slot) => slot.Kind switch {
        StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt => slot.Int64Value != 0,
        StackKind.Float => slot.DoubleValue != 0, // NaN は true (ECMA-335: brtrue は non-zero)
        StackKind.Object or StackKind.ByRef => slot.ObjectValue is not null,
        _ => throw new InvalidOperationException($"分岐条件に使えないスタック型です: {slot.Kind}"),
    };

    private static bool CompareBranch(ILOp op, in StackSlot left, in StackSlot right) => op switch {
        ILOp.Beq or ILOp.Beq_S => Compare(ILOp.Ceq, left, right),
        ILOp.Bne_Un or ILOp.Bne_Un_S => !Compare(ILOp.Ceq, left, right),
        ILOp.Bge or ILOp.Bge_S => Compare(Interpreter.CgeShim, left, right),
        ILOp.Bgt or ILOp.Bgt_S => Compare(ILOp.Cgt, left, right),
        ILOp.Ble or ILOp.Ble_S => Compare(Interpreter.CleShim, left, right),
        ILOp.Blt or ILOp.Blt_S => Compare(ILOp.Clt, left, right),
        ILOp.Bge_Un or ILOp.Bge_Un_S => Compare(Interpreter.CgeUnShim, left, right),
        ILOp.Bgt_Un or ILOp.Bgt_Un_S => Compare(ILOp.Cgt_Un, left, right),
        ILOp.Ble_Un or ILOp.Ble_Un_S => Compare(Interpreter.CleUnShim, left, right),
        ILOp.Blt_Un or ILOp.Blt_Un_S => Compare(ILOp.Clt_Un, left, right),
        _ => throw new InvalidOperationException($"比較分岐でない命令 {op} が渡されました。"),
    };

    // cge/cle は IL に無いので比較関数内部でのみ使う疑似コード
    private const ILOp CgeShim = (ILOp)0xFF01;
    private const ILOp CleShim = (ILOp)0xFF02;
    private const ILOp CgeUnShim = (ILOp)0xFF03;
    private const ILOp CleUnShim = (ILOp)0xFF04;

    /// <summary>ceq/cgt/clt 系 (shim を含む) の共通比較。数値は統一 (i4/i8/native/float)、オブジェクトは参照比較。</summary>
    private static bool Compare(ILOp op, in StackSlot left, in StackSlot right) {
        if (op == ILOp.Ceq) {
            if (left.Kind is StackKind.Object or StackKind.ByRef || right.Kind is StackKind.Object or StackKind.ByRef)
                return ReferenceEquals(left.ObjectValue, right.ObjectValue);
            if (left.Kind == StackKind.Float || right.Kind == StackKind.Float)
                return ToFloat(left) == ToFloat(right);
            return ToLong(left) == ToLong(right);
        }
        if (left.Kind == StackKind.Float || right.Kind == StackKind.Float) {
            var a = ToFloat(left);
            var b = ToFloat(right);
            return op switch {
                ILOp.Cgt => a > b,
                ILOp.Clt => a < b,
                // un 系は「無順序 (NaN) も真」: blt.un/bge.un 等のセマンティクス
                Interpreter.CgeShim => a >= b,
                Interpreter.CleShim => a <= b,
                ILOp.Cgt_Un => double.IsNaN(a) || double.IsNaN(b) || a > b,
                ILOp.Clt_Un => double.IsNaN(a) || double.IsNaN(b) || a < b,
                Interpreter.CgeUnShim => double.IsNaN(a) || double.IsNaN(b) || a >= b,
                Interpreter.CleUnShim => double.IsNaN(a) || double.IsNaN(b) || a <= b,
                _ => throw new InvalidOperationException($"比較命令 {op} は数値比較に対応しません。"),
            };
        }

        // 整数系。un 系は符号なし比較
        if (op is ILOp.Cgt_Un or ILOp.Clt_Un or Interpreter.CgeUnShim or Interpreter.CleUnShim) {
            if (left.Kind == StackKind.Int64 || left.Kind == StackKind.NativeInt) {
                var la = (ulong)left.Int64Value;
                var lb = (ulong)right.Int64Value;
                return op switch {
                    ILOp.Cgt_Un => la > lb,
                    ILOp.Clt_Un => la < lb,
                    Interpreter.CgeUnShim => la >= lb,
                    _ => la <= lb,
                };
            }
            var a = (uint)left.Int64Value;
            var b = (uint)right.Int64Value;
            return op switch {
                ILOp.Cgt_Un => a > b,
                ILOp.Clt_Un => a < b,
                Interpreter.CgeUnShim => a >= b,
                _ => a <= b,
            };
        }

        var li = left.Int64Value;
        var ri = right.Int64Value;
        return op switch {
            ILOp.Cgt => li > ri,
            ILOp.Clt => li < ri,
            Interpreter.CgeShim => li >= ri,
            Interpreter.CleShim => li <= ri,
            _ => throw new InvalidOperationException($"比較命令 {op} は整数比較に対応しません。"),
        };
    }

    private static long ToLong(in StackSlot slot) {
        if (slot.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt)
            return slot.Int64Value;
        throw new InvalidOperationException($"比較に使えないスタック型です: {slot.Kind}");
    }

    private static double ToFloat(in StackSlot slot) => slot.Kind switch {
        StackKind.Float => slot.DoubleValue,
        StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt => slot.Int64Value,
        _ => throw new InvalidOperationException($"比較に使えないスタック型です: {slot.Kind}"),
    };

    // ---- 算術 ----

    private static StackSlot BinaryArithmetic(ILOp op, in StackSlot left, in StackSlot right) {
        // 浮動小数演算
        if (left.Kind == StackKind.Float || right.Kind == StackKind.Float) {
            var a = ToFloat(left);
            var b = ToFloat(right);
            return StackSlot.OfFloat(op switch {
                ILOp.Add or ILOp.Add_Ovf or ILOp.Add_Ovf_Un => a + b,
                ILOp.Sub or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un => a - b,
                ILOp.Mul or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un => a * b,
                ILOp.Div or ILOp.Div_Un => a / b,
                ILOp.Rem or ILOp.Rem_Un => a % b,
                _ => throw new InvalidOperationException($"浮動小数に適用できない算術命令です: {op}"),
            });
        }

        var is64 = left.Kind is StackKind.Int64 or StackKind.NativeInt
            || right.Kind is StackKind.Int64 or StackKind.NativeInt;
        if (!is64) {
            var a = (int)left.Int64Value;
            var b = (int)right.Int64Value;
            return IsUnsigned(op)
                ? StackSlot.OfInt32((int)UnsignedInt32(op, (uint)a, (uint)b))
                : StackSlot.OfInt32(SignedInt32(op, a, b));
        }

        var x = left.Int64Value;
        var y = right.Int64Value;
        var resultKind = left.Kind == StackKind.NativeInt || right.Kind == StackKind.NativeInt
            ? StackKind.NativeInt : StackKind.Int64;
        if (IsUnsigned(op)) {
            var ux = (ulong)x;
            var uy = (ulong)y;
            return OfKind(resultKind, (long)UnsignedInt64(op, ux, uy));
        }
        return OfKind(resultKind, SignedInt64(op, x, y));
    }

    private static StackSlot OfKind(StackKind kind, long value) => kind switch {
        StackKind.NativeInt => StackSlot.OfNativeInt(value),
        _ => StackSlot.OfInt64(value),
    };

    private static bool IsUnsigned(ILOp op) =>
        op is ILOp.Div_Un or ILOp.Rem_Un or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf_Un or ILOp.Mul_Ovf_Un
            or ILOp.Shr_Un;

    /// <summary>整数算術の実体 (int32)。div/rem のゼロ除算・Overflow はゲスト例外に変換。</summary>
    private static int SignedInt32(ILOp op, int a, int b) {
        switch (op) {
            case ILOp.Add: return a + b;
            case ILOp.Sub: return a - b;
            case ILOp.Mul: return a * b;
            case ILOp.Div:
                if (b == 0) ThrowDivideByZero();
                if (a == int.MinValue && b == -1) ThrowOverflow();
                return a / b;
            case ILOp.Rem:
                if (b == 0) ThrowDivideByZero();
                if (a == int.MinValue && b == -1) return 0;
                return a % b;
            case ILOp.And: return a & b;
            case ILOp.Or: return a | b;
            case ILOp.Xor: return a ^ b;
            case ILOp.Shl: return a << (b & 31);
            case ILOp.Shr: return a >> (b & 31);
            case ILOp.Add_Ovf:
            case ILOp.Sub_Ovf:
            case ILOp.Mul_Ovf:
                try {
                    return op switch {
                        ILOp.Add_Ovf => checked(a + b),
                        ILOp.Sub_Ovf => checked(a - b),
                        _ => checked(a * b),
                    };
                } catch (OverflowException) {
                    ThrowOverflow();
                    return 0;
                }
            default:
                throw new InvalidOperationException($"符号付き int32 算術でない命令です: {op}");
        }
    }

    private static uint UnsignedInt32(ILOp op, uint a, uint b) {
        switch (op) {
            case ILOp.Div_Un:
                if (b == 0) ThrowDivideByZero();
                return a / b;
            case ILOp.Rem_Un:
                if (b == 0) ThrowDivideByZero();
                return a % b;
            case ILOp.Shr_Un: return a >> (int)(b & 31);
            case ILOp.Add_Ovf_Un:
                var s = a + b;
                if (s < a) ThrowOverflow();
                return s;
            case ILOp.Sub_Ovf_Un:
                if (b > a) ThrowOverflow();
                return a - b;
            case ILOp.Mul_Ovf_Un:
                var p = a * b;
                if (a != 0 && p / a != b) ThrowOverflow();
                return p;
            default:
                throw new InvalidOperationException($"符号なし int32 算術でない命令です: {op}");
        }
    }

    private static long SignedInt64(ILOp op, long a, long b) {
        switch (op) {
            case ILOp.Add: return a + b;
            case ILOp.Sub: return a - b;
            case ILOp.Mul: return a * b;
            case ILOp.Div:
                if (b == 0) ThrowDivideByZero();
                if (a == long.MinValue && b == -1) ThrowOverflow();
                return a / b;
            case ILOp.Rem:
                if (b == 0) ThrowDivideByZero();
                if (a == long.MinValue && b == -1) return 0;
                return a % b;
            case ILOp.And: return a & b;
            case ILOp.Or: return a | b;
            case ILOp.Xor: return a ^ b;
            case ILOp.Shl: return a << (int)(b & 63);
            case ILOp.Shr: return a >> (int)(b & 63);
            case ILOp.Add_Ovf:
            case ILOp.Sub_Ovf:
            case ILOp.Mul_Ovf:
                try {
                    return op switch {
                        ILOp.Add_Ovf => checked(a + b),
                        ILOp.Sub_Ovf => checked(a - b),
                        _ => checked(a * b),
                    };
                } catch (OverflowException) {
                    ThrowOverflow();
                    return 0;
                }
            default:
                throw new InvalidOperationException($"符号付き int64 算術でない命令です: {op}");
        }
    }

    private static ulong UnsignedInt64(ILOp op, ulong a, ulong b) {
        switch (op) {
            case ILOp.Div_Un:
                if (b == 0) ThrowDivideByZero();
                return a / b;
            case ILOp.Rem_Un:
                if (b == 0) ThrowDivideByZero();
                return a % b;
            case ILOp.Shr_Un: return a >> (int)(b & 63);
            case ILOp.Add_Ovf_Un: {
                var s = a + b;
                if (s < a) ThrowOverflow();
                return s;
            }
            case ILOp.Sub_Ovf_Un:
                if (b > a) ThrowOverflow();
                return a - b;
            case ILOp.Mul_Ovf_Un: {
                var p = a * b;
                if (a != 0 && p / a != b) ThrowOverflow();
                return p;
            }
            default:
                throw new InvalidOperationException($"符号なし int64 算術でない命令です: {op}");
        }
    }

    private static void ThrowDivideByZero() =>
        throw new UnhandledGuestException("System.DivideByZeroException", null);
    private static void ThrowOverflow() =>
        throw new UnhandledGuestException("System.OverflowException", null);

    private static StackSlot UnaryArithmetic(ILOp op, in StackSlot value) {
        if (value.Kind == StackKind.Float)
            return StackSlot.OfFloat(op == ILOp.Neg ? -value.DoubleValue
                : throw new InvalidOperationException("浮動小数に not は適用できません。"));
        if (value.Kind == StackKind.Int32)
            return op == ILOp.Neg ? StackSlot.OfInt32(-(int)value.Int64Value)
                : StackSlot.OfInt32(~(int)value.Int64Value);
        return op == ILOp.Neg ? StackSlot.OfInt64(-value.Int64Value)
            : StackSlot.OfInt64(~value.Int64Value);
    }

    // ---- 変換 ----

    private static StackSlot ConvertValue(ILOp op, in StackSlot value) {
        // ソース値を i8 (または f8) に統一してから切り詰める
        var isFloatSrc = value.Kind == StackKind.Float;
        var f = isFloatSrc ? value.DoubleValue : 0.0;
        var i = isFloatSrc ? 0L : value.Int64Value;

        switch (op) {
            // 無限精度の拡張/縮小 (ラップする)
            case ILOp.Conv_I1: return StackSlot.OfInt32((sbyte)i);
            case ILOp.Conv_I2: return StackSlot.OfInt32((short)i);
            case ILOp.Conv_I4: return StackSlot.OfInt32((int)i);
            case ILOp.Conv_I8: return StackSlot.OfInt64(i);
            case ILOp.Conv_U1: return StackSlot.OfInt32((byte)i);
            case ILOp.Conv_U2: return StackSlot.OfInt32((ushort)i);
            case ILOp.Conv_U4: return StackSlot.OfInt32((int)(uint)i);
            // conv.u8/conv.u はゼロ拡張。i4 スロットの Int64Value は既に符号拡張済みなので Kind で判断する
            case ILOp.Conv_U8:
                return StackSlot.OfInt64(value.Kind == StackKind.Int32
                    ? (long)(uint)value.Int64Value
                    : (long)(ulong)value.Int64Value);
            case ILOp.Conv_I: return StackSlot.OfNativeInt(i);
            case ILOp.Conv_U:
                return StackSlot.OfNativeInt(value.Kind == StackKind.Int32
                    ? (long)(uint)value.Int64Value
                    : (long)(ulong)value.Int64Value);
            case ILOp.Conv_R4: return StackSlot.OfFloat(isFloatSrc ? (double)(float)f : (double)(float)i);
            case ILOp.Conv_R8: return StackSlot.OfFloat(isFloatSrc ? f : i);
            case ILOp.Conv_R_Un:
                if (isFloatSrc) return StackSlot.OfFloat(f);
                return value.Kind == StackKind.Int64
                    ? StackSlot.OfFloat((double)(ulong)value.Int64Value)
                    : StackSlot.OfFloat((double)(uint)value.Int64Value);

            // オーバーフロー検査つき
            case ILOp.Conv_Ovf_I1: return Checked32(((sbyte)Checked64(op, i, isFloatSrc, f)));
            case ILOp.Conv_Ovf_I2: return Checked32((short)Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I4: return Checked32((int)Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I8: return StackSlot.OfInt64(Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_U1: return Checked32((byte)Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_U2: return Checked32((ushort)Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_U4: return Checked32((int)(uint)Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_U8: return StackSlot.OfInt64((long)CheckedU64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I: return StackSlot.OfNativeInt(Checked64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_U: return StackSlot.OfNativeInt((long)CheckedU64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un
                or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un
                or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un:
                return ConvertOvfUnsigned(op, value);
            default:
                throw new InvalidOperationException($"変換命令でない命令です: {op}");
        }
    }

    /// <summary>conv.ovf.*.un: ソースを符号なし整数とみなしてターゲット範囲を検査する。</summary>
    private static StackSlot ConvertOvfUnsigned(ILOp op, in StackSlot value) {
        if (value.Kind == StackKind.Float) {
            var f = value.DoubleValue;
            if (double.IsNaN(f) || f < 0 || f >= 18446744073709551615.0)
                ThrowOverflow();
            var u = (ulong)f;
            return ConvertOvfFromU64(op, u);
        }
        // i4 は符号拡張済みだが .un はビット列を符号なしとして扱う
        var raw = value.Kind == StackKind.Int32 ? (uint)value.Int64Value : (ulong)value.Int64Value;
        return ConvertOvfFromU64(op, raw);
    }

    private static StackSlot ConvertOvfFromU64(ILOp op, ulong value) {
        switch (op) {
            case ILOp.Conv_Ovf_I1_Un: if (value > (ulong)sbyte.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((sbyte)value);
            case ILOp.Conv_Ovf_I2_Un: if (value > (ulong)short.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((short)value);
            case ILOp.Conv_Ovf_I4_Un: if (value > int.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((int)value);
            case ILOp.Conv_Ovf_I8_Un: if (value > long.MaxValue) ThrowOverflow(); return StackSlot.OfInt64((long)value);
            case ILOp.Conv_Ovf_U1_Un: if (value > byte.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((byte)value);
            case ILOp.Conv_Ovf_U2_Un: if (value > ushort.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((ushort)value);
            case ILOp.Conv_Ovf_U4_Un: if (value > uint.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((int)(uint)value);
            case ILOp.Conv_Ovf_U8_Un: return StackSlot.OfInt64((long)value);
            case ILOp.Conv_Ovf_I_Un: if (value > long.MaxValue) ThrowOverflow(); return StackSlot.OfNativeInt((long)value);
            case ILOp.Conv_Ovf_U_Un: return StackSlot.OfNativeInt((long)value);
            default:
                throw new InvalidOperationException($"conv.ovf.*.un でない命令です: {op}");
        }
    }

    private static long Checked64(ILOp op, long i, bool isFloat, double f) {
        if (isFloat) {
            if (double.IsNaN(f) || f < long.MinValue || f >= 9223372036854775808.0)
                ThrowOverflow();
            return (long)f;
        }
        return i;
    }

    private static ulong CheckedU64(ILOp op, long i, bool isFloat, double f) {
        if (isFloat) {
            if (double.IsNaN(f) || f < 0 || f >= 18446744073709551615.0)
                ThrowOverflow();
            return (ulong)f;
        }
        if (i < 0)
            ThrowOverflow();
        return (ulong)i;
    }

    private static StackSlot Checked32(int value) => StackSlot.OfInt32(value);

    // ---- 事前準備キャッシュ ----

    private PreparedMethod Prepare(VmMethod method) {
        if (_prepared.TryGetValue(method, out var cached))
            return cached;

        SigType[] localTypes = [];
        if (method.Body is { } body && body.LocalVarSigToken != 0) {
            var table = (TableKind)(body.LocalVarSigToken >> 24);
            var rid = (int)(body.LocalVarSigToken & 0xFFFFFF);
            if (table != TableKind.StandAloneSig)
                throw new BadImageFormatException($"ローカル変数署名トークン 0x{body.LocalVarSigToken:X8} が不正です。");
            localTypes = SignatureDecoder.DecodeLocalsSignature(
                _loader.Image.GetBlob(_loader.Image.Tables.GetRowIndex(table, rid, 0)).ToArray());
        }

        var prepared = new PreparedMethod(localTypes) {
            Clauses = ResolveExceptionClauses(method),
        };
        _prepared[method] = prepared;
        return prepared;
    }

    /// <summary>EH 句 (IL オフセット基準) を命令インデックス基準に解決する。</summary>
    private static PreparedClause[]? ResolveExceptionClauses(VmMethod method) {
        var raw = method.Body?.ExceptionClauses;
        if (raw is null || raw.Length == 0)
            return null;

        // 内側の句から外側の順で走査できるよう入れ子順にソート (深い = TryOffset が大きく範囲が狭い)
        if (raw.Length > 1) {
            var sorted = (ExceptionClause[])raw.Clone();
            Array.Sort(sorted, (a, b) => {
                var byStart = b.TryOffset.CompareTo(a.TryOffset);
                return byStart != 0 ? byStart : a.TryLength.CompareTo(b.TryLength);
            });
            raw = sorted;
        }

        var code = method.DecodeIl();
        var offsetToIndex = new Dictionary<int, int>(code.Length * 2);
        for (var i = 0; i < code.Length; i++)
            offsetToIndex[code[i].Offset] = i;

        var clauses = new PreparedClause[raw.Length];
        for (var i = 0; i < raw.Length; i++) {
            var clause = raw[i];
            if (!offsetToIndex.TryGetValue(clause.TryOffset, out var tryStart) ||
                !offsetToIndex.TryGetValue(clause.HandlerOffset, out var handlerStart))
                throw new BadImageFormatException(
                    $"EH 句 {i} (try IL_{clause.TryOffset:X4}, handler IL_{clause.HandlerOffset:X4}) が命令境界上にありません。");
            clauses[i] = new PreparedClause {
                Kind = clause.Kind,
                TryStart = tryStart,
                TryEnd = IndexAfter(code, clause.TryOffset + clause.TryLength, tryStart),
                HandlerStart = handlerStart,
                HandlerEnd = IndexAfter(code, clause.HandlerOffset + clause.HandlerLength, handlerStart),
                FilterStart = clause.Kind == ExceptionClauseKind.Filter
                    ? (offsetToIndex.TryGetValue(clause.ClassTokenOrFilterOffset, out var filterStart)
                        ? filterStart
                        : throw new BadImageFormatException(
                            $"フィルタ先 IL_{clause.ClassTokenOrFilterOffset:X4} が命令境界上にありません。"))
                    : -1,
                ClassToken = clause.ClassTokenOrFilterOffset,
            };
        }
        return clauses;
    }

    /// <summary>指定 IL オフセット以上で最初の命令のインデックス (末尾到達なら code.Length)。</summary>
    private static int IndexAfter(DecodedInstruction[] code, int ilOffset, int fallback) {
        for (var i = fallback; i < code.Length; i++)
            if (code[i].Offset >= ilOffset)
                return i;
        return code.Length;
    }

    private sealed class PreparedMethod(SigType[] localTypes) {
        public readonly SigType[] LocalTypes = localTypes;

        /// <summary>解決済み EH 句 (命令インデックス基準)。EH の無いメソッドは null。</summary>
        public PreparedClause[]? Clauses;
    }

    /// <summary>解決済み EH 句 ( PreparedMethod.Clauses の要素)。</summary>
    private sealed class PreparedClause {
        public ExceptionClauseKind Kind;
        public int TryStart;
        public int TryEnd; // 排他
        public int HandlerStart;
        public int HandlerEnd; // 排他 (現状未使用だが範囲検査用に保持)
        /// <summary>フィルタ本体の開始インデックス (Catch/Finally/Fault は -1)。</summary>
        public int FilterStart;
        /// <summary>Catch 句の型トークン (TypeDefOrRef)。</summary>
        public int ClassToken;
    }

    private static void CheckArgIndex(InterpreterFrame frame, int index) {
        if ((uint)index >= (uint)frame.Arguments.Length)
            throw new BadImageFormatException(
                $"引数インデックス {index} が範囲外です ({frame.Method} は引数 {frame.Arguments.Length} 個)。");
    }

    private static void CheckLocalIndex(InterpreterFrame frame, int index) {
        if ((uint)index >= (uint)frame.Locals.Length)
            throw new BadImageFormatException(
                $"ローカル変数インデックス {index} が範囲外です ({frame.Method} はローカル {frame.Locals.Length} 個)。");
    }
}
