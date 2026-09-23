namespace DotnetVM.Runtime.Intrinsics;

/// <summary>
/// ランタイムバインドの由来 (監査用)。ランタイムバインド層 (C4) は、面がどの経路で
/// 提供されているかを登録時に確定させ、実行経路を分からなくしない。
/// </summary>
public enum BindingOrigin {
    /// <summary>マネージ (IL) 実装を持つ面。IL 実行の代わりに VM が同一意味論の実装を提供する。</summary>
    Managed,
    /// <summary>CoreLib の InternalCall 面に対する VM 代替実装。</summary>
    InternalCall,
    /// <summary>P/Invoke 面に対する代替実装 (ネイティブ実行の代替。未登録は fail-closed 拒否)。</summary>
    PInvokeReplacement,
    /// <summary>仮想デバイス面 (System.Console → VmConsole 等)。</summary>
    Device,
}

/// <summary>
/// ランタイムバインドの呼出面の domain (タスク 2 hardening: 特権面の呼出元を限定する)。
/// バインドを TrustedCoreLib domain に限定したものは、CoreLib (identity で確認) からの
/// 呼出でだけ解決される。Device domain はデバイス面の intrinsic (params 呼出含む) が属し、
/// HostContract domain はホスト API 契約として受けられる面、Guest domain はゲストが
/// 直接呼べる面 (既定値) に分ける。
/// </summary>
public enum BindingDomain {
    /// <summary>ゲスト (任意アセンブリ) から呼べる面 (既定。既存のすべての登録面はこれ)。</summary>
    Guest,
    /// <summary>trusted CoreLib (identity が確認された System.Private.CoreBase 画像) の
    /// IL 内からしか呼べない特権面。External コール面 (BeginInit / MemoryMarshal 等) は
    /// ワイルドカード signature を持つがゲストからの直接呼出は拒否する (trusted CoreLib 限定)。</summary>
    TrustedCoreLib,
    /// <summary>仮想デバイス / ゲートウェイ面 (コンソール出力等)。</summary>
    Device,
    /// <summary>ホスト API 契約面 ((Command / Storage 等のゲートウェイは Guest から呼べるが、
    /// 特定引数形状が許可される面)。</summary>
    HostContract,
}

/// <summary>
/// ランタイムバインドのキー。(型完全名, メソッド名, パラメータ型名, 戻り型名, this 有無) で一意。
/// IntrinsicKey (名前 + 引数個数) を署名精度に正式化したもので、パラメータ型名は宣言上の
/// 完全名 (System.Int32 / System.String[] / System.Int32&amp; 等) をカンマ連結した正規化列で保持する。
/// 戻り型名は C5.5 継続で追加 (decimal の op_Implicit / op_Explicit 群のように
/// パラメータ列が同一で戻り型のみ異なる面を区別するため)。空文字列 = 戻り型を問わない
/// ワイルドカード (既存の全登録面はこれ)。
/// ジェネリック メソッドは開いた名 (!!n / !n) でもキー化できる (呼出側は実引数キー → 開いたキーの
/// 順に照合する)。
/// </summary>
public readonly struct BindingKey : IEquatable<BindingKey> {
    /// <summary>全引数一致面 (引数個数・型を実行時に判別する統合面) を示すパラメータ列。</summary>
    public const string AnyParamsSignature = "*";

    /// <summary>戻り型を問わない (ワイルドカード) 戻り型名。</summary>
    public const string AnyReturn = "";

    private BindingKey(string typeFullName, string methodName, string paramSignature, string returnTypeName, bool hasThis, BindingDomain domain) {
        TypeFullName = typeFullName;
        MethodName = methodName;
        ParamSignature = paramSignature;
        ReturnTypeName = returnTypeName;
        HasThis = hasThis;
        Domain = domain;
    }

    public string TypeFullName { get; }

    public string MethodName { get; }

    /// <summary>パラメータ型名の正規化列 (this を含まない)。</summary>
    public string ParamSignature { get; }

    /// <summary>戻り型の完全名。空文字列 = 戻り型を問わない (ワイルドカード照合)。</summary>
    public string ReturnTypeName { get; }

    public bool HasThis { get; }

    /// <summary>バインドの所属 domain (既定 = Guest)。TrustedCoreLib に属する面は
    /// identity で確認された CoreLib 画像からの呼出でのみ照合される (特権 binding)。</summary>
    public BindingDomain Domain { get; }

    /// <summary>全引数一致面のキーか。</summary>
    public bool IsAnyParams => ParamSignature == AnyParamsSignature;

    public static BindingKey Static(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, false, BindingDomain.Guest);

    public static BindingKey Instance(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, true, BindingDomain.Guest);

    /// <summary>戻り型を含む静的キー (op_Implicit / op_Explicit 等の戻り型で区別される面用)。
    /// 既存の params 形資生成と引数個数が衝突しないよう専用名で提供する。</summary>
    public static BindingKey StaticWithReturn(string typeFullName, string methodName, string returnTypeName, string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), returnTypeName, false, BindingDomain.Guest);

    /// <summary>戻り型を含むインスタンスキー。</summary>
    public static BindingKey InstanceWithReturn(string typeFullName, string methodName, string returnTypeName, string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), returnTypeName, true, BindingDomain.Guest);

    /// <summary>引数の型は問わず、宣言引数個数だけを固定したインスタンス面。</summary>
    public static BindingKey StaticByArity(string typeFullName, string methodName, int parameterCount) =>
        new(typeFullName, methodName, AnyTypes(parameterCount), AnyReturn, false, BindingDomain.Guest);

    /// <summary>引数の型は問わず、宣言引数個数だけを固定したインスタンス面。</summary>
    public static BindingKey InstanceByArity(string typeFullName, string methodName, int parameterCount) =>
        new(typeFullName, methodName, AnyTypes(parameterCount), AnyReturn, true, BindingDomain.Guest);

    /// <summary>全引数一致面 (デバイス面の統合オーバーロード等) の静的キー。</summary>
    public static BindingKey StaticAnyParams(string typeFullName, string methodName) =>
        new(typeFullName, methodName, AnyParamsSignature, AnyReturn, false, BindingDomain.Guest);

    /// <summary>全引数一致面のインスタンスキー。</summary>
    public static BindingKey InstanceAnyParams(string typeFullName, string methodName) =>
        new(typeFullName, methodName, AnyParamsSignature, AnyReturn, true, BindingDomain.Guest);

    /// <summary>trusted CoreLib 限定特権面の静的キー (domain = TrustedCoreLib)。
    /// identity で確認された System.Private.CoreLib 画像の IL からの呼出のみ照合される。</summary>
    public static BindingKey TrustedStatic(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, false, BindingDomain.TrustedCoreLib);

    /// <summary>trusted CoreLib 限定のインスタンスキー。</summary>
    public static BindingKey TrustedInstance(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, true, BindingDomain.TrustedCoreLib);

    /// <summary>trusted CoreLib 限定の全引数一致静的キー (メソッド演算面等)。</summary>
    public static BindingKey TrustedStaticAnyParams(string typeFullName, string methodName) =>
        new(typeFullName, methodName, AnyParamsSignature, AnyReturn, false, BindingDomain.TrustedCoreLib);

    /// <summary>同一面の全引数一致キー (ワイルドカード照合用)。</summary>
    public BindingKey WithAnyParams() =>
        new(TypeFullName, MethodName, AnyParamsSignature, ReturnTypeName, HasThis, Domain);

    /// <summary>同じ引数個数で型だけをワイルドカードにした照合キー。</summary>
    public BindingKey WithAnyParamTypes() =>
        new(TypeFullName, MethodName, AnyTypes(ParamSignature.Length == 0 ? 0 : ParamSignature.Split(',').Length),
            ReturnTypeName, HasThis, Domain);

    /// <summary>同一面の domain 違いキー (trusted caller が特権面と汎用面の双方を照合する用)。</summary>
    public BindingKey WithDomain(BindingDomain domain) =>
        new(TypeFullName, MethodName, ParamSignature, ReturnTypeName, HasThis, domain);

    /// <summary>戻り型をワイルドカードに緩めたキー (実引数キーで不成立時の再照合用)。</summary>
    public BindingKey WithAnyReturn() =>
        new(TypeFullName, MethodName, ParamSignature, AnyReturn, HasThis, Domain);

    private static string Normalize(string[] paramTypeNames) =>
        paramTypeNames is { Length: > 0 } ? string.Join(",", paramTypeNames) : "";

    private static string AnyTypes(int count) =>
        count == 0 ? "" : string.Join(",", Enumerable.Repeat("?", count));

    public bool Equals(BindingKey other) =>
        HasThis == other.HasThis &&
        Domain == other.Domain &&
        string.Equals(TypeFullName, other.TypeFullName, StringComparison.Ordinal) &&
        string.Equals(MethodName, other.MethodName, StringComparison.Ordinal) &&
        string.Equals(ParamSignature, other.ParamSignature, StringComparison.Ordinal) &&
        string.Equals(ReturnTypeName, other.ReturnTypeName, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is BindingKey other && Equals(other);

    public override int GetHashCode() =>
        System.HashCode.Combine(TypeFullName, MethodName, ParamSignature, ReturnTypeName, HasThis, Domain);

    public override string ToString() {
        var ret = ReturnTypeName.Length == 0 ? "" : ReturnTypeName + " ";
        var domain = Domain == BindingDomain.Guest ? "" : " [" + Domain + "]";
        return (HasThis ? "instance " : "static ") + ret + TypeFullName + "::" + MethodName + "(" +
        (IsAnyParams ? "*" : ParamSignature) + ")" + domain;
    }

    public static bool operator ==(BindingKey left, BindingKey right) => left.Equals(right);
    public static bool operator !=(BindingKey left, BindingKey right) => !left.Equals(right);
}
