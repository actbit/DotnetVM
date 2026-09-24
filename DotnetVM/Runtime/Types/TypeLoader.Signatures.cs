using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // ---- SigType → VmType 解決 ----

    /// <summary>署名中の型を VmType に解決し、ジェネリックパラメータを context の実引数で置換する。</summary>
    public VmType ResolveToken(SigType sigType, GenericContext? context) {
        lock (MetadataGate)
            return GenericSubstitutor.Substitute(ResolveToken(sigType), context);
    }

    /// <summary>署名中の型を VmType に解決する。ジェネリックパラメータは置換コンテキストがないため
    /// VmGenericParameterType をそのまま返す (インタプリタは ResolveToken(sigType, context) を使う)。
    /// GenericInst の解決時にもネスト上限を強制する (TypeSpec 連鎖の再帰対策)。</summary>
    public VmType ResolveToken(SigType sigType) {
        lock (MetadataGate)
            return ResolveTokenWithDepth(sigType, 0);
    }

    private VmType ResolveTokenWithDepth(SigType sigType, int genericDepth) => sigType.Kind switch {
        SigKind.TypeToken => ResolveTypeDefOrRefToken(sigType.Token),
        SigKind.GenericInst => ResolveGenericInst(sigType, genericDepth),
        SigKind.SzArray => ArrayWithBase(new VmArrayType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) }),
        SigKind.Array => ArrayWithBase(new VmMultiDimArrayType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth), Rank = sigType.Rank }),
        SigKind.ByRef => new VmByRefType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) },
        SigKind.Pointer => new VmByRefType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) }, // ポインタは ByRef と同様に扱う (未対応扱い)
        SigKind.GenericVar => new VmGenericParameterType { IsMethodParameter = false, Number = sigType.VarNumber },
        SigKind.GenericMethodVar => new VmGenericParameterType { IsMethodParameter = true, Number = sigType.VarNumber },
        SigKind.Boolean => RequiredIntrinsic("System.Boolean"),
        SigKind.Char => RequiredIntrinsic("System.Char"),
        SigKind.I1 => RequiredIntrinsic("System.SByte"),
        SigKind.U1 => RequiredIntrinsic("System.Byte"),
        SigKind.I2 => RequiredIntrinsic("System.Int16"),
        SigKind.U2 => RequiredIntrinsic("System.UInt16"),
        SigKind.I4 => RequiredIntrinsic("System.Int32"),
        SigKind.U4 => RequiredIntrinsic("System.UInt32"),
        SigKind.I8 => RequiredIntrinsic("System.Int64"),
        SigKind.U8 => RequiredIntrinsic("System.UInt64"),
        SigKind.R4 => RequiredIntrinsic("System.Single"),
        SigKind.R8 => RequiredIntrinsic("System.Double"),
        SigKind.I => RequiredIntrinsic("System.IntPtr"),
        SigKind.U => RequiredIntrinsic("System.UIntPtr"),
        SigKind.String => RequiredIntrinsic("System.String"),
        SigKind.Object => RequiredIntrinsic("System.Object"),
        SigKind.TypedByRef => RequiredIntrinsic("System.TypedReference"),
        SigKind.Void => RequiredIntrinsic("System.Void"),
        _ => throw new NotSupportedException($"未対応の署名型です: {sigType}"),
    };

    private VmType ResolveGenericInst(SigType sigType, int genericDepth) {
        var max = _image.Limits?.MaxGenericNestingDepth ?? 64;
        if (genericDepth + 1 > max)
            throw new BadImageFormatException(
                $"ジェネリックのネスト深さ {genericDepth + 1:N0} が上限 {max:N0} を超えています。");
        return new VmConstructedType {
            Definition = ResolveTypeDefOrRefToken(sigType.Token),
            TypeArguments = sigType.Args!.Select(a => ResolveTokenWithDepth(a, genericDepth + 1)).ToArray(),
        };
    }

    /// <summary>配列型に System.Array (実型またはファサード) を基底として接続する。</summary>
    private VmType ArrayWithBase(VmType arrayType) {
        var baseType = ResolveWellKnownType("System.Array");
        switch (arrayType) {
            case VmArrayType szArray:
                szArray.SetBaseType(baseType);
                break;
            case VmMultiDimArrayType multiDim:
                multiDim.SetBaseType(baseType);
                break;
        }
        return arrayType;
    }

    private VmType ResolveTypeDefOrRefToken(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        return table switch {
            TableKind.TypeDef => GetTypeDef(rid),
            TableKind.TypeRef => ResolveTypeRef(rid),
            TableKind.TypeSpec => ResolveTypeSpec(rid),
            _ => throw new BadImageFormatException($"型トークンのテーブル 0x{table:X} が不正です。"),
        };
    }

    /// <summary>TypeSpec rid を解決する (ジェネリックパラメータは context の実引数で置換)。
    /// GenericInst のネスト深度は署名デコード時と解決時の双方で強制する。</summary>
    public VmType ResolveTypeSpec(int typeSpecRid, GenericContext? context = null) {
        lock (MetadataGate)
            return ResolveTypeSpecCore(typeSpecRid, context);
    }

    private VmType ResolveTypeSpecCore(int typeSpecRid, GenericContext? context) {
        var blob = _image.GetBlob(_image.Tables.GetRowIndex(TableKind.TypeSpec, typeSpecRid, 0));
        var sigType = SignatureDecoder.DecodeTypeSpecSignature(blob.ToArray(),
            _image.Limits?.MaxSignatureDepth ?? 64,
            _image.Limits?.MaxGenericNestingDepth ?? 64).Type;
        CheckGenericNestingDepth(sigType, 0);
        return GenericSubstitutor.Substitute(ResolveToken(sigType), context);
    }

    /// <summary>解決済み SigType の GenericInst ネストが上限を超えていないか検査する
    /// (デコード時は blob 再帰、解決時は TypeSpec 連鎖の双方があり得るため)。</summary>
    private void CheckGenericNestingDepth(SigType type, int depth) {
        var max = _image.Limits?.MaxGenericNestingDepth ?? 64;
        if (type.Kind == SigKind.GenericInst) {
            depth++;
            if (depth > max)
                throw new BadImageFormatException(
                    $"ジェネリックのネスト深さ {depth:N0} が上限 {max:N0} を超えています。");
            foreach (var arg in type.Args!)
                CheckGenericNestingDepth(arg, depth);
        } else if (type.Inner is not null) {
            CheckGenericNestingDepth(type.Inner, depth);
        } else if (type.Args is not null) {
            foreach (var arg in type.Args)
                CheckGenericNestingDepth(arg, depth);
        }
    }

    /// <summary>SigKind 直引きの既知型 (プリミティブ/Object/String 等) を解決する。
    /// trusted 実型 (CoreLib) があれば優先し、無ければ intrinsic ファサード。
    /// 非 trusted なゲスト画像の同名型には統合しない (fake 混入防止)。</summary>
    private VmType RequiredIntrinsic(string fullName) =>
        TryResolveTrustedUnifiedType(fullName) ?? _intrinsicTypes[fullName];
}
