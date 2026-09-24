using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    /// <summary>構築型 TypeSpec の GenericInst 定義トークンを取得する。runtime binding の
    /// provenance 検査で、解決後の facade だけでなく元の TypeRef scope も確認するために使う。</summary>
    internal uint GetTypeSpecDefinitionToken(int typeSpecRid) {
        var blob = _image.GetBlob(_image.Tables.GetRowIndex(TableKind.TypeSpec, typeSpecRid, 0));
        var sigType = SignatureDecoder.DecodeTypeSpecSignature(blob.ToArray(),
            _image.Limits?.MaxSignatureDepth ?? 64,
            _image.Limits?.MaxGenericNestingDepth ?? 64).Type;
        if (sigType.Kind != SigKind.GenericInst)
            throw new BadImageFormatException($"TypeSpec 0x02{typeSpecRid:X6} は GenericInst ではありません。");
        return sigType.Token;
    }

    /// <summary>TypeRef rid を解決する。解決順:
    /// ① Context 登録済みの実アセンブリ (AssemblyRef スコープの依存解決 / Module スコープの自己参照 /
    ///    TypeRef スコープのネスト型) → ② intrinsic ファサード → ③ ファサード無し時のネスト/自己解決 →
    ///    ④ fail-closed (AssemblyDependencyNotFoundException / NotSupportedException)。</summary>
    private VmType ResolveTypeRef(int typeRefRid) {
        var (ns, name, scope) = _image.GetTypeRefName(typeRefRid);
        var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        var (scopeTable, scopeRid) = scope;

        // ① 実アセンブリからの解決 (Context に登録された画像が優先。CoreLib ロード時は
        //    ファサードより実 TypeDef が勝つ)
        var context = Context;
        if (context is not null) {
            var real = scopeTable switch {
                TableKind.Module => (VmType?)FindTypeByFullName(fullName),
                TableKind.AssemblyRef => ResolveViaAssemblyRef(fullName, scopeRid),
                TableKind.TypeRef => ResolveNestedTypeRef(typeRefRid),
                _ => null,
            };
            if (real is not null)
                return real;
        }

        // ② intrinsic ファサード (CoreLib 未ロード時の既定面)。legacy intrinsic の型解決は
        // ここで維持するが、runtime binding 側は TypeRef の AssemblyRef identity を別途検証
        // する。型の FullName だけでは trusted provenance とはみなさない。
        if (_intrinsicTypes.TryGetValue(fullName, out var intrinsic))
            return intrinsic;
        if (scopeTable == TableKind.TypeRef && ResolveIntrinsicNestedTypeRef(typeRefRid) is { } intrinsicNested)
            return intrinsicNested;

        // ③ Context 未所属時の従来解決 (ネスト型 TypeRef は包含チェーンごと TypeDef と照合)
        if (context is null && scopeTable == TableKind.TypeRef &&
            ResolveNestedTypeRef(typeRefRid) is { } nested)
            return nested;

        // ④ fail-closed
        if (scopeTable == TableKind.AssemblyRef) {
            var refIdentity = _image.GetAssemblyRefIdentity(scopeRid);
            if (context?.TryResolveAssembly(refIdentity, _image) is null)
                throw new AssemblyDependencyNotFoundException(refIdentity.Name,
                    $"参照アセンブリ '{refIdentity}' (型 '{fullName}' の解決に必要) がロード済みでも" +
                    $"同一ディレクトリ ({LoaderDependencyDirectoryHint()}) にも見つかりません。");
        }
        throw new NotSupportedException($"型参照 '{fullName}' を解決できません (スコープ {scopeTable})。");
    }

    /// <summary>依存アセンブリ同一ディレクトリ探索のヒント文言。Stream ロード (SourcePath 無し)
    /// の場合は「同一ディレクトリ探索も行われない (明示 resolver でのロードが必須)」と示す。
    /// host current directory への暗黙フォールバック (SourcePath ?? ".") を廃止した。</summary>
    private string LoaderDependencyDirectoryHint() =>
        _image.SourcePath is { } path
            ? $"同一ディレクトリ ({Path.GetDirectoryName(Path.GetFullPath(path))})"
            : "同一ディレクトリ (Stream ロードのため探索なし。依存は明示 resolver / LoadAssembly(path) でのロードが必要)";

    /// <summary>AssemblyRef スコープの TypeRef を解決する。優先順:
    /// ①自分自身への参照は自己画像 (identity 照合) → ②依存アセンブリを identity で確定し、
    /// その loader 内の TypeDef を解決する (ExportedType forwarder 経由を含む) →
    /// ③trusted assembly (IsTrustedCoreLib) に限定した FullName 統合 (BCL ref→実装の解決)。
    /// global な FullName 探索を先に行わない (同名別 identity への誤結合防止)。</summary>
    private VmType? ResolveViaAssemblyRef(string fullName, int assemblyRefRid) {
        var refIdentity = _image.GetAssemblyRefIdentity(assemblyRefRid);
        // ① 自分自身への参照は自己画像で解決する (単一画像ロード時の自己参照 TypeRef)。
        //    identity (Name + 公開鍵トークン) で照合する
        if (refIdentity.Matches(_image.Identity))
            return FindTypeByFullName(fullName) ?? ResolveForwarderIn(_image, fullName);
        // ② 依存アセンブリを identity で確定し、その loader 内で解決する
        var target = Context!.TryResolveAssembly(refIdentity, _image);
        if (target is not null) {
            if (target.FindTypeByFullName(fullName) is { } direct)
                return direct;
            // TypeForwarder: global 探索ではなく対象画像の ExportedType で転送先を辿る
            if (ResolveForwarderIn(target.Image, fullName) is { } forwarded)
                return forwarded;
        }
        // ③ trusted assembly に限定した FullName 統合 (BCL の参照アセンブリ→実装の解決)。
        // ただし単に「解決できなかった AssemblyRef」であることだけを根拠にすると、
        // 任意の AssemblyRef が同名 CoreLib 型へ統合される。既知の framework contract identity
        // からの参照、または正式な TypeForwarder 経路だけを許可する。
        return IsKnownFrameworkContract(refIdentity) ? TryResolveTrustedUnifiedType(fullName) : null;
    }

    /// <summary>画像の ExportedType テーブルに FullName の転送宣言があれば転送先を解決する
    /// (TypeForwardedTo 相当。global FullName 探索はしない)。</summary>
    private VmType? ResolveForwarderIn(AssemblyImage image, string fullName) {
        var count = image.Tables.GetRowCount(TableKind.ExportedType);
        for (var rid = 1; rid <= count; rid++) {
            var typeName = image.GetString(image.Tables.GetRowIndex(TableKind.ExportedType, rid, 2));
            var typeNs = image.GetString(image.Tables.GetRowIndex(TableKind.ExportedType, rid, 3));
            var candidate = string.IsNullOrEmpty(typeNs) ? typeName : typeNs + "." + typeName;
            if (!string.Equals(candidate, fullName, StringComparison.Ordinal))
                continue;
            var impl = image.Tables.DecodeCoded(TableKind.ExportedType, rid, 4, CodedIndexKind.Implementation);
            if (impl.Table == TableKind.AssemblyRef) {
                var fwdIdentity = image.GetAssemblyRefIdentity(impl.Rid);
                var fwdTarget = Context?.TryResolveAssembly(fwdIdentity, image);
                if (fwdTarget?.FindTypeByFullName(fullName) is { } fwdType)
                    return fwdType;
                // 転送先が未ロードでも、forwarder の参照先が既知 framework contract の場合だけ
                // trusted 統合で拾える (任意 AssemblyRef の同名統合はしない)。
                if (IsKnownFrameworkContract(fwdIdentity) &&
                    TryResolveTrustedUnifiedType(fullName) is { } trusted)
                    return trusted;
            }
        }
        return null;
    }

    /// <summary>TypeRef rid を解決した結果を返す (intrinsic ファサードまたは実 VmClassType)。
    /// 呼出/オブジェクト生成/フィールド解決が MemberRef の TypeRef 親を実体に解決するのに使う。</summary>
    public VmType ResolveTypeRefType(int typeRefRid) => ResolveTypeRef(typeRefRid);

    /// <summary>AssemblyRef rid の参照先アセンブリの単純名。</summary>
    public string GetAssemblyRefName(int assemblyRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.AssemblyRef, assemblyRefRid, 6));

    /// <summary>ネスト型 TypeRef を TypeDef のネスト構造 (NestedClass) と名前照合で解決する。
    /// 終端スコープが AssemblyRef の場合は identity で対象画像を確定してから (自画像の
    /// 場合は自己解決)、その画像内で包含チェーンを辿る。BCL (System.Runtime 等) は
    /// trusted 実装画像への統合で救済する (global 探索ではなく identity 起点)。</summary>
    private VmClassType? ResolveNestedTypeRef(int typeRefRid) {
        // 先に共有 helper で scope chain の cycle / 過深度 / invalid RID を検証する。
        // その後の名前収集ループは、malformed metadata でも無限に回らない。
        _image.GetTerminalTypeRefScope(typeRefRid);
        // TypeRef のスコープチェーン (内側 → 外側) を名前として集める
        var names = new List<string>();
        var current = typeRefRid;
        var (terminalTable, terminalRid) = (TableKind.Module, 0);
        while (true) {
            var (_, nestedName, nestedScope) = _image.GetTypeRefName(current);
            names.Insert(0, nestedName);
            (terminalTable, terminalRid) = nestedScope;
            if (terminalTable != TableKind.TypeRef)
                break;
            current = terminalRid;
        }

        // 終端スコープの画像を確定する
        TypeLoader targetLoader = this;
        if (terminalTable == TableKind.AssemblyRef) {
            var refIdentity = _image.GetAssemblyRefIdentity(terminalRid);
            if (refIdentity.Matches(_image.Identity)) {
                targetLoader = this;
            } else if (Context?.TryResolveAssembly(refIdentity, _image) is { } resolved) {
                targetLoader = resolved;
            } else {
                // BCL 参照アセンブリ (System.Runtime 等) のネスト型は trusted 実装で救済する
                var unified = IsKnownFrameworkContract(refIdentity)
                    ? TryResolveTrustedUnifiedType(names[0]) : null;
                if (unified is VmClassType unifiedClass)
                    return WalkNested(unifiedClass, names, 1);
                return null;
            }
        } else if (terminalTable != TableKind.Module) {
            return null;
        }

        var owner = targetLoader.FindTypeByFullName(names[0]) ?? targetLoader.FindTypeByName(names[0]);
        if (owner is null) {
            // 自画像に無く BCL 統合で拾える場合 (参照アセンブリ経由の BCL ネスト型)
            if (targetLoader == this && terminalTable == TableKind.AssemblyRef &&
                IsKnownFrameworkContract(_image.GetAssemblyRefIdentity(terminalRid)) &&
                TryResolveTrustedUnifiedType(names[0]) is VmClassType unifiedClass)
                return WalkNested(unifiedClass, names, 1);
            return null;
        }
        return WalkNested(owner, names, 1);
    }

    /// <summary>CoreLib の実画像がロードされていないときでも、intrinsic facade として
    /// 合成したネスト型 (ConfiguredTaskAwaitable+ConfiguredTaskAwaiter 等) を解決する。</summary>
    private VmIntrinsicType? ResolveIntrinsicNestedTypeRef(int typeRefRid) {
        _image.GetTerminalTypeRefScope(typeRefRid);
        var names = new List<string>();
        var current = typeRefRid;
        string? outerNamespace = null;
        while (true) {
            var (ns, name, scope) = _image.GetTypeRefName(current);
            names.Insert(0, name);
            outerNamespace = ns;
            if (scope.Item1 != TableKind.TypeRef)
                break;
            current = scope.Item2;
        }
        if (names.Count < 2)
            return null;
        var fullName = string.IsNullOrEmpty(outerNamespace) ? names[0] : outerNamespace + "." + names[0];
        for (var i = 1; i < names.Count; i++)
            fullName += "+" + names[i];
        return _intrinsicTypes.GetValueOrDefault(fullName);
    }

    /// <summary>包含型からネスト名列を辿る (対象画像の NestedClass で解決する)。</summary>
    private VmClassType? WalkNested(VmClassType owner, List<string> names, int start) {
        var ownerLoader = owner.Loader ?? this;
        for (var i = start; i < names.Count; i++) {
            VmClassType? child = null;
            var image = ownerLoader.Image;
            var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
            for (var rid = 1; rid <= typeDefs; rid++) {
                if (image.GetEnclosingTypeDef(rid) != owner.TypeDefRid)
                    continue;
                var (_, childName) = image.GetTypeDefName(rid);
                if (childName == names[i]) {
                    child = ownerLoader.GetTypeDef(rid);
                    break;
                }
            }
            if (child is null)
                return null;
            owner = child;
            ownerLoader = owner.Loader ?? ownerLoader;
        }
        return owner;
    }

    /// <summary>
    /// CoreLib 統合 fallback に使える framework contract identity。
    /// 名前だけでは fake System.Runtime.dll を区別できないため、既知の strong-name token を
    /// 必須にする。正式な AssemblyRef→TypeForwarder 解決はこの制約とは別に上で処理する。
    /// </summary>
    internal static bool IsKnownFrameworkContract(AssemblyIdentity identity) =>
        identity.IsStrongNamed && (identity.Name, identity.PublicKeyToken) is
            ("System.Private.CoreLib", "7cec85d7bea7798e") or
            ("System.Runtime", "b03f5f7f11d50a3a") or
            ("System.Runtime.Extensions", "b03f5f7f11d50a3a") or
            ("System.Console", "b03f5f7f11d50a3a") or
            ("System.Linq", "b03f5f7f11d50a3a") or
            ("System.Collections", "b03f5f7f11d50a3a") or
            ("System.Collections.Concurrent", "b03f5f7f11d50a3a") or
            ("System.Threading", "b03f5f7f11d50a3a") or
            ("System.Threading.Tasks", "b03f5f7f11d50a3a") or
            ("System.Reflection", "b03f5f7f11d50a3a") or
            ("System.Reflection.Emit", "b03f5f7f11d50a3a") or
            ("System.Runtime.InteropServices", "b03f5f7f11d50a3a") or
            ("System.Runtime.Loader", "b03f5f7f11d50a3a") or
            ("System.Runtime.CompilerServices.Unsafe", "b03f5f7f11d50a3a") or
            ("System.Threading.Tasks.Extensions", "cc7b13ffcd2ddd51") or
            ("netstandard", "cc7b13ffcd2ddd51");
}
