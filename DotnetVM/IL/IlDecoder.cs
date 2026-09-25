using DotnetVM.Binary;

namespace DotnetVM.IL;

/// <summary>事前デコード済みの 1 命令。オフセットはメソッド IL 本体の先頭からの相対。</summary>
public readonly record struct DecodedInstruction(
    int Offset,
    ILOp Op,
    IlOperandKind OperandKind,
    /// <summary>命令の合計バイト数 (switch は可変)。</summary>
    int Size,
    /// <summary>I4/ShortI/ShortVar/Var/Token 系のオペランド、分岐の絶対ターゲット。</summary>
    int IntOperand,
    /// <summary>I8 オペランド。</summary>
    long LongOperand,
    /// <summary>R4/R8 オペランド (R4 も double に正規化)。</summary>
    double DoubleOperand,
    /// <summary>switch の絶対ターゲット列 (Switch のみ)。</summary>
    int[]? SwitchTargets);

/// <summary>
/// IL バイト列の事前デコーダ。1 パスで命令列に変換し、分岐ターゲットを絶対オフセットに解決する。
/// インタプリタ・スタック型推論・逆アセンブラで共用する。
/// </summary>
public static class IlDecoder {
    /// <summary>IL 本体を全命令にデコードする。分岐が命令境界以外を指す場合は BadImageFormatException。</summary>
    public static DecodedInstruction[] Decode(ReadOnlySpan<byte> il) {
        try {
            var instructions = new List<DecodedInstruction>(il.Length / 3 + 1);
            var reader = new SpanReader(il);
            while (!reader.EndOfBuffer) {
                var offset = reader.Offset;
                var instruction = DecodeOne(il, reader);
                instructions.Add(instruction);
                reader.Advance(instruction.Size);
                if (reader.Offset > il.Length)
                    throw new BadImageFormatException($"IL 命令がメソッド本体を超えています (offset {offset})。");
            }

            var result = instructions.ToArray();
            ValidateBranchTargets(result, il.Length);
            return result;
        } catch (BadImageFormatException) {
            throw;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            throw new BadImageFormatException("IL の命令形式が不正です。", ex);
        }
    }

    private static DecodedInstruction DecodeOne(ReadOnlySpan<byte> il, SpanReader reader) {
        var first = reader.ReadByte();
        ushort opValue;
        if (first == 0xFE) {
            opValue = (ushort)(0xFE00 | reader.ReadByte());
        } else {
            opValue = first;
        }

        var info = IlOpcodeTable.Get((ILOp)opValue)
            ?? throw new BadImageFormatException($"未定義の IL オペコード 0x{opValue:X4} があります。");
        var op = info.Op;
        var offset = reader.Offset - (info.IsTwoByte ? 2 : 1);

        switch (info.Operand) {
            case IlOperandKind.None:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, 0, 0, 0, null);
            case IlOperandKind.ShortVar:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, reader.ReadByte(), 0, 0, null);
            case IlOperandKind.Var:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, reader.ReadUInt16(), 0, 0, null);
            case IlOperandKind.ShortI:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, (sbyte)reader.ReadByte(), 0, 0, null);
            case IlOperandKind.I4:
            case IlOperandKind.Token:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, reader.ReadInt32(), 0, 0, null);
            case IlOperandKind.I8:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, 0, reader.ReadInt64(), 0, null);
            case IlOperandKind.R4:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, 0, 0, reader.ReadSingle(), null);
            case IlOperandKind.R8:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, 0, 0, reader.ReadDouble(), null);
            case IlOperandKind.ShortBrTarget: {
                var relative = (sbyte)reader.ReadByte();
                return new DecodedInstruction(offset, op, info.Operand, info.Size,
                    offset + info.Size + relative, 0, 0, null);
            }
            case IlOperandKind.BrTarget: {
                var relative = reader.ReadInt32();
                return new DecodedInstruction(offset, op, info.Operand, info.Size,
                    offset + info.Size + relative, 0, 0, null);
            }
            case IlOperandKind.Switch: {
                var count = reader.ReadUInt32();
                var targets = new int[count];
                var baseOffset = offset + 5 + 4 * (int)count;
                for (var i = 0; i < count; i++)
                    targets[i] = baseOffset + reader.ReadInt32();
                return new DecodedInstruction(offset, op, info.Operand,
                    5 + 4 * (int)count, targets.Length, 0, 0, targets);
            }
            case IlOperandKind.Method:
            case IlOperandKind.Signature:
            case IlOperandKind.Type:
            case IlOperandKind.Field:
                return new DecodedInstruction(offset, op, info.Operand, info.Size, reader.ReadInt32(), 0, 0, null);
            case IlOperandKind.String: {
                // #US オフセットは符号なし 4 バイト
                return new DecodedInstruction(offset, op, info.Operand, info.Size,
                    (int)reader.ReadUInt32(), 0, 0, null);
            }
            default:
                throw new BadImageFormatException($"未知のオペランド形式です ({info.Operand})。");
        }
    }

    private static void ValidateBranchTargets(DecodedInstruction[] instructions, int ilLength) {
        var boundaries = new HashSet<int>(instructions.Length * 2);
        foreach (var instruction in instructions)
            boundaries.Add(instruction.Offset);

        foreach (var instruction in instructions) {
            if (instruction.SwitchTargets is { } targets) {
                foreach (var target in targets)
                    Check(target, instruction);
            } else if (instruction.OperandKind is IlOperandKind.ShortBrTarget or IlOperandKind.BrTarget) {
                Check(instruction.IntOperand, instruction);
            }
        }

        void Check(int target, DecodedInstruction from) {
            if (target < 0 || target >= ilLength || !boundaries.Contains(target))
                throw new BadImageFormatException(
                    $"分岐ターゲット IL_{target:X4} が命令境界上にありません (IL_{from.Offset:X4} から)。分岐ターゲットは命令の先頭でなければなりません。");
        }
    }
}
