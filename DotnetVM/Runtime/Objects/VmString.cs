using DotnetVM.Runtime.Heap;

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
    private readonly VmHeap? _heap;

    public VmStringPool(VmHeap? heap = null) {
        _heap = heap;
    }

    /// <summary>文字列を取得 (未登録なら新規作成して登録。新規作成時はアロケーションを計上する)。</summary>
    public VmString Get(string value) {
        if (_pool.TryGetValue(value, out var existing))
            return existing;
        // インタニング済み文字列はヒープ管理外だが、新規作成は計上する
        // (intrinsic/ホスト境界経由の文字列生成も IL の ldstr と等価に制約される)
        _heap?.ChargeString(value.Length);
        var created = new VmString(value);
        _pool[value] = created;
        return created;
    }

    /// <summary>既に同一内容がプール済みならそれを返し、無ければ新規オブジェクトを作る (演算結果用)。</summary>
    public VmString GetOrNew(string value) => Get(value);

    public int Count => _pool.Count;
}
