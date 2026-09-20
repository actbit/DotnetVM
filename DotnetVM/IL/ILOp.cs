namespace DotnetVM.IL;

/// <summary>IL オペコード。1 バイト命令はそのままの値、2 バイト命令 (0xFE xx) は 0xFE00 | xx。</summary>
public enum ILOp : ushort {
    Nop = 0x00,
    Break = 0x01,
    Ldarg_0 = 0x02,
    Ldarg_1 = 0x03,
    Ldarg_2 = 0x04,
    Ldarg_3 = 0x05,
    Ldloc_0 = 0x06,
    Ldloc_1 = 0x07,
    Ldloc_2 = 0x08,
    Ldloc_3 = 0x09,
    Stloc_0 = 0x0A,
    Stloc_1 = 0x0B,
    Stloc_2 = 0x0C,
    Stloc_3 = 0x0D,
    Ldarg_S = 0x0E,
    Ldarga_S = 0x0F,
    Starg_S = 0x10,
    Ldloc_S = 0x11,
    Ldloca_S = 0x12,
    Stloc_S = 0x13,
    Ldnull = 0x14,
    Ldc_I4_M1 = 0x15,
    Ldc_I4_0 = 0x16,
    Ldc_I4_1 = 0x17,
    Ldc_I4_2 = 0x18,
    Ldc_I4_3 = 0x19,
    Ldc_I4_4 = 0x1A,
    Ldc_I4_5 = 0x1B,
    Ldc_I4_6 = 0x1C,
    Ldc_I4_7 = 0x1D,
    Ldc_I4_8 = 0x1E,
    Ldc_I4_S = 0x1F,
    Ldc_I4 = 0x20,
    Ldc_I8 = 0x21,
    Ldc_R4 = 0x22,
    Ldc_R8 = 0x23,
    Dup = 0x25,
    Pop = 0x26,
    Jmp = 0x27,
    Call = 0x28,
    Calli = 0x29,
    Ret = 0x2A,
    Br_S = 0x2B,
    BrFalse_S = 0x2C,
    BrTrue_S = 0x2D,
    Beq_S = 0x2E,
    Bge_S = 0x2F,
    Bgt_S = 0x30,
    Ble_S = 0x31,
    Blt_S = 0x32,
    Bne_Un_S = 0x33,
    Bge_Un_S = 0x34,
    Bgt_Un_S = 0x35,
    Ble_Un_S = 0x36,
    Blt_Un_S = 0x37,
    Br = 0x38,
    BrFalse = 0x39,
    BrTrue = 0x3A,
    Beq = 0x3B,
    Bge = 0x3C,
    Bgt = 0x3D,
    Ble = 0x3E,
    Blt = 0x3F,
    Bne_Un = 0x40,
    Bge_Un = 0x41,
    Bgt_Un = 0x42,
    Ble_Un = 0x43,
    Blt_Un = 0x44,
    Switch = 0x45,
    Ldind_I1 = 0x46,
    Ldind_U1 = 0x47,
    Ldind_I2 = 0x48,
    Ldind_U2 = 0x49,
    Ldind_I4 = 0x4A,
    Ldind_U4 = 0x4B,
    Ldind_I8 = 0x4C,
    Ldind_I = 0x4D,
    Ldind_R4 = 0x4E,
    Ldind_R8 = 0x4F,
    Ldind_Ref = 0x50,
    Stind_Ref = 0x51,
    Stind_I1 = 0x52,
    Stind_I2 = 0x53,
    Stind_I4 = 0x54,
    Stind_I8 = 0x55,
    Stind_R4 = 0x56,
    Stind_R8 = 0x57,
    Add = 0x58,
    Sub = 0x59,
    Mul = 0x5A,
    Div = 0x5B,
    Div_Un = 0x5C,
    Rem = 0x5D,
    Rem_Un = 0x5E,
    And = 0x5F,
    Or = 0x60,
    Xor = 0x61,
    Shl = 0x62,
    Shr = 0x63,
    Shr_Un = 0x64,
    Neg = 0x65,
    Not = 0x66,
    Conv_I1 = 0x67,
    Conv_I2 = 0x68,
    Conv_I4 = 0x69,
    Conv_I8 = 0x6A,
    Conv_R4 = 0x6B,
    Conv_R8 = 0x6C,
    Conv_U4 = 0x6D,
    Conv_U8 = 0x6E,
    Callvirt = 0x6F,
    Cpobj = 0x70,
    Ldobj = 0x71,
    Ldstr = 0x72,
    Newobj = 0x73,
    Castclass = 0x74,
    Isinst = 0x75,
    Conv_R_Un = 0x76,
    Unbox = 0x79,
    Throw = 0x7A,
    Ldfld = 0x7B,
    Ldflda = 0x7C,
    Stfld = 0x7D,
    Ldsfld = 0x7E,
    Ldsflda = 0x7F,
    Stsfld = 0x80,
    Stobj = 0x81,
    Conv_Ovf_I1_Un = 0x82,
    Conv_Ovf_I2_Un = 0x83,
    Conv_Ovf_I4_Un = 0x84,
    Conv_Ovf_I8_Un = 0x85,
    Conv_Ovf_U1_Un = 0x86,
    Conv_Ovf_U2_Un = 0x87,
    Conv_Ovf_U4_Un = 0x88,
    Conv_Ovf_U8_Un = 0x89,
    Conv_Ovf_I_Un = 0x8A,
    Conv_Ovf_U_Un = 0x8B,
    Box = 0x8C,
    Newarr = 0x8D,
    Ldlen = 0x8E,
    Ldelema = 0x8F,
    Ldelem_I1 = 0x90,
    Ldelem_U1 = 0x91,
    Ldelem_I2 = 0x92,
    Ldelem_U2 = 0x93,
    Ldelem_I4 = 0x94,
    Ldelem_U4 = 0x95,
    Ldelem_I8 = 0x96,
    Ldelem_I = 0x97,
    Ldelem_R4 = 0x98,
    Ldelem_R8 = 0x99,
    Ldelem_Ref = 0x9A,
    Stelem_I = 0x9B,
    Stelem_I1 = 0x9C,
    Stelem_I2 = 0x9D,
    Stelem_I4 = 0x9E,
    Stelem_I8 = 0x9F,
    Stelem_R4 = 0xA0,
    Stelem_R8 = 0xA1,
    Stelem_Ref = 0xA2,
    Ldelem = 0xA3,
    Stelem = 0xA4,
    Unbox_Any = 0xA5,
    Conv_Ovf_I1 = 0xB3,
    Conv_Ovf_U1 = 0xB4,
    Conv_Ovf_I2 = 0xB5,
    Conv_Ovf_U2 = 0xB6,
    Conv_Ovf_I4 = 0xB7,
    Conv_Ovf_U4 = 0xB8,
    Conv_Ovf_I8 = 0xB9,
    Conv_Ovf_U8 = 0xBA,
    Refanyval = 0xC2,
    Ckfinite = 0xC3,
    Mkrefany = 0xC6,
    Ldtoken = 0xD0,
    Conv_U2 = 0xD1,
    Conv_U1 = 0xD2,
    Conv_I = 0xD3,
    Conv_Ovf_I = 0xD4,
    Conv_Ovf_U = 0xD5,
    Add_Ovf = 0xD6,
    Add_Ovf_Un = 0xD7,
    Mul_Ovf = 0xD8,
    Mul_Ovf_Un = 0xD9,
    Sub_Ovf = 0xDA,
    Sub_Ovf_Un = 0xDB,
    Endfinally = 0xDC,
    Leave = 0xDD,
    Leave_S = 0xDE,
    Stind_I = 0xDF,
    Conv_U = 0xE0,

    // 2 バイト命令 (0xFE xx → 0xFE00 | xx)
    Arglist = 0xFE00,
    Ceq = 0xFE01,
    Cgt = 0xFE02,
    Cgt_Un = 0xFE03,
    Clt = 0xFE04,
    Clt_Un = 0xFE05,
    Ldftn = 0xFE06,
    Ldvirtftn = 0xFE07,
    Ldarg = 0xFE09,
    Ldarga = 0xFE0A,
    Starg = 0xFE0B,
    Ldloc = 0xFE0C,
    Ldloca = 0xFE0D,
    Stloc = 0xFE0E,
    Localloc = 0xFE0F,
    Endfilter = 0xFE11,
    Unaligned = 0xFE12,
    Volatile = 0xFE13,
    Tail = 0xFE14,
    Initobj = 0xFE15,
    Constrained = 0xFE16,
    Cpblk = 0xFE17,
    Initblk = 0xFE18,
    Rethrow = 0xFE1A,
    Sizeof = 0xFE1C,
    Refanytype = 0xFE1D,
    Readonly = 0xFE1E,
}

/// <summary>オペランドの形式 (ECMA-335 III.1.2 表 1-1)。</summary>
public enum IlOperandKind : byte {
    None,
    /// <summary>1 バイトの引数/ローカル番号。</summary>
    ShortVar,
    /// <summary>2 バイトの引数/ローカル番号。</summary>
    Var,
    /// <summary>符号付き 1 バイト即値。</summary>
    ShortI,
    /// <summary>符号付き 4 バイト即値。</summary>
    I4,
    /// <summary>符号付き 8 バイト即値。</summary>
    I8,
    /// <summary>4 バイト浮動小数。</summary>
    R4,
    /// <summary>8 バイト浮動小数。</summary>
    R8,
    /// <summary>1 バイト分岐相対オフセット。</summary>
    ShortBrTarget,
    /// <summary>4 バイト分岐相対オフセット。</summary>
    BrTarget,
    /// <summary>u4 個数 + 4 バイト相対オフセット列。</summary>
    Switch,
    /// <summary>4 バイトメタデータトークン (メソッド)。</summary>
    Method,
    /// <summary>4 バイトメタデータトークン (シグネチャ)。</summary>
    Signature,
    /// <summary>4 バイトメタデータトークン (型)。</summary>
    Type,
    /// <summary>4 バイトメタデータトークン (フィールド)。</summary>
    Field,
    /// <summary>4 バイトメタデータトークン (文字列 #US オフセット)。</summary>
    String,
    /// <summary>4 バイトメタデータトークン (任意: ldtoken)。</summary>
    Token,
}

/// <summary>オペコードの静的情報。</summary>
public sealed record IlOpcodeInfo(ILOp Op, string Name, IlOperandKind Operand) {
    /// <summary>命令の合計バイト数 (オペランドを含む)。可変長 (switch) は -1。</summary>
    public int Size => BaseSize + (IsTwoByte ? 1 : 0);

    private int BaseSize => Operand switch {
        IlOperandKind.None => 1,
        IlOperandKind.ShortVar or IlOperandKind.ShortI or IlOperandKind.ShortBrTarget => 2,
        IlOperandKind.Var or IlOperandKind.I4 or IlOperandKind.R4 or IlOperandKind.BrTarget
            or IlOperandKind.Method or IlOperandKind.Signature or IlOperandKind.Type
            or IlOperandKind.Field or IlOperandKind.String or IlOperandKind.Token => 5,
        IlOperandKind.I8 or IlOperandKind.R8 => 9,
        _ => -1, // Switch
    };

    /// <summary>0xFE プレフィックスを持つ 2 バイト命令か。</summary>
    public bool IsTwoByte => (ushort)Op >= 0xFE00;
}

/// <summary>オペコード → 静的情報のテーブル。起動時に 1 回構築する。</summary>
public static class IlOpcodeTable {
    private static readonly IlOpcodeInfo[] s_single = BuildSingle();
    private static readonly IlOpcodeInfo[] s_double = BuildDouble();

    private static void Set(IlOpcodeInfo[] table, ILOp op, string name, IlOperandKind operand) {
        var value = (ushort)op;
        var index = value >= 0xFE00 ? value & 0xFF : value;
        table[index] = new IlOpcodeInfo(op, name, operand);
    }

    private static IlOpcodeInfo[] BuildSingle() {
        var table = new IlOpcodeInfo[256];
        Set(table, ILOp.Nop, "nop", IlOperandKind.None);
        Set(table, ILOp.Break, "break", IlOperandKind.None);
        for (var i = 0; i < 4; i++)
            Set(table, (ILOp)(0x02 + i), $"ldarg.{i}", IlOperandKind.None);
        for (var i = 0; i < 4; i++)
            Set(table, (ILOp)(0x06 + i), $"ldloc.{i}", IlOperandKind.None);
        for (var i = 0; i < 4; i++)
            Set(table, (ILOp)(0x0A + i), $"stloc.{i}", IlOperandKind.None);
        Set(table, ILOp.Ldarg_S, "ldarg.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Ldarga_S, "ldarga.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Starg_S, "starg.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Ldloc_S, "ldloc.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Ldloca_S, "ldloca.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Stloc_S, "stloc.s", IlOperandKind.ShortVar);
        Set(table, ILOp.Ldnull, "ldnull", IlOperandKind.None);
        Set(table, ILOp.Ldc_I4_M1, "ldc.i4.m1", IlOperandKind.None);
        for (var i = 0; i <= 8; i++)
            Set(table, (ILOp)(0x16 + i), $"ldc.i4.{i}", IlOperandKind.None);
        Set(table, ILOp.Ldc_I4_S, "ldc.i4.s", IlOperandKind.ShortI);
        Set(table, ILOp.Ldc_I4, "ldc.i4", IlOperandKind.I4);
        Set(table, ILOp.Ldc_I8, "ldc.i8", IlOperandKind.I8);
        Set(table, ILOp.Ldc_R4, "ldc.r4", IlOperandKind.R4);
        Set(table, ILOp.Ldc_R8, "ldc.r8", IlOperandKind.R8);
        Set(table, ILOp.Dup, "dup", IlOperandKind.None);
        Set(table, ILOp.Pop, "pop", IlOperandKind.None);
        Set(table, ILOp.Jmp, "jmp", IlOperandKind.Method);
        Set(table, ILOp.Call, "call", IlOperandKind.Method);
        Set(table, ILOp.Calli, "calli", IlOperandKind.Signature);
        Set(table, ILOp.Ret, "ret", IlOperandKind.None);
        Set(table, ILOp.Br_S, "br.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.BrFalse_S, "brfalse.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.BrTrue_S, "brtrue.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Beq_S, "beq.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Bge_S, "bge.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Bgt_S, "bgt.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Ble_S, "ble.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Blt_S, "blt.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Bne_Un_S, "bne.un.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Bge_Un_S, "bge.un.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Bgt_Un_S, "bgt.un.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Ble_Un_S, "ble.un.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Blt_Un_S, "blt.un.s", IlOperandKind.ShortBrTarget);
        Set(table, ILOp.Br, "br", IlOperandKind.BrTarget);
        Set(table, ILOp.BrFalse, "brfalse", IlOperandKind.BrTarget);
        Set(table, ILOp.BrTrue, "brtrue", IlOperandKind.BrTarget);
        Set(table, ILOp.Beq, "beq", IlOperandKind.BrTarget);
        Set(table, ILOp.Bge, "bge", IlOperandKind.BrTarget);
        Set(table, ILOp.Bgt, "bgt", IlOperandKind.BrTarget);
        Set(table, ILOp.Ble, "ble", IlOperandKind.BrTarget);
        Set(table, ILOp.Blt, "blt", IlOperandKind.BrTarget);
        Set(table, ILOp.Bne_Un, "bne.un", IlOperandKind.BrTarget);
        Set(table, ILOp.Bge_Un, "bge.un", IlOperandKind.BrTarget);
        Set(table, ILOp.Bgt_Un, "bgt.un", IlOperandKind.BrTarget);
        Set(table, ILOp.Ble_Un, "ble.un", IlOperandKind.BrTarget);
        Set(table, ILOp.Blt_Un, "blt.un", IlOperandKind.BrTarget);
        Set(table, ILOp.Switch, "switch", IlOperandKind.Switch);
        var ldinds = new (ILOp, string)[] {
            (ILOp.Ldind_I1, "ldind.i1"), (ILOp.Ldind_U1, "ldind.u1"),
            (ILOp.Ldind_I2, "ldind.i2"), (ILOp.Ldind_U2, "ldind.u2"),
            (ILOp.Ldind_I4, "ldind.i4"), (ILOp.Ldind_U4, "ldind.u4"),
            (ILOp.Ldind_I8, "ldind.i8"), (ILOp.Ldind_I, "ldind.i"),
            (ILOp.Ldind_R4, "ldind.r4"), (ILOp.Ldind_R8, "ldind.r8"),
            (ILOp.Ldind_Ref, "ldind.ref"),
        };
        foreach (var (op, name) in ldinds)
            Set(table, op, name, IlOperandKind.None);
        var stinds = new (ILOp, string)[] {
            (ILOp.Stind_Ref, "stind.ref"), (ILOp.Stind_I1, "stind.i1"),
            (ILOp.Stind_I2, "stind.i2"), (ILOp.Stind_I4, "stind.i4"),
            (ILOp.Stind_I8, "stind.i8"), (ILOp.Stind_R4, "stind.r4"),
            (ILOp.Stind_R8, "stind.r8"), (ILOp.Stind_I, "stind.i"),
        };
        foreach (var (op, name) in stinds)
            Set(table, op, name, IlOperandKind.None);
        var arith = new (ILOp, string)[] {
            (ILOp.Add, "add"), (ILOp.Sub, "sub"), (ILOp.Mul, "mul"),
            (ILOp.Div, "div"), (ILOp.Div_Un, "div.un"), (ILOp.Rem, "rem"),
            (ILOp.Rem_Un, "rem.un"), (ILOp.And, "and"), (ILOp.Or, "or"),
            (ILOp.Xor, "xor"), (ILOp.Shl, "shl"), (ILOp.Shr, "shr"),
            (ILOp.Shr_Un, "shr.un"), (ILOp.Neg, "neg"), (ILOp.Not, "not"),
        };
        foreach (var (op, name) in arith)
            Set(table, op, name, IlOperandKind.None);
        var convs = new (ILOp, string)[] {
            (ILOp.Conv_I1, "conv.i1"), (ILOp.Conv_I2, "conv.i2"), (ILOp.Conv_I4, "conv.i4"),
            (ILOp.Conv_I8, "conv.i8"), (ILOp.Conv_R4, "conv.r4"), (ILOp.Conv_R8, "conv.r8"),
            (ILOp.Conv_U4, "conv.u4"), (ILOp.Conv_U8, "conv.u8"), (ILOp.Conv_R_Un, "conv.r.un"),
        };
        foreach (var (op, name) in convs)
            Set(table, op, name, IlOperandKind.None);
        Set(table, ILOp.Callvirt, "callvirt", IlOperandKind.Method);
        Set(table, ILOp.Cpobj, "cpobj", IlOperandKind.Type);
        Set(table, ILOp.Ldobj, "ldobj", IlOperandKind.Type);
        Set(table, ILOp.Ldstr, "ldstr", IlOperandKind.String);
        Set(table, ILOp.Newobj, "newobj", IlOperandKind.Method);
        Set(table, ILOp.Castclass, "castclass", IlOperandKind.Type);
        Set(table, ILOp.Isinst, "isinst", IlOperandKind.Type);
        Set(table, ILOp.Unbox, "unbox", IlOperandKind.Type);
        Set(table, ILOp.Throw, "throw", IlOperandKind.None);
        Set(table, ILOp.Ldfld, "ldfld", IlOperandKind.Field);
        Set(table, ILOp.Ldflda, "ldflda", IlOperandKind.Field);
        Set(table, ILOp.Stfld, "stfld", IlOperandKind.Field);
        Set(table, ILOp.Ldsfld, "ldsfld", IlOperandKind.Field);
        Set(table, ILOp.Ldsflda, "ldsflda", IlOperandKind.Field);
        Set(table, ILOp.Stsfld, "stsfld", IlOperandKind.Field);
        Set(table, ILOp.Stobj, "stobj", IlOperandKind.Type);
        for (var i = 0; i < 10; i++)
            Set(table, (ILOp)(0x82 + i), ConvOvfUnName(i), IlOperandKind.None);
        Set(table, ILOp.Box, "box", IlOperandKind.Type);
        Set(table, ILOp.Newarr, "newarr", IlOperandKind.Type);
        Set(table, ILOp.Ldlen, "ldlen", IlOperandKind.None);
        Set(table, ILOp.Ldelema, "ldelema", IlOperandKind.Type);
        var ldelems = new (ILOp, string)[] {
            (ILOp.Ldelem_I1, "ldelem.i1"), (ILOp.Ldelem_U1, "ldelem.u1"),
            (ILOp.Ldelem_I2, "ldelem.i2"), (ILOp.Ldelem_U2, "ldelem.u2"),
            (ILOp.Ldelem_I4, "ldelem.i4"), (ILOp.Ldelem_U4, "ldelem.u4"),
            (ILOp.Ldelem_I8, "ldelem.i8"), (ILOp.Ldelem_I, "ldelem.i"),
            (ILOp.Ldelem_R4, "ldelem.r4"), (ILOp.Ldelem_R8, "ldelem.r8"),
            (ILOp.Ldelem_Ref, "ldelem.ref"),
        };
        foreach (var (op, name) in ldelems)
            Set(table, op, name, IlOperandKind.None);
        var stelems = new (ILOp, string)[] {
            (ILOp.Stelem_I, "stelem.i"), (ILOp.Stelem_I1, "stelem.i1"),
            (ILOp.Stelem_I2, "stelem.i2"), (ILOp.Stelem_I4, "stelem.i4"),
            (ILOp.Stelem_I8, "stelem.i8"), (ILOp.Stelem_R4, "stelem.r4"),
            (ILOp.Stelem_R8, "stelem.r8"), (ILOp.Stelem_Ref, "stelem.ref"),
        };
        foreach (var (op, name) in stelems)
            Set(table, op, name, IlOperandKind.None);
        Set(table, ILOp.Ldelem, "ldelem", IlOperandKind.Type);
        Set(table, ILOp.Stelem, "stelem", IlOperandKind.Type);
        Set(table, ILOp.Unbox_Any, "unbox.any", IlOperandKind.Type);
        for (var i = 0; i < 8; i++)
            Set(table, (ILOp)(0xB3 + i), ConvOvfName(i), IlOperandKind.None);
        Set(table, ILOp.Refanyval, "refanyval", IlOperandKind.Type);
        Set(table, ILOp.Ckfinite, "ckfinite", IlOperandKind.None);
        Set(table, ILOp.Mkrefany, "mkrefany", IlOperandKind.Type);
        Set(table, ILOp.Ldtoken, "ldtoken", IlOperandKind.Token);
        var convs2 = new (ILOp, string)[] {
            (ILOp.Conv_U2, "conv.u2"), (ILOp.Conv_U1, "conv.u1"), (ILOp.Conv_I, "conv.i"),
            (ILOp.Conv_Ovf_I, "conv.ovf.i"), (ILOp.Conv_Ovf_U, "conv.ovf.u"),
            (ILOp.Add_Ovf, "add.ovf"), (ILOp.Add_Ovf_Un, "add.ovf.un"),
            (ILOp.Mul_Ovf, "mul.ovf"), (ILOp.Mul_Ovf_Un, "mul.ovf.un"),
            (ILOp.Sub_Ovf, "sub.ovf"), (ILOp.Sub_Ovf_Un, "sub.ovf.un"),
            (ILOp.Endfinally, "endfinally"), (ILOp.Stind_I, "stind.i"), (ILOp.Conv_U, "conv.u"),
        };
        foreach (var (op, name) in convs2)
            Set(table, op, name, IlOperandKind.None);
        Set(table, ILOp.Leave, "leave", IlOperandKind.BrTarget);
        Set(table, ILOp.Leave_S, "leave.s", IlOperandKind.ShortBrTarget);
        return table;
    }

    private static string ConvOvfUnName(int i) {
        var kinds = new[] { "i1", "i2", "i4", "i8", "u1", "u2", "u4", "u8", "i", "u" };
        return $"conv.ovf.{kinds[i]}.un";
    }

    private static string ConvOvfName(int i) {
        var kinds = new[] { "i1", "u1", "i2", "u2", "i4", "u4", "i8", "u8" };
        return $"conv.ovf.{kinds[i]}";
    }

    private static IlOpcodeInfo[] BuildDouble() {
        var table = new IlOpcodeInfo[256];
        Set(table, ILOp.Arglist, "arglist", IlOperandKind.None);
        Set(table, ILOp.Ceq, "ceq", IlOperandKind.None);
        Set(table, ILOp.Cgt, "cgt", IlOperandKind.None);
        Set(table, ILOp.Cgt_Un, "cgt.un", IlOperandKind.None);
        Set(table, ILOp.Clt, "clt", IlOperandKind.None);
        Set(table, ILOp.Clt_Un, "clt.un", IlOperandKind.None);
        Set(table, ILOp.Ldftn, "ldftn", IlOperandKind.Method);
        Set(table, ILOp.Ldvirtftn, "ldvirtftn", IlOperandKind.Method);
        Set(table, ILOp.Ldarg, "ldarg", IlOperandKind.Var);
        Set(table, ILOp.Ldarga, "ldarga", IlOperandKind.Var);
        Set(table, ILOp.Starg, "starg", IlOperandKind.Var);
        Set(table, ILOp.Ldloc, "ldloc", IlOperandKind.Var);
        Set(table, ILOp.Ldloca, "ldloca", IlOperandKind.Var);
        Set(table, ILOp.Stloc, "stloc", IlOperandKind.Var);
        Set(table, ILOp.Localloc, "localloc", IlOperandKind.None);
        Set(table, ILOp.Endfilter, "endfilter", IlOperandKind.None);
        Set(table, ILOp.Unaligned, "unaligned.", IlOperandKind.ShortI);
        Set(table, ILOp.Volatile, "volatile.", IlOperandKind.None);
        Set(table, ILOp.Tail, "tail.", IlOperandKind.None);
        Set(table, ILOp.Initobj, "initobj", IlOperandKind.Type);
        Set(table, ILOp.Constrained, "constrained.", IlOperandKind.Type);
        Set(table, ILOp.Cpblk, "cpblk", IlOperandKind.None);
        Set(table, ILOp.Initblk, "initblk", IlOperandKind.None);
        Set(table, ILOp.Rethrow, "rethrow", IlOperandKind.None);
        Set(table, ILOp.Sizeof, "sizeof", IlOperandKind.Type);
        Set(table, ILOp.Refanytype, "refanytype", IlOperandKind.None);
        Set(table, ILOp.Readonly, "readonly.", IlOperandKind.None);
        return table;
    }

    /// <summary>オペコードの静的情報。未定義の値は null。</summary>
    public static IlOpcodeInfo? Get(ILOp op) {
        var value = (ushort)op;
        return value >= 0xFE00 ? s_double[value & 0xFF] : s_single[value];
    }
}
