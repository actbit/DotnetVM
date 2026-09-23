using DotnetVM.Metadata;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// 複数アセンブリ (TypeLoader) を束ねるロード コンテキスト。AssemblyRef による依存解決を担う。
/// 解決順: ①ロード済みアセンブリ (単純名照合、大文字小文字を無視) → ②参照元アセンブリと
/// 同一ディレクトリの同名 DLL (ホストが見つけた明示パスの同一配置探索) → ③見つからなければ
/// 呼び出し側に null を返し、型解決は fail-closed で拒否される。VM 側で任意のディスク走査はしない。
/// </summary>
public sealed class VmAssemblyContext(
    Func<string, TypeLoader> loaderFactory,
    VmAssemblyContext? parent = null,
    Func<string, bool>? pathExists = null) {
    private readonly object _gate = new();
    private readonly List<TypeLoader> _loaders = [];
    private readonly Dictionary<string, TypeLoader> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);
    // 依存解決の再入 (A→B→A の循環参照) で同じファイルを二重ロードしないための排他セット
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly VmAssemblyContext? _parent = parent;
    private readonly Func<string, bool> _pathExists = pathExists ?? File.Exists;
    internal Guid Identity { get; } = Guid.NewGuid();

    /// <summary>ロード済みアセンブリの TypeLoader 一覧 (ロード順)。</summary>
    public IReadOnlyList<TypeLoader> Loaders { get { lock (_gate) return _loaders.ToArray(); } }

    /// <summary>ロード済みアセンブリを単純名で取得 (無ければ null)。</summary>
    public TypeLoader? FindBySimpleName(string simpleName) {
        lock (_gate)
            if (_bySimpleName.TryGetValue(simpleName, out var loader))
                return loader;
        return _parent?.FindBySimpleName(simpleName);
    }

    /// <summary>ロード済みアセンブリを完全な AssemblyName identity で取得する。</summary>
    public TypeLoader? FindByIdentity(AssemblyIdentity identity) {
        foreach (var loader in Loaders)
            if (identity.MatchesExactly(loader.Image.Identity))
                return loader;
        return _parent?.FindByIdentity(identity);
    }

    /// <summary>型がこの ALC の画像、またはそれを含む構築型由来かを調べる。</summary>
    internal bool OwnsType(VmType type) => type switch {
        VmClassType cls => cls.Loader?.LoadContextIdentity == Identity,
        VmConstructedType constructed => OwnsType(constructed.Definition) ||
            constructed.TypeArguments.Any(OwnsType),
        VmArrayType array => OwnsType(array.ElementType),
        VmMultiDimArrayType array => OwnsType(array.ElementType),
        VmByRefType byRef => OwnsType(byRef.ElementType),
        _ => false,
    };

    /// <summary>アセンブリをコンテキストに登録する (VirtualMachine.LoadAssembly から呼ぶ)。</summary>
    internal void Register(TypeLoader loader) {
        lock (_gate) {
            if (_loaders.Contains(loader))
                return;
            _loaders.Add(loader);
            loader.Context = this;
            loader.LoadContextIdentity = Identity;
            // 同一単純名の再ロードは最初のものを優先 (CLR のアセンブリ統合と同じ先行勝ち)
            _bySimpleName.TryAdd(loader.Image.Name, loader);
        }
    }

    internal void Unregister(TypeLoader loader) {
        lock (_gate) {
            _loaders.Remove(loader);
            if (_bySimpleName.TryGetValue(loader.Image.Name, out var current) && ReferenceEquals(current, loader)) {
                _bySimpleName.Remove(loader.Image.Name);
                var replacement = _loaders.FirstOrDefault(candidate =>
                    string.Equals(candidate.Image.Name, loader.Image.Name, StringComparison.OrdinalIgnoreCase));
                if (replacement is not null)
                    _bySimpleName[loader.Image.Name] = replacement;
            }
            if (ReferenceEquals(loader.Context, this))
                loader.Context = null;
        }
    }

    /// <summary>
    /// AssemblyRef の単純名をロード済みアセンブリに解決する。
    /// 依存アセンブリの同一ディレクトリ探索は「参照元がファイルからロードされている
    /// (SourcePath が確定している)」場合にのみ行う (Stream ロードはホストが明示 resolver /
    /// LoadAssembly(path) で解決する — host current directory への暗黙フォールバック禁止)。
    /// 見つからなければ null (fail-closed は呼び出し側の型解決が行う)。
    /// </summary>
    public TypeLoader? TryResolveAssembly(string simpleName, AssemblyImage requesting) {
        TypeLoader? loaded;
        lock (_gate)
            loaded = _bySimpleName.GetValueOrDefault(simpleName);
        if (loaded is not null)
            return loaded;
        return TryDiscoverFromDirectory(simpleName, requesting) ?? _parent?.TryResolveAssembly(simpleName, requesting);
    }

    /// <summary>
    /// AssemblyRef の identity (Name / Version / Culture / PublicKeyToken) で解決する。
    /// 単純名一致に加え、参照側が strong-named なら公開鍵トークンの一致を要求する
    /// (タスク 2 hardening: 同名別 identity のアセンブリへ誤結合しない)。
    /// ①ロード済みを identity 照合 → ②同一ディレクトリ探索 (identity 照合)。
    /// </summary>
    public TypeLoader? TryResolveAssembly(AssemblyIdentity reference, AssemblyImage requesting) {
        // ① ロード済みアセンブリを identity で照合 (ロード数は小さいため線形で十分)
        foreach (var loader in Loaders) {
            if (reference.Matches(loader.Image.Identity))
                return loader;
        }
        // ② 同一ディレクトリの同名 DLL をロードし、identity で照合する
        var discovered = TryDiscoverFromDirectory(reference.Name, requesting);
        if (discovered is not null && reference.Matches(discovered.Image.Identity))
            return discovered;
        return _parent?.TryResolveAssembly(reference, requesting);
    }

    /// <summary>参照元と同一ディレクトリの同名 DLL を探索してロードする (SourcePath 無しは探索しない)。</summary>
    private TypeLoader? TryDiscoverFromDirectory(string simpleName, AssemblyImage requesting) {
        // SourcePath 無し (Stream ロード) は自動探索を行わない:
        // requesting.SourcePath ?? "." による host current directory フォールバックは廃止
        var sourcePath = requesting.SourcePath;
        if (sourcePath is null)
            return null;

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (sourceDir is null)
            return null;
        var candidatePath = Path.Combine(sourceDir, simpleName + ".dll");
        if (!_pathExists(candidatePath))
            return null;

        var fullPath = Path.GetFullPath(candidatePath);
        // 循環参照 (依存ロード中に同じ依存へ逆参照) 時はロード中のため null は返せない。
        // ロード完了まで待つのではなく、単純名辞書への登録は Register が行うので、
        // ここでは二重ロードを避けるためロード中マークを置いて再入を検知したら null を返す
        lock (_gate) {
            if (!_loading.Add(fullPath))
                return null;
        }
        try {
            // 同一 simple name の別 identity が既に辞書にあっても、今回 identity 照合のために
            // 実際にロードした loader を返す。辞書の先頭へ戻すと正しい依存先を隠してしまう。
            return loaderFactory(fullPath);
        } finally {
            lock (_gate)
                _loading.Remove(fullPath);
        }
    }
}
