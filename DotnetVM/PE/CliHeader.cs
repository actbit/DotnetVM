using System.Buffers.Binary;

namespace DotnetVM.PE;

[Flags]
public enum CliRuntimeFlags : uint {
    ILOnly = 0x0000_0001,
    Requires32Bit = 0x0000_0002,
    StrongNameSigned = 0x0000_0008,
    TrackDebugData = 0x0001_0000,
    Prefer32Bit = 0x0002_0000,
}

/// <summary>CLI ヘッダ (データディレクトリ 14 が指す 72 バイト構造)。</summary>
public sealed class CliHeader {
    public ushort MajorRuntimeVersion { get; }
    public ushort MinorRuntimeVersion { get; }
    public int MetadataRva { get; }
    public int MetadataSize { get; }
    public CliRuntimeFlags Flags { get; }
    public int EntryPointToken { get; }

    private CliHeader(ushort major, ushort minor, int metadataRva, int metadataSize,
                      CliRuntimeFlags flags, int entryPointToken) {
        MajorRuntimeVersion = major;
        MinorRuntimeVersion = minor;
        MetadataRva = metadataRva;
        MetadataSize = metadataSize;
        Flags = flags;
        EntryPointToken = entryPointToken;
    }

    /// <summary>PE イメージの CorHeaderRva 位置から解析する。</summary>
    public static CliHeader ParseFrom(PEImage pe) {
        var header = pe.GetSegment(pe.CorHeaderRva, 72);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        var metadataRva = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var metadataSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var flags = (CliRuntimeFlags)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var entryPointToken = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        return new CliHeader(major, minor, metadataRva, metadataSize, flags, entryPointToken);
    }
}
