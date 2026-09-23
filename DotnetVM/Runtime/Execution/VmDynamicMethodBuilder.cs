using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>Reflection.Emit の DynamicMethod を VM IL として蓄積する小さな builder。</summary>
internal sealed class VmDynamicMethodBuilder(TypeLoader loader, VmHeap heap, string name, SigType returnType,
    SigType[] parameterTypes, int maxMethodBodyBytes) {
    private readonly List<byte> _code = [];
    private readonly List<SigType> _locals = [];
    private readonly Dictionary<uint, string> _strings = [];
    private readonly Dictionary<uint, object> _references = [];
    private readonly Dictionary<int, int> _labels = [];
    private readonly List<(int Position, int Label, bool Short)> _labelFixups = [];
    private readonly List<(int Position, int Label, int BasePosition)> _switchFixups = [];
    private int _nextLabel;
    private VmMethod? _method;

    public TypeLoader Loader { get; } = loader;
    private readonly VmHeap _heap = heap;
    public string Name { get; } = name;
    public SigType ReturnType { get; } = returnType;
    public SigType[] ParameterTypes { get; } = parameterTypes;

    public static SigType SignatureType(VmType type) => type.FullName switch {
        "System.Void" => new SigType(SigKind.Void),
        "System.Boolean" => new SigType(SigKind.Boolean),
        "System.Char" => new SigType(SigKind.Char),
        "System.SByte" => new SigType(SigKind.I1),
        "System.Byte" => new SigType(SigKind.U1),
        "System.Int16" => new SigType(SigKind.I2),
        "System.UInt16" => new SigType(SigKind.U2),
        "System.Int32" => new SigType(SigKind.I4),
        "System.UInt32" => new SigType(SigKind.U4),
        "System.Int64" => new SigType(SigKind.I8),
        "System.UInt64" => new SigType(SigKind.U8),
        "System.Single" => new SigType(SigKind.R4),
        "System.Double" => new SigType(SigKind.R8),
        "System.String" => new SigType(SigKind.String),
        "System.Object" => new SigType(SigKind.Object),
        _ when type is VmArrayType array => new SigType(SigKind.SzArray, Inner: SignatureType(array.ElementType)),
        _ when type is VmByRefType byRef => new SigType(SigKind.ByRef, Inner: SignatureType(byRef.ElementType)),
        _ => throw new NotSupportedException($"DynamicMethod の型 {type.FullName} はまだ対応していません。"),
    };

    public void EmitOpcode(ushort opcode) {
        EnsureWritable();
        var info = IlOpcodeTable.Get((ILOp)opcode)
            ?? throw new UnhandledGuestException("System.ArgumentException", $"未知の OpCode: 0x{opcode:X4}");
        if (info.Operand != IlOperandKind.None)
            throw new UnhandledGuestException("System.ArgumentException", $"OpCode {info.Name} はオペランドが必要です。");
        AppendOpcode(opcode);
    }

    public int DefineLabel() {
        EnsureWritable();
        return _nextLabel++;
    }

    public int DeclareLocal(SigType type) {
        EnsureWritable();
        var index = _locals.Count;
        _locals.Add(type);
        return index;
    }

    public void MarkLabel(int label) {
        EnsureWritable();
        if (!_labels.TryAdd(label, _code.Count))
            throw new UnhandledGuestException("System.ArgumentException", "Label は一度だけ MarkLabel できます。");
    }

    public void EmitLabel(ushort opcode, int label) {
        EnsureWritable();
        var info = GetOpcode(opcode);
        var isShort = info.Operand == IlOperandKind.ShortBrTarget;
        if (!isShort && info.Operand != IlOperandKind.BrTarget)
            throw UnsupportedOperand(info);
        EnsureCapacity(OpcodeSize(opcode) + (isShort ? sizeof(sbyte) : sizeof(int)));
        AppendOpcode(opcode);
        var position = _code.Count;
        if (isShort)
            Append(0);
        else {
            Append(0);
            Append(0);
            Append(0);
            Append(0);
        }
        _labelFixups.Add((position, label, isShort));
    }

    public void EmitLabels(ushort opcode, int[] labels) {
        EnsureWritable();
        var info = GetOpcode(opcode);
        if (info.Operand != IlOperandKind.Switch)
            throw UnsupportedOperand(info);
        EnsureCapacity(OpcodeSize(opcode) + sizeof(int) + checked(labels.Length * sizeof(int)));
        AppendOpcode(opcode);
        AppendInt32(labels.Length);
        var basePosition = _code.Count + labels.Length * sizeof(int);
        foreach (var label in labels) {
            var position = _code.Count;
            AppendInt32(0);
            _switchFixups.Add((position, label, basePosition));
        }
    }

    public void EmitToken(ushort opcode, uint token) {
        EnsureWritable();
        var info = GetOpcode(opcode);
        if (info.Operand is not (IlOperandKind.Method or IlOperandKind.Field or IlOperandKind.Type or IlOperandKind.String or IlOperandKind.Token))
            throw UnsupportedOperand(info);
        EnsureCapacity(OpcodeSize(opcode) + sizeof(uint));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, token);
        Append(bytes);
    }

    public void EmitReference(ushort opcode, object reference) {
        EnsureWritable();
        var info = GetOpcode(opcode);
        if (info.Operand is not (IlOperandKind.Method or IlOperandKind.Field or IlOperandKind.Type or IlOperandKind.String or IlOperandKind.Token))
            throw UnsupportedOperand(info);
        var token = 0x0A000000u | (uint)(_references.Count + 1);
        _references[token] = reference;
        EnsureCapacity(OpcodeSize(opcode) + sizeof(uint));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, token);
        Append(bytes);
    }

    public void EmitString(ushort opcode, string value) {
        EnsureWritable();
        var info = GetOpcode(opcode);
        if (info.Operand != IlOperandKind.String)
            throw UnsupportedOperand(info);
        var token = 0x70000000u | (uint)(_strings.Count + 1);
        _heap.ChargeHostBuffer(value.Length);
        _strings[token] = value;
        EnsureCapacity(OpcodeSize(opcode) + sizeof(uint));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, token);
        Append(bytes);
    }

    public void EmitInt32(ushort opcode, int value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        switch (operand.Operand) {
            case IlOperandKind.ShortVar:
                if ((uint)value > byte.MaxValue)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "short variable operand は 0..255 の範囲です。");
                EnsureCapacity(OpcodeSize(opcode) + 1);
                AppendOpcode(opcode);
                Append((byte)value);
                break;
            case IlOperandKind.Var:
                if ((uint)value > ushort.MaxValue)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "variable operand は 0..65535 の範囲です。");
                EnsureCapacity(OpcodeSize(opcode) + sizeof(ushort));
                AppendOpcode(opcode);
                AppendUInt16((ushort)value);
                break;
            case IlOperandKind.ShortI:
                if (value is < sbyte.MinValue or > sbyte.MaxValue)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "short integer operand は -128..127 の範囲です。");
                EnsureCapacity(OpcodeSize(opcode) + 1);
                AppendOpcode(opcode);
                Append(unchecked((byte)(sbyte)value));
                break;
            case IlOperandKind.I4: {
                EnsureCapacity(OpcodeSize(opcode) + sizeof(int));
                AppendOpcode(opcode);
                Span<byte> bytes = stackalloc byte[sizeof(int)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                Append(bytes);
                break;
            }
            default:
                throw UnsupportedOperand(operand);
        }
    }

    public void EmitByte(ushort opcode, int value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand is not (IlOperandKind.ShortI or IlOperandKind.ShortVar))
            throw UnsupportedOperand(operand);
        if ((uint)value > byte.MaxValue)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "byte operand は 0..255 の範囲です。");
        EnsureCapacity(OpcodeSize(opcode) + sizeof(byte));
        AppendOpcode(opcode);
        Append((byte)value);
    }

    public void EmitSByte(ushort opcode, int value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand != IlOperandKind.ShortI)
            throw UnsupportedOperand(operand);
        if (value is < sbyte.MinValue or > sbyte.MaxValue)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "sbyte operand は -128..127 の範囲です。");
        EnsureCapacity(OpcodeSize(opcode) + sizeof(sbyte));
        AppendOpcode(opcode);
        Append(unchecked((byte)(sbyte)value));
    }

    public void EmitInt16(ushort opcode, int value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand != IlOperandKind.Var)
            throw UnsupportedOperand(operand);
        if (value is < short.MinValue or > short.MaxValue)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "short operand は -32768..32767 の範囲です。");
        EnsureCapacity(OpcodeSize(opcode) + sizeof(short));
        AppendOpcode(opcode);
        AppendUInt16(unchecked((ushort)(short)value));
    }

    public void EmitInt64(ushort opcode, long value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand != IlOperandKind.I8)
            throw UnsupportedOperand(operand);
        EnsureCapacity(OpcodeSize(opcode) + sizeof(long));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        Append(bytes);
    }

    public void EmitSingle(ushort opcode, float value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand != IlOperandKind.R4)
            throw UnsupportedOperand(operand);
        EnsureCapacity(OpcodeSize(opcode) + sizeof(float));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        Append(bytes);
    }

    public void EmitDouble(ushort opcode, double value) {
        EnsureWritable();
        var operand = GetOpcode(opcode);
        if (operand.Operand != IlOperandKind.R8)
            throw UnsupportedOperand(operand);
        EnsureCapacity(OpcodeSize(opcode) + sizeof(double));
        AppendOpcode(opcode);
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        Append(bytes);
    }

    private void Append(byte value) {
        EnsureCapacity(1);
        _heap.ChargeHostBuffer(1);
        _code.Add(value);
    }

    private void Append(ReadOnlySpan<byte> bytes) {
        EnsureCapacity(bytes.Length);
        _heap.ChargeHostBuffer(bytes.Length);
        foreach (var value in bytes)
            _code.Add(value);
    }

    private void EnsureWritable() {
        if (_method is not null)
            throw new UnhandledGuestException("System.InvalidOperationException", "DynamicMethod は既に delegate 化されています。");
    }

    private void AppendUInt16(ushort value) {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Append(bytes);
    }

    private void AppendInt32(int value) {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Append(bytes);
    }

    private static UnhandledGuestException UnsupportedOperand(IlOpcodeInfo info) =>
        new("System.NotSupportedException", $"ILGenerator.Emit の {info.Name} オペランド {info.Operand} は未対応です。");

    private static IlOpcodeInfo GetOpcode(ushort opcode) => IlOpcodeTable.Get((ILOp)opcode)
        ?? throw new UnhandledGuestException("System.ArgumentException", $"未知の OpCode: 0x{opcode:X4}");

    private static int OpcodeSize(ushort opcode) => opcode > byte.MaxValue ? 2 : 1;

    private void AppendOpcode(ushort opcode) {
        if (opcode > byte.MaxValue) {
            Append((byte)(opcode >> 8));
            Append((byte)opcode);
        } else {
            Append((byte)opcode);
        }
    }

    private void EnsureCapacity(int additional) {
        if ((long)_code.Count + additional > maxMethodBodyBytes)
            throw new OperationNotAllowedException(
                $"DynamicMethod の IL が上限 {maxMethodBodyBytes:N0} バイトを超えます。");
    }

    public VmMethod CreateMethod() {
        if (_method is not null)
            return _method;
        var code = _code.ToArray();
        foreach (var (position, label, isShort) in _labelFixups) {
            if (!_labels.TryGetValue(label, out var target))
                throw new UnhandledGuestException("System.InvalidOperationException", "DynamicMethod の Label が MarkLabel されていません。");
            var next = position + (isShort ? sizeof(sbyte) : sizeof(int));
            var delta = target - next;
            if (isShort && (delta < sbyte.MinValue || delta > sbyte.MaxValue))
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "短い分岐先が範囲外です。");
            if (isShort)
                code[position] = unchecked((byte)(sbyte)delta);
            else
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(position, sizeof(int)), delta);
        }
        foreach (var (position, label, basePosition) in _switchFixups) {
            if (!_labels.TryGetValue(label, out var target))
                throw new UnhandledGuestException("System.InvalidOperationException", "DynamicMethod の switch Label が MarkLabel されていません。");
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                code.AsSpan(position, sizeof(int)), target - basePosition);
        }
        ValidateDynamicCode(code);
        return _method = new VmMethod {
            DeclaringType = DynamicMethodType,
            MethodDefRid = 0,
            Name = Name,
            Signature = new MethodSignature(false, false, 0, ReturnType, ParameterTypes),
            Flags = 0x0010,
            ImplFlags = 0,
            Body = MethodBodyBlock.FromDynamicCode(code, _locals.ToArray()),
            DynamicStrings = _strings.ToDictionary(pair => pair.Key, pair => pair.Value),
            DynamicTokens = _references.ToDictionary(pair => pair.Key, pair => pair.Value),
            Loader = Loader,
        };
    }

    /// <summary>生成 IL を実行可能にする前に命令境界・分岐・引数/ローカル・token をまとめて検証する。</summary>
    private void ValidateDynamicCode(byte[] code) {
        var instructions = IlDecoder.Decode(code);
        foreach (var instruction in instructions) {
            if (TryGetShortLocalIndex(instruction.Op, out var fixedLocal))
                ValidateLocal(fixedLocal, instruction);
            else if (instruction.Op is ILOp.Ldloc_S or ILOp.Ldloca_S or ILOp.Stloc_S or
                     ILOp.Ldloc or ILOp.Ldloca or ILOp.Stloc)
                ValidateLocal(instruction.IntOperand, instruction);

            if (TryGetShortArgumentIndex(instruction.Op, out var fixedArgument))
                ValidateArgument(fixedArgument, instruction);
            else if (instruction.Op is ILOp.Ldarg_S or ILOp.Ldarga_S or ILOp.Starg_S or
                     ILOp.Ldarg or ILOp.Ldarga or ILOp.Starg)
                ValidateArgument(instruction.IntOperand, instruction);

            if (instruction.OperandKind is IlOperandKind.Method or IlOperandKind.Field or
                IlOperandKind.Type or IlOperandKind.String or IlOperandKind.Token)
                ValidateToken(instruction);
        }
    }

    private void ValidateToken(DecodedInstruction instruction) {
        var token = unchecked((uint)instruction.IntOperand);
        if (instruction.OperandKind == IlOperandKind.String) {
            if (!_strings.ContainsKey(token))
                throw new BadImageFormatException($"DynamicMethod の string token 0x{token:X8} が登録されていません。");
            return;
        }

        if (_references.TryGetValue(token, out var reference)) {
            var valid = instruction.OperandKind switch {
                IlOperandKind.Method => reference is VmMethod,
                IlOperandKind.Field => reference is VmField,
                IlOperandKind.Type => reference is VmType,
                IlOperandKind.Token => reference is VmMethod or VmField or VmType,
                _ => false,
            };
            if (!valid)
                throw new BadImageFormatException(
                    $"DynamicMethod の {instruction.OperandKind} token 0x{token:X8} に不正な参照型があります。");
            return;
        }

        var metadataToken = new Token(token);
        var allowedTables = instruction.OperandKind switch {
            IlOperandKind.Method => new[] { TableKind.MethodDef, TableKind.MemberRef, TableKind.MethodSpec },
            IlOperandKind.Field => new[] { TableKind.Field, TableKind.MemberRef },
            IlOperandKind.Type => new[] { TableKind.TypeDef, TableKind.TypeRef, TableKind.TypeSpec },
            IlOperandKind.Token => new[] {
                TableKind.TypeDef, TableKind.TypeRef, TableKind.TypeSpec,
                TableKind.Field, TableKind.MethodDef, TableKind.MemberRef, TableKind.MethodSpec,
            },
            _ => [],
        };
        if (metadataToken.Rid < 1 || !allowedTables.Contains(metadataToken.Table) ||
            metadataToken.Rid > Loader.Image.Tables.GetRowCount(metadataToken.Table))
            throw new BadImageFormatException(
                $"DynamicMethod の {instruction.OperandKind} token 0x{token:X8} は不正です。");
    }

    private void ValidateLocal(int index, DecodedInstruction instruction) {
        if ((uint)index >= (uint)_locals.Count)
            throw new BadImageFormatException(
                $"DynamicMethod のローカル番号 {index} が範囲外です (ローカル数 {_locals.Count}, IL_{instruction.Offset:X4})。");
    }

    private void ValidateArgument(int index, DecodedInstruction instruction) {
        if ((uint)index >= (uint)ParameterTypes.Length)
            throw new BadImageFormatException(
                $"DynamicMethod の引数番号 {index} が範囲外です (引数数 {ParameterTypes.Length}, IL_{instruction.Offset:X4})。");
    }

    private static bool TryGetShortLocalIndex(ILOp op, out int index) {
        var value = (ushort)op;
        if (value >= (ushort)ILOp.Ldloc_0 && value <= (ushort)ILOp.Ldloc_3) {
            index = value - (ushort)ILOp.Ldloc_0;
            return true;
        }
        if (value >= (ushort)ILOp.Stloc_0 && value <= (ushort)ILOp.Stloc_3) {
            index = value - (ushort)ILOp.Stloc_0;
            return true;
        }
        index = 0;
        return false;
    }

    private static bool TryGetShortArgumentIndex(ILOp op, out int index) {
        var value = (ushort)op;
        if (value >= (ushort)ILOp.Ldarg_0 && value <= (ushort)ILOp.Ldarg_3) {
            index = value - (ushort)ILOp.Ldarg_0;
            return true;
        }
        index = 0;
        return false;
    }

    private static readonly VmIntrinsicType DynamicMethodType =
        new() { Namespace = "System.Reflection.Emit", Name = "DynamicMethod", IsValue = false };
}
