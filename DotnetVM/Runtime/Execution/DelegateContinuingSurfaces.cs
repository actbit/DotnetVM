namespace DotnetVM.Runtime.Execution;

/// <summary>
/// 表現境界 (設計原則 3): VM の内部表現に深く依存する型のうち、バインドが揃うまで
/// 「IL 本体を実行せず intrinsic / ランタイムバインドへの委譲を継続する面」。
/// このリストに載った型のメソッドは実行経路 ② (IL 本体実行) を通らず、
/// ① ランタイムバインド → ③ legacy intrinsic → ④ fail-closed のみで処理される。
/// バインドの追加に応じて縮小していく監査可能な境界であり、恣意的な切り詰めではない
/// (C5 で String の内部面をバインドした時点で IL 実行へ移行する)。
/// </summary>
internal static class DelegateContinuingSurfaces {
    /// <summary>
    /// VmString はホスト string の不変ラップであり、CoreLib String の可変 char バッファを
    /// 前提にした IL (FastAllocateString で確保 → GetRawStringData 経由で書き込み) を
    /// そのまま実行できないため、内部面のバインドが揃うまで委譲を継続する。
    /// </summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) {
        "System.String",
    };

    public static bool Contains(string typeFullName) => Types.Contains(typeFullName);
}
