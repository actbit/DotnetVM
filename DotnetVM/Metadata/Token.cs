namespace DotnetVM.Metadata;

/// <summary>メタデータトークン (上位バイト = テーブル、下位 3 バイト = 1 始まりの rid)。</summary>
public readonly record struct Token(uint Value) : IComparable<Token> {
    public TableKind Table => (TableKind)(Value >> 24);
    public int Rid => (int)(Value & 0x00FF_FFFF);
    public bool IsNull => Value == 0;

    public static Token From(TableKind table, int rid) => new(((uint)table << 24) | (uint)rid);

    public int CompareTo(Token other) => Value.CompareTo(other.Value);

    public override string ToString() => $"0x{Value:X8} ({Table}, rid={Rid})";
}
