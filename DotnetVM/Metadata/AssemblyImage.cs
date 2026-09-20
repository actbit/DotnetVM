using DotnetVM.PE;

namespace DotnetVM.Metadata;

/// <summary>
/// 1 つの CLI アセンブリ (PE + メタデータ) の統合ビュー。
/// 型解決・実行・逆アセンブルのすべての入口。
/// </summary>
public sealed class AssemblyImage {
    public PEImage PE { get; }
    public CliHeader Cli { get; }
    public MetadataRoot Root { get; }
    public MetadataTables Tables { get; }
    public StringHeap Strings { get; }
    public UserStringHeap UserStrings { get; }
    public BlobHeap Blobs { get; }
    public GuidHeap Guids { get; }

    /// <summary>アセンブリの単純名 (Assembly テーブル、なければ Module 名)。</summary>
    public string Name { get; }

    private AssemblyImage(PEImage pe, CliHeader cli, MetadataRoot root, MetadataTables tables,
                          StringHeap strings, UserStringHeap userStrings, BlobHeap blobs, GuidHeap guids,
                          string name) {
        PE = pe;
        Cli = cli;
        Root = root;
        Tables = tables;
        Strings = strings;
        UserStrings = userStrings;
        Blobs = blobs;
        Guids = guids;
        Name = name;
    }

    public static AssemblyImage Parse(ReadOnlyMemory<byte> image) {
        var pe = PEImage.Parse(image);
        var cli = CliHeader.ParseFrom(pe);
        var root = MetadataRoot.Parse(pe, cli);
        var tables = new MetadataTables(root.TablesStream);
        var strings = new StringHeap(root.StringsStream);
        var userStrings = new UserStringHeap(root.UserStringsStream);
        var blobs = new BlobHeap(root.BlobStream);
        var guids = new GuidHeap(root.GuidStream);

        string name;
        if (tables.GetRowCount(TableKind.Assembly) > 0)
            name = strings.GetString(tables.GetRowIndex(TableKind.Assembly, 1, 7));
        else
            name = strings.GetString(tables.GetRowIndex(TableKind.Module, 1, 1));

        return new AssemblyImage(pe, cli, root, tables, strings, userStrings, blobs, guids, name);
    }

    // ---- 文字列/ブロブ/ボディ取得ヘルパー ----

    /// <summary>#Strings ヒープから文字列を取得 (メタデータ文字列用)。</summary>
    public string GetString(int stringIndex) => Strings.GetString(stringIndex);

    /// <summary>#US ヒープからリテラル文字列を取得 (ldstr 用)。</summary>
    public string GetUserString(int userStringIndex) => UserStrings.GetString(userStringIndex);

    /// <summary>#Blob ヒープからブロブを取得。</summary>
    public ReadOnlySpan<byte> GetBlob(int blobIndex) => Blobs.GetBlob(blobIndex);

    /// <summary>MethodDef のメソッド本体。RVA = 0 (abstract/pinvoke) は null。</summary>
    public MethodBodyBlock? GetMethodBody(int methodDefRid) {
        var rva = (int)Tables.GetCell(TableKind.MethodDef, methodDefRid, 0);
        return MethodBodyBlock.FromRva(PE, rva);
    }

    // ---- トークン → 行アクセスの簡易ヘルパー ----

    /// <summary>TypeDef の名前空間名 + 型名 ("Ns.Type" 形式、ネスト型は包含型名を含まない)。</summary>
    public (string Namespace, string Name) GetTypeDefName(int typeDefRid) {
        var ns = GetString(Tables.GetRowIndex(TableKind.TypeDef, typeDefRid, 2));
        var name = GetString(Tables.GetRowIndex(TableKind.TypeDef, typeDefRid, 1));
        return (ns, name);
    }

    /// <summary>TypeRef の名前空間名 + 型名と解決スコープ。</summary>
    public (string Namespace, string Name, (TableKind Table, int Rid) ResolutionScope) GetTypeRefName(int typeRefRid) {
        var scope = Tables.DecodeCoded(TableKind.TypeRef, typeRefRid, 0, CodedIndexKind.ResolutionScope);
        var ns = GetString(Tables.GetRowIndex(TableKind.TypeRef, typeRefRid, 2));
        var name = GetString(Tables.GetRowIndex(TableKind.TypeRef, typeRefRid, 1));
        return (ns, name, scope);
    }

    /// <summary>MethodDef の名前。</summary>
    public string GetMethodName(int methodDefRid) =>
        GetString(Tables.GetRowIndex(TableKind.MethodDef, methodDefRid, 3));

    /// <summary>MethodDef の署名ブロブ。</summary>
    public ReadOnlySpan<byte> GetMethodSignature(int methodDefRid) =>
        GetBlob(Tables.GetRowIndex(TableKind.MethodDef, methodDefRid, 4));

    /// <summary>MemberRef の親 (MemberRefParent coded) と名前。</summary>
    public ((TableKind Table, int Rid) Parent, string Name) GetMemberRef(int memberRefRid) {
        var parent = Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
        var name = GetString(Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));
        return (parent, name);
    }

    /// <summary>MemberRef の署名ブロブ。</summary>
    public ReadOnlySpan<byte> GetMemberRefSignature(int memberRefRid) =>
        GetBlob(Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 2));

    /// <summary>ネスト型の包含型 TypeDef rid を取得。存在しなければ 0。</summary>
    public int GetEnclosingTypeDef(int typeDefRid) {
        var count = Tables.GetRowCount(TableKind.NestedClass);
        for (var rid = 1; rid <= count; rid++) {
            if (Tables.GetRowIndex(TableKind.NestedClass, rid, 0) == typeDefRid)
                return Tables.GetRowIndex(TableKind.NestedClass, rid, 1);
        }
        return 0;
    }

    /// <summary>TypeDef rid からトークンを作る。</summary>
    public static Token TypeDefToken(int rid) => Token.From(TableKind.TypeDef, rid);
}
