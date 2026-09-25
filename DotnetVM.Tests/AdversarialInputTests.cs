using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetVM.Binary;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.PE;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// 境界値、切り詰め、整数オーバーフローを意図的に組み合わせた入力の拒否テスト。
/// これらは正常な C# コンパイラ出力では通常現れないが、VM が受け取る外部 PE/IL は
/// 信頼できないため、ホスト例外や巨大確保へフォールスルーしないことを確認する。
/// </summary>
public sealed class AdversarialInputTests {
    [Fact]
    public void SpanReader_AdvanceRejectsOverflowWithoutChangingOffset() {
        var reader = new SpanReader(new byte[2]);
        reader.ReadByte();

        try {
            reader.Advance(int.MaxValue);
            Assert.Fail("オーバーフローする Advance は拒否される必要があります。");
        } catch (ArgumentOutOfRangeException) {
            // expected
        }

        Assert.Equal(1, reader.Offset);
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void SpanReader_ReadBytesRejectsNegativeCount() {
        var reader = new SpanReader(new byte[4]);

        try {
            reader.ReadBytes(-1);
            Assert.Fail("負の長さは拒否される必要があります。");
        } catch (ArgumentOutOfRangeException) {
            // expected
        }

        Assert.Equal(0, reader.Offset);
    }

    [Fact]
    public void SpanReader_RejectsUnsignedLeb128Overflow() {
        var reader = new SpanReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 });
        try {
            reader.ReadULEB128();
            Assert.Fail("uint32 を超える ULEB128 は拒否される必要があります。");
        } catch (FormatException) {
            // expected
        }
    }

    [Fact]
    public void SignatureDecoder_RejectsHugeCollectionCountsBeforeAllocation() {
        var huge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeMethodSignature([0x00, .. huge, 0x08]));
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeLocalsSignature([0x07, .. huge]));
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeMethodSpecInstantiation([0x0A, .. huge]));
    }

    [Fact]
    public void SignatureDecoder_RejectsCollectionCountsLargerThanRemainingBlob() {
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeMethodSignature([0x00, 0x03, 0x08]));
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeType([0x15, 0x12, 0x05, 0x02, 0x08]));
        Assert.Throws<BadImageFormatException>(() =>
            SignatureDecoder.DecodeType([0x14, 0x08, 0x01, 0x02]));
    }

    [Theory]
    [InlineData(new byte[] { 0xFE })]
    [InlineData(new byte[] { 0x20 })]
    [InlineData(new byte[] { 0x21, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x45, 0x01, 0x00, 0x00, 0x00 })]
    public void IlDecoder_ConvertsTruncatedOperandsToBadImage(byte[] il) {
        Assert.Throws<BadImageFormatException>(() => IlDecoder.Decode(il));
    }

    [Fact]
    public void IlDecoder_SwitchCountCannotCauseAnUnboundedAllocation() {
        Assert.Throws<BadImageFormatException>(() => IlDecoder.Decode(
            [0x45, 0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void IlDecoder_SwitchTargetTableMustBeComplete() {
        Assert.Throws<BadImageFormatException>(() => IlDecoder.Decode(
            [0x45, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void MetadataRoot_RejectsVersionLengthIntegerOverflow() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "MetadataVersionOverflow");
        var metadata = FindMetadata(bytes);

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(metadata.Offset + 12), int.MaxValue - 3);

        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(bytes));
    }

    [Fact]
    public void MetadataRoot_RejectsStreamRangeIntegerOverflow() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "MetadataStreamOverflow");
        var stream = FindMetadataStream(bytes, "#~");

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stream.HeaderOffset), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stream.HeaderOffset + 4), 1);

        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(bytes));
    }

    [Fact]
    public void MetadataTables_RejectsUnsignedRowCountThatBecomesNegative() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "MetadataRowOverflow");
        var tables = FindMetadataStream(bytes, "#~");
        var tablesOffset = tables.DataOffset;
        var validMask = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(tablesOffset + 8));
        var rowCountOffset = tablesOffset + 24;
        while ((validMask & 1) == 0) {
            validMask >>= 1;
            rowCountOffset += 4;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(rowCountOffset), uint.MaxValue);

        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(bytes));
    }

    [Fact]
    public void PeImage_RejectsOptionalHeaderThatCannotContainCliDirectory() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "OptionalHeaderTooShort");
        var peHeader = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var coffHeader = peHeader + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(coffHeader + 16), 2);

        Assert.Throws<BadImageFormatException>(() => PEImage.Parse(bytes));
    }

    [Fact]
    public void PeImage_GetSegmentRejectsNegativeSize() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "NegativeSegmentSize");
        var pe = PEImage.Parse(bytes);

        Assert.Throws<BadImageFormatException>(() => pe.GetSegment(pe.CorHeaderRva, -1).ToArray());
    }

    [Fact]
    public void RvaMap_UsesWideArithmeticForLargeVirtualRanges() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "RvaRangeOverflow");
        var sectionTable = FindSectionTable(bytes);
        var rawOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(sectionTable + 20));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sectionTable + 8), 0x7FFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sectionTable + 12), 1);

        var pe = PEImage.Parse(bytes);

        Assert.Equal(rawOffset + 99, pe.GetOffset(100));
    }

    [Fact]
    public void UserStringHeap_RejectsZeroLengthEntry() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static string Run() => \"hostile\"; }", "UserStringZeroLength");
        var userStrings = FindMetadataStream(bytes, "#US");
        Assert.True(userStrings.Size > 1, "#US ヒープにテスト文字列が必要です。");

        bytes[userStrings.DataOffset + 1] = 0;
        var image = AssemblyImage.Parse(bytes);

        Assert.Throws<BadImageFormatException>(() => image.GetUserString(1));
    }

    [Fact]
    public void BlobHeap_RejectsLengthThatExceedsRemainingStream() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run(int value) => value + 1; }", "BlobLengthOverflow");
        var image = AssemblyImage.Parse(bytes);
        var blobIndex = image.Tables.GetRowIndex(TableKind.MethodDef, 1, 4);
        Assert.True(blobIndex > 0, "メソッド署名 blob が必要です。");
        var blob = FindMetadataStream(bytes, "#Blob");

        bytes[blob.DataOffset + blobIndex] = 0xDF;
        bytes[blob.DataOffset + blobIndex + 1] = 0xFF;
        bytes[blob.DataOffset + blobIndex + 2] = 0xFF;
        bytes[blob.DataOffset + blobIndex + 3] = 0xFF;
        var malformed = AssemblyImage.Parse(bytes);

        Assert.Throws<BadImageFormatException>(() => malformed.GetBlob(blobIndex).ToArray());
    }

    [Fact]
    public void GuidHeap_RejectsIndexMultiplicationOverflow() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "GuidIndexOverflow");
        var image = AssemblyImage.Parse(bytes);

        Assert.Throws<BadImageFormatException>(() => image.Guids.GetGuid(int.MaxValue));
    }

    [Fact]
    public void MethodBody_RejectsMalformedExceptionSectionLength() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "using System; public static class Probe { public static int Run() { try { return 1; } catch (Exception) { return 2; } } }",
            "EhSectionLength");
        var image = AssemblyImage.Parse(bytes);
        var rid = FindMethodWithExceptionClauses(image);
        var rva = (int)image.Tables.GetCell(TableKind.MethodDef, rid, 0);
        var bodyOffset = image.PE.GetOffset(rva);
        var body = image.GetMethodBody(rid)!;
        var headerDwords = (bytes[bodyOffset] | (bytes[bodyOffset + 1] << 8)) >> 12 & 0xF;
        var headerSize = headerDwords * 4;
        var codeSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bodyOffset + 4));
        var sectionOffset = bodyOffset + ((headerSize + codeSize + 3) & ~3);

        if ((bytes[sectionOffset] & 0x40) == 0)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sectionOffset + 1), 3);
        else
            bytes[sectionOffset + 1] = 3;

        var malformed = AssemblyImage.Parse(bytes);
        Assert.Throws<BadImageFormatException>(() => malformed.GetMethodBody(rid));
        Assert.NotNull(body.ExceptionClauses);
    }

    [Fact]
    public void MethodBody_RejectsExceptionRangesOutsideIl() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "using System; public static class Probe { public static int Run() { try { return 1; } catch (Exception) { return 2; } } }",
            "EhRangeOverflow");
        var image = AssemblyImage.Parse(bytes);
        var rid = FindMethodWithExceptionClauses(image);
        var rva = (int)image.Tables.GetCell(TableKind.MethodDef, rid, 0);
        var bodyOffset = image.PE.GetOffset(rva);
        var body = image.GetMethodBody(rid)!;
        var headerDwords = (bytes[bodyOffset] | (bytes[bodyOffset + 1] << 8)) >> 12 & 0xF;
        var headerSize = headerDwords * 4;
        var codeSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bodyOffset + 4));
        var sectionOffset = bodyOffset + ((headerSize + codeSize + 3) & ~3);
        var clauseOffset = sectionOffset + 4;

        if ((bytes[sectionOffset] & 0x40) == 0) {
            var flagsAndTry = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(clauseOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(clauseOffset), (flagsAndTry & 0xFFFF) | ((uint)codeSize << 16));
        } else {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(clauseOffset + 4), (uint)codeSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(clauseOffset + 8), 1);
        }

        var malformed = AssemblyImage.Parse(bytes);
        Assert.Throws<BadImageFormatException>(() => malformed.GetMethodBody(rid));
        Assert.NotNull(body.ExceptionClauses);
    }

    [Fact]
    public void LoadAssembly_RejectsOverLimitBeforeRegisteringLoader() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "AssemblyQuota");
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { MaxAssemblyBytes = bytes.Length - 1 },
        });

        Assert.Throws<OperationNotAllowedException>(() => vm.LoadAssembly(new MemoryStream(bytes)));
        Assert.Empty(vm.Loaders);
    }

    [Fact]
    public void LoadAssembly_RejectsTruncatedImageWithoutRegisteringLoader() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }", "AssemblyTruncated");
        using var vm = new VirtualMachine();

        Assert.Throws<BadImageFormatException>(() =>
            vm.LoadAssembly(new MemoryStream(bytes, 0, 64, writable: false)));
        Assert.Empty(vm.Loaders);
    }

    [Fact]
    public void VirtualMachine_DisposeIsIdempotentAndClosesPublicBoundary() {
        var vm = new VirtualMachine();
        vm.Dispose();
        vm.Dispose();

        Assert.Throws<ObjectDisposedException>(() => vm.LoadAssembly(new MemoryStream([1, 2, 3])));
        Assert.Throws<ObjectDisposedException>(() => vm.Invoke("Missing.Type", "Run"));
    }

    private static int FindMethodWithExceptionClauses(AssemblyImage image) {
        var count = image.Tables.GetRowCount(TableKind.MethodDef);
        for (var rid = 1; rid <= count; rid++) {
            if (image.Tables.GetCell(TableKind.MethodDef, rid, 0) == 0)
                continue;
            var body = image.GetMethodBody(rid);
            if (body?.ExceptionClauses is { Length: > 0 })
                return rid;
        }
        throw new InvalidOperationException("EH 句を持つメソッドが見つかりません。");
    }

    private static (int Offset, int Size) FindMetadata(byte[] pe) {
        using var reader = new PEReader(new MemoryStream(pe, writable: false));
        var directory = reader.PEHeaders.CorHeader?.MetadataDirectory
            ?? throw new InvalidOperationException("CLI metadata がありません。");
        var section = reader.PEHeaders.SectionHeaders.First(s =>
            directory.RelativeVirtualAddress >= s.VirtualAddress &&
            directory.RelativeVirtualAddress < s.VirtualAddress + s.SizeOfRawData);
        return (checked(section.PointerToRawData +
            directory.RelativeVirtualAddress - section.VirtualAddress), directory.Size);
    }

    private static int FindSectionTable(byte[] pe) {
        var peHeader = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        var coffHeader = peHeader + 4;
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(coffHeader + 16));
        return checked(coffHeader + 20 + optionalHeaderSize);
    }

    private static (int HeaderOffset, int DataOffset, int Size) FindMetadataStream(
        byte[] pe, string wantedName) {
        var metadata = FindMetadata(pe);
        var root = pe.AsSpan(metadata.Offset, metadata.Size);
        var versionLength = BinaryPrimitives.ReadInt32LittleEndian(root[12..]);
        var versionEnd = checked(16 + versionLength);
        var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(root[(versionEnd + 2)..]);
        var cursor = versionEnd + 4;
        for (var i = 0; i < streamCount; i++) {
            var headerOffset = cursor;
            var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(root[cursor..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(root[(cursor + 4)..]);
            cursor += 8;
            var nameStart = cursor;
            while (cursor < root.Length && root[cursor] != 0)
                cursor++;
            var name = Encoding.ASCII.GetString(root[nameStart..cursor]);
            cursor = (cursor + 1 + 3) & ~3;
            if (name == wantedName)
                return (metadata.Offset + headerOffset, metadata.Offset + relativeOffset, size);
        }
        throw new InvalidOperationException($"メタデータストリーム '{wantedName}' がありません。");
    }
}
