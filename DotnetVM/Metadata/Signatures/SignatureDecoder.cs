using System.Buffers.Binary;
using DotnetVM.Binary;

namespace DotnetVM.Metadata.Signatures;

/// <summary>メソッド署名 (II.23.2.1)。呼び出し規約とパラメータ型の並び。</summary>
public sealed record MethodSignature(
    bool HasThis,
    /// <summary>既定呼び出し規約以外 (vararg 等) か。</summary>
    bool IsVarArg,
    int GenericParamCount,
    SigType ReturnType,
    SigType[] ParamTypes);

/// <summary>フィールド署名 (II.23.2.4) の結果。</summary>
public sealed record FieldSignature(SigType FieldType);

/// <summary>型インスタンス化署名 (TypeSpec、II.23.2.14) の結果。</summary>
public sealed record TypeSpecSignature(SigType Type);

/// <summary>
/// 圧縮署名ブロブのデコーダ。メタデータ側の構文解析のみを行い、
/// トークンから実際の型への解決は行わない (型システムの担当)。
/// </summary>
public static class SignatureDecoder {
    /// <summary>MethodDef/MethodRef/StandAloneSig のメソッド署名をデコードする。</summary>
    public static MethodSignature DecodeMethodSignature(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        var callingConv = reader.ReadByte();

        // 汎用 (0x10) と vararg (0x05)、instance (0x20) フラグ
        var hasThis = (callingConv & 0x20) != 0;
        var isGeneric = (callingConv & 0x10) != 0;
        var baseConv = callingConv & 0x0F;
        if (baseConv is not (0x00 or 0x05))
            throw new NotSupportedException($"未対応の呼び出し規約 0x{callingConv:X2} です。");
        var isVarArg = baseConv == 0x05;

        var genericParamCount = isGeneric ? (int)reader.ReadCompressedUInt32() : 0;
        var paramCount = (int)reader.ReadCompressedUInt32();
        var returnType = DecodeType(ref reader);
        var paramTypes = new SigType[paramCount];
        for (var i = 0; i < paramCount; i++)
            paramTypes[i] = DecodeType(ref reader);

        return new MethodSignature(hasThis, isVarArg, genericParamCount, returnType, paramTypes);
    }

    public static FieldSignature DecodeFieldSignature(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        var callingConv = reader.ReadByte();
        if (callingConv != 0x06)
            throw new BadImageFormatException($"フィールド署名の呼び出し規約が不正です (0x{callingConv:X2})。");
        return new FieldSignature(DecodeType(ref reader));
    }

    /// <summary>Property 署名 = Field 署名と同一形式 (先頭が 0x08)。</summary>
    public static FieldSignature DecodePropertySignature(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        var callingConv = reader.ReadByte();
        if (callingConv != 0x08)
            throw new BadImageFormatException($"プロパティ署名の呼び出し規約が不正です (0x{callingConv:X2})。");
        reader.ReadCompressedUInt32(); // ParamCount
        return new FieldSignature(DecodeType(ref reader));
    }

    /// <summary>TypeSpec (II.23.2.14) をデコードする。</summary>
    public static TypeSpecSignature DecodeTypeSpecSignature(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        return new TypeSpecSignature(DecodeType(ref reader));
    }

    /// <summary>MethodSpec の Instantiation (II.23.2.15) をデコードする。
    /// 形式: GENERICINST (0x0A) &lt;argCount&gt; &lt;type...&gt;。</summary>
    public static SigType[] DecodeMethodSpecInstantiation(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        var kind = reader.ReadByte();
        if (kind != 0x0A)
            throw new BadImageFormatException($"MethodSpec Instantiation の先頭バイトが不正です (0x{kind:X2})。");
        var count = (int)reader.ReadCompressedUInt32();
        var args = new SigType[count];
        for (var i = 0; i < count; i++)
            args[i] = DecodeType(ref reader);
        return args;
    }

    /// <summary>ローカル変数署名 (StandAloneSig、II.23.2.10) をデコードする。</summary>
    public static SigType[] DecodeLocalsSignature(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        var callingConv = reader.ReadByte();
        if (callingConv != 0x07)
            throw new BadImageFormatException($"ローカル変数署名の呼び出し規約が不正です (0x{callingConv:X2})。");
        var count = (int)reader.ReadCompressedUInt32();
        var locals = new SigType[count];
        for (var i = 0; i < count; i++)
            locals[i] = DecodeType(ref reader);
        return locals;
    }

    public static SigType DecodeType(ReadOnlySpan<byte> blob) {
        var reader = new SpanReader(blob);
        return DecodeType(ref reader);
    }

    /// <summary>
    /// 署名内の TypeDefOrRefOrSpecEncoded (II.23.2.8) を通常のメタデータトークンに変換する。
    /// 下位 2 ビット = タグ (0=TypeDef, 1=TypeRef, 2=TypeSpec)、残り = rid。
    /// </summary>
    private static uint DecodeTypeDefOrRefToken(uint encoded) {
        var table = (encoded & 0x3) switch {
            0 => 0x02,   // TypeDef
            1 => 0x01,   // TypeRef
            2 => 0x1B,   // TypeSpec
            _ => throw new BadImageFormatException($"TypeDefOrRef エンコーディングのタグが不正です (0x{encoded:X})。"),
        };
        return ((uint)table << 24) | (encoded >> 2);
    }

    private static SigType DecodeType(ref SpanReader reader) {
        var kind = reader.ReadByte();
        switch (kind) {
            // 単純型
            case 0x01: return new SigType(SigKind.Void);
            case 0x02: return new SigType(SigKind.Boolean);
            case 0x03: return new SigType(SigKind.Char);
            case 0x04: return new SigType(SigKind.I1);
            case 0x05: return new SigType(SigKind.U1);
            case 0x06: return new SigType(SigKind.I2);
            case 0x07: return new SigType(SigKind.U2);
            case 0x08: return new SigType(SigKind.I4);
            case 0x09: return new SigType(SigKind.U4);
            case 0x0A: return new SigType(SigKind.I8);
            case 0x0B: return new SigType(SigKind.U8);
            case 0x0C: return new SigType(SigKind.R4);
            case 0x0D: return new SigType(SigKind.R8);
            case 0x0E: return new SigType(SigKind.String);
            case 0x18: return new SigType(SigKind.I);
            case 0x19: return new SigType(SigKind.U);
            case 0x1C: return new SigType(SigKind.Object);
            case 0x16: return new SigType(SigKind.TypedByRef);

            case 0x0F: // PTR <type>
                return new SigType(SigKind.Pointer, Inner: DecodeType(ref reader));
            case 0x10: // BYREF <type>
                return new SigType(SigKind.ByRef, Inner: DecodeType(ref reader));
            case 0x1D: // SZARRAY <type>
                return new SigType(SigKind.SzArray, Inner: DecodeType(ref reader));

            case 0x11: // VALUETYPE <TypeDefOrRefOrSpecEncoded>
            case 0x12: // CLASS <TypeDefOrRefOrSpecEncoded>
                return new SigType(SigKind.TypeToken, Token: DecodeTypeDefOrRefToken(reader.ReadCompressedUInt32()));

            case 0x13: // VAR n
                return new SigType(SigKind.GenericVar, VarNumber: (int)reader.ReadCompressedUInt32());
            case 0x1E: // MVAR n
                return new SigType(SigKind.GenericMethodVar, VarNumber: (int)reader.ReadCompressedUInt32());

            case 0x15: { // GENERICINST (CLASS|VALUETYPE) <token> <argcount> <args...>
                var typeFlag = reader.ReadByte();
                if (typeFlag is not (0x11 or 0x12))
                    throw new BadImageFormatException($"GenericInst の型種別が不正です (0x{typeFlag:X2})。");
                var token = DecodeTypeDefOrRefToken(reader.ReadCompressedUInt32());
                var argCount = (int)reader.ReadCompressedUInt32();
                var args = new SigType[argCount];
                for (var i = 0; i < argCount; i++)
                    args[i] = DecodeType(ref reader);
                return new SigType(SigKind.GenericInst, Token: token, Args: args);
            }

            case 0x14: { // ARRAY <type> <rank> <sizes...> <lobounds...>
                var inner = DecodeType(ref reader);
                var rank = (int)reader.ReadCompressedUInt32();
                var numSizes = (int)reader.ReadCompressedUInt32();
                for (var i = 0; i < numSizes; i++)
                    reader.ReadCompressedUInt32();
                var numLoBounds = (int)reader.ReadCompressedUInt32();
                for (var i = 0; i < numLoBounds; i++)
                    reader.ReadCompressedUInt32();
                return new SigType(SigKind.Array, Inner: inner, Rank: rank);
            }

            case 0x1F or 0x20: // CMOD_REQD / CMOD_OPT <token> <type>
                reader.ReadCompressedUInt32();
                return DecodeType(ref reader);

            case 0x45: // PINNED <type> (ローカル変数のみ)
                return DecodeType(ref reader);

            case 0x1B: // FNPTR <method sig> — 未対応 (MethodSignature は返せない)
                throw new NotSupportedException("関数ポインタ (FNPTR) の署名は対応していません。");

            default:
                throw new BadImageFormatException($"未知の ELEMENT_TYPE 0x{kind:X2} が署名に含まれています。");
        }
    }
}
