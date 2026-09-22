namespace DotnetVM.Runtime.Execution;

using DotnetVM.Runtime.Types;

/// <summary>
/// 画像をまたいで共有する静的フィールドストレージ (型ユニフィケーションされた実型の 1 次置き場)。
/// 多アセンブリ実行 (C1) ではローダ (画像) ごとに ObjectEngine が分かれるが、実 CLR の
/// 静的フィールドは型ごとに 1 つのストレージ (System.Decimal.One 等の CoreLib 型の .cctor が
/// 書く値) のため、ユニフィケーションで 1 つの VmType に解決された型の静的ストレージは
/// VM (Interpreter) 単位で共有しないと崩れが生じる。
///
/// タスク 2 hardening: キーを FullName 文字列から「canonical 型 identity」に変更。
/// 同一 FullName の別アセンブリ型 (ゲストが fake System.Foo を宣言したケース) では
/// ストレージを共有しない (VmType identity 参照で鍵付けする)。構築ジェネリクス型は
/// (定義型 identity + 実引数 identity 名) で鍵化する (CLR の「実引数ごとに別静的
/// ストレージ」規約と同じ形)。
/// </summary>
public sealed class UnifiedStaticStorage {
    /// <summary>storage キー (definition VmType identity + 構築文脈キー)。identity 参照で鍵化。</summary>
    private readonly Dictionary<VmTypeIdentityKey, StackSlot[]> Table = [];

    /// <summary>GC ルート源 (生成済みの全ストレージを強参照で保持する)。</summary>
    internal readonly List<StackSlot[]> Registry = [];

    /// <summary>storage identity キー (canonical VmType 参照そのもの + 構築キー)。</summary>
    private readonly record struct VmTypeIdentityKey(VmType Type, string ConstructedKey)
        : System.IEquatable<VmTypeIdentityKey>;

    /// <summary>静的ストレージを取得。canonicalType は型 identity (VmType 参照)、
    /// constructedKey は非構築型では ""、構築型では「実引数 identity を含む文脈キー」。</summary>
    internal StackSlot[] GetOrCreate(VmType canonicalType, string constructedKey, Func<StackSlot[]> factory) {
        var identityKey = new VmTypeIdentityKey(
            canonicalType ?? throw new ArgumentNullException(nameof(canonicalType)),
            constructedKey ?? "");
        if (Table.TryGetValue(identityKey, out var existing))
            return existing;
        var storage = factory();
        Table[identityKey] = storage;
        Registry.Add(storage);
        return storage;
    }

    internal IEnumerable<StackSlot[]> EnumerateRoots() => Registry;
}
