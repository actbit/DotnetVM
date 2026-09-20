namespace DotnetVM.PE;

/// <summary>PE セクションテーブルに基づく RVA → ファイルオフセット変換。</summary>
public sealed class RvaMap {
    private readonly record struct Section(int Rva, int VirtualSize, int FileOffset, int RawSize);

    private readonly Section[] _sections;

    internal RvaMap(ReadOnlySpan<byte> image, int sectionCount, int sectionTableOffset) {
        _sections = new Section[sectionCount];
        for (var i = 0; i < sectionCount; i++) {
            var row = sectionTableOffset + i * 40;
            var virtualSize = (int)ReadU32(image, row + 8);
            var virtualAddress = (int)ReadU32(image, row + 12);
            var rawSize = (int)ReadU32(image, row + 16);
            var rawOffset = (int)ReadU32(image, row + 20);
            _sections[i] = new Section(virtualAddress, virtualSize, rawOffset, rawSize);
        }
    }

    /// <summary>RVA をファイルオフセットへ変換。範囲外なら例外。</summary>
    public int GetOffset(int rva) {
        foreach (var s in _sections) {
            var size = Math.Max(s.VirtualSize, s.RawSize);
            if (rva >= s.Rva && rva < s.Rva + size)
                return s.FileOffset + (rva - s.Rva);
        }
        throw new BadImageFormatException($"RVA 0x{rva:X} はどのセクションにも属しません。");
    }

    private static uint ReadU32(ReadOnlySpan<byte> image, int offset) =>
        (uint)(image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24));
}
