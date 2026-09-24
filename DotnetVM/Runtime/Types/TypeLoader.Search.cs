using DotnetVM.Metadata;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // ---- 検索 API ----

    /// <summary>MethodDef トークン (0x06xxxxxx) を VmMethod に解決する。所有 TypeDef を MethodList 範囲走査で特定する。</summary>
    public VmMethod? GetMethodByToken(uint token) {
        lock (MetadataGate)
            return GetMethodByTokenCore(token);
    }

    private VmMethod? GetMethodByTokenCore(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.MethodDef)
            return null;
        if (_methods.TryGetValue(rid, out var cached))
            return cached;

        // 所有型が未ロードでも解決できるよう TypeDef 全体から MethodList 範囲で探す
        var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var (_, _, methodStart, methodEnd) = MemberRanges(typeRid);
            if (rid < methodStart || rid >= methodEnd)
                continue;
            return GetTypeDef(typeRid).Methods.Single(m => m.MethodDefRid == rid);
        }
        return null;
    }

    /// <summary>TypeDef のフルネームで型を検索する (ネスト型は "Outer/Inner"、独自表記は "Outer.Inner")。
    /// 最初の呼び出しで完全名索引を 1 回だけ構築する (CoreLib 規模の画像での線形走査を避ける)。</summary>
    public VmClassType? FindTypeByFullName(string fullName) {
        lock (MetadataGate)
            return FindTypeByFullNameCore(fullName);
    }

    private VmClassType? FindTypeByFullNameCore(string fullName) {
        if (_typeDefByFullName is null) {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var count = _image.Tables.GetRowCount(TableKind.TypeDef);
            for (var rid = 1; rid <= count; rid++) {
                var (ns, name) = _image.GetTypeDefName(rid);
                var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                // ネスト型は包含チェーンを辿って FullName を構成する
                var enclosing = _image.GetEnclosingTypeDef(rid);
                while (enclosing != 0) {
                    var (ens, ename) = _image.GetTypeDefName(enclosing);
                    full = (string.IsNullOrEmpty(ens) ? ename : ens + "." + ename) + "/" + full;
                    enclosing = _image.GetEnclosingTypeDef(enclosing);
                }
                index.TryAdd(full, rid);
                index.TryAdd(full.Replace('/', '+'), rid);
            }
            _typeDefByFullName = index;
        }
        return _typeDefByFullName.TryGetValue(fullName, out var ridMatch) ? GetTypeDef(ridMatch) : null;
    }

    /// <summary>名前 (末尾要素) で TypeDef を検索する。</summary>
    public VmClassType? FindTypeByName(string name) {
        lock (MetadataGate) {
            var count = _image.Tables.GetRowCount(TableKind.TypeDef);
            for (var rid = 1; rid <= count; rid++) {
                var (_, typeName) = _image.GetTypeDefName(rid);
                if (typeName == name)
                    return GetTypeDef(rid);
            }
            return null;
        }
    }

    /// <summary>Field トークン (0x04xxxxxx) を VmField に解決する。</summary>
    public VmField? GetFieldByToken(uint token) {
        lock (MetadataGate)
            return GetFieldByTokenCore(token);
    }

    private VmField? GetFieldByTokenCore(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.Field)
            return null;
        if (_fields.TryGetValue(rid, out var cached))
            return cached;
        var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var (fieldStart, fieldEnd, _, _) = MemberRanges(typeRid);
            if (rid < fieldStart || rid >= fieldEnd)
                continue;
            return GetTypeDef(typeRid).Fields.Single(f => f.FieldRid == rid);
        }
        return null;
    }

    /// <summary>MemberRef rid のフィールド名 (ldsfld/stsfld の MemberRef 形式用)。</summary>
    public string GetMemberRefFieldName(int memberRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));

    /// <summary>MemberRef rid から親型名 (TypeRef 親のフルネーム。TypeRef 以外は null) を得る。
    /// ネスト型親 (Interop+BCrypt 等) は包含チェーンを辿って CLR 規約の '+' 連結完全名にする
    /// (単純末尾名ではバインド照合できないため)。</summary>
    public string? GetMemberRefParentTypeName(int memberRefRid) {
        var parent = _image.Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
        if (parent.Table != TableKind.TypeRef)
            return null;
        // 内側→外側へ辿り、名前を集めると同時に名前空間を持つ最外要素を探す
        // (ネスト型自体の Namespace 列は空のため)
        var names = new List<string>();
        string? ns = null;
        var current = parent.Rid;
        while (true) {
            var (innerNs, innerName, scope) = _image.GetTypeRefName(current);
            names.Insert(0, innerName);
            if (!string.IsNullOrEmpty(innerNs))
                ns = innerNs;
            if (scope.Table != TableKind.TypeRef)
                break;
            current = scope.Rid;
        }
        var full = string.Join("+", names);
        return string.IsNullOrEmpty(ns) ? full : ns + "." + full;
    }

    /// <summary>MemberRef rid のメソッド名。</summary>
    public string GetMemberRefName(int memberRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));

    /// <summary>完全名を Context 配下の全画像 (ロード順 = CoreLib 優先) の実 TypeDef に解決する。
    /// 見つからなければ null (ファサード等のフォールバックは呼び出し側)。実体化に失敗する型
    /// (未対応の署名を含む画像固有の型) はその画像をスキップする。
    /// 注意: 非 trusted 画像を含む global 統合は新規コードでは使わないこと。
    /// 新規は TryResolveTrustedUnifiedType (trusted 限定) を使う。</summary>
    public VmType? TryResolveUnifiedType(string fullName) {
        lock (MetadataGate)
            return TryResolveUnifiedTypeCore(fullName);
    }

    private VmType? TryResolveUnifiedTypeCore(string fullName) {
        if (_unifiedTypes.TryGetValue(fullName, out var cached))
            return cached;
        if (Context is not { } context)
            return null;
        foreach (var loader in context.Loaders) {
            VmClassType? real;
            try {
                real = loader.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is VmExecutionException or NotSupportedException
                or BadImageFormatException or InvalidOperationException) {
                continue; // この画像では実体化できない型 (未対応面)。ファサードに委ねる
            }
            if (real is not null) {
                _unifiedTypes[fullName] = real;
                return real;
            }
        }
        return null;
    }

    /// <summary>完全名を trusted 画像 (IsTrustedCoreLib) の実 TypeDef に限定して解決する。
    /// BCL の参照アセンブリ→実装の統合はここに限定する (非 trusted なゲスト画像同士は
    /// 統合しない)。プリミティブ等の既知型や ResolveViaAssemblyRef の最終救済に使う。</summary>
    public VmType? TryResolveTrustedUnifiedType(string fullName) {
        lock (MetadataGate)
            return TryResolveTrustedUnifiedTypeCore(fullName);
    }

    private VmType? TryResolveTrustedUnifiedTypeCore(string fullName) {
        if (_unifiedTypes.TryGetValue(fullName, out var cached) && cached is VmClassType cachedCls &&
            cachedCls.Loader?.IsTrustedCoreLib == true)
            return cached;
        if (Context is not { } context)
            return null;
        foreach (var loader in context.Loaders) {
            if (!loader.IsTrustedCoreLib)
                continue;
            VmClassType? real;
            try {
                real = loader.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is VmExecutionException or NotSupportedException
                or BadImageFormatException or InvalidOperationException) {
                continue;
            }
            if (real is not null) {
                _unifiedTypes[fullName] = real;
                return real;
            }
        }
        return null;
    }

    /// <summary>既知型 (プリミティブ/String/Object/Array 等) を実型またはファサードで解決する
    /// (ホスト境界のボックス化や配列基底型の接続に使う。trusted 実型に限定)。</summary>
    public VmType ResolveWellKnownType(string fullName) =>
        TryResolveTrustedUnifiedType(fullName) ?? _intrinsicTypes[fullName];
}
