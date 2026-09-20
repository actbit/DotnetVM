namespace DotnetVM.Metadata.Signatures;

/// <summary>署名中の型種別 (ELEMENT_TYPE 対応)。</summary>
public enum SigKind : byte {
    Void = 0x01,
    Boolean = 0x02,
    Char = 0x03,
    I1 = 0x04,
    U1 = 0x05,
    I2 = 0x06,
    U2 = 0x07,
    I4 = 0x08,
    U4 = 0x09,
    I8 = 0x0A,
    U8 = 0x0B,
    R4 = 0x0C,
    R8 = 0x0D,
    String = 0x0E,
    /// <summary>ネイティブ int (ELEMENT_TYPE_I)。</summary>
    I = 0x18,
    /// <summary>ネイティブ uint (ELEMENT_TYPE_U)。</summary>
    U = 0x19,
    Object = 0x1C,
    /// <summary>TypeDef または TypeRef へのトークン (ValueToken/ClassToken)。</summary>
    TypeToken,
    /// <summary>型ジェネリックパラメータ (!n)。</summary>
    GenericVar,
    /// <summary>メソッドジェネリックパラメータ (!!n)。</summary>
    GenericMethodVar,
    ByRef,
    Pointer,
    /// <summary>1 次元 0 原点配列 T[]。</summary>
    SzArray,
    /// <summary>多次元配列 T[ranks]。</summary>
    Array,
    /// <summary>構築ジェネリック型 (GenericInst)。</summary>
    GenericInst,
    TypedByRef,
}

/// <summary>
/// 署名中の 1 つの型を表す不変ノード。
/// Kind に応じて Token (TypeToken/GenericInst の定義側)、Inner (ByRef/Pointer/配列要素)、
/// Args (GenericInst の型引数)、VarNumber (ジェネリックパラメータ番号) を使う。
/// </summary>
public sealed record SigType(SigKind Kind, uint Token = 0, SigType? Inner = null,
                             SigType[]? Args = null, int VarNumber = 0, int Rank = 0) {
    public bool IsPrimitive =>
        Kind is SigKind.Boolean or SigKind.Char or SigKind.I1 or SigKind.U1
            or SigKind.I2 or SigKind.U2 or SigKind.I4 or SigKind.U4
            or SigKind.I8 or SigKind.U8 or SigKind.R4 or SigKind.R8
            or SigKind.I or SigKind.U;

    // 値型/参照型の確定はトークンを VmType に解決した後に行う (署名レベルでは判定しない)。

    /// <summary>人間可読な表示名 (デバッグ用。トークンは解決しない)。</summary>
    public override string ToString() => Kind switch {
        SigKind.TypeToken => $"token(0x{Token:X8})",
        SigKind.GenericVar => $"!{VarNumber}",
        SigKind.GenericMethodVar => $"!!{VarNumber}",
        SigKind.ByRef => Inner + "&",
        SigKind.Pointer => Inner + "*",
        SigKind.SzArray => Inner + "[]",
        SigKind.Array => $"{Inner}[{Rank}]",
        SigKind.GenericInst when Args is not null =>
            $"token(0x{Token:X8})<{string.Join(", ", Args.Select(a => a.ToString()))}>",
        _ => Kind.ToString(),
    };
}
