using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class ObjectEngine {
    // ---- 型トークン解決 ----

    public VmType ResolveTypeToken(int token, GenericContext? context = null,
        IReadOnlyDictionary<uint, object>? dynamicTokens = null) {
        if (dynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmType dynamicType)
            return dynamicType;
        return _loader.ResolveToken(new SigType(SigKind.TypeToken, Token: (uint)token), context);
    }

    /// <summary>MemberRef の TypeSpec 親を構築型として解決する。VAR/MVAR を含む場合は context で置換する。</summary>
    public VmConstructedType ResolveConstructedParent(int typeSpecRid, GenericContext? context) =>
        _loader.ResolveTypeSpec(typeSpecRid, context) as VmConstructedType
            ?? throw new BadImageFormatException($"TypeSpec 0x02{typeSpecRid:X6} は構築ジェネリック型ではありません。");

    /// <summary>intrinsic を派生ファサード型から基底連鎖まで辿って解決する (継承面のフォールバック)。</summary>
    public bool TryGetIntrinsicThroughHierarchy(string typeName, string name, int arity, bool hasThis, out IntrinsicImpl impl) {
        for (VmType? t = _loader.FindIntrinsicType(typeName); t is not null; t = t.BaseType) {
            if (_intrinsics.TryGet(new IntrinsicKey(t.FullName, name, arity, hasThis), out impl!))
                return true;
        }
        impl = null!;
        return false;
    }
}
