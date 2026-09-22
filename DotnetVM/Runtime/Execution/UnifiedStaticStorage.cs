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
/// 定義型 identity + 型引数の VmType identity 列で鍵化する (FullName 文字列ではなく
/// 参照同一性。同一 FullName でも別 identity の型引数では別ストレージ。CLR の
/// 「実引数ごとに別静的ストレージ」規約と同じ形)。
/// </summary>
public sealed class UnifiedStaticStorage {
    /// <summary>storage キー (definition VmType identity + 型引数 identity 列)。参照で鍵化。</summary>
    private readonly Dictionary<VmTypeIdentityKey, StackSlot[]> Table = [];

    /// <summary>GC ルート源 (生成済みの全ストレージを強参照で保持する)。</summary>
    internal readonly List<StackSlot[]> Registry = [];

    /// <summary>storage identity キー (定義 VmType 参照 + 型引数 VmType 参照列)。
    /// FullName 文字列は使わない (同名別 identity の混同防止)。</summary>
    private readonly struct VmTypeIdentityKey : IEquatable<VmTypeIdentityKey> {
        public readonly VmType Definition;
        public readonly VmType[] TypeArguments;

        public VmTypeIdentityKey(VmType definition, VmType[]? typeArguments) {
            Definition = definition;
            TypeArguments = typeArguments is { Length: > 0 } ? [.. typeArguments] : [];
        }

        public bool Equals(VmTypeIdentityKey other) {
            if (!ReferenceEquals(Definition, other.Definition))
                return false;
            if (TypeArguments.Length != other.TypeArguments.Length)
                return false;
            for (var i = 0; i < TypeArguments.Length; i++)
                if (!ReferenceEquals(TypeArguments[i], other.TypeArguments[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is VmTypeIdentityKey other && Equals(other);

        public override int GetHashCode() {
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Definition));
            hash.Add(TypeArguments.Length);
            foreach (var arg in TypeArguments)
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(arg));
            return hash.ToHashCode();
        }
    }

    /// <summary>静的ストレージを取得。definition は型 identity (VmType 参照)、
    /// typeArguments は非構築型では null/空、構築型では実引数の VmType identity 列。</summary>
    internal StackSlot[] GetOrCreate(VmType definition, VmType[]? typeArguments, Func<StackSlot[]> factory) {
        var identityKey = new VmTypeIdentityKey(
            definition ?? throw new ArgumentNullException(nameof(definition)),
            typeArguments);
        if (Table.TryGetValue(identityKey, out var existing))
            return existing;
        var storage = factory();
        Table[identityKey] = storage;
        Registry.Add(storage);
        return storage;
    }

    /// <summary>後方互換 (string キー経路)。新規コードでは使わないこと。
    /// 旧呼び出し (constructed.FullName) を定義参照のみのキーに縮退させる。</summary>
    [Obsolete("VmType identity 列の GetOrCreate(VmType, VmType[]?) を使うこと。")]
    internal StackSlot[] GetOrCreate(VmType canonicalType, string constructedKey, Func<StackSlot[]> factory) =>
        GetOrCreate(canonicalType, (VmType[]?)null, factory);

    internal IEnumerable<StackSlot[]> EnumerateRoots() => Registry;
}
