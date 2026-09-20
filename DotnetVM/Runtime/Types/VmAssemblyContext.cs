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
    /// AssemblyRef の単純名をロード済みアセンブリに解決する。未ロードなら参照元と同一ディレクトリの
    /// 同名 DLL を loaderFactory (VirtualMachine の LoadAssembly) でロードして返す。
    /// 見つからなければ null (fail-closed は呼び出し側の型解決が行う)。
    /// </summary>
    public TypeLoader? TryResolveAssembly(string simpleName, AssemblyImage requesting) {
        if (_bySimpleName.TryGetValue(simpleName, out var loaded))
            return loaded;

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(requesting.SourcePath ?? "."));
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
