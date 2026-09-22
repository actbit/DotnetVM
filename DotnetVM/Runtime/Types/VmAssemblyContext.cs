using DotnetVM.Metadata;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// 複数アセンブリ (TypeLoader) を束ねるロード コンテキスト。AssemblyRef による依存解決を担う。
/// 解決順: ①ロード済みアセンブリ (単純名照合、大文字小文字を無視) → ②参照元アセンブリと
/// 同一ディレクトリの同名 DLL (ホストが見つけた明示パスの同一配置探索) → ③見つからなければ
/// 呼び出し側に null を返し、型解決は fail-closed で拒否される。VM 側で任意のディスク走査はしない。
/// </summary>
public sealed class VmAssemblyContext(Func<string, TypeLoader> loaderFactory) {
    private readonly List<TypeLoader> _loaders = [];
    private readonly Dictionary<string, TypeLoader> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);
    // 依存解決の再入 (A→B→A の循環参照) で同じファイルを二重ロードしないための排他セット
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ロード済みアセンブリの TypeLoader 一覧 (ロード順)。</summary>
    public IReadOnlyList<TypeLoader> Loaders => _loaders;

    /// <summary>ロード済みアセンブリを単純名で取得 (無ければ null)。</summary>
    public TypeLoader? FindBySimpleName(string simpleName) =>
        _bySimpleName.GetValueOrDefault(simpleName);

    /// <summary>アセンブリをコンテキストに登録する (VirtualMachine.LoadAssembly から呼ぶ)。</summary>
    internal void Register(TypeLoader loader) {
        _loaders.Add(loader);
        loader.Context = this;
        // 同一単純名の再ロードは最初のものを優先 (CLR のアセンブリ統合と同じ先行勝ち)
        _bySimpleName.TryAdd(loader.Image.Name, loader);
    }

    /// <summary>
    /// AssemblyRef の単純名をロード済みアセンブリに解決する。
    /// 依存アセンブリの同一ディレクトリ探索は「参照元がファイルからロードされている
    /// (SourcePath が確定している)」場合にのみ行う (Stream ロードはホストが明示 resolver /
    /// LoadAssembly(path) で解決する — host current directory への暗黙フォールバック禁止)。
    /// 見つからなければ null (fail-closed は呼び出し側の型解決が行う)。
    /// </summary>
    public TypeLoader? TryResolveAssembly(string simpleName, AssemblyImage requesting) {
        if (_bySimpleName.TryGetValue(simpleName, out var loaded))
            return loaded;
        return TryDiscoverFromDirectory(simpleName, requesting);
    }

    /// <summary>
    /// AssemblyRef の identity (Name / Version / Culture / PublicKeyToken) で解決する。
    /// 単純名一致に加え、参照側が strong-named なら公開鍵トークンの一致を要求する
    /// (タスク 2 hardening: 同名別 identity のアセンブリへ誤結合しない)。
    /// ①ロード済みを identity 照合 → ②同一ディレクトリ探索 (identity 照合)。
    /// </summary>
    public TypeLoader? TryResolveAssembly(AssemblyIdentity reference, AssemblyImage requesting) {
        // ① ロード済みアセンブリを identity で照合 (ロード数は小さいため線形で十分)
        foreach (var loader in _loaders) {
            if (reference.Matches(loader.Image.Identity))
                return loader;
        }
        // ② 同一ディレクトリの同名 DLL をロードし、identity で照合する
        var discovered = TryDiscoverFromDirectory(reference.Name, requesting);
        return discovered is not null && reference.Matches(discovered.Image.Identity) ? discovered : null;
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
        if (!File.Exists(candidatePath))
            return null;

        var fullPath = Path.GetFullPath(candidatePath);
        // 循環参照 (依存ロード中に同じ依存へ逆参照) 時はロード中のため null は返せない。
        // ロード完了まで待つのではなく、単純名辞書への登録は Register が行うので、
        // ここでは二重ロードを避けるためロード中マークを置いて再入を検知したら null を返す
        if (!_loading.Add(fullPath))
            return null;
        try {
            var loader = loaderFactory(fullPath);
            return _bySimpleName.GetValueOrDefault(simpleName, loader);
        } finally {
            _loading.Remove(fullPath);
        }
    }
}
