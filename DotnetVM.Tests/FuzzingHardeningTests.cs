using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
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
/// Deterministic, bounded fuzz-style tests for the untrusted byte boundaries.
/// These are deliberately seedable and run in the normal test suite so a
/// regression is reproducible from the failing seed instead of depending on a
/// wall-clock random campaign.
/// </summary>
public sealed class FuzzingHardeningTests {
    [Fact]
    public void SpanReader_RandomInputsOnlyProduceExpectedParseFailures() {
        var random = new Random(0x5EED_2026);
        for (var iteration = 0; iteration < 12_000; iteration++) {
            var data = new byte[random.Next(0, 48)];
            random.NextBytes(data);

            try { _ = new SpanReader(data).ReadULEB128(); }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException) { }
            try { _ = new SpanReader(data).ReadSLEB128(); }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException) { }
            try { _ = new SpanReader(data).ReadCompressedUInt32(); }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException) { }
            try { _ = new SpanReader(data).ReadCompressedInt32(); }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException) { }
        }
    }

    [Fact]
    public void IlDecoder_RandomInputsNeverLeakImplementationExceptions() {
        var random = new Random(unchecked((int)0x1A26_1007));
        for (var iteration = 0; iteration < 12_000; iteration++) {
            var data = new byte[random.Next(0, 384)];
            random.NextBytes(data);
            try {
                _ = IlDecoder.Decode(data);
            } catch (BadImageFormatException) {
                // Expected for the overwhelming majority of random IL.
            }
        }
    }

    [Fact]
    public void SignatureDecoder_RandomInputsNeverLeakImplementationExceptions() {
        var random = new Random(0x51_9A_2026);
        for (var iteration = 0; iteration < 10_000; iteration++) {
            var data = new byte[random.Next(0, 192)];
            random.NextBytes(data);

            AssertSafeSignature(() => SignatureDecoder.DecodeMethodSignature(data));
            AssertSafeSignature(() => SignatureDecoder.DecodeFieldSignature(data));
            AssertSafeSignature(() => SignatureDecoder.DecodeTypeSpecSignature(data));
            AssertSafeSignature(() => SignatureDecoder.DecodeMethodSpecInstantiation(data));
            AssertSafeSignature(() => SignatureDecoder.DecodeLocalsSignature(data));
        }
    }

    [Fact]
    public void PeAndMetadataMutationCampaign_FailsClosedForThousandsOfInputs() {
        var original = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run(int value) => value + 1; }",
            "FuzzMutationCorpus");
        var limits = new MemoryPolicy {
            MaxAssemblyBytes = original.Length + 1,
            MaxMetadataStreamBytes = original.Length + 1,
            MaxMetadataRows = 100_000,
            MaxMethodBodyBytes = 256 * 1024,
        };

        // Every truncation boundary is a useful corpus item and catches
        // readers that assume a later structure exists after an earlier one.
        for (var length = 0; length <= original.Length; length += 7)
            AssertSafeImageParse(original.AsMemory(0, length), limits);
        AssertSafeImageParse(original, limits);

        var random = new Random(unchecked((int)0xF0_22_2026));
        for (var iteration = 0; iteration < 2_500; iteration++) {
            var mutated = original.ToArray();
            var mutations = 1 + random.Next(5);
            for (var mutation = 0; mutation < mutations; mutation++)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            AssertSafePeParse(mutated);
            AssertSafeImageParse(mutated, limits);
        }
    }

    [Fact]
    public void PeImage_DoesNotMapVirtualZeroFillTailToFileBytes() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }",
            "VirtualTailCorpus");
        var sectionTable = FindSectionTable(bytes);
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C)) + 4 + 2, 2));
        Assert.True(sectionCount > 0);

        var row = sectionTable;
        var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(row + 12));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(row + 16));
        Assert.True(rawSize > 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row + 8), rawSize + 1);

        var pe = PEImage.Parse(bytes);
        Assert.Throws<BadImageFormatException>(() => pe.GetOffset(checked((int)(virtualAddress + rawSize))));
    }

    [Fact]
    public void TruncatedTablesStreamIsNormalizedToBadImageFormat() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Probe { public static int Run() => 1; }",
            "TruncatedTablesStream");
        var stream = FindMetadataStream(bytes, "#~");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stream.HeaderOffset + 4), 1);

        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(bytes));
    }

    private static void AssertSafeSignature(Action parse) {
        try {
            parse();
        } catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException) {
            // Both are intentional fail-closed outcomes for unsupported or
            // malformed signature encodings.
        }
    }

    private static void AssertSafePeParse(byte[] bytes) {
        try {
            _ = PEImage.Parse(bytes);
        } catch (BadImageFormatException) {
            // Expected for malformed PE data.
        }
    }

    private static void AssertSafeImageParse(ReadOnlyMemory<byte> bytes, MemoryPolicy limits) {
        try {
            _ = AssemblyImage.Parse(bytes, limits);
        } catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or OperationNotAllowedException) {
            // Expected fail-closed outcomes for a mutated external image.
        }
    }

    private static int FindSectionTable(byte[] pe) {
        var peHeader = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        var coffHeader = checked(peHeader + 4);
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(coffHeader + 16, 2));
        return checked(coffHeader + 20 + optionalHeaderSize);
    }

    private static (int HeaderOffset, int DataOffset, int Size) FindMetadataStream(
        byte[] pe, string wantedName) {
        using var reader = new PEReader(new MemoryStream(pe, writable: false));
        var directory = reader.PEHeaders.CorHeader?.MetadataDirectory
            ?? throw new InvalidOperationException("CLI metadata directory がありません。");
        var section = reader.PEHeaders.SectionHeaders.First(s =>
            directory.RelativeVirtualAddress >= s.VirtualAddress &&
            directory.RelativeVirtualAddress < s.VirtualAddress + s.SizeOfRawData);
        var metadataOffset = checked(section.PointerToRawData +
            directory.RelativeVirtualAddress - section.VirtualAddress);
        var root = pe.AsSpan(metadataOffset, directory.Size);
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
            var name = System.Text.Encoding.ASCII.GetString(root[nameStart..cursor]);
            cursor = checked((cursor + 1 + 3) & ~3);
            if (name == wantedName)
                return (metadataOffset + headerOffset, metadataOffset + relativeOffset, size);
        }
        throw new InvalidOperationException($"metadata stream '{wantedName}' がありません。");
    }
}
