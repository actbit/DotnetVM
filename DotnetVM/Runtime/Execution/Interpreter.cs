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
/// - intrinsic 呼出は必ず実行ゲート (IExecutionGate) 経由 (クォータ消費・セーフポイント・正規化を強制)
/// - ゲスト呼出はフレームを積み、深さは MaxRecursionDepth で事前拒否
/// 実装は責務ごとの独立クラスに分割 (Interpreter が IGuestInvoker/IExecutionGate/IFrameRunner を実装し注入):
///   SlotOps               スロット演算 (算術/比較/変換/コピー) — static
///   MemoryOps             配列/生メモリ/間接アクセス/sizeof — static
///   TypeChecks            型代入可否・デリゲート/例外ファサード判定 — static
///   MethodPreparer        メソッド事前準備キャッシュ (ローカル署名/EH 句)
///   ObjectEngine          newobj/フィールド/静的ストレージ/型初期化
///   CallEngine            呼出解決/仮想ディスパッチ/デリゲート
///   ExceptionDispatcher   EH (catch/finally/fault/filter) と分岐制御
/// </summary>
public sealed partial class Interpreter : IGuestInvoker, IExecutionGate, IFrameRunner {

    private const int SafepointInterval = 1024;

    private readonly MemoryPolicy _memory;
    private readonly bool _enableJit;
    private readonly int _jitPromotionThreshold;
    private readonly JitResourceBudget _jitResourceBudget;
    private readonly IntrinsicRegistry _intrinsics;
    private readonly VmConsole _console;
    private readonly VmHeap _heap;
    private readonly NetworkGateway? _network;
    private readonly StorageGateway? _storage;
    private readonly Diagnostics.ExecutionTracer? _tracer;
    private readonly VmCoreLibSurfaces? _coreLibSurfaces;
    private readonly VmType? _stringType;
    private readonly VmSharedState _shared;
    private readonly Func<ReadOnlyMemory<byte>, TypeLoader>? _loadAssemblyFromBytes;
    private readonly VmAssemblyLoadContext? _defaultAssemblyLoadContext;
    private readonly Func<string?, bool, VmAssemblyLoadContext>? _createAssemblyLoadContext;
    private readonly Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? _loadAssemblyInContext;
    private readonly Func<VmAssemblyLoadContext, string, TypeLoader>? _loadAssemblyFromPath;
    private readonly InterpreterServices _services;
    private readonly MethodPreparer _preparer;
    private readonly ObjectEngine _objectEngine;
    private readonly CallEngine _callEngine;
    private readonly ExceptionDispatcher _exceptionDispatcher;
    /// <summary>loader ごとのエンジンセット (多アセンブリ実行: メソッドの所属画像で token 解決する)。</summary>
    private readonly Dictionary<TypeLoader, LoaderEngines> _engines = [];
    private readonly object _enginesGate = new();
    /// <summary>VM 単位で共有する静的ストレージ (ユニフィケーションされた実型の静的フィールドは 1 つ)。</summary>
    private readonly UnifiedStaticStorage _unifiedStaticStorage = new();
    private readonly object _cacheRemovalGate = new();
    private readonly Dictionary<VmAssemblyContext, TypeLoader[]> _pendingCacheRemovals = [];
    private bool? _enginesRegisteredRootKey;
    // 実行状態はホストスレッドごとに分離する。フレーム一覧は stop-the-world GC が
    // 全ゲスト命令を停止した状態で走査し、独立した呼出しのルートをまとめて返す。
    private sealed class ExecutionState {
        public readonly object Gate = new();
        public readonly List<InterpreterFrame> Frames = [];
        public readonly List<StackSlot[]> TemporaryRoots = [];
        public int Depth;
        public long InstructionCount;
    }
    private readonly ThreadLocal<ExecutionState> _currentExecution = new(() => new ExecutionState());
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ExecutionState, byte> _executionStates = new();
    private readonly VmExecutionCoordinator _coordinator = new();
    private long _instructionCount;
    private int _running;
    private int _disposed;
    private readonly Func<IEnumerable<StackSlot[]>> _frameRootSource;

    public long InstructionCount => Interlocked.Read(ref _instructionCount);
    internal bool HasInstructionBudget => Interlocked.Read(ref _instructionCount) < _memory.InstructionQuota;

    internal long CurrentThreadInstructionCount => CurrentState.InstructionCount;

    internal bool IsJitCompiled(VmMethod method) => EnginesFor(method).Jit.IsCompiled(method);
    internal int JitInvocationCount(VmMethod method) => EnginesFor(method).Jit.InvocationCount(method);

    internal IDisposable EnterHostOperation() => _coordinator.EnterRead();

    private ExecutionState CurrentState {
        get {
            var state = _currentExecution.Value!;
            _executionStates.TryAdd(state, 0);
            return state;
        }
    }

    void IExecutionGate.ConsumeInstruction() => ConsumeInstruction();

    void IExecutionGate.CheckSafepoint() => CheckSafepoint();

    StackSlot IFrameRunner.RunFrameCore(InterpreterFrame frame) => RunFrameCore(frame);

    // ---- クォータ/セーフポイント ----

    private void ConsumeInstruction() {
        _shared.ThrowIfDisposed();
        var count = Interlocked.Increment(ref _instructionCount);
        if (count > _memory.InstructionQuota)
            throw new InstructionQuotaExceededException(
                $"命令数クォータ {_memory.InstructionQuota:N0} を超過しました (実行命令数: {count:N0})。");
        var state = CurrentState;
        state.InstructionCount++;
        if (count % SafepointInterval == 0)
            CheckSafepoint();
    }

    /// <summary>セーフポイント。命令境界 = 全ゲスト状態がフレームに含まれる時点なので、ここでのみ GC を起動してよい
    /// (newobj 処理中のオブジェクトがホストローカルにのみ保持される瞬間があり、そこで回収すると誤 sweep する)。</summary>
    private void CheckSafepoint() {
        _shared.ShutdownToken.ThrowIfCancellationRequested();
        _shared.ThrowIfDisposed();
        // IL 命令またはその intrinsic 呼出中は共有 read lease を保持している。
        // その場で GC せず、RunFrameCore の次の命令境界で stop-the-world 回収する。
        if (_coordinator.IsInsideGuestInstruction)
            return;
        using (_coordinator.StopTheWorldAtBoundary())
            _services.Heap.CollectIfDue();
    }

    // JIT frames use the same quota and coordinator gates as interpreter
    // instructions.  These narrow wrappers keep the coordinator private while
    // allowing the generated delegate to bracket each instruction safely.
    internal void ConsumeJitInstruction() => ConsumeInstruction();
    internal void CheckJitSafepoint() => CheckSafepoint();
    internal IDisposable EnterJitInstruction() => _coordinator.EnterInstruction();
    internal CallEngine JitCallsFor(VmMethod method) => EnginesFor(method).Calls;
    internal ObjectEngine JitObjectsFor(VmMethod method) => EnginesFor(method).Objects;
    internal ExceptionDispatcher JitExceptionsFor(VmMethod method) => EnginesFor(method).Exceptions;

    // ---- 命令ディスパッチ ループ ----

    private StackSlot RunFrameCore(InterpreterFrame frame) {
        // メソッドの所属画像に対応するエンジンセットで実行する (多アセンブリ: dep の IL は
        // dep の loader で ldstr/ldtoken/呼出を解決する)
        var engines = EnginesFor(frame.Method);
        var loader = engines.Services.Loader;
        var prepared = engines.Preparer.Prepare(frame.Method);
        var eh = engines.Exceptions;
        var calls = engines.Calls;
        var objects = engines.Objects;
        while (true) {
            CheckSafepoint();
            using var instructionLease = _coordinator.EnterInstruction();
            ConsumeInstruction();
            var instruction = frame.Code[frame.Ip];
            var isPrefix = IsPrefix(instruction.Op);
            var volatileAccess = frame.PendingVolatile && !isPrefix && IsVolatileMemoryAccess(instruction.Op);
            var readonlyArrayAddress = frame.PendingReadonly && !isPrefix && instruction.Op == ILOp.Ldelema;
            var tailCallAllowed = frame.PendingTail && !isPrefix &&
                (instruction.Op is ILOp.Call or ILOp.Callvirt or ILOp.Calli) &&
                frame.Ip + 1 < frame.Code.Length && frame.Code[frame.Ip + 1].Op == ILOp.Ret &&
                !frame.PendingTailInProtectedRegion && !IsInProtectedRegion(frame.Ip, prepared.Clauses);
            if (!isPrefix) {
                frame.PendingVolatile = false;
                frame.PendingTail = false;
                frame.PendingTailInProtectedRegion = false;
                frame.PendingReadonly = false;
                if (instruction.Op is not (ILOp.Call or ILOp.Callvirt))
                    frame.PendingConstrained = 0;
            }
            if (volatileAccess)
                Thread.MemoryBarrier();
            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break:
                case ILOp.Unaligned: // VM の仮想メモリではアラインメント制約なし
                    break;
                case ILOp.Readonly:
                    frame.PendingReadonly = true;
                    break;
                case ILOp.Volatile:
                    frame.PendingVolatile = true;
                    break;
                case ILOp.Tail:
                    frame.PendingTail = true;
                    frame.PendingTailInProtectedRegion |= IsInProtectedRegion(frame.Ip, prepared.Clauses);
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
                    frame.Stack.Push(SlotOps.PushCopyOfValue(frame.Locals[instruction.Op - ILOp.Ldloc_0]));
                    break;
                case ILOp.Ldloc_S or ILOp.Ldloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(frame.Locals[instruction.IntOperand]));
                    break;
                case ILOp.Ldloca_S or ILOp.Ldloca:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(frame.Locals, instruction.IntOperand)));
                    break;
                case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                    CheckLocalIndex(frame, instruction.Op - ILOp.Stloc_0);
                    frame.Locals[instruction.Op - ILOp.Stloc_0] = SlotOps.StoreCopyOfValue(frame.Stack.Pop());
                    break;
                case ILOp.Stloc_S or ILOp.Stloc:
                    CheckLocalIndex(frame, instruction.IntOperand);
                    frame.Locals[instruction.IntOperand] = SlotOps.StoreCopyOfValue(frame.Stack.Pop());
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
                    eh.JumpTo(frame, instruction.IntOperand);
                    continue;
                case ILOp.BrFalse or ILOp.BrFalse_S:
                    if (!SlotOps.IsTrue(frame.Stack.Pop()))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.BrTrue or ILOp.BrTrue_S:
                    if (SlotOps.IsTrue(frame.Stack.Pop()))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S
                    or ILOp.Bge or ILOp.Bge_S or ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S
                    or ILOp.Blt or ILOp.Blt_S or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S
                    or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    if (SlotOps.CompareBranch(instruction.Op, left, right))
                        { eh.JumpTo(frame, instruction.IntOperand); continue; }
                    break;
                }
                case ILOp.Switch: {
                    var index = frame.Stack.Pop().AsInt32;
                    var targets = instruction.SwitchTargets!;
                    if ((uint)index < (uint)targets.Length) {
                        eh.JumpTo(frame, targets[index]);
                        continue;
                    }
                    break;
                }

                // ---- 比較 (スタックに 0/1 を積む) ----
                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un: {
                    var right = frame.Stack.Pop();
                    var left = frame.Stack.Pop();
                    frame.Stack.Push(StackSlot.OfInt32(SlotOps.Compare(instruction.Op, left, right) ? 1 : 0));
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
                    frame.Stack.Push(MemoryOps.TryPointerArithmetic(instruction.Op, left, right)
                        ?? SlotOps.BinaryArithmetic(instruction.Op, left, right));
                    break;
                }
                case ILOp.Neg or ILOp.Not: {
                    var value = frame.Stack.Pop();
                    frame.Stack.Push(SlotOps.UnaryArithmetic(instruction.Op, value));
                    break;
                }

                // ---- 変換 ----
                case >= ILOp.Conv_I1 and <= ILOp.Conv_R_Un
                    when instruction.Op is ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8
                        or ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U4 or ILOp.Conv_U8 or ILOp.Conv_R_Un:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Conv_U2 or ILOp.Conv_U1 or ILOp.Conv_I or ILOp.Conv_U:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un
                    or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un
                    or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un
                    or ILOp.Conv_Ovf_I1 or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or ILOp.Conv_Ovf_U2
                    or ILOp.Conv_Ovf_I4 or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I8 or ILOp.Conv_Ovf_U8
                    or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U:
                    frame.Stack.Push(SlotOps.ConvertValue(instruction.Op, frame.Stack.Pop()));
                    break;

                // ---- 文字列 ----
                case ILOp.Ldstr:
                    frame.Stack.Push(StackSlot.OfObject(_services.Strings.GetOrNew(
                        frame.Method.DynamicStrings is { } dynamicStrings &&
                        dynamicStrings.TryGetValue(unchecked((uint)instruction.IntOperand), out var dynamicString)
                            ? dynamicString
                            : loader.Image.GetUserString(instruction.IntOperand & 0xFFFFFF))));
                    break;

                // ---- 呼出 ----
                case ILOp.Call or ILOp.Callvirt: {
                    var result = calls.Call(instruction.IntOperand, frame,
                        instruction.Op == ILOp.Callvirt, frame.PendingConstrained,
                        tailCallAllowed, out var tailCallRequest);
                    frame.PendingConstrained = 0; // constrained. は直後の 1 呼出でのみ有効
                    if (tailCallRequest is not null)
                        throw new TailCallTransfer(tailCallRequest);
                    if (result is { } value)
                        frame.Stack.Push(value);
                    break;
                }
                case ILOp.Ret:
                    return SlotOps.SignatureReturnsValue(frame.Method.Signature) ? frame.Stack.Pop() : default;

                // ---- オブジェクト生成 ----
                case ILOp.Newobj: {
                    var value = objects.NewObject(instruction.IntOperand, frame);
                    if (value is { } pushed)
                        frame.Stack.Push(pushed);
                    break;
                }
                case ILOp.Newarr: {
                    var count = frame.Stack.Pop().AsInt32;
                    if (count < 0)
                        throw new UnhandledGuestException("System.OverflowException", null);
                    var elementType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    // localloc と同じく予約トランザクション (Reserve → 実確保 → Commit、失敗時は自動巻き戻し)
                    using var reservation = _services.Heap.ReserveArray(count);
                    var elements = new StackSlot[count];
                    for (var i = 0; i < count; i++)
                        elements[i] = engines.Services.Objects.DefaultForType(elementType, loader);
                    frame.Stack.Push(StackSlot.OfObject(reservation.Commit(
                        new VmArray(new VmArrayType { ElementType = elementType }, elements))));
                    break;
                }
                case ILOp.Ldlen:
                    frame.Stack.Push(StackSlot.OfNativeInt(MemoryOps.GetArray(frame.Stack.Pop()).Length));
                    break;

                // ---- 配列要素 ----
                case ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2
                    or ILOp.Ldelem_I4 or ILOp.Ldelem_U4:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Int32));
                    break;
                case ILOp.Ldelem_I8 or ILOp.Ldelem_I:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Int64));
                    break;
                case ILOp.Ldelem_R4 or ILOp.Ldelem_R8:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Float));
                    break;
                case ILOp.Ldelem_Ref:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame, MemoryOps.ArrayElementKind.Object));
                    break;
                case ILOp.Ldelem:
                    frame.Stack.Push(MemoryOps.ArrayLoad(frame,
                        MemoryOps.ElementKindFromType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens))));
                    break;
                case ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2 or ILOp.Stelem_I4:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Int32);
                    break;
                case ILOp.Stelem_I8:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Int64);
                    break;
                case ILOp.Stelem_R4 or ILOp.Stelem_R8:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Float);
                    break;
                case ILOp.Stelem_Ref:
                    MemoryOps.ArrayStore(frame, MemoryOps.ArrayElementKind.Object, _services.StringType);
                    break;
                case ILOp.Stelem:
                    MemoryOps.ArrayStore(frame,
                        MemoryOps.ElementKindFromType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens)));
                    break;
                case ILOp.Ldelema: {
                    var index = frame.Stack.Pop().AsInt32;
                    var array = MemoryOps.GetArray(frame.Stack.Pop());
                    MemoryOps.CheckArrayBounds(array, index);
                     frame.Stack.Push(StackSlot.OfByRef(new VmByRef(array.Elements, index, readonlyArrayAddress, array)));
                    break;
                }

                // ---- フィールド ----
                case ILOp.Ldfld or ILOp.Ldflda: {
                    var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var objSlot = frame.Stack.Pop();
                    if (instruction.Op == ILOp.Ldflda) {
                        // VnString (可変 char バッファ) はバイト実体への unmanaged ポインタ、
                        // それ以外は ByRef (CoreLib IL が Unsafe.Add / Buffer.Memmove に渡す形)
                        frame.Stack.Push(objects.FieldAddress(objSlot, field));
                        break;
                    }
                    var location = objects.FieldLocation(objSlot, field);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(location.Read()));
                    break;
                }
                case ILOp.Stfld: {
                    var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    EnsureFieldWritable(field, frame.Method);
                    var value = frame.Stack.Pop();
                    var objSlot = frame.Stack.Pop();
                    // VnString レシーバはバイト実体 (真実源) に直接書く
                    if (objects.TryStoreStringField(objSlot, field, value))
                        break;
                    objects.FieldLocation(objSlot, field).Write(SlotOps.StoreCopyOfValue(value));
                    break;
                }
                case ILOp.Ldsfld: {
                    var slot = objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    frame.Stack.Push(SlotOps.PushCopyOfValue(slot.Read()));
                    break;
                }
                case ILOp.Ldsflda: {
                    // 静的 FieldRVA データ (<PrivateImplementationDetails> 初期化データ) は
                    // バイト実体への unmanaged ポインタ (CoreLib IL が ReadOnlySpan(void*) ctor に渡す形)、
                    // それ以外は通常の静的ストレージへの ByRef
                    if (objects.TryGetStaticFieldRvaAddress(instruction.IntOperand) is { } rvaSlot)
                        frame.Stack.Push(rvaSlot);
                    else
                        frame.Stack.Push(StackSlot.OfByRef(objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens)));
                    break;
                }
                case ILOp.Stsfld: {
                    var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    EnsureFieldWritable(field, frame.Method);
                    var value = frame.Stack.Pop();
                    objects.StaticFieldLocation(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens).Write(value);
                    break;
                }

                // ---- 間接アクセス (ldind/stind。マネージポインタ ByRef と unmanaged ポインタの両対応) ----
                case ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2
                    or ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I
                    or ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref:
                    frame.Stack.Push(MemoryOps.LoadIndirect(instruction.Op, frame.Stack.Pop()));
                    break;
                case ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or ILOp.Stind_I2
                    or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8: {
                    var value = frame.Stack.Pop();
                    MemoryOps.StoreIndirect(instruction.Op, frame.Stack.Pop(), value);
                    break;
                }

                // ---- オブジェクト/値型のコピー ----
                case ILOp.Ldobj: {
                    var address = frame.Stack.Pop();
                    if (address.ObjectValue is VmNativePointer ldNative) {
                        // unmanaged ポインタ先からの読み出し (Guid 解析等の RawData 経路)。
                        // 型のバイト幅で LE 読みし VM スロットへ展開する
                        var ldType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var ldSize = MemoryOps.SizeOfType(ldType);
                        if (ldNative.ByteOffset < 0 || (long)ldNative.ByteOffset + ldSize > ldNative.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                                $"ldobj がブロック外を参照します (offset={ldNative.ByteOffset}, {ldSize} バイト)。");
                        frame.Stack.Push(MemoryOps.ValueFromBytes(
                            ldNative.Bytes.AsSpan(ldNative.ByteOffset, ldSize).ToArray(), ldType, ldSize));
                        break;
                    }
                    var byref = address.ObjectValue as VmByRef
                        ?? throw new UnhandledGuestException("System.InvalidProgramException",
                            "ldobj のアドレスがマネージ参照ではありません。");
                    frame.Stack.Push(SlotOps.PushCopyOfValue(byref.Slot));
                    break;
                }
                case ILOp.Stobj: {
                    var value = frame.Stack.Pop();
                    var stAddress = frame.Stack.Pop();
                    if (stAddress.ObjectValue is VmNativePointer stNative) {
                        var stType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var stSize = MemoryOps.SizeOfType(stType);
                        stNative.EnsureWritable();
                        if (stNative.ByteOffset < 0 || (long)stNative.ByteOffset + stSize > stNative.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException",
                                $"stobj がブロック外を参照します (offset={stNative.ByteOffset}, {stSize} バイト)。");
                        var stBytes = MemoryOps.BytesOfValue(value, stType, stSize);
                        Array.Copy(stBytes, 0, stNative.Bytes, stNative.ByteOffset, stSize);
                        break;
                    }
                    var byref = stAddress.ObjectValue as VmByRef
                        ?? throw new UnhandledGuestException("System.InvalidProgramException",
                            "stobj のアドレスがマネージ参照ではありません。");
                    byref.Write(SlotOps.StoreCopyOfValue(value));
                    break;
                }
                case ILOp.Cpobj: {
                    var src = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    var cpType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var cpSize = MemoryOps.SizeOfType(cpType);
                    if (src.ObjectValue is VmNativePointer srcNative && dst.ObjectValue is VmNativePointer dstNative) {
                        if ((long)srcNative.ByteOffset + cpSize > srcNative.Bytes.Length ||
                            (long)dstNative.ByteOffset + cpSize > dstNative.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException", "cpobj がブロック外を参照します。");
                        dstNative.EnsureWritable();
                        Array.Copy(srcNative.Bytes, srcNative.ByteOffset, dstNative.Bytes, dstNative.ByteOffset, cpSize);
                        break;
                    }
                    if (src.ObjectValue is VmNativePointer srcOnly) {
                        if (srcOnly.ByteOffset < 0 || (long)srcOnly.ByteOffset + cpSize > srcOnly.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException", "cpobj がブロック外を参照します。");
                        var value = MemoryOps.ValueFromBytes(
                            srcOnly.Bytes.AsSpan(srcOnly.ByteOffset, cpSize).ToArray(), cpType, cpSize);
                        ((VmByRef)dst.ObjectValue!).Write(SlotOps.StoreCopyOfValue(value));
                        break;
                    }
                    if (dst.ObjectValue is VmNativePointer dstOnly) {
                        var srcValue = ((VmByRef)src.ObjectValue!).Read();
                        var dstBytes = MemoryOps.BytesOfValue(srcValue, cpType, cpSize);
                        if ((long)dstOnly.ByteOffset + cpSize > dstOnly.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException", "cpobj がブロック外を参照します。");
                        dstOnly.EnsureWritable();
                        Array.Copy(dstBytes, 0, dstOnly.Bytes, dstOnly.ByteOffset, cpSize);
                        break;
                    }
                    var srcRef = src.ObjectValue as VmByRef
                        ?? throw new UnhandledGuestException("System.InvalidProgramException", "cpobj の送信元がマネージ参照ではありません。");
                    var dstRef = dst.ObjectValue as VmByRef
                        ?? throw new UnhandledGuestException("System.InvalidProgramException", "cpobj の宛先がマネージ参照ではありません。");
                    dstRef.Write(SlotOps.StoreCopyOfValue(srcRef.Read()));
                    break;
                }
                case ILOp.Initobj: {
                    var initAddress = frame.Stack.Pop();
                    if (initAddress.ObjectValue is VmNativePointer initNative) {
                        var initType = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                        var initSize = MemoryOps.SizeOfType(initType);
                        initNative.EnsureWritable();
                        if (initNative.ByteOffset < 0 || (long)initNative.ByteOffset + initSize > initNative.Bytes.Length)
                            throw new UnhandledGuestException("System.IndexOutOfRangeException", "initobj がブロック外を参照します。");
                        Array.Clear(initNative.Bytes, initNative.ByteOffset, initSize);
                        break;
                    }
                    var byref = initAddress.ObjectValue as VmByRef
                        ?? throw new UnhandledGuestException("System.InvalidProgramException", "initobj のアドレスがマネージ参照ではありません。");
                    byref.Write(engines.Services.Objects.DefaultForType(
                        objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens), loader));
                    break;
                }

                // ---- ボックス化 ----
                case ILOp.Box: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    var fields = value.Kind == StackKind.ValueType
                        ? ((VmStructValue)value.ObjectValue!).Clone().Fields
                        : [value];
                    frame.Stack.Push(StackSlot.OfObject(_services.Heap.Allocate(new VmBoxedValue(type, fields))));
                    break;
                }
                case ILOp.Unbox: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{SlotOps.Describe(value)} を {target.FullName} として unbox できません。");
                    frame.Stack.Push(StackSlot.OfByRef(new VmByRef(boxed.Fields, 0)));
                    break;
                }
                case ILOp.Unbox_Any: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (target.IsValueType) {
                        // 値型への unbox.any はボックス化実体からコピーを取り出す
                        if (value.ObjectValue is not VmBoxedValue boxed || !boxed.Type.IsAssignableTo(target))
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{SlotOps.Describe(value)} を {target.FullName} に unbox.any できません。");
                        if (VmPrimitiveTypes.IsSlotPrimitive(target.FullName)) {
                            // プリミティブ (実型 / intrinsic ファサードとも) はスロットをそのまま取り出す
                            frame.Stack.Push(boxed.Fields[0]);
                        } else if (target is VmClassType or VmConstructedType) {
                            // 構造体 (構築ジェネリック構造体を含む) は実体をコピーして取り出す
                            var args = boxed.Type is VmConstructedType ct ? ct.TypeArguments : null;
                            var sv = new VmStructValue(boxed.Type, (StackSlot[])boxed.Fields.Clone(), args);
                            frame.Stack.Push(StackSlot.OfValueType(sv));
                        } else {
                            // その他の intrinsic 値型 (TypedReference 等) もスロットで保持される
                            frame.Stack.Push(boxed.Fields[0]);
                        }
                    } else {
                        // 参照型への unbox.any は castclass と同等
                        var ok = value.ObjectValue is null ||
                            TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
                        if (!ok)
                            throw new UnhandledGuestException("System.InvalidCastException",
                                $"{SlotOps.Describe(value)} を {target.FullName} に変換できません。");
                        frame.Stack.Push(value);
                    }
                    break;
                }

                // ---- キャスト ----
                case ILOp.Castclass or ILOp.Isinst: {
                    var target = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    var ok = value.ObjectValue is null ||
                        TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType);
                    if (!ok && instruction.Op == ILOp.Castclass)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"{SlotOps.Describe(value)} を {target.FullName} にキャストできません。");
                    if (ok)
                        frame.Stack.Push(value);
                    else
                        frame.Stack.Push(StackSlot.Null);
                    break;
                }

                // ---- 例外処理 ----
                case ILOp.Throw: {
                    var value = frame.Stack.Pop();
                    throw eh.MakeGuestThrow(value);
                }
                case ILOp.Rethrow:
                    // 処理中の例外を元の情報のまま再送出 (現在の catch コンテキストの外へ)
                    throw frame.CurrentThrow
                        ?? throw new BadImageFormatException("rethrow が catch ハンドラの外で実行されました。");
                case ILOp.Leave or ILOp.Leave_S:
                    eh.DoLeave(frame, prepared.Clauses, instruction.IntOperand);
                    continue;
                case ILOp.Endfinally: {
                    if (frame.FinallyResume.Count == 0)
                        throw new BadImageFormatException("endfinally が finally/fault の外で実行されました。");
                    var next = frame.FinallyResume[^1];
                    frame.FinallyResume.RemoveAt(frame.FinallyResume.Count - 1);
                    if (next == ExceptionDispatcher.PropagateSentinel)
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
                        if (!eh.TryDispatchHandlerFrom(frame, frame.CurrentThrow!, clauseIndex + 1))
                            throw frame.CurrentThrow!;
                    }
                    continue;
                }

                // ---- 関数ポインタ / デリゲート / 特殊命令 ----
                case ILOp.Ldftn: {
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    if (target.Method is null)
                        throw new OperationNotAllowedException(
                            $"ldftn: intrinsic 面 {target.DeclaringType}::{target.Name} への関数ポインタ取得は対応していません。");
                    frame.Stack.Push(StackSlot.OfObject(new VmMethodPointer { Target = target.Method }));
                    break;
                }
                case ILOp.Ldvirtftn: {
                    // レシーバを実行時型で最派生実装に解決してから関数ポインタ化する (仮想束縛の確定)
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    var receiver = frame.Stack.Pop();
                    if (SlotOps.IsNullReference(receiver))
                        throw new UnhandledGuestException("System.NullReferenceException", null);
                    var resolved = target.Method is { } method
                        ? calls.DispatchVirtual(method, receiver)
                        : calls.TryDispatchVirtual(target.Name!, target.ParamCount, receiver)
                          ?? throw new OperationNotAllowedException(
                              $"ldvirtftn: {target.DeclaringType}::{target.Name} への関数ポインタ取得は対応していません。");
                    frame.Stack.Push(StackSlot.OfObject(new VmMethodPointer { Target = resolved }));
                    break;
                }
                case ILOp.Calli: {
                    // 間接呼出。スタック: fnptr, argN...arg1。オペランドは StandAloneSig (呼出規約 + 署名)。
                    // 関数ポインタはゲスト実装のみ (ldftn が intrinsic 面を拒否するため)
                    var signature = calls.DecodeStandAloneSignature(instruction.IntOperand);
                    var argCount = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                    var fnptr = frame.Stack.Pop();
                    var args = new StackSlot[argCount];
                    for (var i = argCount - 1; i >= 0; i--)
                        args[i] = frame.Stack.Pop();
                    ConsumeInstruction(); // 呼出ゲート: クォータ + セーフポイント
                    CheckSafepoint();
                    StackSlot? result;
                    if (fnptr.ObjectValue is VmMethodPointer pointer) {
                        if (tailCallAllowed && SignaturesMatch(signature, pointer.Target.Signature) &&
                            this is IGuestInvoker guestInvoker &&
                            guestInvoker.TryCreateTailCall(frame, pointer.Target, args, null, out var tailRequest))
                            throw new TailCallTransfer(tailRequest!);
                        result = Invoke(pointer.Target, args);
                    } else if (fnptr.ObjectValue is VmDelegate @delegate) {
                        result = calls.InvokeDelegate(@delegate, args);
                    } else {
                        throw new UnhandledGuestException("System.ArgumentException",
                            "calli の関数ポインタが無効です (ldftn/ldvirtftn の結果を指定してください)。");
                    }
                    if (SlotOps.SignatureReturnsValue(signature))
                        frame.Stack.Push(result ?? default);
                    break;
                }
                case ILOp.Ckfinite: {
                    var value = frame.Stack.Pop();
                    if (value.Kind == StackKind.Float &&
                        (double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue)))
                        throw new UnhandledGuestException("System.ArithmeticException", null);
                    frame.Stack.Push(value);
                    break;
                }
                case ILOp.Jmp: {
                    // 尾呼び移行: 現フレームの残りを実行せず、呼出先の戻り値をこのメソッドの戻り値とする
                    var target = calls.ResolveCallTarget(instruction.IntOperand, frame.Context,
                        dynamicTokens: frame.Method.DynamicTokens);
                    if (target.Method is null)
                        throw new OperationNotAllowedException(
                            $"jmp: intrinsic 面 {target.DeclaringType}::{target.Name} への尾呼び移行は対応していません。");
                    if (frame.Stack.Count != 0)
                        throw new BadImageFormatException("jmp の実行スタックは空でなければなりません。");
                    if (frame.Arguments.Length != target.Arity)
                        throw new BadImageFormatException("jmp の呼出先シグネチャが現在の引数数と一致しません。");
                    var args = frame.Arguments.ToArray();
                    var method = target.Method!;
                    var context = calls.BuildCallContext(target, method, method.Signature.HasThis ? args[0] : default);
                    if (!IsInProtectedRegion(frame.Ip, prepared.Clauses) &&
                        SignaturesMatch(frame.Method.Signature, method.Signature) &&
                        this is IGuestInvoker guestInvoker &&
                        guestInvoker.TryCreateTailCall(frame, method, args, context, out var tailRequest))
                        throw new TailCallTransfer(tailRequest!);
                    return Invoke(method, args, context);
                }

                // ---- TypedReference / varargs ----
                case ILOp.Mkrefany: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.Kind != StackKind.ByRef)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"mkrefany はマネージポインタ (&) を要求します: {SlotOps.Describe(value)}");
                    frame.Stack.Push(StackSlot.OfObject(new VmTypedReference { Slot = value, RefType = type }));
                    break;
                }
                case ILOp.Refanyval: {
                    var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmTypedReference typed || !typed.RefType.IsAssignableTo(type))
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"refanyval: TypedReference の型 {SlotOps.Describe(value)} を {type.FullName} として取り出せません。");
                    frame.Stack.Push(typed.Slot);
                    break;
                }
                case ILOp.Refanytype: {
                    var value = frame.Stack.Pop();
                    if (value.ObjectValue is not VmTypedReference typed)
                        throw new UnhandledGuestException("System.InvalidCastException",
                            $"refanytype の被演算子が TypedReference ではありません: {SlotOps.Describe(value)}");
                    frame.Stack.Push(StackSlot.OfObject(_services.Heap.Allocate(new VmTypeHandle { Target = typed.RefType })));
                    break;
                }
                case ILOp.Arglist:
                    // varargs 呼出そのものは fail-closed (C# 産の IL では生成されない)。ハンドルの一貫性のみ提供
                    frame.Stack.Push(StackSlot.OfObject(new VmArgList { Args = [] }));
                    break;

                // ---- 生メモリ系 ----
                case ILOp.Localloc: {
                    var bytes = MemoryOps.ByteCount(frame.Stack.Pop(), "localloc");
                    // VM 内表現: 実バイト列の仮想メモリブロック (ヒープ確保・実バイト数を計上) を作り、
                    // 先頭バイトへの unmanaged ポインタを返す。実 CLR と異なり初期化は 0 (安全側の
                    // 上限動作)、フレーム終了でも解放されない (GC 管理) = 脱出 stackalloc も安全側に動く
                    // 上限検査と計上はホスト側の実確保 (new byte[]) より先に行う — 巨大確保が
                    // チェック前にホストメモリを圧迫しないよう Reserve → 実確保 → Commit の順。
                    // 実確保が失敗したら予約トランザクションの Dispose が計上を巻き戻す
                    using var reservation = _services.Heap.ReserveLocalloc(bytes);
                    var memory = reservation.Commit(new VmLocallocMemory { Bytes = new byte[bytes] });
                    frame.Stack.Push(StackSlot.OfObject(new VmNativePointer { Memory = memory, ByteOffset = 0 }));
                    break;
                }
                case ILOp.Cpblk: {
                    // スタック: dst, src, size (逆順に pop)
                    var size = MemoryOps.ByteCount(frame.Stack.Pop(), "cpblk");
                    var src = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    MemoryOps.CopyMemoryBlock(dst, src, size);
                    break;
                }
                case ILOp.Initblk: {
                    var size = MemoryOps.ByteCount(frame.Stack.Pop(), "initblk");
                    var value = frame.Stack.Pop();
                    var dst = frame.Stack.Pop();
                    MemoryOps.InitMemoryBlock(dst, value, size);
                    break;
                }
                case ILOp.Sizeof:
                    // ECMA-335 III.4.14: 結果は unsigned int32 として積む (C# の sizeof(T) の結果型は int)
                    frame.Stack.Push(StackSlot.OfInt32(
                        MemoryOps.SizeOfType(objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens))));
                    break;

                // ---- M5 以降の命令 ----
                case ILOp.Ldtoken: {
                    // ldtoken Field は FieldRVA 初期データのハンドル (RuntimeHelpers::InitializeArray 用)、
                // Type は typeof() 用の RuntimeTypeHandle、Method は MethodBase::GetMethodFromHandle 用
                    if (frame.Method.DynamicTokens?.TryGetValue(unchecked((uint)instruction.IntOperand), out var dynamicReference) == true) {
                        switch (dynamicReference) {
                            case VmType dynamicType:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmTypeHandle { Target = dynamicType })));
                                break;
                            case VmMethod dynamicMethod:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmMethodHandle { Target = dynamicMethod })));
                                break;
                            case VmField dynamicField:
                                frame.Stack.Push(StackSlot.OfObject(
                                    _services.Heap.Allocate(new VmFieldHandle { Target = dynamicField })));
                                break;
                            default:
                                throw new NotSupportedException("動的 ldtoken の参照種別は未対応です。");
                        }
                        break;
                    }
                    var tokenTable = (TableKind)((uint)instruction.IntOperand >> 24);
                    var tokenRid = (int)((uint)instruction.IntOperand & 0xFFFFFF);
                    switch (tokenTable) {
                        case TableKind.Field: {
                            var rva = loader.Image.GetFieldRva(tokenRid);
                            if (rva == 0)
                                throw new BadImageFormatException($"Field rid {tokenRid} に FieldRVA エントリがありません。");
                            var field = objects.ResolveFieldToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                            var fieldType = field.FieldType
                                ?? throw new BadImageFormatException($"FieldRVA {field} の型を解決できません。");
                            var fieldSize = MemoryOps.SizeOfType(fieldType);
                            var imageData = loader.Image.GetRvaDataToEnd(rva);
                            if (fieldSize > imageData.Length)
                                throw new BadImageFormatException(
                                    $"FieldRVA {field} のデータ長 {imageData.Length} が型サイズ {fieldSize} 未満です。");
                            var handle = _services.Heap.Allocate(new VmFieldRvaData {
                                Data = imageData[..fieldSize].ToArray(),
                            });
                            frame.Stack.Push(StackSlot.OfObject(handle));
                            break;
                        }
                        case TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec: {
                            var type = objects.ResolveTypeToken(instruction.IntOperand, frame.Context, frame.Method.DynamicTokens);
                            frame.Stack.Push(StackSlot.OfObject(
                                _services.Heap.Allocate(new VmTypeHandle { Target = type })));
                            break;
                        }
                        case TableKind.MethodDef: {
                            var method = loader.GetMethodByToken((uint)instruction.IntOperand)
                                ?? throw new BadImageFormatException($"MethodDef rid {tokenRid} を解決できません。");
                            frame.Stack.Push(StackSlot.OfObject(
                                _services.Heap.Allocate(new VmMethodHandle { Target = method })));
                            break;
                        }
                        default:
                            throw new NotSupportedException(
                                $"ldtoken は Field/Type/Method トークンのみ対応しています (要求: {tokenTable})。");
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"IL 命令 {IlOpcodeTable.Get(instruction.Op)?.Name ?? instruction.Op.ToString()} は未対応です (ジェネリック/JIT は今後のフェーズで実装)。");
            }
            if (volatileAccess)
                Thread.MemoryBarrier();
            frame.Ip++;
        }
    }

    private static void EnsureFieldWritable(VmField field, VmMethod method) {
        if (!field.IsInitOnly)
            return;
        var allowed = field.IsStatic ? method.Name == ".cctor" : method.Name == ".ctor";
        if (!allowed)
            throw new UnhandledGuestException("System.FieldAccessException",
                $"readonly フィールド {field} はコンストラクター外から書き込めません。");
    }

    private static bool IsPrefix(ILOp op) =>
        op is ILOp.Unaligned or ILOp.Volatile or ILOp.Tail or ILOp.Constrained or ILOp.Readonly;

    private static bool IsInProtectedRegion(int instructionIndex, PreparedClause[]? clauses) =>
        clauses?.Any(clause =>
            instructionIndex >= clause.TryStart && instructionIndex < clause.TryEnd ||
            instructionIndex >= clause.HandlerStart && instructionIndex < clause.HandlerEnd ||
            clause.FilterStart >= 0 && instructionIndex >= clause.FilterStart &&
                instructionIndex < clause.HandlerStart) == true;

    private static bool SignaturesMatch(MethodSignature left, MethodSignature right) =>
        left.HasThis == right.HasThis && left.IsVarArg == right.IsVarArg &&
        left.GenericParamCount == right.GenericParamCount &&
        SignatureTypesMatch(left.ReturnType, right.ReturnType) &&
        left.ParamTypes.Length == right.ParamTypes.Length &&
        left.ParamTypes.Zip(right.ParamTypes).All(pair => SignatureTypesMatch(pair.First, pair.Second));

    private static bool SignatureTypesMatch(SigType left, SigType right) =>
        left.Kind == right.Kind && left.Token == right.Token && left.VarNumber == right.VarNumber &&
        left.Rank == right.Rank &&
        (left.Inner is null ? right.Inner is null : right.Inner is not null && SignatureTypesMatch(left.Inner, right.Inner)) &&
        (left.Args is null ? right.Args is null : right.Args is not null && left.Args.Length == right.Args.Length &&
            left.Args.Zip(right.Args).All(pair => SignatureTypesMatch(pair.First, pair.Second)));

    private static bool IsVolatileMemoryAccess(ILOp op) => op is
        ILOp.Ldfld or ILOp.Ldsfld or ILOp.Stfld or ILOp.Stsfld or
        ILOp.Ldobj or ILOp.Stobj or ILOp.Cpblk or ILOp.Initblk or
        ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2 or
        ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I or
        ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref or
        ILOp.Stind_Ref or ILOp.Stind_I1 or ILOp.Stind_I2 or ILOp.Stind_I4 or
        ILOp.Stind_I8 or ILOp.Stind_I or ILOp.Stind_R4 or ILOp.Stind_R8;

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
