using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// ゲスト例外の内部伝播媒体 (制御フロー例外方式)。
/// ゲストが throw した VM オブジェクト (VmExceptionObject / VmClassInstance) を載せて
/// インタプリタのフレーム間を伝播する。EH 句での catch マッチングは ExceptionType で行う。
/// 未処理のまま VM 外に出た場合は VirtualMachine 境界で UnhandledGuestException に変換される。
/// ResourceExhaustedException 系 (メモリ/命令クォータ) はこの媒体に載らず管理例外として最上位まで伝播する。
/// </summary>
internal sealed class VmGuestThrow : Exception {
    /// <summary>ゲストに見える例外オブジェクト (catch 句にスタックへ積まれる)。</summary>
    public VmObject ExceptionObject { get; }

    /// <summary>catch マッチング用の実行時型。</summary>
    public VmType ExceptionType { get; }

    /// <summary>ホスト境界報告用の型フルネーム。</summary>
    public string ExceptionTypeName { get; }

    /// <summary>ホスト境界報告用のメッセージ (取れない場合は null)。</summary>
    public string? MessageText { get; }

    public VmGuestThrow(VmObject exceptionObject, VmType exceptionType, string exceptionTypeName, string? messageText) {
        ExceptionObject = exceptionObject;
        ExceptionType = exceptionType;
        ExceptionTypeName = exceptionTypeName;
        MessageText = messageText;
    }
}
