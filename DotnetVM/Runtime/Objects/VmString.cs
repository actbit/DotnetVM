namespace DotnetVM.Runtime.Objects;

/// <summary>
/// VM 内の文字列オブジェクト。ゲストが見る System.String の実体。
/// ホストの string をそのまま参照として渡さず VM 独自オブジェクトで包む
/// (アロケーション計上・オブジェクトモデル統一のため)。
/// </summary>
public sealed class VmString {
    public string Value { get; }

    public VmString(string value) {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// VM ごとの文字列インタン プール。ldstr 等のリテラルはここを通して同一 VmString を共有する。
/// </summary>
public sealed class VmStringPool {
    private readonly Dictionary<string, VmString> _pool = [];

    /// <summary>文字列を取得 (未登録なら新規作成して登録)。</summary>
    public VmString Get(string value) {
        if (_pool.TryGetValue(value, out var existing))
            return existing;
        var created = new VmString(value);
        _pool[value] = created;
        return created;
    }

    /// <summary>既に同一内容がプール済みならそれを返し、無ければ新規オブジェクトを作る (演算結果用)。</summary>
    public VmString GetOrNew(string value) => Get(value);

    public int Count => _pool.Count;
}
