using System.Security.Cryptography;
using System.Text;

namespace DotnetVM.Metadata;

/// <summary>
/// アセンブリの識別情報 (ECMA-335 の Assembly / AssemblyRef identity 列)。
/// 単純名だけでなく Version / Culture / PublicKeyToken を含み、依存解決と trusted 判定を
/// identity レベルで行えるようにする (タスク 2 hardening)。AssemblyDef の PublicKey (完全な
/// 公開鍵 blob) と AssemblyRef の PublicKeyOrToken (トークンまたは完全鍵) の双方を
/// <see cref="PublicKeyToken"/> (下位 8 バイトの 16 進小文字列) に正規化する。
/// </summary>
public readonly record struct AssemblyIdentity(
    string Name,
    Version Version,
    string Culture,
    string PublicKeyToken) {

    /// <summary>公開鍵トークンを保持する (strong-named) か。</summary>
    public bool IsStrongNamed => PublicKeyToken.Length > 0;

    /// <summary>Culture が中立 (空) か。</summary>
    public bool IsNeutralCulture => Culture.Length == 0;

    /// <summary>依存解決の照合: 単純名 (大文字小文字無視) が一致し、参照側が strong-named なら
    /// 公開鍵トークンも一致することを要求する (CLR の照合規則の必須部分)。Version / Culture は
    /// 情報として保持するが照合は soft (バージョン差で解決を壊さない。トークンが同一性の要)。</summary>
    public bool Matches(AssemblyIdentity candidate) {
        if (!string.Equals(Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsStrongNamed &&
            !string.Equals(PublicKeyToken, candidate.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public override string ToString() {
        var token = IsStrongNamed ? ", PublicKeyToken=" + PublicKeyToken : "";
        var culture = IsNeutralCulture ? ", Culture=neutral" : ", Culture=" + Culture;
        return $"{Name}, Version={Version}{culture}{token}";
    }

    /// <summary>Assembly テーブル (定義側) の identity を構築する。PublicKey は完全な公開鍵 blob
    /// のためトークンへ正規化する。Assembly テーブルが無い (純モジュール) 場合は Module 名を
    /// 使い、identity 列は既定値 (Version 0.0.0.0 / 中立 / トークン無し) にする。</summary>
    public static AssemblyIdentity FromAssemblyDef(AssemblyImage image) {
        var tables = image.Tables;
        if (tables.GetRowCount(TableKind.Assembly) == 0)
            return new AssemblyIdentity(image.Name, new Version(0, 0, 0, 0), "", "");
        return new AssemblyIdentity(
            image.GetString(tables.GetRowIndex(TableKind.Assembly, 1, 7)),
            ReadVersion(tables, TableKind.Assembly, 1, 1),
            image.GetString(tables.GetRowIndex(TableKind.Assembly, 1, 8)),
            TokenFromBlob(image.GetBlob(tables.GetRowIndex(TableKind.Assembly, 1, 6))));
    }

    /// <summary>AssemblyRef テーブル行の identity を構築する。PublicKeyOrToken は
    /// トークン (8 バイト) または完全鍵のいずれかで、どちらもトークンへ正規化する。</summary>
    public static AssemblyIdentity FromAssemblyRef(AssemblyImage image, int rid) {
        var tables = image.Tables;
        return new AssemblyIdentity(
            image.GetString(tables.GetRowIndex(TableKind.AssemblyRef, rid, 6)),
            ReadVersion(tables, TableKind.AssemblyRef, rid, 0),
            image.GetString(tables.GetRowIndex(TableKind.AssemblyRef, rid, 7)),
            TokenFromBlob(image.GetBlob(tables.GetRowIndex(TableKind.AssemblyRef, rid, 5))));
    }

    /// <summary>4 つの U2 列 (Major/Minor/Build/Revision) から Version を読む。</summary>
    private static Version ReadVersion(MetadataTables tables, TableKind table, int rid, int firstColumn) =>
        new((int)tables.GetCell(table, rid, firstColumn),
            (int)tables.GetCell(table, rid, firstColumn + 1),
            (int)tables.GetCell(table, rid, firstColumn + 2),
            (int)tables.GetCell(table, rid, firstColumn + 3));

    /// <summary>公開鍵 (またはトークン) blob を公開鍵トークン (下位 8 バイトの 16 進小文字列) に
    /// 正規化する。8 バイトはトークンそのもの、それ以外 (完全鍵) は SHA-1 の下位 8 バイトを
    /// 逆順にしたもの (ECMA-335 / strong name の規約)。空 blob はトークン無し。</summary>
    private static string TokenFromBlob(ReadOnlySpan<byte> blob) {
        if (blob.IsEmpty)
            return "";
        if (blob.Length == 8)
            return Convert.ToHexString(blob).ToLowerInvariant();
        Span<byte> hash = stackalloc byte[20];
        if (!SHA1.TryHashData(blob, hash, out _))
            return "";
        var token = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            token.Append(hash[19 - i].ToString("x2"));
        return token.ToString();
    }
}
