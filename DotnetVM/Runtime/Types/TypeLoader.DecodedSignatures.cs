using System.Collections.Concurrent;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // Cache immutable signature syntax only. Resolution, caller provenance and
    // generic substitution still run against the current loader/context.
    // Entries are bounded by the image's metadata rows and die with the loader.
    private readonly ConcurrentDictionary<int, MethodSignature> _memberRefMethodSignatures = new();
    private readonly ConcurrentDictionary<int, SigType[]> _methodSpecSignatures = new();
    private readonly ConcurrentDictionary<int, SigType> _typeSpecSignatures = new();

    internal MethodSignature DecodeMemberRefMethodSignature(int rid) =>
        _memberRefMethodSignatures.GetOrAdd(rid, static (key, loader) =>
            SignatureDecoder.DecodeMethodSignature(loader.Image.GetMemberRefSignature(key),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64), this);

    internal SigType[] DecodeMethodSpecSignature(int rid) =>
        _methodSpecSignatures.GetOrAdd(rid, static (key, loader) =>
            SignatureDecoder.DecodeMethodSpecInstantiation(loader.Image.GetBlob(
                loader.Image.Tables.GetRowIndex(TableKind.MethodSpec, key, 1)),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64), this);

    private SigType DecodeTypeSpecSignature(int rid) =>
        _typeSpecSignatures.GetOrAdd(rid, static (key, loader) =>
            SignatureDecoder.DecodeTypeSpecSignature(loader.Image.GetBlob(
                loader.Image.Tables.GetRowIndex(TableKind.TypeSpec, key, 0)),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64).Type, this);
}
