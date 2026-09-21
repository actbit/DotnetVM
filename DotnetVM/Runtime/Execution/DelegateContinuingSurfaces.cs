namespace DotnetVM.Runtime.Execution;

/// <summary>
/// 表現境界 (設計原則 3): VM の内部表現に深く依存する型のうち、バインドが揃うまで
/// 「IL 本体を実行せず intrinsic / ランタイムバインドへの委譲を継続する面」。
/// このリストに載った型のメソッドは実行経路 ② (IL 本体実行) を通らず、
/// ① ランタイムバインド → ③ legacy intrinsic → ④ fail-closed のみで処理される。
/// バインドの追加に応じて縮小していく監査可能な境界であり、恣意的な切り詰めではない。
///
/// C5: String の内部面 (FastAllocateString / GetRawStringData 系 / Buffer.Memmove /
/// Unsafe.*) をバインドし、VmString を CoreLib と同一の可変 char バッファ表現
/// (4 バイト LE 長ヘッダ + UTF-16LE) にしたため委譲継続面は空になった。
/// CoreLib IL が VM 表現で実行できない面が発見されればここへ戻す。
/// </summary>
internal static class DelegateContinuingSurfaces {
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// IL 優先面 (C5): 実型 (CoreLib TypeDef) に本体が存在する面を legacy intrinsic
    /// (③) より先に IL 本体実行 (②) へ落とす型。優先順位 ①→②→③ の本来の順序を
    /// 適用する面であり、CoreLib の内部面バインドが揃った型から順に移していく。
    /// (ここに無い型は従来どおり ①→③→② の順で解決され、culture 依存面など
    ///  legacy 委譲が正当な面を保護する)
    /// </summary>
    public static readonly IReadOnlySet<string> IlPreferred = new HashSet<string>(StringComparer.Ordinal) {
        "System.String",
        "System.Math",
        "System.Int32",
        // 整数 ToString/Parse は VmCoreLibSurfaces (DotnetVM.CoreLib の managed IL) への
        // 置換面と組で有効化する (② で実型 IL へ解決 → Invoke の choke point で差し替え)
        "System.Int64",
        "System.UInt32",
        "System.UInt64",
        // Object の既定面 (ToString() / Equals ×2 / .ctor) は C5.5 Wave 4 で逆アセンブル確認
        // の上 managed IL 本体のみで構成されることが確定 (ToString は GetType() 呼び、
        // Equals は参照比較 + 仮想呼び、.ctor は空本体。依存は GetType / Type 面 intrinsic
        // リーフで完結)。③ legacy intrinsic (VM ランタイム オブジェクト向け特殊化) より
        // ② IL 本体を優先する。GetType / GetHashCode は内部表現依存のため ① バインドが
        // 常に先に握る (CoreLibBindings.RegisterObject)
        "System.Object",
    };

    public static bool Contains(string typeFullName) => Types.Contains(typeFullName);

    public static bool PrefersIl(string typeFullName) => IlPreferred.Contains(typeFullName);

    /// <summary>
    /// 面単位の IL 優先 (C5.5 Wave 1): 型全体を IlPreferred に上げるには依存面が揃っていない
    /// 型でも、「実型 IL に本体があり VM の依存面で完結する」特定の面だけを legacy intrinsic
    /// (③) より先に IL 本体実行 (②) へ落とす。キーは (型, メソッド, 宣言パラメータ型名の連結)。
    ///
    /// Convert の object 経由面 (Convert.ToXxx(object)) は本家 IL が
    /// ((IConvertible)value).ToXxx(null) の interface ディスパッチ 1 呼出のみで構成されるため、
    /// box レシーバの EII 解決に委ねて実 IL 直実行できる (Enum / プリミティブの EII は
    /// Enum.GetValue 等の internal-call リーフバインドで完結)。
    /// 浮動小数点 2 面 (ToSingle / ToDouble) も C5.5 Wave 3 で文字列入力時の解析が
    /// DotnetVM.CoreLib.DoubleParsing 置換面 (b) として揃ったため IL 優先に上げた。
    /// CoreLib 未ロード時は解決に失敗して ③ に落ちるため legacy 動作は変わらない。
    /// </summary>
    public static readonly IReadOnlySet<(string Type, string Method, string Params)> IlPreferredFaces =
        new HashSet<(string, string, string)>(
            new[] { "ToBoolean", "ToChar", "ToSByte", "ToByte", "ToInt16", "ToUInt16",
                    "ToInt32", "ToUInt32", "ToInt64", "ToUInt64", "ToString",
                    "ToSingle", "ToDouble" }
                .Select(name => ("System.Convert", name, "System.Object")));
            // (string, string, string) の既定等値比較は要素ごとの ordinal string 比較なので
            // 明示的な comparer は不要

    /// <summary>宣言パラメータ型名が一致する面単位 IL 優先の指定があるか (優先順位 ② の追加条件)。</summary>
    public static bool PrefersIlFace(string typeFullName, string methodName, string[]? paramTypeNames) =>
        paramTypeNames is not null &&
        IlPreferredFaces.Contains((typeFullName, methodName, string.Join(",", paramTypeNames)));
}
