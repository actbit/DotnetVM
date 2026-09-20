using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// 仮想/インターフェースディスパッチ表 (VTable + InterfaceMap) の構築担当。
/// メソッド識別 = (名前, パラメータ型の正規化キー)。ジェネリック型は「宣言側の
/// ジェネリックパラメータ (!n) を残した開いたキー」で統一し、継承パスの型引数による
/// 具体化は基底表の引き継ぎ時 (実装側文脈へ) と呼出側クエリの構築時に行う。
///
/// InterfaceImpl / MethodImpl テーブルは初回構築時に rid 索引化する (CoreLib 規模の
/// 画像で型ごとの全表走査を避ける)。表は VmClassType ごとに 1 回だけ構築しキャッシュする。
/// </summary>
internal sealed class DispatchMapBuilder {
    private readonly TypeLoader _loader;
    private readonly Dictionary<VmClassType, DispatchMaps> _maps = [];
    private readonly HashSet<VmClassType> _building = [];
    private Dictionary<int, List<int>>? _interfaceImpls;   // TypeDef rid → InterfaceImpl rid 群
    private Dictionary<int, List<int>>? _methodImpls;      // TypeDef rid → MethodImpl rid 群

    internal DispatchMapBuilder(TypeLoader loader) => _loader = loader;

    internal DispatchMaps EnsureMaps(VmClassType type) {
        if (_maps.TryGetValue(type, out var cached))
            return cached;
        // 循環する基底連鎖 (異常画像) の再入は空表で打ち切る (無限再帰防止)
        if (!_building.Add(type))
            return new DispatchMaps();
        try {
            var maps = Build(type);
            _maps[type] = maps;
            return maps;
        } finally {
            _building.Remove(type);
        }
    }

    private DispatchMaps Build(VmClassType type) {
        var maps = new DispatchMaps();
        InheritBaseMaps(type, maps);
        RegisterOwnMethods(type, maps);
        ApplyMethodImpls(type, maps);
        MapImplementedInterfaces(type, maps);
        PropagateMostDerivedOverrides(maps);
        return maps;
    }

    /// <summary>基底クラスの表を引き継ぐ。構築基底 (D : A&lt;int&gt;) ならその型引数で
    /// スロットを D の文脈へ置換する。インターフェースマップのキーはインターフェース定義文脈
    /// (実体化に依存しない) なので置換なしでそのまま引き継ぐ。</summary>
    private void InheritBaseMaps(VmClassType type, DispatchMaps maps) {
        VmType[]? baseArgs = null;
        VmClassType? baseClass = null;
        switch (type.BaseType) {
            case VmConstructedType constructed when constructed.Definition is VmClassType definition:
                baseClass = definition;
                baseArgs = constructed.TypeArguments;
                break;
            case VmClassType definition:
                baseClass = definition;
                break;
        }
        if (baseClass is null)
            return; // ファサード基底 (System.Object 等) / 基底無し。実装は従来の名前照合にフォールバック

        var baseMaps = OwnerBuilder(baseClass).EnsureMaps(baseClass);
        var substitution = baseArgs is { Length: > 0 } ? new GenericContext { ClassArgs = baseArgs } : null;
        foreach (var slot in baseMaps.VTable.Values) {
            var parameters = substitution is null ? slot.ParamTypes
                : [.. slot.ParamTypes.Select(p => GenericSubstitutor.Substitute(p, substitution))];
            var inherited = new DispatchSlot { Name = slot.Name, ParamTypes = parameters, Method = slot.Method };
            maps.VTable[inherited.Key] = inherited;
        }
        foreach (var (key, impl) in baseMaps.InterfaceMap)
            maps.InterfaceMap[key] = impl;
    }

    /// <summary>自身のインスタンスメソッドをスロットに登録する (基底の同一スロットを派生側で上書き)。
    /// abstract / 本体無し (InternalCall / P/Invoke) は実装スロットにならない。</summary>
    private void RegisterOwnMethods(VmClassType type, DispatchMaps maps) {
        foreach (var method in type.Methods) {
            if (method.IsStatic || !method.Signature.HasThis || method.Body is null)
                continue;
            var parameters = LoaderOf(method).TryResolveSlotParams(method.Signature.ParamTypes);
            if (parameters is null)
                continue; // 署名解決不能なメソッドは表に載せない (呼出側の名前照合にフォールバック)
            var slot = new DispatchSlot { Name = method.Name, ParamTypes = parameters, Method = method };
            maps.VTable[slot.Key] = slot;
        }
    }

    /// <summary>MethodImpl テーブル (明示的 override / 明示的インターフェース実装 EII) を反映する。
    /// 宣言がインターフェース メソッド (MethodDef 直参照または MemberRef) なら InterfaceMap、
    /// それ以外は VTable の明示 override として反映する。</summary>
    private void ApplyMethodImpls(VmClassType type, DispatchMaps maps) {
        foreach (var rid in MethodImplRids(type)) {
            var bodyTag = _loader.Image.Tables.DecodeCoded(TableKind.MethodImpl, rid, 1, CodedIndexKind.MethodDefOrRef);
            var declTag = _loader.Image.Tables.DecodeCoded(TableKind.MethodImpl, rid, 2, CodedIndexKind.MethodDefOrRef);
            if (bodyTag.Table != TableKind.MethodDef)
                continue;
            var body = _loader.GetMethodByToken(Token.From(TableKind.MethodDef, bodyTag.Rid).Value);
            if (body is null)
                continue;
            if (declTag.Table == TableKind.MethodDef) {
                var decl = _loader.GetMethodByToken(Token.From(TableKind.MethodDef, declTag.Rid).Value);
                if (decl is null)
                    continue;
                var declParams = LoaderOf(decl).TryResolveSlotParams(decl.Signature.ParamTypes);
                if (declParams is null)
                    continue;
                if (decl.DeclaringType is VmClassType declaring && declaring.IsInterface) {
                    // EII (宣言がインターフェース メソッドの MethodDef 直参照 — 同アセンブリ
                    // インターフェースで Roslyn が使用)。InterfaceMap に登録する
                    maps.InterfaceMap[VmSlotKeys.InterfaceSlotKey(declaring.FullName, decl.Name, declParams)] = body;
                } else {
                    // メソッド→メソッドの明示 override: 宣言スロットを実装で差し替える
                    var slot = new DispatchSlot { Name = decl.Name, ParamTypes = declParams, Method = body };
                    maps.VTable[slot.Key] = slot;
                }
            } else if (declTag.Table == TableKind.MemberRef) {
                MapExplicitInterfaceImplementation(maps, declTag.Rid, body);
            }
        }
    }

    /// <summary>明示的インターフェース実装 (EII) を InterfaceMap に登録する。
    /// 宣言 MemberRef の親 (インターフェース定義) と署名からスロットキーを組み、body を実装とする。
    /// スロットキーは暗黙実装と同じくインターフェース定義文脈で正規化する。</summary>
    private void MapExplicitInterfaceImplementation(DispatchMaps maps, int memberRefRid, VmMethod body) {
        try {
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(memberRefRid).ToArray());
            var name = _loader.GetMemberRefName(memberRefRid);
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
            var declaring = _loader.ResolveToken(new SigType(SigKind.TypeToken, Token: Token.From(parent.Table, parent.Rid).Value));
            // 構築インターフェース親 (TypeSpec) は定義側へ解く (キーは定義文脈で統一)
            var declaringName = declaring is VmConstructedType constructed
                ? constructed.Definition.FullName : declaring.FullName;
            var parameters = _loader.TryResolveSlotParams(signature.ParamTypes);
            if (parameters is null)
                return;
            maps.InterfaceMap[VmSlotKeys.InterfaceSlotKey(declaringName, name, parameters)] = body;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
            or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
            // EII スロットの解決に失敗した場合は登録しない (呼出時に従来の名前照合へフォールバック)
        }
    }

    /// <summary>実装インターフェース (直接実装 + インターフェース継承を再帰的に辿ったもの) の
    /// 各メソッドについて実装を探して InterfaceMap に登録する。MethodImpl (EII) 済みの
    /// スロットと基底から引き継いだスロットは上書きしない。</summary>
    private void MapImplementedInterfaces(VmClassType type, DispatchMaps maps) {
        foreach (var iface in ImplementedInterfaces(type)) {
            var parametersByMethod = new List<(VmMethod Method, VmType[] Params)>();
            foreach (var ifaceMethod in iface.Methods) {
                if (ifaceMethod.IsStatic || !ifaceMethod.Signature.HasThis)
                    continue;
                if (LoaderOf(ifaceMethod).TryResolveSlotParams(ifaceMethod.Signature.ParamTypes) is not { } ifaceParams)
                    continue;
                parametersByMethod.Add((ifaceMethod, ifaceParams));
            }
            if (parametersByMethod.Count == 0)
                continue;

            // 暗黙実装照合用: インターフェース定義のパラメータを実装側文脈の型引数で置換する
            var interfaceArgs = ImplementedInterfaceArgs(type, iface);
            var substitution = interfaceArgs is { Length: > 0 } ? new GenericContext { ClassArgs = interfaceArgs } : null;
            foreach (var (ifaceMethod, ifaceParams) in parametersByMethod) {
                var slotKey = VmSlotKeys.InterfaceSlotKey(iface.FullName, ifaceMethod.Name, ifaceParams);
                if (maps.InterfaceMap.ContainsKey(slotKey))
                    continue; // EII 済み / 基底から引き継ぎ済み
                var expected = substitution is null ? ifaceParams
                    : [.. ifaceParams.Select(p => GenericSubstitutor.Substitute(p, substitution))];
                var impl = FindImplicitImplementation(type, ifaceMethod.Name, VmSlotKeys.Of(ifaceMethod.Name, expected));
                if (impl is not null)
                    maps.InterfaceMap[slotKey] = impl;
            }
        }
    }

    /// <summary>基底から引き継いだインターフェース実装を、VTable 上の最派生 override で更新する
    /// (基底の暗黙実装が virtual で派生が override している場合、インターフェース呼出も最派生に着地する)。</summary>
    private static void PropagateMostDerivedOverrides(DispatchMaps maps) {
        foreach (var (slotKey, impl) in maps.InterfaceMap.ToList()) {
            if (impl is not { IsStatic: false } || LoaderOf(impl).TryResolveSlotParams(impl.Signature.ParamTypes) is not { } implParams)
                continue;
            if (maps.VTable.TryGetValue(VmSlotKeys.Of(impl.Name, implParams), out var slot) &&
                !ReferenceEquals(slot.Method, impl))
                maps.InterfaceMap[slotKey] = slot.Method;
        }
    }

    /// <summary>暗黙実装を型自身のメソッドから探す (基底側の実装は基底表の引き継ぎが担う)。</summary>
    private VmMethod? FindImplicitImplementation(VmClassType type, string name, string expectedKey) {
        foreach (var method in type.Methods) {
            if (method.IsStatic || !method.Signature.HasThis || method.Body is null)
                continue;
            if (LoaderOf(method).TryResolveSlotParams(method.Signature.ParamTypes) is not { } parameters)
                continue;
            if (VmSlotKeys.Of(method.Name, parameters) == expectedKey)
                return method;
        }
        return null;
    }

    /// <summary>type が実装するインターフェース定義の列挙 (直接実装 + インターフェースの継承を展開、重複排除)。</summary>
    private IEnumerable<VmClassType> ImplementedInterfaces(VmClassType type) {
        var queue = new Queue<VmClassType>();
        foreach (var rid in InterfaceImplRids(type)) {
            var tag = _loader.Image.Tables.DecodeCoded(TableKind.InterfaceImpl, rid, 1, CodedIndexKind.TypeDefOrRef);
            if (ResolveInterfaceDefinition(tag) is { } iface)
                queue.Enqueue(iface);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0) {
            var iface = queue.Dequeue();
            if (!visited.Add(iface.FullName))
                continue;
            yield return iface;
            // インターフェース継承 (I : IBase) — 実装クラスのマップに基底インターフェースも載せる
            foreach (var rid in InterfaceImplRids(iface)) {
                var tag = _loader.Image.Tables.DecodeCoded(TableKind.InterfaceImpl, rid, 1, CodedIndexKind.TypeDefOrRef);
                if (ResolveInterfaceDefinition(tag) is { } baseIface)
                    queue.Enqueue(baseIface);
            }
        }
    }

    /// <summary>InterfaceImpl の Interface 列 (TypeDefOrRef) をインターフェース定義 (VmClassType) に解決する。
    /// ファサード インターフェース / 解決不能な場合は null (マップ対象外。intrinsic 面が担う)。</summary>
    private VmClassType? ResolveInterfaceDefinition((TableKind Table, int Rid) tag) {
        try {
            var token = Token.From(tag.Table, tag.Rid).Value;
            return _loader.ResolveToken(new SigType(SigKind.TypeToken, Token: token)) is VmClassType cls && cls.IsInterface
                ? cls : null;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
            or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
            return null;
        }
    }

    /// <summary>type の実装するインターフェース ifaceDef の型引数 (type 文脈) を取得する。
    /// 非ジェネリック インターフェースは空配列。実装していない場合は null。</summary>
    private static VmType[]? ImplementedInterfaceArgs(VmClassType type, VmClassType ifaceDef) {
        foreach (var iface in type.Interfaces) {
            var definition = iface is VmConstructedType constructed ? constructed.Definition : iface;
            if (!ReferenceEquals(definition, ifaceDef) && definition.FullName != ifaceDef.FullName)
                continue;
            return iface is VmConstructedType constructedType ? constructedType.TypeArguments : [];
        }
        return null;
    }

    /// <summary>TypeDef rid で索引化した InterfaceImpl rid 群 (初回呼出時に全表を 1 回走査)。</summary>
    private List<int> InterfaceImplRids(VmClassType type) {
        var builder = OwnerBuilder(type);
        if (builder._interfaceImpls is null) {
            var index = new Dictionary<int, List<int>>();
            var count = builder._loader.Image.Tables.GetRowCount(TableKind.InterfaceImpl);
            for (var rid = 1; rid <= count; rid++) {
                var owner = (int)builder._loader.Image.Tables.GetCell(TableKind.InterfaceImpl, rid, 0);
                if (!index.TryGetValue(owner, out var list))
                    index[owner] = list = [];
                list.Add(rid);
            }
            builder._interfaceImpls = index;
        }
        return builder._interfaceImpls.GetValueOrDefault(type.TypeDefRid) ?? [];
    }

    /// <summary>TypeDef rid で索引化した MethodImpl rid 群 (初回呼出時に全表を 1 回走査)。</summary>
    private List<int> MethodImplRids(VmClassType type) {
        var builder = OwnerBuilder(type);
        if (builder._methodImpls is null) {
            var index = new Dictionary<int, List<int>>();
            var count = builder._loader.Image.Tables.GetRowCount(TableKind.MethodImpl);
            for (var rid = 1; rid <= count; rid++) {
                var owner = (int)builder._loader.Image.Tables.GetCell(TableKind.MethodImpl, rid, 0);
                if (!index.TryGetValue(owner, out var list))
                    index[owner] = list = [];
                list.Add(rid);
            }
            builder._methodImpls = index;
        }
        return builder._methodImpls.GetValueOrDefault(type.TypeDefRid) ?? [];
    }

    /// <summary>型が所属するローダのビルダ (他アセンブリの型はそのローダの索引/表を使う)。</summary>
    private DispatchMapBuilder OwnerBuilder(VmClassType type) =>
        type.Loader is { } owner && !ReferenceEquals(owner, _loader) ? owner.DispatchBuilder : this;

    /// <summary>メソッドの署名解決は「そのメソッドを定義したローダ」で行う
    /// (TypeToken が自画像の TypeDef rid を指すため)。</summary>
    private static TypeLoader LoaderOf(VmMethod method) =>
        method.Loader ?? (method.DeclaringType as VmClassType)?.Loader
        ?? throw new InvalidOperationException($"メソッド {method} のローダが未設定です。");
}
