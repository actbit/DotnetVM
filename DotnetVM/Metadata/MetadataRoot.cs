using System.Buffers.Binary;
using DotnetVM.PE;

namespace DotnetVM.Metadata;

/// <summary>メタデータルート (BSJB) とストリームヘッダの解析結果。</summary>
public sealed class MetadataRoot {
    /// <summary>#~ ストリーム (圧縮テーブル)。</summary>
    public ReadOnlyMemory<byte> TablesStream { get; private init; }
    /// <summary>#Strings ストリーム。</summary>
    public ReadOnlyMemory<byte> StringsStream { get; private init; }
    /// <summary>#US ストリーム。</summary>
    public ReadOnlyMemory<byte> UserStringsStream { get; private init; }
    /// <summary>#Blob ストリーム。</summary>
    public ReadOnlyMemory<byte> BlobStream { get; private init; }
    /// <summary>#GUID ストリーム。</summary>
    public ReadOnlyMemory<byte> GuidStream { get; private init; }

    public string VersionString { get; private init; } = "";

    /// <summary>CLI ヘッダが指すメタデータ領域 (RVA + size) を解析する。
    /// limits を渡すと各ストリームを受理する前に MaxMetadataStreamBytes を強制する
    /// (巨大 #Blob / #Strings 等を丸ごと受理する前に拒否する)。</summary>
    public static MetadataRoot Parse(PEImage pe, CliHeader cli, Host.MemoryPolicy? limits = null) {
        var root = pe.GetSegment(cli.MetadataRva, cli.MetadataSize);

        if (root.Length < 16 || root[0] != (byte)'B' || root[1] != (byte)'S'
            || root[2] != (byte)'J' || root[3] != (byte)'B')
            throw new BadImageFormatException("メタデータルートのシグネチャ ('BSJB') が不正です。");

        var versionLength = BinaryPrimitives.ReadInt32LittleEndian(root[12..]);
        var versionEndLong = 16L + versionLength;
        if (versionLength < 0 || versionLength % 4 != 0 ||
            versionEndLong < 16 || versionEndLong > root.Length - 4)
            throw new BadImageFormatException("メタデータルートのバージョン文字列長が不正です。");
        var versionEnd = (int)versionEndLong;

        var versionString = System.Text.Encoding.ASCII.GetString(root.Slice(16, versionLength).TrimEnd((byte)0));
        BinaryPrimitives.ReadUInt16LittleEndian(root[versionEnd..]);      // Flags (未使用)
        var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(root[(versionEnd + 2)..]);

        var tables = default(ReadOnlyMemory<byte>);
        var strings = default(ReadOnlyMemory<byte>);
        var userStrings = default(ReadOnlyMemory<byte>);
        var blob = default(ReadOnlyMemory<byte>);
        var guid = default(ReadOnlyMemory<byte>);

        // ストリームヘッダ: オフセット (u4, ルート先頭からの相対) + サイズ (u4) + 名前 (null 終端 ASCII, 4 バイト整列)
        var p = versionEnd + 4;
        for (var i = 0; i < streamCount; i++) {
            if (p + 8 > root.Length)
                throw new BadImageFormatException("ストリームヘッダが範囲外です。");
            var streamOffset = BinaryPrimitives.ReadInt32LittleEndian(root[p..]);
            var streamSize = BinaryPrimitives.ReadInt32LittleEndian(root[(p + 4)..]);
            p += 8;

            var nameStart = p;
            while (p < root.Length && root[p] != 0)
                p++;
            if (p >= root.Length)
                throw new BadImageFormatException("メタデータストリーム名が null 終端されていません。");
            var name = System.Text.Encoding.ASCII.GetString(root.Slice(nameStart, p - nameStart));
            p++;                       // null 終端
            p = checked((p + 3) & ~3);  // 4 バイト整列
            if (p > root.Length)
                throw new BadImageFormatException("メタデータストリームヘッダの整列位置が範囲外です。");

            if (streamOffset < 0 || streamSize < 0 || streamOffset > root.Length ||
                streamSize > root.Length - streamOffset)
                throw new BadImageFormatException($"ストリーム '{name}' がメタデータ領域を超えています。");
            if (limits is not null && streamSize > limits.MaxMetadataStreamBytes)
                throw new BadImageFormatException(
                    $"メタデータストリーム '{name}' のサイズ {streamSize:N0} が上限 {limits.MaxMetadataStreamBytes:N0} を超えています。");
            var data = root.Slice(streamOffset, streamSize).ToArray();

            switch (name) {
                case "#~":
                    tables = data;
                    break;
                case "#-":
                    throw new NotSupportedException("非圧縮メタデータ (#-) は対応していません。");
                case "#Strings":
                    strings = data;
                    break;
                case "#US":
                    userStrings = data;
                    break;
                case "#Blob":
                    blob = data;
                    break;
                case "#GUID":
                    guid = data;
                    break;
                default:
                    throw new NotSupportedException($"未知のメタデータストリーム: '{name}'");
            }
        }

        if (tables.Length == 0)
            throw new BadImageFormatException("#~ ストリームがありません。");

        return new MetadataRoot {
            TablesStream = tables,
            StringsStream = strings,
            UserStringsStream = userStrings,
            BlobStream = blob,
            GuidStream = guid,
            VersionString = versionString,
        };
    }
}
