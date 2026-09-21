using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>
/// VM CoreLib 置換面 (C5): 実在 System.Private.CoreLib の面のうち、その managed IL が
/// VM の表現モデルに落ちないもの (Number.Formatting の byte* ポインタ演算 / NumberBuffer /
/// culture 機構の静的キャッシュ等) を、自前の互換ライブラリ DotnetVM.CoreLib
/// (VM で IL 実行できる形に限定した managed IL / かつ CLR 上でも動く普通のクラスライブラリ)
/// の実装に差し替える監査可能な対応表。
///
/// - 置換先は VM で IL 実行されるため、ExecutionTracer にはアセンブリ名 "DotnetVM.CoreLib"
///   のフレームとして記録される (legacy intrinsic への委譲とは経路が区別できる)
/// - DotnetVM.CoreLib の正当性は CLR 上での差分テスト (VmCoreLibClrTests) で担保してから
///   ここに配線する
/// - 差し替え本体は Interpreter.Invoke (唯一の IL 実行入口) で行う。MemberRef 解決
///   (CallEngine) でも仮想ディスパッチでも最終的にそこを通るため、CoreLib IL 内の
///   boxed int の callvirt ToString も同じ面で置換される
/// - ここに載っていない面 (System.Convert::ToInt32(object) 等のボックス化経由面) は
///   従来どおり ①バインド → ③legacy intrinsic で処理される (勝手に絞らない・増やさない)
/// </summary>
internal sealed class VmCoreLibSurfaces {
    /// <summary>監査可能な対応表: 実在 CoreLib の面 declaringType::name(paramTypeNames) →
    /// DotnetVM.CoreLib 側の static 実装 (ImplType::ImplMethod(ImplParams))。
    /// paramTypeNames / ImplParams は統合後の実型完全名。</summary>
    private static readonly
        (string Type, string Method, string[] Params, string ImplType, string ImplMethod, string[] ImplParams)[]
        Faces = [
        // 整数 10 進書式 (実在側は Number.Formatting + NumberFormatInfo culture 機構 +
        // char* 生ポインタ演算 + smallNumberCache 静的キャッシュで構成される)
        ("System.Int32", "ToString", [], "DotnetVM.CoreLib.NumberFormatting", "Int32ToString", ["System.Int32"]),
        ("System.Int64", "ToString", [], "DotnetVM.CoreLib.NumberFormatting", "Int64ToString", ["System.Int64"]),
        ("System.UInt32", "ToString", [], "DotnetVM.CoreLib.NumberFormatting", "UInt32ToString", ["System.UInt32"]),
        ("System.UInt64", "ToString", [], "DotnetVM.CoreLib.NumberFormatting", "UInt64ToString", ["System.UInt64"]),
        // IConvertible.ToString(IFormatProvider) の暗黙実装面 (Convert.ToString(object) の
        // 実 IL が interface ディスパッチで辿る)。provider は不変カルチャ固定で無視
        // (置換後の static 実装は引数 1 個のみ受け、余剰スロットは choke point が無視する)
        ("System.Int32", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.NumberFormatting", "Int32ToString", ["System.Int32"]),
        ("System.Int64", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.NumberFormatting", "Int64ToString", ["System.Int64"]),
        ("System.UInt32", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.NumberFormatting", "UInt32ToString", ["System.UInt32"]),
        ("System.UInt64", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.NumberFormatting", "UInt64ToString", ["System.UInt64"]),
        // 整数 10 進解析 (NumberStyles.Integer / 不変カルチャ相当)
        ("System.Int32", "Parse", ["System.String"], "DotnetVM.CoreLib.NumberFormatting", "ParseInt32", ["System.String"]),
        ("System.Int64", "Parse", ["System.String"], "DotnetVM.CoreLib.NumberFormatting", "ParseInt64", ["System.String"]),
        // Convert 面の文字列⇔整数変換 (ボックス化 object 経由の面は載せない = legacy 経路継続)
        ("System.Convert", "ToInt32", ["System.String"], "DotnetVM.CoreLib.IntegerConvert", "ToInt32", ["System.String"]),
        ("System.Convert", "ToInt64", ["System.String"], "DotnetVM.CoreLib.IntegerConvert", "ToInt64", ["System.String"]),
        ("System.Convert", "ToString", ["System.Int32"], "DotnetVM.CoreLib.IntegerConvert", "ToString", ["System.Int32"]),
        ("System.Convert", "ToBoolean", ["System.Int32"], "DotnetVM.CoreLib.IntegerConvert", "ToBoolean", ["System.Int32"]),
        ("System.Convert", "ToChar", ["System.Int32"], "DotnetVM.CoreLib.IntegerConvert", "ToChar", ["System.Int32"]),
        // Convert の (string, IFormatProvider) 面 (C5.5 Wave 1): Convert.ToXxx(object) を
        // 実 IL 化した際に String の IConvertible EII が Convert.ToXxx(value, provider) を
        // 辿り、先の先が Number.Formatting (char* / NumberBuffer) に落ちるため
        // 不変カルチャ解析の置換 IL へ差し替える (provider は不変カルチャ固定で無視)
        ("System.Convert", "ToInt32", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToInt32", ["System.String"]),
        ("System.Convert", "ToInt64", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToInt64", ["System.String"]),
        ("System.Convert", "ToBoolean", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToBoolean", ["System.String"]),
        ("System.Convert", "ToByte", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToByte", ["System.String"]),
        ("System.Convert", "ToSByte", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToSByte", ["System.String"]),
        ("System.Convert", "ToInt16", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToInt16", ["System.String"]),
        ("System.Convert", "ToUInt16", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToUInt16", ["System.String"]),
        ("System.Convert", "ToUInt32", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToUInt32", ["System.String"]),
        ("System.Convert", "ToUInt64", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.IntegerConvert", "ToUInt64", ["System.String"]),
        // ---- C5.5 Wave 2: 整数書式 overload (標準書式 G/D/X/B/C/F/N/E/P/R + カスタム書式)。
        // 実在側は Number.Formatting (byte* 生ポインタ + NumberBuffer + stackalloc) +
        // NumberFormatInfo culture 機構で構成され VM の表現モデルに落ちないため、
        // DotnetVM.CoreLib.FormatSpecifiers (意味論移植・不変カルチャ固定) へ差し替える。
        // provider は不変カルチャ固定で無視するが 3 引数面は impl 側も 3 パラメータで受ける
        // (余剰スロット無視は末尾切捨てのみで、位置がずれる format には使えない)
        ("System.Byte", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "ByteToString", ["System.Byte", "System.String"]),
        ("System.SByte", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "SByteToString", ["System.SByte", "System.String"]),
        ("System.Int16", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString", ["System.Int16", "System.String"]),
        ("System.UInt16", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt16ToString", ["System.UInt16", "System.String"]),
        ("System.Int32", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "Int32ToString", ["System.Int32", "System.String"]),
        ("System.UInt32", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt32ToString", ["System.UInt32", "System.String"]),
        ("System.Int64", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "Int64ToString", ["System.Int64", "System.String"]),
        ("System.UInt64", "ToString", ["System.String"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt64ToString", ["System.UInt64", "System.String"]),
        ("System.Byte", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "ByteToString", ["System.Byte", "System.String", "System.Object"]),
        ("System.SByte", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "SByteToString", ["System.SByte", "System.String", "System.Object"]),
        ("System.Int16", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString", ["System.Int16", "System.String", "System.Object"]),
        ("System.UInt16", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt16ToString", ["System.UInt16", "System.String", "System.Object"]),
        ("System.Int32", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "Int32ToString", ["System.Int32", "System.String", "System.Object"]),
        ("System.UInt32", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt32ToString", ["System.UInt32", "System.String", "System.Object"]),
        ("System.Int64", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "Int64ToString", ["System.Int64", "System.String", "System.Object"]),
        ("System.UInt64", "ToString", ["System.String", "System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt64ToString", ["System.UInt64", "System.String", "System.Object"]),
        // 整数 4 小型 (Byte/SByte/Int16/UInt16) の無引数 / provider のみの面 (既存の NumberFormatting
        // 面は Int32/64/UInt32/64 のみだった)。IFormatProvider のみの面は余剰スロット無視で 1 引数 impl へ
        ("System.Byte", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "ByteToString", ["System.Byte"]),
        ("System.SByte", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "SByteToString", ["System.SByte"]),
        ("System.Int16", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString", ["System.Int16"]),
        ("System.UInt16", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "UInt16ToString", ["System.UInt16"]),
        ("System.Byte", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "ByteToString", ["System.Byte"]),
        ("System.SByte", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "SByteToString", ["System.SByte"]),
        ("System.Int16", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "Int16ToString", ["System.Int16"]),
        ("System.UInt16", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "UInt16ToString", ["System.UInt16"]),
        // Boolean / Char の ToString 面 (実在側は culture 機構を辿る。True/False と 1 文字は
        // 不変)。Char の 1 文字生成は char.ToString() を使わない (Faces 置換の再帰になるため
        // new string(char, int) で生成する)
        ("System.Boolean", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "BooleanToString", ["System.Boolean"]),
        ("System.Boolean", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "BooleanToString", ["System.Boolean"]),
        ("System.Char", "ToString", [], "DotnetVM.CoreLib.FormatSpecifiers", "CharToString", ["System.Char"]),
        ("System.Char", "ToString", ["System.IFormatProvider"], "DotnetVM.CoreLib.FormatSpecifiers", "CharToString", ["System.Char"]),
    ];

    private readonly TypeLoader _coreLibLoader;
    private readonly Dictionary<string, VmMethod> _substituteByFace = new(StringComparer.Ordinal);
    private readonly Dictionary<VmMethod, string?> _faceKeyByMethod = [];

    private VmCoreLibSurfaces(TypeLoader coreLibLoader) => _coreLibLoader = coreLibLoader;

    /// <summary>DotnetVM.CoreLib のローダから対応表を構築する。
    /// 監査表どおりの実装が 1 つでも欠けていれば VM 構築を失敗させる (fail-closed)。</summary>
    public static VmCoreLibSurfaces Create(TypeLoader vmCoreLibLoader) {
        var surfaces = new VmCoreLibSurfaces(vmCoreLibLoader);
        foreach (var (type, method, paramTypes, implType, implMethod, implParams) in Faces) {
            var impl = vmCoreLibLoader.FindTypeByFullName(implType)?.Methods.FirstOrDefault(m => {
                    if (m.Name != implMethod || !m.IsStatic || m.Body is null ||
                        m.Signature.ParamTypes.Length != implParams.Length)
                        return false;
                    // オーバーロード (IntegerConvert.ToString(int) / ToString(long) 等) を
                    // パラメータ実型で区別する (解決できない署名は不一致扱い)
                    var resolved = vmCoreLibLoader.TryResolveSlotParams(m.Signature.ParamTypes);
                    return resolved is not null &&
                        resolved.Select(p => p.FullName).SequenceEqual(implParams, StringComparer.Ordinal);
                })
                ?? throw new InvalidOperationException(
                    $"VM CoreLib の置換面 {implType}::{implMethod}({string.Join(",", implParams)}) が見つかりません (DotnetVM.CoreLib.dll の内容が監査表と不一致)。");
            surfaces._substituteByFace[FaceKey(type, method, paramTypes)] = impl;
        }
        return surfaces;
    }

    /// <summary>監査テスト用: 置換面 (b) の実配線一覧 (CoreLibSurfaceAuditTests の
    /// シャドウ検査が この実体を唯一の真実源として参照する)。</summary>
    internal static IReadOnlyList<(string Type, string Method, string[] Params)> FacesForAudit =>
        Faces.Select(f => (f.Type, f.Method, f.Params)).ToArray();

    /// <summary>MemberRef 解決用: この面が置換対象なら true (呼出側は実型 IL へ解決し、
    /// 実際の差し替えは Interpreter.Invoke で行う。検査だけなら cost は辞書 1 回分)。</summary>
    public bool HasFace(string typeFullName, string methodName, string[] paramTypeNames) =>
        _substituteByFace.ContainsKey(FaceKey(typeFullName, methodName, paramTypeNames));

    /// <summary>Interpreter.Invoke 用: 実在 CoreLib 由来のメソッドが置換面に載っていれば
    /// DotnetVM.CoreLib 側の実装へ差し替える。面キーはメソッドごとにキャッシュ
    /// (Invoke は頻出するため署名再解決を繰り返さない)。</summary>
    public VmMethod? Substitute(VmMethod method) {
        if (ReferenceEquals(method.Loader, _coreLibLoader))
            return null; // 置換先 (DotnetVM.CoreLib) 自身の IL はこれ以上置換しない
        if (!_faceKeyByMethod.TryGetValue(method, out var key)) {
            var paramNames = method.Loader?.TryResolveSlotParams(method.Signature.ParamTypes);
            key = paramNames is null
                ? null // 署名が解決できない面は置換せず従来経路にフォールバック
                : FaceKey(method.DeclaringType.FullName, method.Name,
                    paramNames.Select(p => p.FullName).ToArray());
            _faceKeyByMethod[method] = key;
        }
        return key is not null && _substituteByFace.TryGetValue(key, out var substitute) ? substitute : null;
    }

    private static string FaceKey(string typeFullName, string methodName, string[] paramTypeNames) =>
        $"{typeFullName}::{methodName}({string.Join(",", paramTypeNames)})";
}
