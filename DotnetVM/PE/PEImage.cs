using System.Buffers.Binary;

namespace DotnetVM.PE;

/// <summary>PE32 / PE32+ イメージの解析結果。DLL アセンブリ (.NET 5+) を対象とする。</summary>
public sealed class PEImage {
    private readonly ReadOnlyMemory<byte> _image;

    public int CorHeaderRva { get; }
    public RvaMap RvaMap { get; }
    public int ByteLength => _image.Length;

    private PEImage(ReadOnlyMemory<byte> image, int corHeaderRva, RvaMap rvaMap) {
        _image = image;
        CorHeaderRva = corHeaderRva;
        RvaMap = rvaMap;
    }

    public static PEImage Parse(ReadOnlyMemory<byte> image) {
        var span = image.Span;

        // DOS ヘッダ: 'MZ' シグネチャ、e_lfanew (0x3C)
        if (span.Length < 0x40 || span[0] != (byte)'M' || span[1] != (byte)'Z')
            throw new BadImageFormatException("DOS ヘッダ ('MZ') が見つかりません。");
        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(span[0x3C..]);
        if (peHeaderOffset < 0 || peHeaderOffset > span.Length - 24)
            throw new BadImageFormatException("PE ヘッダのオフセットが不正です。");

        // PE シグネチャ 'PE\0\0' + COFF ヘッダ (20バイト)
        if (span[peHeaderOffset] != (byte)'P' || span[peHeaderOffset + 1] != (byte)'E'
            || span[peHeaderOffset + 2] != 0 || span[peHeaderOffset + 3] != 0)
            throw new BadImageFormatException("PE シグネチャ ('PE\\0\\0') が見つかりません。");
        var coffHeader = peHeaderOffset + 4;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[(coffHeader + 2)..]);
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(coffHeader + 16)..]);
        var optionalHeader = coffHeader + 20;
        if (optionalHeader > span.Length || optionalHeaderSize > span.Length - optionalHeader || optionalHeaderSize < 2)
            throw new BadImageFormatException("オプションヘッダが範囲外です。");

        // オプションヘッダ: Magic 0x10B = PE32, 0x20B = PE32+
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(span[optionalHeader..]);
        var dataDirectoryOffset = magic switch {
            0x10B => optionalHeader + 96,
            0x20B => optionalHeader + 112,
            _ => throw new BadImageFormatException($"未知のオプションヘッダ Magic: 0x{magic:X}"),
        };
        const int CorDataDirectoryIndex = 14;   // CLR Runtime Header
        var corDirectoryOffset = checked(dataDirectoryOffset + CorDataDirectoryIndex * 8);
        if (corDirectoryOffset < optionalHeader || corDirectoryOffset > optionalHeader + optionalHeaderSize - 8 ||
            corDirectoryOffset > span.Length - 8)
            throw new BadImageFormatException("CLI データディレクトリがオプションヘッダの範囲外です。");
        var corHeaderRva = BinaryPrimitives.ReadInt32LittleEndian(span[corDirectoryOffset..]);
        if (corHeaderRva == 0)
            throw new BadImageFormatException("CLR ランタイムヘッダ (データディレクトリ 14) が空です。CLI アセンブリではありません。");

        var sectionTableOffset = optionalHeader + optionalHeaderSize;
        if (sectionTableOffset > span.Length || sectionCount > (span.Length - sectionTableOffset) / 40)
            throw new BadImageFormatException("セクションテーブルが範囲外です。");

        return new PEImage(image, corHeaderRva, new RvaMap(span, sectionCount, sectionTableOffset));
    }

    /// <summary>RVA に対応するファイルオフセット。</summary>
    public int GetOffset(int rva) => RvaMap.GetOffset(rva);

    /// <summary>RVA 位置から size バイトのセグメントを返す。</summary>
    public ReadOnlySpan<byte> GetSegment(int rva, int size) {
        var offset = GetOffset(rva);
        var span = _image.Span;
        if (size < 0 || offset < 0 || offset > span.Length - size)
            throw new BadImageFormatException($"RVA 0x{rva:X} のセグメント (size={size}) がファイル末尾を超えます。");
        return span.Slice(offset, size);
    }

    /// <summary>RVA 位置から始まるデータ全体 (メタデータルート等の可変長データ用)。</summary>
    public ReadOnlyMemory<byte> GetSegmentToEnd(int rva) {
        var offset = GetOffset(rva);
        return _image[offset..];
    }
}
