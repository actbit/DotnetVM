using DotnetVM.Host;
using DotnetVM.PE;

namespace DotnetVM.Metadata;

/// <summary>
/// 1 つの CLI アセンブリ (PE + メタデータ) の統合ビュー。
/// 型解決・実行・逆アセンブルのすべての入口。
/// </summary>
public sealed class AssemblyImage {
    public PEImage PE { get; }
    /// <summary>入力画像の保持バイト数 (PEImage が参照する backing memory の長さ)。</summary>
    public int ByteLength => PE.ByteLength;
    public CliHeader Cli { get; }
    public MetadataRoot Root { get; }
    public MetadataTables Tables { get; }
    public StringHeap Strings { get; }
    public UserStringHeap UserStrings { get; }
    public BlobHeap Blobs { get; }
    public GuidHeap Guids { get; }

    /// <summary>アセンブリの単純名 (Assembly テーブル、なければ Module 名)。</summary>
    public string Name { get; }

    /// <summary>アセンブリの identity (Name / Version / Culture / PublicKeyToken)。遅延構築
    /// (Assembly テーブルの解析を要するため)。タスク 2 hardening の依存解決 / trusted 判定に使う。</summary>
    private AssemblyIdentity? _identity;
    public AssemblyIdentity Identity => _identity ??= AssemblyIdentity.FromAssemblyDef(this);

    /// <summary>AssemblyRef rid の参照先 identity (依存解決の照合に使う)。</summary>
    public AssemblyIdentity GetAssemblyRefIdentity(int assemblyRefRid) =>
        AssemblyIdentity.FromAssemblyRef(this, assemblyRefRid);

    /// <summary>ファイルからロードした場合の元パス (依存アセンブリの同一ディレクトリ探索に使う。ストリームロードは null)。</summary>
    public string? SourcePath { get; internal set; }

    /// <summary>ロード時に適用した上限 (署名デコード / メソッド本体の遅延読み込みで再利用する)。</summary>
    public MemoryPolicy? Limits { get; private init; }

    /// <summary>NestedClass テーブルの逆引き索引 (Nested rid → Enclosing rid。遅延構築)。</summary>
    private Dictionary<int, int>? _nestedToEnclosing;

    private AssemblyImage(PEImage pe, CliHeader cli, MetadataRoot root, MetadataTables tables,
                          StringHeap strings, UserStringHeap userStrings, BlobHeap blobs, GuidHeap guids,
                          string name, MemoryPolicy? limits) {
        PE = pe;
        Cli = cli;
        Root = root;
        Tables = tables;
        Strings = strings;
        UserStrings = userStrings;
        Blobs = blobs;
        Guids = guids;
        Name = name;
        Limits = limits;
    }

    public static AssemblyImage Parse(ReadOnlyMemory<byte> image) => Parse(image, limits: null);

    /// <summary>PE / メタデータの解析 (loader hardening の検証平面相当)。limits を渡すと
    /// ロード時検証 (validation phase) が入る: 画像総量 / メタデータ行数 / 各ストリーム
    /// サイズを MemoryPolicy の上限と比較して超過はロード拒否 (OperationNotAllowed) にする。
    /// hostile 画像は実行の前にここで落ちる (タスク 2 hardening)。</summary>
    public static AssemblyImage Parse(ReadOnlyMemory<byte> image, MemoryPolicy? limits) {
        limits?.Validate();
        var pe = PEImage.Parse(image);
        var cli = CliHeader.ParseFrom(pe);
        var root = MetadataRoot.Parse(pe, cli, limits);
        var tables = new MetadataTables(root.TablesStream);
        var strings = new StringHeap(root.StringsStream);
        var userStrings = new UserStringHeap(root.UserStringsStream);
        var blobs = new BlobHeap(root.BlobStream);
        var guids = new GuidHeap(root.GuidStream);

        if (limits is not null) {
            // ロード時検証 (実行前拒否): メタデータ総行数 / ストリームサイズ
            long rowTotal = 0;
            foreach (TableKind table in Enum.GetValues<TableKind>()) {
                var rows = (long)tables.GetRowCount(table);
                rowTotal += rows;
                if (rows > limits.MaxMetadataRows)
                    throw new BadImageFormatException(
                        $"メタデータテーブル {table} の行数 {rows:N0} が上限 {limits.MaxMetadataRows:N0} を超えています。");
            }
            if (rowTotal > limits.MaxMetadataRows)
                throw new BadImageFormatException(
                    $"メタデータ行の合計 {rowTotal:N0} が上限 {limits.MaxMetadataRows:N0} を超えています。");
        }

        string name;
        if (tables.GetRowCount(TableKind.Assembly) > 0)
            name = strings.GetString(tables.GetRowIndex(TableKind.Assembly, 1, 7));
        else
            name = strings.GetString(tables.GetRowIndex(TableKind.Module, 1, 1));

        return new AssemblyImage(pe, cli, root, tables, strings, userStrings, blobs, guids, name, limits);
    }

    // ---- 文字列/ブロブ/ボディ取得ヘルパー ----

    /// <summary>#Strings ヒープから文字列を取得 (メタデータ文字列用)。</summary>
    public string GetString(int stringIndex) => Strings.GetString(stringIndex);

    /// <summary>#US ヒープからリテラル文字列を取得 (ldstr 用)。</summary>
    public string GetUserString(int userStringIndex) => UserStrings.GetString(userStringIndex);

    /// <summary>#Blob ヒープからブロブを取得。</summary>
    public ReadOnlySpan<byte> GetBlob(int blobIndex) => Blobs.GetBlob(blobIndex);

    /// <summary>MethodDef のメソッド本体。RVA = 0 (abstract/pinvoke) は null。
    /// MaxMethodBodyBytes (Limits) を読み込み時に強制する。</summary>
    public MethodBodyBlock? GetMethodBody(int methodDefRid) {
        var rva = (int)Tables.GetCell(TableKind.MethodDef, methodDefRid, 0);
        return MethodBodyBlock.FromRva(PE, rva, Limits?.MaxMethodBodyBytes);
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
        var typeRefCount = Tables.GetRowCount(TableKind.TypeRef);
        if (typeRefRid < 1 || typeRefRid > typeRefCount)
            throw new BadImageFormatException(
                $"TypeRef rid {typeRefRid} は範囲外です (行数 {typeRefCount})。");
        var scope = Tables.DecodeCoded(TableKind.TypeRef, typeRefRid, 0, CodedIndexKind.ResolutionScope);
        var targetCount = Tables.GetRowCount(scope.Table);
        if (scope.Rid < 1 || scope.Rid > targetCount)
            throw new BadImageFormatException(
                $"TypeRef rid {typeRefRid} の ResolutionScope {scope.Table} rid {scope.Rid} は範囲外です (行数 {targetCount})。");
        var ns = GetString(Tables.GetRowIndex(TableKind.TypeRef, typeRefRid, 2));
        var name = GetString(Tables.GetRowIndex(TableKind.TypeRef, typeRefRid, 1));
        return (ns, name, scope);
    }

    /// <summary>TypeRef の ResolutionScope を終端まで辿る。悪意あるメタデータの
    /// self-cycle / 相互 cycle / 過深度が loader の CPU を占有しないよう、全呼出側で
    /// この上限と visited RID 検査を共有する。</summary>
    internal (TableKind Table, int Rid) GetTerminalTypeRefScope(int typeRefRid) {
        const int maxDepth = 64;
        var visited = new HashSet<int>();
        for (var depth = 0; ; depth++) {
            if (depth >= maxDepth)
                throw new BadImageFormatException(
                    $"TypeRef ResolutionScope のネストが上限 {maxDepth} を超えています。");
            if (!visited.Add(typeRefRid))
                throw new BadImageFormatException(
                    $"TypeRef ResolutionScope に cycle が存在します (rid {typeRefRid})。");

            var scope = GetTypeRefName(typeRefRid).ResolutionScope;
            if (scope.Table != TableKind.TypeRef)
                return scope;
            typeRefRid = scope.Rid;
        }
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

    /// <summary>ネスト型の包含型 TypeDef rid を取得。存在しなければ 0 (逆引き索引を遅延構築して O(1) 参照)。</summary>
    public int GetEnclosingTypeDef(int typeDefRid) {
        if (_nestedToEnclosing is null) {
            var index = new Dictionary<int, int>();
            var count = Tables.GetRowCount(TableKind.NestedClass);
            for (var rid = 1; rid <= count; rid++)
                index[(int)Tables.GetRowIndex(TableKind.NestedClass, rid, 0)] =
                    (int)Tables.GetRowIndex(TableKind.NestedClass, rid, 1);
            _nestedToEnclosing = index;
        }
        return _nestedToEnclosing.GetValueOrDefault(typeDefRid);
    }

    /// <summary>FieldRVA テーブルからフィールドの初期データ RVA を取得する (未登録なら 0)。</summary>
    public int GetFieldRva(int fieldDefRid) {
        var count = Tables.GetRowCount(TableKind.FieldRVA);
        for (var rid = 1; rid <= count; rid++) {
            if (Tables.GetRowIndex(TableKind.FieldRVA, rid, 1) == fieldDefRid)
                return (int)Tables.GetCell(TableKind.FieldRVA, rid, 0);
        }
        return 0;
    }

    /// <summary>RVA から所属セクション終端までの生バイト (FieldRVA 初期データの取得用)。</summary>
    public ReadOnlyMemory<byte> GetRvaDataToEnd(int rva) => PE.GetSegmentToEnd(rva);

    /// <summary>TypeDef rid からトークンを作る。</summary>
    public static Token TypeDefToken(int rid) => Token.From(TableKind.TypeDef, rid);
}
