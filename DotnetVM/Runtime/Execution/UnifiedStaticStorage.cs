namespace DotnetVM.Runtime.Execution;

/// <summary>
/// 画像をまたいで共有する静的フィールドストレージ (型ユニフィケーションされた実型の 1 次置き場)。
/// 多アセンブリ実行 (C1) ではローダ (画像) ごとに ObjectEngine が分かれるが、実 CLR の
/// 静的フィールドは型ごとに 1 つのストレージ (System.Decimal.One 等の CoreLib 型の .cctor が
/// 書く値) のため、ユニフィケーションで 1 つの VmType に解決された型の静的ストレージは
/// VM (Interpreter) 単位で共有しないと「CoreLib の .cctor が書いた値をゲスト画像の
/// ldsfld が読めない」崩れが生じる。構築ジェネリクス型 (CLR と同じく実引数ごとに別
/// ストレージ) も同一キー規約 (構築 FullName) でここに置く。
/// </summary>
public sealed class UnifiedStaticStorage {
    private readonly Dictionary<string, StackSlot[]> Table = [];

    /// <summary>GC ルート源 (生成済みの全ストレージを強参照で保持する)。</summary>
    internal readonly List<StackSlot[]> Registry = [];

    internal StackSlot[] GetOrCreate(string storageKey, Func<StackSlot[]> factory) {
        if (Table.TryGetValue(storageKey, out var existing))
            return existing;
        var storage = factory();
        Table[storageKey] = storage;
        Registry.Add(storage);
        return storage;
    }

    internal IEnumerable<StackSlot[]> EnumerateRoots() => Registry;
}
