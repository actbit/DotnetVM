using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // ---- 仮想/インターフェースディスパッチ表 (C3) ----

    /// <summary>本画像のディスパッチ表ビルダ (遅延生成)。</summary>
    internal DispatchMapBuilder DispatchBuilder => _dispatchBuilder ??= new DispatchMapBuilder(this);

    /// <summary>型のディスパッチ表 (VTable + InterfaceMap) を取得する (遅延構築・キャッシュ)。
    /// 他アセンブリの型はそのローダのビルダに委譲する (索引は画像ごとに分かれる)。</summary>
    internal DispatchMaps EnsureDispatchMaps(VmClassType type) {
        var owner = type.Loader;
        lock (MetadataGate)
            return owner is null || ReferenceEquals(owner, this) ? DispatchBuilder.EnsureMaps(type) : owner.EnsureDispatchMaps(type);
    }

    /// <summary>スロットキー用にパラメータ型列 (SigType) を VmType 列へ解決する。
    /// 1 つでも解決できない型があれば null (呼出側はパラメータ数照合にフォールバック)。</summary>
    internal VmType[]? TryResolveSlotParams(SigType[] paramTypes) {
        var result = new VmType[paramTypes.Length];
        for (var i = 0; i < paramTypes.Length; i++) {
            try {
                result[i] = ResolveToken(paramTypes[i]);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
                or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
                return null;
            }
        }
        return result;
    }
}
