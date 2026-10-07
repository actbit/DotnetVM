using System.Buffers.Binary;

namespace DotnetVM.PE;

/// <summary>PE セクションテーブルに基づく RVA → ファイルオフセット変換。</summary>
public sealed class RvaMap {
    private readonly record struct Section(int Rva, int VirtualSize, int FileOffset, int RawSize);

    private readonly Section[] _sections;
    private readonly int _imageLength;

    internal RvaMap(ReadOnlySpan<byte> image, int sectionCount, int sectionTableOffset) {
        _imageLength = image.Length;
        _sections = new Section[sectionCount];
        for (var i = 0; i < sectionCount; i++) {
            var row = sectionTableOffset + i * 40;
            var virtualSize = ReadInt32(image, row + 8, "VirtualSize");
            var virtualAddress = ReadInt32(image, row + 12, "VirtualAddress");
            var rawSize = ReadInt32(image, row + 16, "SizeOfRawData");
            var rawOffset = ReadInt32(image, row + 20, "PointerToRawData");
            _sections[i] = new Section(virtualAddress, virtualSize, rawOffset, rawSize);
        }
    }

    /// <summary>RVA をファイルオフセットへ変換。範囲外なら例外。</summary>
    public int GetOffset(int rva) {
        if (rva < 0)
            throw new BadImageFormatException($"負の RVA 0x{rva:X8} は不正です。");
        foreach (var s in _sections) {
            var delta = (long)rva - s.Rva;
            // VirtualSize may be larger than SizeOfRawData. The tail is
            // zero-filled memory in the loaded image, not bytes in the file,
            // and must never be exposed as a file offset.
            if (delta >= 0 && delta < s.RawSize) {
                var fileOffset = (long)s.FileOffset + delta;
                if (fileOffset < 0 || fileOffset > _imageLength)
                    throw new BadImageFormatException($"RVA 0x{rva:X} のファイルオフセットが範囲外です。");
                return (int)fileOffset;
            }
        }
        throw new BadImageFormatException($"RVA 0x{rva:X} はどのセクションにも属しません。");
    }

    private static int ReadInt32(ReadOnlySpan<byte> image, int offset, string field) {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]);
        if (value > int.MaxValue)
            throw new BadImageFormatException($"PE セクションの {field} がサポート範囲外です ({value:X8})。");
        return (int)value;
    }
}
