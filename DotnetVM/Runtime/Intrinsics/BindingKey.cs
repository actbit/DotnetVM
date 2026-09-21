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
    /// <summary>P/Invoke 面に対する代替実装 (ネイティブ実行の代わり。未登録は fail-closed 拒否)。</summary>
    PInvokeReplacement,
    /// <summary>仮想デバイス面 (System.Console → VmConsole 等)。</summary>
    Device,
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

    private BindingKey(string typeFullName, string methodName, string paramSignature, string returnTypeName, bool hasThis) {
        TypeFullName = typeFullName;
        MethodName = methodName;
        ParamSignature = paramSignature;
        ReturnTypeName = returnTypeName;
        HasThis = hasThis;
    }

    public string TypeFullName { get; }

    public string MethodName { get; }

    /// <summary>パラメータ型名の正規化列 (this を含まない)。</summary>
    public string ParamSignature { get; }

    /// <summary>戻り型の完全名。空文字列 = 戻り型を問わない (ワイルドカード照合)。</summary>
    public string ReturnTypeName { get; }

    public bool HasThis { get; }

    /// <summary>全引数一致面のキーか。</summary>
    public bool IsAnyParams => ParamSignature == AnyParamsSignature;

    public static BindingKey Static(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, false);

    public static BindingKey Instance(string typeFullName, string methodName, params string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), AnyReturn, true);

    /// <summary>戻り型を含む静的キー (op_Implicit / op_Explicit 等の戻り型で区別される面用)。
    /// 既存の params 形資生成と引数個数が衝突しないよう専用名で提供する。</summary>
    public static BindingKey StaticWithReturn(string typeFullName, string methodName, string returnTypeName, string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), returnTypeName, false);

    /// <summary>戻り型を含むインスタンスキー。</summary>
    public static BindingKey InstanceWithReturn(string typeFullName, string methodName, string returnTypeName, string[] paramTypeNames) =>
        new(typeFullName, methodName, Normalize(paramTypeNames), returnTypeName, true);

    /// <summary>全引数一致面 (デバイス面の統合オーバーロード等) の静的キー。</summary>
    public static BindingKey StaticAnyParams(string typeFullName, string methodName) =>
        new(typeFullName, methodName, AnyParamsSignature, AnyReturn, false);

    /// <summary>全引数一致面のインスタンスキー。</summary>
    public static BindingKey InstanceAnyParams(string typeFullName, string methodName) =>
        new(typeFullName, methodName, AnyParamsSignature, AnyReturn, true);

    /// <summary>同一面の全引数一致キー (ワイルドカード照合用)。</summary>
    public BindingKey WithAnyParams() =>
        new(TypeFullName, MethodName, AnyParamsSignature, ReturnTypeName, HasThis);

    /// <summary>戻り型をワイルドカードに緩めたキー (実引数キーで不成立時の再照合用)。</summary>
    public BindingKey WithAnyReturn() =>
        new(TypeFullName, MethodName, ParamSignature, AnyReturn, HasThis);

    private static string Normalize(string[] paramTypeNames) =>
        paramTypeNames is { Length: > 0 } ? string.Join(",", paramTypeNames) : "";

    public bool Equals(BindingKey other) =>
        HasThis == other.HasThis &&
        string.Equals(TypeFullName, other.TypeFullName, StringComparison.Ordinal) &&
        string.Equals(MethodName, other.MethodName, StringComparison.Ordinal) &&
        string.Equals(ParamSignature, other.ParamSignature, StringComparison.Ordinal) &&
        string.Equals(ReturnTypeName, other.ReturnTypeName, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is BindingKey other && Equals(other);

    public override int GetHashCode() =>
        System.HashCode.Combine(TypeFullName, MethodName, ParamSignature, ReturnTypeName, HasThis);

    public override string ToString() {
        var ret = ReturnTypeName.Length == 0 ? "" : ReturnTypeName + " ";
        return (HasThis ? "instance " : "static ") + ret + TypeFullName + "::" + MethodName + "(" +
        (IsAnyParams ? "*" : ParamSignature) + ")";
    }

    public static bool operator ==(BindingKey left, BindingKey right) => left.Equals(right);
    public static bool operator !=(BindingKey left, BindingKey right) => !left.Equals(right);
}
