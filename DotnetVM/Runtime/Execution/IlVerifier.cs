using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>IL 検証で使う、実行スタック上の型の粗い抽象化。</summary>
internal enum IlAbstractType : byte {
    Unknown,
    Int32,
    Int64,
    NativeInt,
    Float,
    Object,
    ByRef,
    TypedByRef,
    ValueType,
}

/// <summary>
/// デコード済み IL の first-phase verifier。
/// 完全な CLR 型安全性を再実装するのではなく、インタプリタのスタック操作が
/// ホスト例外へ落ちる形を実行前に拒否する。解決できない外部型は Unknown に倒す。
/// </summary>
internal static class IlVerifier {
    public static void Verify(TypeLoader loader, VmMethod method, DecodedInstruction[] code,
        SigType[] localTypes, PreparedClause[]? clauses) {
        try {
            new Verifier(loader, method, code, localTypes, clauses).Run();
        } catch (BadImageFormatException) {
            throw;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            // metadata の遅延解決が投げる host exception を verifier の境界から漏らさない。
            throw new BadImageFormatException(
                $"メソッド {method} の IL 検証に失敗しました: {ex.Message}", ex);
        }
    }

    private sealed class Verifier {
        private readonly TypeLoader _loader;
        private readonly VmMethod _method;
        private readonly DecodedInstruction[] _code;
        private readonly SigType[] _localTypes;
        private readonly PreparedClause[]? _clauses;
        private readonly Dictionary<int, int> _offsetToIndex;
        private readonly Dictionary<int, AbstractState> _states = [];
        private readonly Queue<int> _work = [];
        private readonly int _maxStack;
        private readonly IlAbstractType[] _argumentTypes;
        private readonly IlAbstractType[] _localAbstractTypes;

        public Verifier(TypeLoader loader, VmMethod method, DecodedInstruction[] code,
            SigType[] localTypes, PreparedClause[]? clauses) {
            _loader = loader;
            _method = method;
            _code = code;
            _localTypes = localTypes;
            _clauses = clauses;
            _maxStack = method.Body?.MaxStack ?? 0;
            _offsetToIndex = new Dictionary<int, int>(code.Length * 2);
            for (var i = 0; i < code.Length; i++)
                _offsetToIndex[code[i].Offset] = i;

            _argumentTypes = BuildArgumentTypes();
            _localAbstractTypes = localTypes.Select(TypeOf).ToArray();
        }

        public void Run() {
            if (_code.Length == 0)
                Fail("IL 本体が空です。");
            if (_maxStack < 0)
                Fail("maxstack が負です。");

            ValidateLocalSignatureToken();
            ValidateExceptionRegions();
            ValidateStaticInstructions();

            Enqueue(0, new AbstractState(_argumentTypes, _localAbstractTypes));
            AddExceptionEntries();
            while (_work.Count > 0) {
                var index = _work.Dequeue();
                Analyze(index, _states[index]);
            }
        }

        private IlAbstractType[] BuildArgumentTypes() {
            var result = new IlAbstractType[_method.Signature.ParamTypes.Length + (_method.Signature.HasThis ? 1 : 0)];
            var offset = 0;
            if (_method.Signature.HasThis) {
                result[0] = TypeOf(_method.DeclaringType);
                if (_method.DeclaringType.IsValueType)
                    result[0] = IlAbstractType.ByRef;
                offset = 1;
            }
            for (var i = 0; i < _method.Signature.ParamTypes.Length; i++)
                result[offset + i] = TypeOf(_method.Signature.ParamTypes[i]);
            return result;
        }

        private void ValidateLocalSignatureToken() {
            var body = _method.Body!;
            if (body.LocalVarSigToken == 0)
                return;
            var token = unchecked((uint)body.LocalVarSigToken);
            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            ValidateRid(table, rid, "ローカル変数署名");
            if (table != TableKind.StandAloneSig)
                Fail($"ローカル変数署名トークン 0x{token:X8} が StandAloneSig ではありません。");
            // MethodPreparer が実際に使用した型列と同じ blob を再確認する。
            ParseLocalsBlob(_loader.Image.GetBlob(
                _loader.Image.Tables.GetRowIndex(TableKind.StandAloneSig, rid, 0)));
        }

        private void ValidateExceptionRegions() {
            if (_clauses is null)
                return;
            for (var i = 0; i < _clauses.Length; i++) {
                var clause = _clauses[i];
                if (clause.TryStart < 0 || clause.TryStart >= _code.Length ||
                    clause.TryEnd <= clause.TryStart || clause.TryEnd > _code.Length ||
                    clause.HandlerStart < 0 || clause.HandlerStart >= _code.Length ||
                    clause.HandlerEnd <= clause.HandlerStart || clause.HandlerEnd > _code.Length)
                    Fail($"EH 句 {i} の try/handler 範囲が不正です。");

                if (clause.Kind == ExceptionClauseKind.Filter) {
                    if (clause.FilterStart < 0 || clause.FilterStart >= clause.HandlerStart)
                        Fail($"EH 句 {i} のフィルタ範囲が不正です。");
                } else if (clause.FilterStart != -1) {
                    Fail($"EH 句 {i} 以外のフィルタ開始位置が設定されています。");
                }

                if (clause.Kind == ExceptionClauseKind.Catch) {
                    // A zero catch token is the valid catch-all encoding.
                    if (clause.ClassToken != 0)
                        ValidateTypeToken(clause.ClassToken, "catch 型");
                } else if (clause.ClassToken != 0) {
                    Fail($"EH 句 {i} に不要な型トークンがあります。");
                }
            }
        }

        private void ValidateStaticInstructions() {
            for (var index = 0; index < _code.Length; index++) {
                var instruction = _code[index];
                if (TryGetFixedLocalIndex(instruction.Op, out var local))
                    ValidateLocalIndex(local, instruction);
                else if (instruction.Op is ILOp.Ldloc_S or ILOp.Ldloca_S or ILOp.Stloc_S or
                         ILOp.Ldloc or ILOp.Ldloca or ILOp.Stloc)
                    ValidateLocalIndex(instruction.IntOperand, instruction);

                if (TryGetFixedArgumentIndex(instruction.Op, out var argument))
                    ValidateArgumentIndex(argument, instruction);
                else if (instruction.Op is ILOp.Ldarg_S or ILOp.Ldarga_S or ILOp.Starg_S or
                         ILOp.Ldarg or ILOp.Ldarga or ILOp.Starg)
                    ValidateArgumentIndex(instruction.IntOperand, instruction);

                switch (instruction.OperandKind) {
                    case IlOperandKind.Method:
                        _ = GetMethodShape(instruction);
                        break;
                    case IlOperandKind.Field:
                        _ = GetFieldShape(instruction);
                        break;
                    case IlOperandKind.Type:
                        _ = GetTypeKind(instruction.IntOperand, "型");
                        break;
                    case IlOperandKind.Signature:
                        _ = GetCalliShape(instruction.IntOperand);
                        break;
                    case IlOperandKind.String:
                        ValidateStringToken(instruction);
                        break;
                    case IlOperandKind.Token:
                        ValidateLdtoken(instruction);
                        break;
                }

                if (instruction.Op == ILOp.Unaligned && instruction.IntOperand is not (1 or 2 or 4))
                    FailAt(instruction, "unaligned. の値は 1、2、4 のいずれかでなければなりません。");
            }

            for (var index = 0; index < _code.Length; index++) {
                var op = _code[index].Op;
                if (!IsPrefix(op))
                    continue;
                var next = NextNonPrefix(index + 1);
                if (next < 0)
                    FailAt(_code[index], "IL prefix がメソッド末尾にあります。");
                var nextOp = _code[next].Op;
                switch (op) {
                    case ILOp.Tail when nextOp is not (ILOp.Call or ILOp.Callvirt or ILOp.Calli):
                        FailAt(_code[index], "tail. の直後は call/callvirt/calli でなければなりません。");
                        break;
                    case ILOp.Tail when next + 1 >= _code.Length || _code[next + 1].Op != ILOp.Ret:
                        FailAt(_code[index], "tail. 呼出の直後は ret でなければなりません。");
                        break;
                    case ILOp.Constrained when nextOp is not (ILOp.Call or ILOp.Callvirt):
                        FailAt(_code[index], "constrained. の直後は call または callvirt でなければなりません。");
                        break;
                    case ILOp.Readonly when nextOp != ILOp.Ldelema:
                        FailAt(_code[index], "readonly. の直後は ldelema でなければなりません。");
                        break;
                    case ILOp.Volatile when !IsMemoryAccess(nextOp):
                        FailAt(_code[index], "volatile. の直後はメモリアクセス命令でなければなりません。");
                        break;
                    case ILOp.Unaligned when !IsMemoryAccess(nextOp):
                        FailAt(_code[index], "unaligned. の直後はメモリアクセス命令でなければなりません。");
                        break;
                }
            }
        }

        private void AddExceptionEntries() {
            if (_clauses is null)
                return;
            foreach (var clause in _clauses) {
                var handlerState = new AbstractState(_argumentTypes, _localAbstractTypes);
                if (clause.Kind is ExceptionClauseKind.Catch or ExceptionClauseKind.Filter)
                    handlerState.Stack.Add(IlAbstractType.Object);
                Enqueue(clause.Kind == ExceptionClauseKind.Filter ? clause.FilterStart : clause.HandlerStart,
                    handlerState);
            }
        }

        private void Analyze(int index, AbstractState incoming) {
            if ((uint)index >= (uint)_code.Length)
                Fail("CFG の命令インデックスが範囲外です。");
            var instruction = _code[index];
            var state = incoming.Clone();

            switch (instruction.Op) {
                case ILOp.Nop or ILOp.Break or ILOp.Unaligned or ILOp.Volatile or ILOp.Tail or
                    ILOp.Constrained or ILOp.Readonly:
                    FallThrough(index, state);
                    return;

                case ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3:
                    state.Stack.Add(_argumentTypes[instruction.Op - ILOp.Ldarg_0]);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldarg_S or ILOp.Ldarg:
                    state.Stack.Add(_argumentTypes[instruction.IntOperand]);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldarga_S or ILOp.Ldarga:
                    state.Stack.Add(IlAbstractType.ByRef);
                    FallThrough(index, state);
                    return;
                case ILOp.Starg_S or ILOp.Starg:
                    StoreArgument(state, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3:
                    state.Stack.Add(state.Locals[instruction.Op - ILOp.Ldloc_0]);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldloc_S or ILOp.Ldloc:
                    state.Stack.Add(state.Locals[instruction.IntOperand]);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldloca_S or ILOp.Ldloca:
                    state.Stack.Add(IlAbstractType.ByRef);
                    FallThrough(index, state);
                    return;
                case ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or ILOp.Stloc_3:
                    StoreLocal(state, instruction, instruction.Op - ILOp.Stloc_0);
                    FallThrough(index, state);
                    return;
                case ILOp.Stloc_S or ILOp.Stloc:
                    StoreLocal(state, instruction, instruction.IntOperand);
                    FallThrough(index, state);
                    return;

                case ILOp.Ldnull or ILOp.Ldstr:
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or ILOp.Ldc_I4_2 or
                    ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or ILOp.Ldc_I4_6 or
                    ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or ILOp.Ldc_I4:
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldc_I8:
                    Push(state, IlAbstractType.Int64, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldc_R4 or ILOp.Ldc_R8:
                    Push(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Dup:
                    var duplicated = Pop(state, instruction);
                    Push(state, duplicated, instruction);
                    Push(state, duplicated, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Pop:
                    _ = Pop(state, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Br or ILOp.Br_S:
                    Branch(index, state, instruction.IntOperand, instruction, clearStack: false);
                    return;
                case ILOp.BrFalse or ILOp.BrFalse_S or ILOp.BrTrue or ILOp.BrTrue_S:
                    RequireBranchCondition(Pop(state, instruction), instruction);
                    Branch(index, state, instruction.IntOperand, instruction, clearStack: false);
                    FallThrough(index, state);
                    return;
                case ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S
                    or ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S or ILOp.Blt or ILOp.Blt_S
                    or ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S
                    or ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S:
                    RequireComparable(Pop(state, instruction), Pop(state, instruction), instruction);
                    Branch(index, state, instruction.IntOperand, instruction, clearStack: false);
                    FallThrough(index, state);
                    return;
                case ILOp.Switch:
                    RequireInteger(Pop(state, instruction), instruction);
                    foreach (var target in instruction.SwitchTargets ?? [])
                        Branch(index, state, target, instruction, clearStack: false);
                    FallThrough(index, state);
                    return;

                case ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un:
                    RequireComparable(Pop(state, instruction), Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un
                    or ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un
                    or ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un
                    or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un:
                    Push(state, BinaryResult(Pop(state, instruction), Pop(state, instruction), instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Neg or ILOp.Not: {
                    var value = Pop(state, instruction);
                    RequireNumeric(value, instruction);
                    if (instruction.Op == ILOp.Not && value == IlAbstractType.Float)
                        FailAt(instruction, "not に浮動小数型を適用しています。");
                    Push(state, value, instruction);
                    FallThrough(index, state);
                    return;
                }

                case ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_U1 or ILOp.Conv_U2 or
                    ILOp.Conv_U4 or ILOp.Conv_Ovf_I1 or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or
                    ILOp.Conv_Ovf_U2 or ILOp.Conv_Ovf_I4 or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I1_Un or
                    ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_U2_Un or
                    ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_U4_Un:
                    RequireNumeric(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Conv_I8 or ILOp.Conv_U8 or ILOp.Conv_Ovf_I8 or ILOp.Conv_Ovf_U8 or
                    ILOp.Conv_Ovf_I8_Un or ILOp.Conv_Ovf_U8_Un:
                    RequireNumeric(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Int64, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Conv_I or ILOp.Conv_U or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U or
                    ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un:
                    RequireNumericOrPointer(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_R_Un:
                    RequireNumeric(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Call or ILOp.Callvirt: {
                    var shape = GetMethodShape(instruction);
                    ApplyCall(state, shape, instruction, requireReceiver: true);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Calli: {
                    var shape = GetCalliShape(instruction.IntOperand);
                    // ECMA-335 represents a function pointer as native int.  The
                    // runtime stores its VM pointer object in an Object slot, but
                    // that implementation detail must not weaken verification.
                    RequireAssignable(Pop(state, instruction), IlAbstractType.NativeInt,
                        instruction, "calli の関数ポインタ");
                    ApplyArguments(state, shape, instruction, includeReceiver: shape.HasThis);
                    PushReturn(state, shape, instruction);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Ret:
                    ValidateReturn(state, instruction);
                    return;

                case ILOp.Jmp:
                    _ = GetMethodShape(instruction);
                    if (state.Stack.Count != 0)
                        FailAt(instruction, "jmp の実行スタックは空でなければなりません。");
                    return;

                case ILOp.Newobj: {
                    var shape = GetMethodShape(instruction);
                    ApplyArguments(state, shape, instruction, includeReceiver: false);
                    Push(state, GetNewObjectKind(instruction), instruction);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Newarr:
                    RequireInteger(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldlen:
                    RequireObjectLike(Pop(state, instruction), instruction, "ldlen の配列");
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2 or
                    ILOp.Ldelem_I4 or ILOp.Ldelem_U4:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelem_I8:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.Int64, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelem_I:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelem_R4 or ILOp.Ldelem_R8:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelem_Ref:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelem:
                    PopArrayIndex(state, instruction);
                    Push(state, GetTypeKind(instruction.IntOperand, "ldelem 型"), instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Stelem_I:
                    PopArrayValue(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stelem_I1 or ILOp.Stelem_I2 or ILOp.Stelem_I4:
                    PopArrayValue(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stelem_I8:
                    PopArrayValue(state, IlAbstractType.Int64, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stelem_R4 or ILOp.Stelem_R8:
                    PopArrayValue(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stelem_Ref:
                    PopArrayValue(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stelem:
                    PopArrayValue(state, GetTypeKind(instruction.IntOperand, "stelem 型"), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldelema:
                    PopArrayIndex(state, instruction);
                    Push(state, IlAbstractType.ByRef, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Ldfld or ILOp.Ldflda or ILOp.Stfld: {
                    var field = GetFieldShape(instruction);
                    if (instruction.Op == ILOp.Stfld) {
                        var value = Pop(state, instruction);
                        RequireAssignable(value, field.FieldType, instruction, "stfld の値");
                        RequireReceiver(Pop(state, instruction), instruction);
                    } else {
                        RequireReceiver(Pop(state, instruction), instruction);
                        Push(state, instruction.Op == ILOp.Ldflda ? IlAbstractType.ByRef : field.FieldType, instruction);
                    }
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Ldsfld or ILOp.Ldsflda or ILOp.Stsfld: {
                    var field = GetFieldShape(instruction);
                    if (instruction.Op == ILOp.Stsfld) {
                        RequireAssignable(Pop(state, instruction), field.FieldType, instruction, "stsfld の値");
                    } else {
                        Push(state, instruction.Op == ILOp.Ldsflda ? StaticFieldAddressKind(instruction) : field.FieldType,
                            instruction);
                    }
                    FallThrough(index, state);
                    return;
                }

                case ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2 or
                    ILOp.Ldind_I4 or ILOp.Ldind_U4:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldind_I8:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Int64, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldind_I:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldind_R4 or ILOp.Ldind_R8:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldind_Ref:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stind_Ref:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Object, instruction, "stind.ref の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stind_I:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.NativeInt, instruction, "stind.i の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stind_I1 or ILOp.Stind_I2 or ILOp.Stind_I4:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Int32, instruction, "stind.i4 の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stind_I8:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Int64, instruction, "stind.i8 の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stind_R4 or ILOp.Stind_R8:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Float, instruction, "stind.r の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Ldobj:
                    RequireAddress(Pop(state, instruction), instruction);
                    Push(state, GetTypeKind(instruction.IntOperand, "ldobj 型"), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Stobj:
                    RequireAssignable(Pop(state, instruction), GetTypeKind(instruction.IntOperand, "stobj 型"), instruction, "stobj の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Cpobj:
                    RequireAddress(Pop(state, instruction), instruction);
                    RequireAddress(Pop(state, instruction), instruction);
                    _ = GetTypeKind(instruction.IntOperand, "cpobj 型");
                    FallThrough(index, state);
                    return;
                case ILOp.Initobj:
                    RequireAddress(Pop(state, instruction), instruction);
                    _ = GetTypeKind(instruction.IntOperand, "initobj 型");
                    FallThrough(index, state);
                    return;

                case ILOp.Box: {
                    var type = GetTypeKind(instruction.IntOperand, "box 型");
                    var value = Pop(state, instruction);
                    if (type is IlAbstractType.Object or IlAbstractType.ByRef or IlAbstractType.NativeInt or
                        IlAbstractType.TypedByRef)
                        FailAt(instruction, "box の対象型が値型ではありません。");
                    RequireAssignable(value, type, instruction, "box の値");
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Unbox: {
                    RequireObjectLike(Pop(state, instruction), instruction, "unbox の対象");
                    var type = GetTypeKind(instruction.IntOperand, "unbox 型");
                    RequireBoxableValueType(type, instruction, "unbox");
                    Push(state, IlAbstractType.ByRef, instruction);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Unbox_Any: {
                    RequireObjectLike(Pop(state, instruction), instruction, "unbox.any の対象");
                    var type = GetTypeKind(instruction.IntOperand, "unbox.any 型");
                    RequireBoxableValueType(type, instruction, "unbox.any");
                    Push(state, type, instruction);
                    FallThrough(index, state);
                    return;
                }
                case ILOp.Castclass or ILOp.Isinst:
                    RequireObjectLike(Pop(state, instruction), instruction, instruction.Op == ILOp.Castclass ? "castclass の対象" : "isinst の対象");
                    _ = GetTypeKind(instruction.IntOperand, instruction.Op == ILOp.Castclass ? "castclass 型" : "isinst 型");
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Throw:
                    RequireObjectLike(Pop(state, instruction), instruction, "throw の対象");
                    return;
                case ILOp.Rethrow:
                    if (FindRegion(index, handlerOnly: true)?.Kind != ExceptionClauseKind.Catch)
                        FailAt(instruction, "rethrow が catch ハンドラの外で実行されています。");
                    if (state.Stack.Count != 0)
                        FailAt(instruction, "rethrow の実行スタックは空でなければなりません。");
                    return;
                case ILOp.Leave or ILOp.Leave_S:
                    state.Stack.Clear();
                    Branch(index, state, instruction.IntOperand, instruction, clearStack: false, allowRegionExit: true);
                    return;
                case ILOp.Endfinally:
                    if (FindRegion(index, handlerOnly: true)?.Kind is not (ExceptionClauseKind.Finally or ExceptionClauseKind.Fault))
                        FailAt(instruction, "endfinally が finally/fault ハンドラの外で実行されています。");
                    if (state.Stack.Count != 0)
                        FailAt(instruction, "endfinally の実行スタックは空でなければなりません。");
                    return;
                case ILOp.Endfilter:
                    if (FindRegion(index, filterOnly: true) is null)
                        FailAt(instruction, "endfilter がフィルタの外で実行されています。");
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Int32, instruction, "endfilter の判定値");
                    if (state.Stack.Count != 0)
                        FailAt(instruction, "endfilter の実行スタックが空ではありません。");
                    return;

                case ILOp.Ldftn:
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldvirtftn:
                    RequireReceiver(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Mkrefany:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.ByRef, instruction, "mkrefany の対象");
                    Push(state, IlAbstractType.TypedByRef, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Refanyval:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.TypedByRef, instruction, "refanyval の対象");
                    _ = GetTypeKind(instruction.IntOperand, "refanyval 型");
                    Push(state, IlAbstractType.ByRef, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Refanytype:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.TypedByRef, instruction, "refanytype の対象");
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Arglist:
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ldtoken:
                    // Runtime handles are VM objects at the execution boundary
                    // (GetTypeFromHandle/GetMethodFromHandle consume an object
                    // reference), even though CLR exposes the handle as a value
                    // type facade.
                    Push(state, IlAbstractType.Object, instruction);
                    FallThrough(index, state);
                    return;

                case ILOp.Localloc:
                    RequireInteger(Pop(state, instruction), instruction);
                    Push(state, IlAbstractType.NativeInt, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Cpblk:
                    RequireInteger(Pop(state, instruction), instruction);
                    RequireAddress(Pop(state, instruction), instruction);
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Initblk:
                    RequireInteger(Pop(state, instruction), instruction);
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Int32, instruction, "initblk の値");
                    RequireAddress(Pop(state, instruction), instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Sizeof:
                    _ = GetTypeKind(instruction.IntOperand, "sizeof 型");
                    Push(state, IlAbstractType.Int32, instruction);
                    FallThrough(index, state);
                    return;
                case ILOp.Ckfinite:
                    RequireAssignable(Pop(state, instruction), IlAbstractType.Float, instruction, "ckfinite の値");
                    Push(state, IlAbstractType.Float, instruction);
                    FallThrough(index, state);
                    return;
                default:
                    FailAt(instruction, $"未対応の IL 命令 {instruction.Op} です。");
                    return;
            }
        }

        private void ValidateReturn(AbstractState state, DecodedInstruction instruction) {
            var expected = TypeOf(_method.Signature.ReturnType);
            if (_method.Signature.ReturnType.Kind == SigKind.Void) {
                if (state.Stack.Count != 0)
                    FailAt(instruction, "void メソッドの ret 前に実行スタックが空ではありません。");
                return;
            }
            if (state.Stack.Count != 1)
                FailAt(instruction, "ret の実行スタック高さが戻り値の要件と一致しません。");
            RequireAssignable(Pop(state, instruction), expected, instruction, "ret の戻り値");
        }

        private void ApplyCall(AbstractState state, MethodShape shape, DecodedInstruction instruction,
            bool requireReceiver) {
            ApplyArguments(state, shape, instruction, includeReceiver: requireReceiver && shape.HasThis);
            PushReturn(state, shape, instruction);
        }

        private void ApplyArguments(AbstractState state, MethodShape shape, DecodedInstruction instruction,
            bool includeReceiver) {
            for (var i = shape.ParameterTypes.Length - 1; i >= 0; i--)
                RequireAssignable(Pop(state, instruction), shape.ParameterTypes[i], instruction, "呼出引数");
            if (includeReceiver)
                RequireReceiver(Pop(state, instruction), instruction);
        }

        private void PushReturn(AbstractState state, MethodShape shape, DecodedInstruction instruction) {
            if (!shape.ReturnIsVoid)
                Push(state, shape.ReturnType, instruction);
        }

        private void StoreArgument(AbstractState state, DecodedInstruction instruction) {
            var value = Pop(state, instruction);
            RequireAssignable(value, _argumentTypes[instruction.IntOperand], instruction, "starg の値");
            if (_argumentTypes[instruction.IntOperand] == IlAbstractType.Unknown)
                state.Arguments[instruction.IntOperand] = value;
        }

        private void StoreLocal(AbstractState state, DecodedInstruction instruction, int index) {
            var value = Pop(state, instruction);
            RequireAssignable(value, _localAbstractTypes[index], instruction, "stloc の値");
            state.Locals[index] = _localAbstractTypes[index] == IlAbstractType.Unknown
                ? value : _localAbstractTypes[index];
        }

        private void PopArrayIndex(AbstractState state, DecodedInstruction instruction) {
            RequireInteger(Pop(state, instruction), instruction);
            RequireObjectLike(Pop(state, instruction), instruction, "配列");
        }

        private void PopArrayValue(AbstractState state, IlAbstractType expected, DecodedInstruction instruction) {
            RequireAssignable(Pop(state, instruction), expected, instruction, "配列要素");
            RequireInteger(Pop(state, instruction), instruction);
            RequireObjectLike(Pop(state, instruction), instruction, "配列");
        }

        private MethodShape GetMethodShape(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (TryGetDynamicToken(token, instruction, out var dynamicReference)) {
                if (dynamicReference is not VmMethod)
                    FailAt(instruction, $"動的 method token 0x{token:X8} が VmMethod ではありません。");
                return MethodShape.From((VmMethod)dynamicReference, TypeOf);
            }

            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            switch (table) {
                case TableKind.MethodDef:
                    ValidateRid(table, rid, "メソッド");
                    return ParseMethodBlob(_loader.Image.GetMethodSignature(rid));
                case TableKind.MemberRef:
                    ValidateRid(table, rid, "MemberRef");
                    ValidateMemberRefParent(rid, instruction);
                    return ParseMethodBlob(_loader.Image.GetMemberRefSignature(rid));
                case TableKind.MethodSpec:
                    return GetMethodSpecShape(rid, instruction);
                default:
                    FailAt(instruction, $"呼出トークン 0x{token:X8} のテーブル {table} が不正です。");
                    return null!;
            }
        }

        private MethodShape GetMethodSpecShape(int rid, DecodedInstruction instruction) {
            ValidateRid(TableKind.MethodSpec, rid, "MethodSpec");
            var underlying = _loader.Image.Tables.DecodeCoded(TableKind.MethodSpec, rid, 0, CodedIndexKind.MethodDefOrRef);
            if (underlying.Table is not (TableKind.MethodDef or TableKind.MemberRef) || underlying.Rid < 1)
                FailAt(instruction, $"MethodSpec rid {rid} の参照先が不正です。");
            var instantiation = ParseMethodSpecBlob(_loader.Image.GetBlob(
                _loader.Image.Tables.GetRowIndex(TableKind.MethodSpec, rid, 1)));
            var shape = underlying.Table == TableKind.MethodDef
                ? ParseMethodBlob(_loader.Image.GetMethodSignature(underlying.Rid))
                : ParseMethodBlob(_loader.Image.GetMemberRefSignature(underlying.Rid));
            if (shape.GenericParameterCount != instantiation)
                FailAt(instruction,
                    $"MethodSpec rid {rid} の型引数数 {instantiation} が署名の要求数 {shape.GenericParameterCount} と一致しません。");
            return shape;
        }

        private MethodShape GetCalliShape(int token) {
            var table = (TableKind)((uint)token >> 24);
            var rid = (int)((uint)token & 0xFFFFFF);
            if (table != TableKind.StandAloneSig)
                Fail($"calli のオペランド 0x{unchecked((uint)token):X8} は StandAloneSig ではありません。");
            ValidateRid(table, rid, "calli 署名");
            return ParseMethodBlob(_loader.Image.GetBlob(
                _loader.Image.Tables.GetRowIndex(TableKind.StandAloneSig, rid, 0)));
        }

        private FieldShape GetFieldShape(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (TryGetDynamicToken(token, instruction, out var dynamicReference)) {
                if (dynamicReference is not VmField)
                    FailAt(instruction, $"動的 field token 0x{token:X8} が VmField ではありません。");
                return new FieldShape(TypeOf(((VmField)dynamicReference).Signature.FieldType));
            }
            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            if (table == TableKind.Field) {
                ValidateRid(table, rid, "フィールド");
                return ParseFieldBlob(_loader.Image.GetBlob(_loader.Image.Tables.GetRowIndex(table, rid, 2)));
            }
            if (table == TableKind.MemberRef) {
                ValidateRid(table, rid, "フィールド MemberRef");
                ValidateMemberRefParent(rid, instruction);
                return ParseFieldBlob(_loader.Image.GetMemberRefSignature(rid));
            }
            FailAt(instruction, $"フィールドトークン 0x{token:X8} のテーブルが不正です。");
            return null!;
        }

        private IlAbstractType GetNewObjectKind(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (TryGetDynamicToken(token, instruction, out var dynamicReference)) {
                if (dynamicReference is not VmMethod)
                    FailAt(instruction, "newobj の動的 token が VmMethod ではありません。");
                var method = (VmMethod)dynamicReference;
                if (method.Name != ".ctor")
                    FailAt(instruction, "newobj の動的メソッドは .ctor でなければなりません。");
                return TypeOf(method.DeclaringType);
            }

            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            if (table == TableKind.MethodDef) {
                ValidateRid(table, rid, "newobj メソッド");
                var method = _loader.GetMethodByToken(token);
                if (method is not null && method.Name != ".ctor")
                    FailAt(instruction, "newobj のメソッドは .ctor でなければなりません。");
                return method is null ? IlAbstractType.Unknown : TypeOf(method.DeclaringType);
            }
            if (table == TableKind.MemberRef) {
                ValidateRid(table, rid, "newobj MemberRef");
                ValidateMemberRefParent(rid, instruction);
                if (_loader.GetMemberRefName(rid) != ".ctor")
                    FailAt(instruction, "newobj の MemberRef は .ctor でなければなりません。");
                var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                return parent.Table is TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec
                    ? GetTypeKind(unchecked((int)Token.From(parent.Table, parent.Rid).Value), "newobj の所有型")
                    : IlAbstractType.Unknown;
            }
            if (table == TableKind.MethodSpec) {
                ValidateRid(table, rid, "newobj MethodSpec");
                var underlying = _loader.Image.Tables.DecodeCoded(TableKind.MethodSpec, rid, 0, CodedIndexKind.MethodDefOrRef);
                if (underlying.Table == TableKind.MethodDef) {
                    var method = _loader.GetMethodByToken(Token.From(underlying.Table, underlying.Rid).Value);
                    if (method is not null && method.Name != ".ctor")
                        FailAt(instruction, "newobj の MethodSpec は .ctor でなければなりません。");
                    _ = GetMethodSpecShape(rid, instruction);
                    return method is null ? IlAbstractType.Unknown : TypeOf(method.DeclaringType);
                }
                _ = GetMethodSpecShape(rid, instruction);
                return IlAbstractType.Unknown;
            }
            FailAt(instruction, $"newobj トークン 0x{token:X8} のテーブルが不正です。");
            return IlAbstractType.Unknown;
        }

        private IlAbstractType StaticFieldAddressKind(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (TryGetDynamicToken(token, instruction, out _))
                return IlAbstractType.ByRef;
            return IlAbstractType.ByRef;
        }

        private IlAbstractType GetTypeKind(int tokenValue, string what) {
            var token = unchecked((uint)tokenValue);
            if (TryGetDynamicToken(token, null, out var dynamicReference)) {
                if (dynamicReference is not VmType type)
                    Fail($"動的 {what} token 0x{token:X8} が VmType ではありません。");
                return TypeOf(dynamicReference as VmType ?? throw new InvalidOperationException());
            }
            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            ValidateRid(table, rid, what);
            if (table == TableKind.TypeSpec)
                return ParseTypeSpecBlob(_loader.Image.GetBlob(_loader.Image.Tables.GetRowIndex(table, rid, 0)));
            try {
                return TypeOf(_loader.ResolveToken(new SigType(SigKind.TypeToken, Token: token)));
            } catch (BadImageFormatException) {
                throw;
            } catch (Exception) {
                return IlAbstractType.Unknown;
            }
        }

        private void ValidateTypeToken(int tokenValue, string what) =>
            _ = GetTypeKind(tokenValue, what);

        private void ValidateLdtoken(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (TryGetDynamicToken(token, instruction, out var dynamicReference)) {
                if (dynamicReference is not (VmMethod or VmField or VmType))
                    FailAt(instruction, $"動的 ldtoken 0x{token:X8} の参照種別が不正です。");
                return;
            }
            var table = (TableKind)(token >> 24);
            var rid = (int)(token & 0xFFFFFF);
            switch (table) {
                case TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec:
                    ValidateTypeToken(instruction.IntOperand, "ldtoken 型");
                    return;
                case TableKind.Field:
                    ValidateRid(table, rid, "ldtoken フィールド");
                    return;
                case TableKind.MethodDef:
                    ValidateRid(table, rid, "ldtoken メソッド");
                    return;
                default:
                    FailAt(instruction, $"ldtoken のトークン 0x{token:X8} の種別が不正です。");
                    return;
            }
        }

        private void ValidateStringToken(DecodedInstruction instruction) {
            var token = unchecked((uint)instruction.IntOperand);
            if (_method.DynamicStrings?.ContainsKey(token) == true)
                return;
            if (_method.DynamicTokens?.ContainsKey(token) == true)
                FailAt(instruction, $"ldstr の動的 token 0x{token:X8} が string token ではありません。");
            if ((token >> 24) != 0x70 || (token & 0xFFFFFF) == 0)
                FailAt(instruction, $"ldstr の token 0x{token:X8} が不正です。");
            _ = _loader.Image.GetUserString((int)(token & 0xFFFFFF));
        }

        private bool TryGetDynamicToken(uint token, DecodedInstruction? instruction, out object reference) {
            if (_method.DynamicTokens?.TryGetValue(token, out reference!) == true)
                return true;
            reference = null!;
            return false;
        }

        private void ValidateMemberRefParent(int rid, DecodedInstruction instruction) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table is not (TableKind.TypeDef or TableKind.TypeRef or TableKind.ModuleRef or
                                     TableKind.MethodDef or TableKind.TypeSpec) || parent.Rid < 1)
                FailAt(instruction, $"MemberRef rid {rid} の親が不正です。");
            if (parent.Table != TableKind.ModuleRef)
                ValidateRid(parent.Table, parent.Rid, "MemberRef 親");
        }

        private void ValidateRid(TableKind table, int rid, string what) {
            if (rid < 1 || rid > _loader.Image.Tables.GetRowCount(table))
                Fail($"{what} token の rid {rid} が範囲外です (テーブル {table})。");
        }

        private void Enqueue(int index, AbstractState state) {
            if ((uint)index >= (uint)_code.Length)
                Fail("CFG の分岐先がメソッド本体の外です。");
            if (!_states.TryGetValue(index, out var existing)) {
                _states[index] = state.Clone();
                _work.Enqueue(index);
                return;
            }
            var changed = Merge(existing, state, _code[index]);
            if (changed)
                _work.Enqueue(index);
        }

        private bool Merge(AbstractState target, AbstractState incoming, DecodedInstruction at) {
            if (target.Stack.Count != incoming.Stack.Count)
                FailAt(at, "CFG merge 時の評価スタック高さが一致しません。");
            var changed = false;
            for (var i = 0; i < target.Stack.Count; i++)
                changed |= MergeSlot(target.Stack, i, incoming.Stack[i], at);
            for (var i = 0; i < target.Arguments.Length; i++)
                changed |= MergeSlot(target.Arguments, i, incoming.Arguments[i], at);
            for (var i = 0; i < target.Locals.Length; i++)
                changed |= MergeSlot(target.Locals, i, incoming.Locals[i], at);
            return changed;
        }

        private bool MergeSlot(IList<IlAbstractType> target, int index, IlAbstractType incoming, DecodedInstruction at) {
            var current = target[index];
            if (current == incoming || current == IlAbstractType.Unknown || incoming == IlAbstractType.Unknown) {
                if (current != IlAbstractType.Unknown && incoming == IlAbstractType.Unknown) {
                    target[index] = IlAbstractType.Unknown;
                    return true;
                }
                return false;
            }
            FailAt(at, $"CFG merge 時の型が一致しません ({current} と {incoming})。");
            return false;
        }

        private void FallThrough(int index, AbstractState state) {
            var next = index + 1;
            var region = FindRegion(index);
            if (region is not null && next >= region.End)
                FailAt(_code[index], "EH handler/filter が終端命令なしで範囲外へ fall-through しています。");
            if (next >= _code.Length)
                FailAt(_code[index], "終端命令なしでメソッド本体の末尾へ到達します。");
            Enqueue(next, state);
        }

        private void Branch(int index, AbstractState state, int targetOffset, DecodedInstruction instruction,
            bool clearStack, bool allowRegionExit = false) {
            if (!_offsetToIndex.TryGetValue(targetOffset, out var target))
                FailAt(instruction, $"分岐先 IL_{targetOffset:X4} が命令境界上にありません。");
            if (clearStack)
                state.Stack.Clear();
            var sourceRegion = FindRegion(index);
            var targetRegion = FindRegion(target);
            if (allowRegionExit && targetRegion?.Kind is not null)
                FailAt(instruction, "leave が handler/filter 内へ分岐しています。");
            if (!allowRegionExit && sourceRegion is not null && !ReferenceEquals(sourceRegion.Clause, targetRegion?.Clause))
                FailAt(instruction, "通常分岐が EH handler/filter の外へ出ています。leave を使用してください。");
            if (!allowRegionExit && sourceRegion is null && targetRegion is not null)
                FailAt(instruction, "通常分岐が EH handler/filter の入口へ直接入っています。");
            Enqueue(target, state);
        }

        private Region? FindRegion(int index, bool handlerOnly = false, bool filterOnly = false) {
            Region? found = null;
            if (_clauses is null)
                return null;
            foreach (var clause in _clauses) {
                if (!handlerOnly && !filterOnly && index >= clause.TryStart && index < clause.TryEnd)
                    found = PreferInner(found, new Region(clause, clause.TryStart, clause.TryEnd, null));
                if (!filterOnly && index >= clause.HandlerStart && index < clause.HandlerEnd)
                    found = PreferInner(found, new Region(clause, clause.HandlerStart, clause.HandlerEnd, clause.Kind));
                if (!handlerOnly && clause.FilterStart >= 0 && index >= clause.FilterStart && index < clause.HandlerStart)
                    found = PreferInner(found, new Region(clause, clause.FilterStart, clause.HandlerStart, ExceptionClauseKind.Filter));
            }
            return found;

            static Region PreferInner(Region? current, Region candidate) =>
                current is null || candidate.End - candidate.Start <= current.End - current.Start ? candidate : current;
        }

        private void RequireBranchCondition(IlAbstractType type, DecodedInstruction instruction) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.Int32 or IlAbstractType.Int64 or
                IlAbstractType.NativeInt or IlAbstractType.Float or IlAbstractType.Object or IlAbstractType.ByRef))
                FailAt(instruction, $"条件分岐に使えない型 {type} です。");
        }

        private void RequireComparable(IlAbstractType left, IlAbstractType right, DecodedInstruction instruction) {
            if (left == IlAbstractType.Unknown || right == IlAbstractType.Unknown)
                return;
            if (left == IlAbstractType.ValueType || right == IlAbstractType.ValueType)
                return;
            var leftReference = left is IlAbstractType.Object or IlAbstractType.ByRef;
            var rightReference = right is IlAbstractType.Object or IlAbstractType.ByRef;
            if (leftReference || rightReference) {
                if (leftReference && rightReference)
                    return;
                FailAt(instruction, $"比較対象の型 {left} と {right} が一致しません。");
            }
            if (left is (IlAbstractType.Int32 or IlAbstractType.Int64 or IlAbstractType.NativeInt or IlAbstractType.Float or IlAbstractType.ValueType) &&
                right is (IlAbstractType.Int32 or IlAbstractType.Int64 or IlAbstractType.NativeInt or IlAbstractType.Float or IlAbstractType.ValueType))
                return;
            FailAt(instruction, $"比較できない型 {left} と {right} です。");
        }

        private IlAbstractType BinaryResult(IlAbstractType right, IlAbstractType left, DecodedInstruction instruction) {
            if (right == IlAbstractType.Unknown || left == IlAbstractType.Unknown)
                return IlAbstractType.Unknown;
            if (right == IlAbstractType.ByRef || left == IlAbstractType.ByRef) {
                if (instruction.Op is ILOp.Add or ILOp.Sub &&
                    (right is IlAbstractType.Int32 or IlAbstractType.Int64 or IlAbstractType.NativeInt ||
                     left is IlAbstractType.Int32 or IlAbstractType.Int64 or IlAbstractType.NativeInt))
                    return IlAbstractType.ByRef;
                FailAt(instruction, $"算術に使えない型 {left} と {right} です。");
            }
            RequireNumeric(right, instruction);
            RequireNumeric(left, instruction);
            if (right == IlAbstractType.Float || left == IlAbstractType.Float)
                return IlAbstractType.Float;
            if (right == IlAbstractType.NativeInt || left == IlAbstractType.NativeInt)
                return IlAbstractType.NativeInt;
            return right == IlAbstractType.Int64 || left == IlAbstractType.Int64
                ? IlAbstractType.Int64 : IlAbstractType.Int32;
        }

        private void RequireNumeric(IlAbstractType type, DecodedInstruction instruction) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.Int32 or IlAbstractType.Int64 or
                IlAbstractType.NativeInt or IlAbstractType.Float))
                FailAt(instruction, $"数値演算に使えない型 {type} です。");
        }

        private void RequireNumericOrPointer(IlAbstractType type, DecodedInstruction instruction) {
            if (type is IlAbstractType.ByRef)
                return;
            RequireNumeric(type, instruction);
        }

        private void RequireInteger(IlAbstractType type, DecodedInstruction instruction) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.Int32 or IlAbstractType.Int64 or IlAbstractType.NativeInt))
                FailAt(instruction, $"整数を要求する命令に型 {type} を渡しています。");
        }

        private void RequireObjectLike(IlAbstractType type, DecodedInstruction instruction, string what) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.Object))
                FailAt(instruction, $"{what} はオブジェクト参照を要求します (実際: {type})。");
        }

        private void RequireReceiver(IlAbstractType type, DecodedInstruction instruction) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.Object or IlAbstractType.ByRef or IlAbstractType.ValueType))
                FailAt(instruction, $"インスタンス呼出のレシーバ型 {type} が不正です。");
        }

        private void RequireAddress(IlAbstractType type, DecodedInstruction instruction) {
            if (type is not (IlAbstractType.Unknown or IlAbstractType.ByRef or IlAbstractType.NativeInt or IlAbstractType.Object))
                FailAt(instruction, $"メモリアドレスに使えない型 {type} です。");
        }

        private void RequireBoxableValueType(IlAbstractType type, DecodedInstruction instruction, string operation) {
            if (type is IlAbstractType.Object or IlAbstractType.ByRef or IlAbstractType.NativeInt or
                IlAbstractType.TypedByRef)
                FailAt(instruction, $"{operation} の対象型が値型ではありません。");
        }

        private void RequireAssignable(IlAbstractType actual, IlAbstractType expected,
            DecodedInstruction instruction, string what) {
            if (actual == IlAbstractType.Unknown || expected == IlAbstractType.Unknown || actual == expected)
                return;
            if (expected == IlAbstractType.ByRef)
                if (actual == IlAbstractType.NativeInt)
                    return;
                else
                    FailAt(instruction, $"{what} の型 {actual} を ByRef に代入できません。");
            if (expected == IlAbstractType.NativeInt && actual == IlAbstractType.ByRef)
                return;
            // RuntimeHandle and a few CoreLib value-type facades are represented
            // by VM objects at the execution boundary. Keep the static value-type
            // check while accepting that well-defined representation.
            if (expected == IlAbstractType.ValueType && actual == IlAbstractType.Object)
                return;
            if (expected == IlAbstractType.ValueType && actual is IlAbstractType.Int32 or IlAbstractType.Int64 or
                IlAbstractType.NativeInt or IlAbstractType.Float or IlAbstractType.ValueType)
                return;
            FailAt(instruction, $"{what} の型 {actual} を {expected} に代入できません。");
        }

        private IlAbstractType Pop(AbstractState state, DecodedInstruction instruction) {
            if (state.Stack.Count == 0)
                FailAt(instruction, "評価スタックが underflow します。");
            var index = state.Stack.Count - 1;
            var result = state.Stack[index];
            state.Stack.RemoveAt(index);
            return result;
        }

        private void Push(AbstractState state, IlAbstractType type, DecodedInstruction instruction) {
            if (state.Stack.Count >= _maxStack)
                FailAt(instruction, $"評価スタック高さが maxstack {_maxStack} を超えます。");
            state.Stack.Add(type);
        }

        private void ValidateLocalIndex(int index, DecodedInstruction instruction) {
            if ((uint)index >= (uint)_localTypes.Length)
                FailAt(instruction, $"ローカル変数インデックス {index} が範囲外です (ローカル数 {_localTypes.Length})。");
        }

        private void ValidateArgumentIndex(int index, DecodedInstruction instruction) {
            if ((uint)index >= (uint)_argumentTypes.Length)
                FailAt(instruction, $"引数インデックス {index} が範囲外です (引数数 {_argumentTypes.Length})。");
        }

        private IlAbstractType TypeOf(SigType type) => type.Kind switch {
            SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1 or SigKind.I2 or SigKind.U2 or
                SigKind.I4 or SigKind.U4 => IlAbstractType.Int32,
            SigKind.I8 or SigKind.U8 => IlAbstractType.Int64,
            SigKind.R4 or SigKind.R8 => IlAbstractType.Float,
            SigKind.I or SigKind.U => IlAbstractType.NativeInt,
            SigKind.String or SigKind.Object => IlAbstractType.Object,
            SigKind.ByRef => IlAbstractType.ByRef,
            SigKind.Pointer => IlAbstractType.NativeInt,
            SigKind.SzArray or SigKind.Array => IlAbstractType.Object,
            SigKind.TypedByRef => IlAbstractType.TypedByRef,
            SigKind.GenericVar or SigKind.GenericMethodVar => IlAbstractType.Unknown,
            SigKind.TypeToken or SigKind.GenericInst => ResolveSignatureType(type),
            _ => IlAbstractType.Unknown,
        };

        private IlAbstractType ResolveSignatureType(SigType type) {
            try {
                return TypeOf(_loader.ResolveToken(type));
            } catch (BadImageFormatException) {
                throw;
            } catch (Exception) {
                return IlAbstractType.Unknown;
            }
        }

        private IlAbstractType TypeOf(VmType type) {
            if (type is VmByRefType)
                return IlAbstractType.ByRef;
            return type.FullName switch {
                "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or
                "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => IlAbstractType.Int32,
                "System.Int64" or "System.UInt64" => IlAbstractType.Int64,
                "System.Single" or "System.Double" => IlAbstractType.Float,
                "System.IntPtr" or "System.UIntPtr" => IlAbstractType.NativeInt,
                "System.TypedReference" => IlAbstractType.TypedByRef,
                "System.Enum" or "System.ValueType" => IlAbstractType.Object,
                _ when type is VmGenericParameterType => IlAbstractType.Unknown,
                _ when type.IsEnum => EnumUnderlyingType(type),
                _ when type.IsValueType => IlAbstractType.ValueType,
                _ => IlAbstractType.Object,
            };
        }

        private IlAbstractType EnumUnderlyingType(VmType type) {
            if (type is VmClassType enumType &&
                enumType.Fields.FirstOrDefault(static field => field.Name == "value__")?.FieldType is { } underlying)
                return TypeOf(underlying);
            return IlAbstractType.Int32;
        }

        private static bool TryGetFixedLocalIndex(ILOp op, out int index) {
            var value = (ushort)op;
            if (value is >= (ushort)ILOp.Ldloc_0 and <= (ushort)ILOp.Ldloc_3) {
                index = value - (ushort)ILOp.Ldloc_0;
                return true;
            }
            if (value is >= (ushort)ILOp.Stloc_0 and <= (ushort)ILOp.Stloc_3) {
                index = value - (ushort)ILOp.Stloc_0;
                return true;
            }
            index = 0;
            return false;
        }

        private static bool TryGetFixedArgumentIndex(ILOp op, out int index) {
            var value = (ushort)op;
            if (value is >= (ushort)ILOp.Ldarg_0 and <= (ushort)ILOp.Ldarg_3) {
                index = value - (ushort)ILOp.Ldarg_0;
                return true;
            }
            index = 0;
            return false;
        }

        private static bool IsPrefix(ILOp op) => op is ILOp.Unaligned or ILOp.Volatile or ILOp.Tail or
            ILOp.Constrained or ILOp.Readonly;

        private int NextNonPrefix(int index) {
            while (index < _code.Length && IsPrefix(_code[index].Op))
                index++;
            return index < _code.Length ? index : -1;
        }

        private static bool IsMemoryAccess(ILOp op) => op is
            ILOp.Ldfld or ILOp.Ldsfld or ILOp.Stfld or ILOp.Stsfld or ILOp.Ldflda or ILOp.Ldsflda or
            ILOp.Ldobj or ILOp.Stobj or ILOp.Cpobj or ILOp.Initobj or ILOp.Cpblk or ILOp.Initblk or
            ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or ILOp.Ldind_U2 or ILOp.Ldind_I4 or
            ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I or ILOp.Ldind_R4 or ILOp.Ldind_R8 or
            ILOp.Ldind_Ref or ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or ILOp.Stind_I2 or
            ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8;

        private void FailAt(DecodedInstruction instruction, string message) =>
            throw new BadImageFormatException(
                $"メソッド {_method} の IL_{instruction.Offset:X4}: {message}");

        private void Fail(string message) =>
            throw new BadImageFormatException($"メソッド {_method} の IL 検証: {message}");

        private sealed class AbstractState(IlAbstractType[] arguments, IlAbstractType[] locals) {
            public readonly List<IlAbstractType> Stack = [];
            public readonly IlAbstractType[] Arguments = arguments.ToArray();
            public readonly IlAbstractType[] Locals = locals.ToArray();

            public AbstractState Clone() {
                var copy = new AbstractState(Arguments, Locals);
                copy.Stack.AddRange(Stack);
                return copy;
            }
        }

        private sealed record Region(PreparedClause Clause, int Start, int End, ExceptionClauseKind? Kind);

        private sealed record MethodShape(bool HasThis, int GenericParameterCount, bool ReturnIsVoid,
            IlAbstractType ReturnType, IlAbstractType[] ParameterTypes) {
            public static MethodShape From(VmMethod method, Func<SigType, IlAbstractType> typeOf) =>
                new(method.Signature.HasThis, method.Signature.GenericParamCount,
                    method.Signature.ReturnType.Kind == SigKind.Void,
                    typeOf(method.Signature.ReturnType), method.Signature.ParamTypes.Select(typeOf).ToArray());
        }

        private sealed record FieldShape(IlAbstractType FieldType);

        private MethodShape ParseMethodBlob(ReadOnlySpan<byte> blob) {
            var reader = new SignatureCursor(this, blob, Fail);
            var callingConvention = reader.ReadByte();
            var baseConvention = callingConvention & 0x0F;
            if (baseConvention is not (0x00 or 0x05))
                reader.Fail($"メソッド署名の呼び出し規約 0x{callingConvention:X2} が不正です。");
            if ((callingConvention & 0x80) != 0)
                reader.Fail("メソッド署名に予約済みフラグがあります。");
            var genericCount = (callingConvention & 0x10) != 0 ? reader.ReadCount("ジェネリック引数") : 0;
            var parameterCount = reader.ReadCount("引数");
            var returnIsVoid = reader.PeekIs(0x01);
            var returnType = reader.ReadType(allowVoid: true, allowPinned: false);
            var parameters = new IlAbstractType[parameterCount];
            var sentinelSeen = false;
            for (var i = 0; i < parameterCount; i++) {
                if (reader.PeekIs(0x41)) {
                    if (baseConvention != 0x05 || sentinelSeen || i == parameterCount - 1)
                        reader.Fail("vararg 署名の sentinel が不正です。");
                    reader.ReadByte();
                    sentinelSeen = true;
                }
                parameters[i] = reader.ReadType(allowVoid: false, allowPinned: false);
            }
            reader.RequireEnd("メソッド署名");
            return new MethodShape((callingConvention & 0x20) != 0, genericCount, returnIsVoid, returnType, parameters);
        }

        private FieldShape ParseFieldBlob(ReadOnlySpan<byte> blob) {
            var reader = new SignatureCursor(this, blob, Fail);
            var convention = reader.ReadByte();
            if (convention != 0x06)
                reader.Fail($"フィールド署名の呼び出し規約 0x{convention:X2} が不正です。");
            var type = reader.ReadType(allowVoid: false, allowPinned: false);
            reader.RequireEnd("フィールド署名");
            return new FieldShape(type);
        }

        private int ParseMethodSpecBlob(ReadOnlySpan<byte> blob) {
            var reader = new SignatureCursor(this, blob, Fail);
            if (reader.ReadByte() != 0x0A)
                reader.Fail("MethodSpec Instantiation の先頭バイトが不正です。");
            var count = reader.ReadCount("MethodSpec 型引数");
            for (var i = 0; i < count; i++)
                _ = reader.ReadType(allowVoid: false, allowPinned: false);
            reader.RequireEnd("MethodSpec Instantiation");
            return count;
        }

        private void ParseLocalsBlob(ReadOnlySpan<byte> blob) {
            var reader = new SignatureCursor(this, blob, Fail);
            if (reader.ReadByte() != 0x07)
                reader.Fail("ローカル変数署名の呼び出し規約が不正です。");
            var count = reader.ReadCount("ローカル");
            for (var i = 0; i < count; i++)
                _ = reader.ReadType(allowVoid: false, allowPinned: true);
            reader.RequireEnd("ローカル変数署名");
        }

        private IlAbstractType ParseTypeSpecBlob(ReadOnlySpan<byte> blob) {
            var reader = new SignatureCursor(this, blob, Fail);
            var result = reader.ReadType(allowVoid: false, allowPinned: false);
            reader.RequireEnd("TypeSpec 署名");
            return result;
        }

        private ref struct SignatureCursor {
            private readonly ReadOnlySpan<byte> _blob;
            private readonly Action<string> _fail;
            private readonly Verifier _owner;
            private int _offset;

            public SignatureCursor(Verifier owner, ReadOnlySpan<byte> blob, Action<string> fail) {
                _owner = owner;
                _blob = blob;
                _fail = fail;
            }

            public byte ReadByte() {
                if (_offset >= _blob.Length)
                    Fail("署名が途中で終了しています。");
                return _blob[_offset++];
            }

            public bool PeekIs(byte value) => _offset < _blob.Length && _blob[_offset] == value;

            public int ReadCount(string what) {
                var value = ReadCompressed($"{what} 数");
                if (value > 1_000_000)
                    Fail($"{what} 数が大きすぎます。");
                return (int)value;
            }

            public uint ReadCompressed(string what) {
                var first = ReadByte();
                if ((first & 0x80) == 0)
                    return first;
                if ((first & 0xC0) == 0x80)
                    return (uint)((first & 0x3F) << 8 | ReadByte());
                if ((first & 0xE0) == 0xC0)
                    return (uint)((first & 0x1F) << 24 | ReadByte() << 16 | ReadByte() << 8 | ReadByte());
                Fail($"{what} の圧縮整数が不正です。");
                return 0;
            }

            public IlAbstractType ReadType(bool allowVoid, bool allowPinned) {
                var element = ReadByte();
                switch (element) {
                    case 0x01:
                        if (!allowVoid) Fail("void を型引数として使用しています。");
                        return IlAbstractType.Unknown;
                    case 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09:
                        return IlAbstractType.Int32;
                    case 0x0A or 0x0B:
                        return IlAbstractType.Int64;
                    case 0x0C or 0x0D:
                        return IlAbstractType.Float;
                    case 0x0E or 0x1C:
                        return IlAbstractType.Object;
                    case 0x12:
                        return _owner.GetTypeKind(unchecked((int)ReadTypeToken("class")), "class");
                    case 0x11:
                        return _owner.GetTypeKind(unchecked((int)ReadTypeToken("valuetype")), "valuetype");
                    case 0x16:
                        return IlAbstractType.TypedByRef;
                    case 0x18 or 0x19:
                        return IlAbstractType.NativeInt;
                    case 0x0F:
                        _ = ReadType(allowVoid: true, allowPinned: false);
                        return IlAbstractType.NativeInt;
                    case 0x10:
                        _ = ReadType(allowVoid: false, allowPinned: false);
                        return IlAbstractType.ByRef;
                    case 0x1D:
                        _ = ReadType(allowVoid: false, allowPinned: false);
                        return IlAbstractType.Object;
                    case 0x13 or 0x1E:
                        _ = ReadCompressed("ジェネリック変数番号");
                        return IlAbstractType.Unknown;
                    case 0x15: {
                        var kind = ReadByte();
                        if (kind is not (0x11 or 0x12))
                            Fail("GenericInst の型種別が不正です。");
                        _ = ReadTypeToken("GenericInst");
                        var count = ReadCount("GenericInst 型引数");
                        for (var i = 0; i < count; i++)
                            _ = ReadType(allowVoid: false, allowPinned: false);
                        return kind == 0x11 ? IlAbstractType.ValueType : IlAbstractType.Object;
                    }
                    case 0x14: {
                        _ = ReadType(allowVoid: false, allowPinned: false);
                        var rank = ReadCount("配列 rank");
                        if (rank == 0)
                            Fail("配列 rank が 0 です。");
                        var sizes = ReadCount("配列サイズ");
                        for (var i = 0; i < sizes; i++)
                            _ = ReadCompressed("配列サイズ値");
                        var lowerBounds = ReadCount("配列下限");
                        for (var i = 0; i < lowerBounds; i++)
                            _ = ReadCompressed("配列下限値");
                        return IlAbstractType.Object;
                    }
                    case 0x1F or 0x20:
                        _ = ReadTypeToken("カスタム修飾子");
                        return ReadType(allowVoid, allowPinned);
                    case 0x45:
                        if (!allowPinned)
                            Fail("pinned 型が許可されていない署名に含まれています。");
                        return ReadType(allowVoid: false, allowPinned: false);
                    case 0x1B:
                        ReadMethodSignature();
                        return IlAbstractType.Unknown;
                    default:
                        Fail($"未知の ELEMENT_TYPE 0x{element:X2} が署名に含まれています。");
                        return IlAbstractType.Unknown;
                }
            }

            private void ReadMethodSignature() {
                var convention = ReadByte();
                if ((convention & 0x0F) is not (0x00 or 0x05) || (convention & 0x80) != 0)
                    Fail("FNPTR の呼び出し規約が不正です。");
                if ((convention & 0x10) != 0)
                    _ = ReadCount("FNPTR ジェネリック引数");
                var count = ReadCount("FNPTR 引数");
                _ = ReadType(allowVoid: true, allowPinned: false);
                for (var i = 0; i < count; i++)
                    _ = ReadType(allowVoid: false, allowPinned: false);
            }

            private uint ReadTypeToken(string what) {
                var encoded = ReadCompressed($"{what} token");
                var table = (encoded & 0x3) switch {
                    0 => TableKind.TypeDef,
                    1 => TableKind.TypeRef,
                    2 => TableKind.TypeSpec,
                    _ => (TableKind)0xFF,
                };
                var rid = (int)(encoded >> 2);
                if (table == (TableKind)0xFF || rid < 1)
                    Fail($"{what} token のエンコーディングが不正です。");
                if (rid > _owner._loader.Image.Tables.GetRowCount(table))
                    _owner.Fail($"{what} token の rid {rid} が範囲外です。");
                return Token.From(table, rid).Value;
            }

            public void RequireEnd(string what) {
                if (_offset != _blob.Length)
                    Fail($"{what} の末尾に余分なデータがあります。");
            }

            public void Fail(string message) => _fail(message);
        }
    }
}
