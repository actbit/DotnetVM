using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// Verifies the stack height and control-flow shape of a method before it is
/// exposed to either the interpreter or the JIT.  This is intentionally a
/// height verifier rather than a complete ECMA-335 type verifier; it closes
/// the dangerous underflow/overflow and merge gaps without resolving or
/// executing guest code during preparation.
/// </summary>
internal static class IlStackVerifier {
    private readonly record struct StackEffect(int Pop, int Push, bool Terminates = false);

    public static int Verify(VmMethod method, TypeLoader loader, SigType[] localTypes,
        DecodedInstruction[] code, PreparedClause[]? clauses) {
        if (code.Length == 0)
            throw new BadImageFormatException($"メソッド {method} の IL 本体が空です。");
        if (method.Body is null)
            throw new BadImageFormatException($"メソッド {method} には検証可能な本体がありません。");
        if (method.Body.MaxStack < 0)
            throw new BadImageFormatException($"メソッド {method} の maxstack が不正です。");

        var offsetMap = new Dictionary<int, int>(code.Length * 2);
        for (var i = 0; i < code.Length; i++)
            offsetMap[code[i].Offset] = i;

        ValidateOperands(method, localTypes, code);
        ValidatePrefixes(method, code);

        var heights = new int[code.Length];
        Array.Fill(heights, -1);
        var work = new Queue<int>();
        var maximum = 0;

        void Enqueue(int index, int height) {
            if ((uint)index >= (uint)code.Length)
                throw new BadImageFormatException($"メソッド {method} の制御フローが IL 本体外へ出ています。");
            if (height < 0 || height > method.Body!.MaxStack)
                throw new BadImageFormatException(
                    $"メソッド {method} の評価スタック深さ {height} が maxstack {method.Body.MaxStack} を超えています。");
            if (heights[index] < 0) {
                heights[index] = height;
                maximum = Math.Max(maximum, height);
                work.Enqueue(index);
            } else if (heights[index] != height) {
                throw new BadImageFormatException(
                    $"メソッド {method} の IL_{code[index].Offset:X4} で評価スタックの高さが分岐間で一致しません " +
                    $"(既存 {heights[index]}, 到達 {height})。");
            }
        }

        Enqueue(0, 0);
        if (clauses is not null) {
            foreach (var clause in clauses) {
                if (clause.TryStart >= clause.TryEnd ||
                    clause.HandlerStart >= clause.HandlerEnd ||
                    (uint)clause.HandlerStart >= (uint)code.Length)
                    throw new BadImageFormatException($"メソッド {method} の EH 句の範囲が空または不正です。");

                var handlerHeight = clause.Kind is ExceptionClauseKind.Catch or ExceptionClauseKind.Filter ? 1 : 0;
                Enqueue(clause.HandlerStart, handlerHeight);
                if (clause.Kind == ExceptionClauseKind.Filter) {
                    if ((uint)clause.FilterStart >= (uint)code.Length || clause.FilterStart >= clause.HandlerStart)
                        throw new BadImageFormatException($"メソッド {method} のフィルタ範囲が不正です。");
                    Enqueue(clause.FilterStart, 1);
                }
            }
        }

        while (work.TryDequeue(out var index)) {
            var instruction = code[index];
            var height = heights[index];
            var effect = GetEffect(method, loader, instruction, height);
            if (height < effect.Pop)
                throw new BadImageFormatException(
                    $"メソッド {method} の IL_{instruction.Offset:X4} ({Name(instruction.Op)}) が評価スタックを underflow させます " +
                    $"(必要 {effect.Pop}, 到達 {height})。");

            // leave discards the caller's evaluation stack before transferring
            // control through finally clauses (the runtime does the same).
            var outgoing = instruction.Op is ILOp.Leave or ILOp.Leave_S
                ? 0
                : height - effect.Pop + effect.Push;
            if (outgoing > method.Body.MaxStack)
                throw new BadImageFormatException(
                    $"メソッド {method} の IL_{instruction.Offset:X4} ({Name(instruction.Op)}) が maxstack " +
                    $"{method.Body.MaxStack} を超えます (深さ {outgoing})。");
            maximum = Math.Max(maximum, outgoing);

            if (effect.Terminates)
                continue;

            if (instruction.Op is ILOp.Br or ILOp.Br_S or ILOp.Leave or ILOp.Leave_S)
                Enqueue(TargetIndex(instruction.IntOperand), outgoing);
            else if (IsConditionalBranch(instruction.Op)) {
                Enqueue(TargetIndex(instruction.IntOperand), outgoing);
                Enqueue(index + 1, outgoing);
            } else if (instruction.Op == ILOp.Switch) {
                foreach (var target in instruction.SwitchTargets ?? [])
                    Enqueue(TargetIndex(target), outgoing);
                Enqueue(index + 1, outgoing);
            } else {
                Enqueue(index + 1, outgoing);
            }
        }

        return maximum;

        int TargetIndex(int offset) => offsetMap.TryGetValue(offset, out var target)
            ? target
            : throw new BadImageFormatException(
                $"メソッド {method} の分岐先 IL_{offset:X4} が命令境界上にありません。");
    }

    private static void ValidateOperands(VmMethod method, SigType[] localTypes, DecodedInstruction[] code) {
        var argumentCount = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0);
        for (var i = 0; i < code.Length; i++) {
            var instruction = code[i];
            if (TryGetFixedLocalIndex(instruction.Op, out var fixedLocal))
                ValidateLocal(method, localTypes, fixedLocal, instruction);
            else if (instruction.Op is ILOp.Ldloc_S or ILOp.Ldloca_S or ILOp.Stloc_S or
                     ILOp.Ldloc or ILOp.Ldloca or ILOp.Stloc)
                ValidateLocal(method, localTypes, instruction.IntOperand, instruction);

            if (TryGetFixedArgumentIndex(instruction.Op, out var fixedArgument))
                ValidateArgument(method, argumentCount, fixedArgument, instruction);
            else if (instruction.Op is ILOp.Ldarg_S or ILOp.Ldarga_S or ILOp.Starg_S or
                     ILOp.Ldarg or ILOp.Ldarga or ILOp.Starg)
                ValidateArgument(method, argumentCount, instruction.IntOperand, instruction);
        }
    }

    private static void ValidateLocal(VmMethod method, SigType[] locals, int index, DecodedInstruction instruction) {
        if ((uint)index >= (uint)locals.Length)
            throw new BadImageFormatException(
                $"メソッド {method} のローカル番号 {index} が範囲外です (ローカル数 {locals.Length}, IL_{instruction.Offset:X4})。");
    }

    private static void ValidateArgument(VmMethod method, int argumentCount, int index, DecodedInstruction instruction) {
        if ((uint)index >= (uint)argumentCount)
            throw new BadImageFormatException(
                $"メソッド {method} の引数番号 {index} が範囲外です (引数数 {argumentCount}, IL_{instruction.Offset:X4})。");
    }

    private static void ValidatePrefixes(VmMethod method, DecodedInstruction[] code) {
        for (var i = 0; i < code.Length; i++) {
            if (!IsPrefix(code[i].Op))
                continue;
            if (code[i].Op == ILOp.Unaligned && code[i].IntOperand is not (1 or 2 or 4))
                throw InvalidIl(method, code[i], "unaligned. のアラインメントは 1, 2, 4 のいずれかでなければなりません。");

            var j = i;
            var hasConstrained = false;
            var hasTail = false;
            var hasReadonly = false;
            var hasVolatile = false;
            var hasUnaligned = false;
            while (j < code.Length && IsPrefix(code[j].Op)) {
                hasConstrained |= code[j].Op == ILOp.Constrained;
                hasTail |= code[j].Op == ILOp.Tail;
                hasReadonly |= code[j].Op == ILOp.Readonly;
                hasVolatile |= code[j].Op == ILOp.Volatile;
                hasUnaligned |= code[j].Op == ILOp.Unaligned;
                j++;
            }
            if (j == code.Length)
                throw InvalidIl(method, code[i], "命令を伴わない末尾の prefix です。");

            var consumer = code[j].Op;
            if (hasConstrained && consumer is not (ILOp.Call or ILOp.Callvirt) ||
                hasTail && consumer is not (ILOp.Call or ILOp.Callvirt or ILOp.Calli) ||
                hasReadonly && consumer != ILOp.Ldelema ||
                (hasVolatile || hasUnaligned) && !IsMemoryAccess(consumer))
                throw InvalidIl(method, code[i], $"prefix の配置または消費命令 {Name(consumer)} が不正です。");
            if (hasTail && (j + 1 >= code.Length || code[j + 1].Op != ILOp.Ret))
                throw InvalidIl(method, code[i], "tail. の直後の呼出しは ret へ続かなければなりません。");

            i = j - 1;
        }
    }

    private static StackEffect GetEffect(VmMethod method, TypeLoader loader, DecodedInstruction instruction,
        int height) {
        var op = instruction.Op;
        if (op is ILOp.Ret) {
            var expected = SlotOps.SignatureReturnsValue(method.Signature) ? 1 : 0;
            if (height != expected)
                throw InvalidIl(method, instruction,
                    $"ret のスタック形状が戻り値署名と一致しません (必要 {expected}, 到達 {height})。");
            return new StackEffect(0, 0, true);
        }
        if (op == ILOp.Rethrow) {
            if (height != 0)
                throw InvalidIl(method, instruction, "rethrow 時の評価スタックは空でなければなりません。");
            return new StackEffect(0, 0, true);
        }
        if (op == ILOp.Endfinally) {
            if (height != 0)
                throw InvalidIl(method, instruction, "endfinally 時の評価スタックは空でなければなりません。");
            return new StackEffect(0, 0, true);
        }
        if (op == ILOp.Endfilter) {
            if (height != 1)
                throw InvalidIl(method, instruction, "endfilter は判定値 1 スロットだけを要求します。");
            return new StackEffect(1, 0, true);
        }
        if (op == ILOp.Jmp) {
            var target = ResolveMethodSignature(method, loader, instruction.IntOperand);
            var expected = target.ParamTypes.Length + (target.HasThis ? 1 : 0);
            var current = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0);
            if (height != 0 || expected != current)
                throw InvalidIl(method, instruction, "jmp は空のスタックと現在メソッドと同じ引数形状を要求します。");
            return new StackEffect(0, 0, true);
        }

        return op switch {
            ILOp.Nop or ILOp.Break or ILOp.Unaligned or ILOp.Volatile or ILOp.Tail or
                ILOp.Constrained or ILOp.Readonly => new(0, 0),
            ILOp.Ldarg_0 or ILOp.Ldarg_1 or ILOp.Ldarg_2 or ILOp.Ldarg_3 or
                ILOp.Ldarg_S or ILOp.Ldarg or ILOp.Ldarga_S or ILOp.Ldarga or
                ILOp.Ldloc_0 or ILOp.Ldloc_1 or ILOp.Ldloc_2 or ILOp.Ldloc_3 or
                ILOp.Ldloc_S or ILOp.Ldloc or ILOp.Ldloca_S or ILOp.Ldloca or
                ILOp.Ldnull or ILOp.Ldc_I4_M1 or ILOp.Ldc_I4_0 or ILOp.Ldc_I4_1 or
                ILOp.Ldc_I4_2 or ILOp.Ldc_I4_3 or ILOp.Ldc_I4_4 or ILOp.Ldc_I4_5 or
                ILOp.Ldc_I4_6 or ILOp.Ldc_I4_7 or ILOp.Ldc_I4_8 or ILOp.Ldc_I4_S or
                ILOp.Ldc_I4 or ILOp.Ldc_I8 or ILOp.Ldc_R4 or ILOp.Ldc_R8 or
                ILOp.Ldstr or ILOp.Ldtoken or ILOp.Ldftn or ILOp.Arglist or ILOp.Sizeof => new(0, 1),
            ILOp.Starg_S or ILOp.Starg or ILOp.Stloc_0 or ILOp.Stloc_1 or ILOp.Stloc_2 or
                ILOp.Stloc_3 or ILOp.Stloc_S or ILOp.Stloc or ILOp.Pop => new(1, 0),
            ILOp.Dup => new(1, 2),
            ILOp.BrFalse or ILOp.BrFalse_S or ILOp.BrTrue or ILOp.BrTrue_S or ILOp.Switch => new(1, 0),
            ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
                ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S or ILOp.Blt or ILOp.Blt_S or
                ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
                ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S => new(2, 0),
            ILOp.Ceq or ILOp.Cgt or ILOp.Cgt_Un or ILOp.Clt or ILOp.Clt_Un => new(2, 1),
            ILOp.Add or ILOp.Sub or ILOp.Mul or ILOp.Div or ILOp.Div_Un or ILOp.Rem or ILOp.Rem_Un or
                ILOp.And or ILOp.Or or ILOp.Xor or ILOp.Shl or ILOp.Shr or ILOp.Shr_Un or
                ILOp.Add_Ovf or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un or
                ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un => new(2, 1),
            ILOp.Neg or ILOp.Not or ILOp.Conv_I1 or ILOp.Conv_I2 or ILOp.Conv_I4 or ILOp.Conv_I8 or
                ILOp.Conv_R4 or ILOp.Conv_R8 or ILOp.Conv_U4 or ILOp.Conv_U8 or ILOp.Conv_R_Un or
                ILOp.Conv_U2 or ILOp.Conv_U1 or ILOp.Conv_I or ILOp.Conv_U or ILOp.Conv_Ovf_I1_Un or
                ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un or ILOp.Conv_Ovf_U1_Un or
                ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un or ILOp.Conv_Ovf_I_Un or
                ILOp.Conv_Ovf_U_Un or ILOp.Conv_Ovf_I1 or ILOp.Conv_Ovf_U1 or ILOp.Conv_Ovf_I2 or
                ILOp.Conv_Ovf_U2 or ILOp.Conv_Ovf_I4 or ILOp.Conv_Ovf_U4 or ILOp.Conv_Ovf_I8 or
                ILOp.Conv_Ovf_U8 or ILOp.Conv_Ovf_I or ILOp.Conv_Ovf_U or ILOp.Ckfinite => new(1, 1),
            ILOp.Call or ILOp.Callvirt => CallEffect(ResolveMethodSignature(method, loader, instruction.IntOperand), includeFunctionPointer: false),
            ILOp.Calli => CallEffect(ResolveStandaloneSignature(method, loader, instruction.IntOperand), includeFunctionPointer: true),
            ILOp.Newobj => NewObjectEffect(ResolveMethodSignature(method, loader, instruction.IntOperand)),
            ILOp.Throw => new(1, 0, true),
            ILOp.Leave or ILOp.Leave_S or ILOp.Br or ILOp.Br_S => new(0, 0),
            ILOp.Newarr or ILOp.Ldlen or ILOp.Ldfld or ILOp.Ldflda or
                ILOp.Ldind_I1 or ILOp.Ldind_U1 or ILOp.Ldind_I2 or
                ILOp.Ldind_U2 or ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or ILOp.Ldind_I or
                ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref or ILOp.Ldobj or ILOp.Box or
                ILOp.Unbox or ILOp.Unbox_Any or ILOp.Castclass or ILOp.Isinst or ILOp.Ldvirtftn or
                ILOp.Mkrefany or ILOp.Refanyval or ILOp.Refanytype or ILOp.Localloc => new(1, 1),
            ILOp.Ldelema => new(2, 1),
            ILOp.Ldsfld or ILOp.Ldsflda => new(0, 1),
            ILOp.Ldelem_I1 or ILOp.Ldelem_U1 or ILOp.Ldelem_I2 or ILOp.Ldelem_U2 or ILOp.Ldelem_I4 or
                ILOp.Ldelem_U4 or ILOp.Ldelem_I8 or ILOp.Ldelem_I or ILOp.Ldelem_R4 or ILOp.Ldelem_R8 or
                ILOp.Ldelem_Ref or ILOp.Ldelem => new(2, 1),
            ILOp.Stfld or ILOp.Stobj or ILOp.Stind_Ref or ILOp.Stind_I or ILOp.Stind_I1 or
                ILOp.Stind_I2 or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_R4 or ILOp.Stind_R8 => new(2, 0),
            ILOp.Stsfld or ILOp.Initobj => new(1, 0),
            ILOp.Stelem_I or ILOp.Stelem_I1 or ILOp.Stelem_I2 or ILOp.Stelem_I4 or ILOp.Stelem_I8 or
                ILOp.Stelem_R4 or ILOp.Stelem_R8 or ILOp.Stelem_Ref or ILOp.Stelem => new(3, 0),
            ILOp.Cpobj => new(2, 0),
            ILOp.Cpblk or ILOp.Initblk => new(3, 0),
            _ => throw InvalidIl(method, instruction, $"未対応の IL 命令 {Name(op)} です。"),
        };
    }

    private static StackEffect CallEffect(MethodSignature signature, bool includeFunctionPointer) {
        var pop = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0) + (includeFunctionPointer ? 1 : 0);
        return new StackEffect(pop, signature.ReturnType.Kind == SigKind.Void ? 0 : 1);
    }

    private static StackEffect NewObjectEffect(MethodSignature signature) =>
        new(signature.ParamTypes.Length, 1);

    private static MethodSignature ResolveStandaloneSignature(VmMethod caller, TypeLoader loader, int token) {
        if ((TableKind)((uint)token >> 24) != TableKind.StandAloneSig)
            throw InvalidIl(caller, new DecodedInstruction(0, ILOp.Calli, IlOperandKind.Signature, 0, token, 0, 0, null),
                "calli のトークンは StandAloneSig でなければなりません。");
        try {
            var rid = (int)((uint)token & 0x00FF_FFFF);
            return SignatureDecoder.DecodeMethodSignature(loader.Image.GetBlob(
                loader.Image.Tables.GetRowIndex(TableKind.StandAloneSig, rid, 0)),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
        } catch (BadImageFormatException) {
            throw;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or
            KeyNotFoundException or NotSupportedException) {
            throw new BadImageFormatException($"calli の署名トークン 0x{token:X8} を解決できません。", ex);
        }
    }

    private static MethodSignature ResolveMethodSignature(VmMethod caller, TypeLoader loader, int token) {
        if (caller.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true) {
            if (dynamicReference is VmMethod dynamicMethod)
                return dynamicMethod.Signature;
            throw new BadImageFormatException($"呼出トークン 0x{token:X8} の動的参照がメソッドではありません。");
        }

        var table = (TableKind)((uint)token >> 24);
        var rid = (int)((uint)token & 0x00FF_FFFF);
        try {
            return table switch {
                TableKind.MethodDef => loader.GetMethodByToken((uint)token)?.Signature
                    ?? throw new BadImageFormatException($"MethodDef トークン 0x{token:X8} を解決できません。"),
                TableKind.MemberRef => SignatureDecoder.DecodeMethodSignature(loader.Image.GetMemberRefSignature(rid),
                    loader.Image.Limits?.MaxSignatureDepth ?? 64,
                    loader.Image.Limits?.MaxGenericNestingDepth ?? 64),
                TableKind.MethodSpec => ResolveMethodSpecSignature(loader, rid),
                _ => throw new BadImageFormatException($"呼出トークン 0x{token:X8} のテーブルが不正です。"),
            };
        } catch (BadImageFormatException) {
            throw;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or
            KeyNotFoundException or NotSupportedException) {
            throw new BadImageFormatException($"呼出トークン 0x{token:X8} を解決できません。", ex);
        }
    }

    private static MethodSignature ResolveMethodSpecSignature(TypeLoader loader, int rid) {
        var underlying = loader.Image.Tables.DecodeCoded(TableKind.MethodSpec, rid, 0, CodedIndexKind.MethodDefOrRef);
        var token = Token.From(underlying.Table, underlying.Rid).Value;
        return ResolveMethodSignatureFromToken(loader, (int)token);
    }

    private static MethodSignature ResolveMethodSignatureFromToken(TypeLoader loader, int token) {
        var table = (TableKind)((uint)token >> 24);
        var rid = (int)((uint)token & 0x00FF_FFFF);
        return table switch {
            TableKind.MethodDef => loader.GetMethodByToken((uint)token)?.Signature
                ?? throw new BadImageFormatException($"MethodSpec の MethodDef 0x{token:X8} を解決できません。"),
            TableKind.MemberRef => SignatureDecoder.DecodeMethodSignature(loader.Image.GetMemberRefSignature(rid),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64),
            _ => throw new BadImageFormatException("MethodSpec の参照先が MethodDef/MemberRef ではありません。"),
        };
    }

    private static bool IsConditionalBranch(ILOp op) => op is
        ILOp.BrFalse or ILOp.BrFalse_S or ILOp.BrTrue or ILOp.BrTrue_S or
        ILOp.Beq or ILOp.Beq_S or ILOp.Bne_Un or ILOp.Bne_Un_S or ILOp.Bge or ILOp.Bge_S or
        ILOp.Bgt or ILOp.Bgt_S or ILOp.Ble or ILOp.Ble_S or ILOp.Blt or ILOp.Blt_S or
        ILOp.Bge_Un or ILOp.Bge_Un_S or ILOp.Bgt_Un or ILOp.Bgt_Un_S or
        ILOp.Ble_Un or ILOp.Ble_Un_S or ILOp.Blt_Un or ILOp.Blt_Un_S;

    private static bool IsPrefix(ILOp op) => op is ILOp.Unaligned or ILOp.Volatile or ILOp.Tail or ILOp.Constrained or ILOp.Readonly;

    private static bool IsMemoryAccess(ILOp op) => op is
        ILOp.Ldfld or ILOp.Ldflda or ILOp.Ldsfld or ILOp.Ldsflda or ILOp.Stfld or ILOp.Stsfld or
        ILOp.Ldobj or ILOp.Stobj or ILOp.Cpblk or ILOp.Initblk or ILOp.Ldind_I1 or ILOp.Ldind_U1 or
        ILOp.Ldind_I2 or ILOp.Ldind_U2 or ILOp.Ldind_I4 or ILOp.Ldind_U4 or ILOp.Ldind_I8 or
        ILOp.Ldind_I or ILOp.Ldind_R4 or ILOp.Ldind_R8 or ILOp.Ldind_Ref or ILOp.Stind_Ref or
        ILOp.Stind_I1 or ILOp.Stind_I2 or ILOp.Stind_I4 or ILOp.Stind_I8 or ILOp.Stind_I or
        ILOp.Stind_R4 or ILOp.Stind_R8;

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

    private static BadImageFormatException InvalidIl(VmMethod method, DecodedInstruction instruction, string message) =>
        new($"{method} の IL_{instruction.Offset:X4}: {message}");

    private static string Name(ILOp op) => IlOpcodeTable.Get(op)?.Name ?? op.ToString();
}
