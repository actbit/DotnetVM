namespace DotnetVM.Runtime;

/// <summary>
/// ABI を取り込んだアセンブリのメソッド実装を選ぶ方針。
/// </summary>
public enum AbiExecutionMode {
    /// <summary>既存の VM の解決順を使う (バインド、IL 優先面、intrinsic、IL)。</summary>
    Auto,

    /// <summary>
    /// 本体を持つ managed IL を優先して VM 内で再実行する。
    /// InternalCall / P/Invoke など本体のない面は登録済みブリッジへフォールバックする。
    /// </summary>
    ManagedIl,

    /// <summary>
    /// 登録済みのランタイムバインド／intrinsic を必須にし、managed IL 本体は実行しない。
    /// 対応するブリッジが無い面は fail-closed で拒否する。
    /// </summary>
    HostBridge,
}
