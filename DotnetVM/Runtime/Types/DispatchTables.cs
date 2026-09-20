namespace DotnetVM.Runtime.Types;

/// <summary>仮想ディスパッチ表の 1 スロット。Name/ParamTypes はスロットキーの元 (宣言名 + 保持側文脈の
/// パラメータ型) で、基底表を引き継ぐ際に ParamTypes を置換してキーを再計算するために保持する。
/// Method は実際に実行される実装 (MethodImpl の場合は宣言メソッドと異なる body を指しうる)。</summary>
public sealed class DispatchSlot {
    /// <summary>スロット名 (MethodImpl の明示 override では宣言メソッドの名前)。</summary>
    public required string Name { get; init; }
    /// <summary>スロット保持型の文脈で正規化済みパラメータ型 (ジェネリックパラメータは !n / !!n のまま)。</summary>
    public required VmType[] ParamTypes { get; init; }
    /// <summary>スロットに登録された実装。</summary>
    public required VmMethod Method { get; init; }

    public string Key => VmSlotKeys.Of(Name, ParamTypes);
}

/// <summary>1 つの VmClassType に付く仮想/インターフェースディスパッチ表。
/// いずれも遅延構築し (初回ディスパッチ時)、以降はキャッシュされる。</summary>
public sealed class DispatchMaps {
    /// <summary>VTable: スロットキー (名前 + パラメータ型の正規化形) → 最派生実装。</summary>
    public Dictionary<string, DispatchSlot> VTable { get; } = [];

    /// <summary>インターフェースマップ: 「インターフェース定義完全名::スロットキー」→ 実装メソッド。
    /// スロットキーはインターフェース定義のジェネリックパラメータ文脈 (!n のまま) なので、
    /// 実装側の型引数に依存せず照合できる。</summary>
    public Dictionary<string, VmMethod> InterfaceMap { get; } = [];
}

/// <summary>メソッドスロットの正規化キー。メソッド識別 = (名前, パラメータ型の完全名連結)。
/// オーバーロード誤解決 (名前 + 引数個数のみの従来照合) を解消するための導出関数。</summary>
public static class VmSlotKeys {
    public static string Of(string name, IReadOnlyList<VmType> paramTypes) {
        var builder = new System.Text.StringBuilder(name).Append('(');
        for (var i = 0; i < paramTypes.Count; i++) {
            if (i > 0)
                builder.Append(',');
            builder.Append(paramTypes[i].FullName);
        }
        return builder.Append(')').ToString();
    }

    /// <summary>インターフェースマップのキー (インターフェース定義完全名 + "::" + スロットキー)。</summary>
    public static string InterfaceSlotKey(VmType interfaceDefinition, string slotKey) =>
        interfaceDefinition.FullName + "::" + slotKey;

    public static string InterfaceSlotKey(string interfaceDefinitionName, string name, IReadOnlyList<VmType> paramTypes) =>
        interfaceDefinitionName + "::" + Of(name, paramTypes);
}
