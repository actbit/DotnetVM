using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetVM.Host;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>TypeRef の ResolutionScope を PE metadata 上で書き換えた回帰テスト。
/// 通常の C# コンパイラでは生成されない Module facade と TypeRef cycle を作り、
/// provenance 判定が FullName の intrinsic fallback だけを根拠に trusted binding へ
/// 到達させないこと、また malformed chain が無限ループしないことを確認する。</summary>
public sealed class MalformedMetadataTests {
    private const string IntrinsicSurfaceSource = """
        namespace Vm.Malformed {
            public static class Ops {
                public static int TypeSurface() => System.Type.GetType("System.Object") is null ? 1 : 0;
                public static int SynchronizationSurface() {
                    System.Threading.SynchronizationContext.SetSynchronizationContext(
                        new System.Threading.SynchronizationContext());
                    return 1;
                }
                public static int CancellationSurface() {
                    var source = new System.Threading.CancellationTokenSource();
                    source.Cancel();
                    return 1;
                }
            }
        }
        """;

    [Fact]
    public void UntrustedModuleScopedIntrinsicTypeRefsCannotReachRuntimeBinding() {
        var pe = TestAssemblyCompiler.CompileToBytes(IntrinsicSurfaceSource, "ModuleScopedSurfaces");
        pe = PatchTypeRefScopes(pe,
            ("System.Type", TableKind.Module, 1),
            ("System.Threading.SynchronizationContext", TableKind.Module, 1),
            ("System.Threading.CancellationTokenSource", TableKind.Module, 1));

        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(pe);
        vm.LoadAssembly(stream);

        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Invoke("Vm.Malformed.Ops", "TypeSurface"));
        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Invoke("Vm.Malformed.Ops", "SynchronizationSurface"));
        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Invoke("Vm.Malformed.Ops", "CancellationSurface"));
    }

    [Fact]
    public void SelfReferentialTypeRefFailsClosedInsteadOfLooping() {
        var pe = TestAssemblyCompiler.CompileToBytes(IntrinsicSurfaceSource, "SelfCycle");
        var typeRid = FindTypeRefRids(pe)["System.Type"];
        pe = PatchTypeRefScopes(pe, ("System.Type", TableKind.TypeRef, typeRid));

        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(pe);
        vm.LoadAssembly(stream);

        Assert.Throws<BadImageFormatException>(() =>
            vm.Invoke("Vm.Malformed.Ops", "TypeSurface"));
    }

    [Fact]
    public void TwoNodeTypeRefCycleFailsClosedInsteadOfLooping() {
        var pe = TestAssemblyCompiler.CompileToBytes(IntrinsicSurfaceSource, "TwoNodeCycle");
        var rids = FindTypeRefRids(pe);
        var other = rids.First(pair => pair.Key != "System.Type");
        pe = PatchTypeRefScopes(pe,
            ("System.Type", TableKind.TypeRef, other.Value),
            (other.Key, TableKind.TypeRef, rids["System.Type"]));

        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(pe);
        vm.LoadAssembly(stream);

        Assert.Throws<BadImageFormatException>(() =>
            vm.Invoke("Vm.Malformed.Ops", "TypeSurface"));
    }

    private static Dictionary<string, int> FindTypeRefRids(byte[] pe) {
        using var reader = new PEReader(new MemoryStream(pe, writable: false));
        var metadata = reader.GetMetadataReader();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences) {
            var row = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(row.Namespace);
            var name = metadata.GetString(row.Name);
            var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            result.TryAdd(fullName, MetadataTokens.GetRowNumber(handle));
        }
        return result;
    }

    private static byte[] PatchTypeRefScopes(byte[] original,
        params (string TypeName, TableKind ScopeTable, int ScopeRid)[] patches) {
        var bytes = original.ToArray();
        var typeRefRids = FindTypeRefRids(bytes);
        var requested = patches.ToDictionary(
            patch => typeRefRids[patch.TypeName], patch => EncodeScope(patch.ScopeTable, patch.ScopeRid));

        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var headers = pe.PEHeaders;
        var metadataDirectory = headers.CorHeader?.MetadataDirectory
            ?? throw new InvalidOperationException("CLI metadata directory がありません。");
        var section = headers.SectionHeaders.First(section =>
            metadataDirectory.RelativeVirtualAddress >= section.VirtualAddress &&
            metadataDirectory.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
        var metadataOffset = checked(section.PointerToRawData +
            metadataDirectory.RelativeVirtualAddress - section.VirtualAddress);

        var tablesOffset = FindStreamOffset(bytes, metadataOffset, metadataDirectory.Size, "#~");
        var heapSizes = bytes[tablesOffset + 6];
        var validMask = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(tablesOffset + 8));
        var rowCounts = new int[64];
        var cursor = tablesOffset + 24;
        for (var table = 0; table < rowCounts.Length; table++) {
            if ((validMask & (1UL << table)) != 0) {
                rowCounts[table] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
                cursor += 4;
            }
        }

        var tableDataOffset = cursor;
        var stringWidth = (heapSizes & 0x01) != 0 ? 4 : 2;
        var guidWidth = (heapSizes & 0x02) != 0 ? 4 : 2;
        var moduleRowSize = 2 + stringWidth + guidWidth * 3;
        var typeRefOffset = tableDataOffset + rowCounts[(int)TableKind.Module] * moduleRowSize;
        var maxResolutionScopeRows = new[] {
            rowCounts[(int)TableKind.Module], rowCounts[(int)TableKind.ModuleRef],
            rowCounts[(int)TableKind.AssemblyRef], rowCounts[(int)TableKind.TypeRef],
        }.Max();
        var scopeWidth = maxResolutionScopeRows < 0x4000 ? 2 : 4;
        var typeRefRowSize = scopeWidth + stringWidth * 2;

        foreach (var (rid, scope) in requested) {
            var offset = checked(typeRefOffset + (rid - 1) * typeRefRowSize);
            if (scopeWidth == 2)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)scope));
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), scope);
        }
        return bytes;
    }

    private static uint EncodeScope(TableKind table, int rid) {
        var tag = table switch {
            TableKind.Module => 0,
            TableKind.ModuleRef => 1,
            TableKind.AssemblyRef => 2,
            TableKind.TypeRef => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        return checked(((uint)rid << 2) | (uint)tag);
    }

    private static int FindStreamOffset(byte[] pe, int metadataOffset, int metadataSize, string wantedName) {
        var rootEnd = checked(metadataOffset + metadataSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(metadataOffset)) != 0x424A5342)
            throw new InvalidOperationException("metadata root signature がありません。");
        var versionLength = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(metadataOffset + 12));
        var versionEnd = checked(16 + versionLength);
        var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(metadataOffset + versionEnd + 2));
        var cursor = metadataOffset + versionEnd + 4;
        for (var i = 0; i < streamCount; i++) {
            var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(cursor));
            cursor += 8;
            var nameStart = cursor;
            while (cursor < rootEnd && pe[cursor] != 0)
                cursor++;
            var name = Encoding.ASCII.GetString(pe, nameStart, cursor - nameStart);
            cursor = metadataOffset + (((cursor - metadataOffset) + 4) & ~3);
            if (name == wantedName)
                return checked(metadataOffset + relativeOffset);
        }
        throw new InvalidOperationException($"metadata stream '{wantedName}' がありません。");
    }
}
