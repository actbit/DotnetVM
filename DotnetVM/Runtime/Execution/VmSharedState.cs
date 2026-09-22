namespace DotnetVM.Runtime.Execution;

/// <summary>
/// VM ごとの共有状態 (インスタンス状態)。
/// static なホストプロセス共有にしない: 仮想環境変数ストアも last system error も
/// VM ごとに分離する (ホスト環境の読み替え遮断と VM 間分離のため)。
/// 将来のゲストスレッド対応では lastError をスレッドごとに分離する (現行は VM 単位の単一値)。
/// </summary>
public sealed class VmSharedState {
    /// <summary>VM ごとの仮想環境変数ストア (OrdinalIgnoreCase)。
    /// Kernel32.GetEnvironmentVariable 面は host の Environment を直接呼ばずここだけを読む。</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> VirtualEnvironment =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>VM 代替の last system error (Marshal.SetLastSystemError / GetLastSystemError 面)。
    /// 実 CLR の per-thread TLS スロットの代わりの VM 単位の単一値。</summary>
    public int LastSystemError;
}
