namespace DotnetVM.Runtime.Execution;

/// <summary>
/// CoreLib 委譲面の分類 (C5.5「載っていない面をゼロにする」)。
/// VM に登録された intrinsic / ランタイムバインドの面 (= 実在 CoreLib の managed IL を
/// 実行せず VM 側で処理する面) を、その存在理由つきで全件分類する。
///
/// 分類:
/// - <see cref="CoreLibSurfaceKind.RealCoreLibIl"/>: 実在 CoreLib の IL が実際に実行される面。
///   この Kind を持つ委譲キーは「影」(IlPreferred / ① バインドにより到達しない残置) であり、
///   正当化は "shadowed-..." で始まること (CoreLibSurfaceAuditTests が検査)。
/// - <see cref="CoreLibSurfaceKind.VmCoreLibSubstitute"/>: DotnetVM.CoreLib の置換 IL で実行
///   される面 (実配線は VmCoreLibSurfaces.Faces = 唯一の真実源)。
/// - <see cref="CoreLibSurfaceKind.RuntimeInternal"/>: 実 CLR も IL を実行しない面
///   (JIT intrinsic / InternalCall / ランタイム内部表現依存 / culture スコープ外)。
/// - <see cref="CoreLibSurfaceKind.Device"/>: VM デバイス / ゲートウェイ面
///   (I/O は設備経由限定という設計原則による仮想化対象面)。
///
/// 未知のキー (= 監査表に載っていない新規登録) は CoreLibSurfaceAuditTests が検出して落ちる。
/// </summary>
internal enum CoreLibSurfaceKind {
    RealCoreLibIl,
    VmCoreLibSubstitute,
    RuntimeInternal,
    Device,
}

/// <summary>監査表 1 エントリ。HasThis / ParamCount を null にすると任意に一致する
/// (統合面やオーバーロード群の group 分類用)。ParamCount は this を含まない宣言パラメータ数。</summary>
internal readonly record struct CoreLibSurfaceEntry(
    string Type,
    string Method,
    CoreLibSurfaceKind Kind,
    string Justification,
    bool? HasThis,
    int? ParamCount) {
    public bool Matches(string typeFullName, string methodName, bool hasThis, int? paramCount) =>
        Type == typeFullName &&
        Method == methodName &&
        (HasThis is null || HasThis == hasThis) &&
        (ParamCount is null || (paramCount is { } actual && actual == ParamCount));
}

/// <summary>CoreLib 面の監査表本体。全 intrinsic / バインド登録がこの表に載っていることを
/// CoreLibSurfaceAuditTests が双方向 (順方向: キー→分類 / 逆方向: 分類→キー) で検査する。</summary>
internal static class CoreLibSurfaceAudit {
    // 正当化タグ (shadowed- で始めるものは「実 IL が実際には実行される面の残置キー」)
    public const string JitIntrinsic = "jit-intrinsic: 実 CLR も JIT が IL を丸ごと置き換える面";
    public const string InternalCall = "internal-call: CoreLib 上で本体 IL が存在しない面 (InternalCall)";
    public const string RuntimeRepresentation = "runtime-representation: ランタイム内部表現依存で VM の表現モデルに落ちない面";
    public const string CultureOutOfScope = "culture-out-of-scope: culture 機構スコープ外 (不変カルチャ固定) のため暫定委譲";
    public const string DeviceFace = "device: I/O は仮想デバイス / ゲートウェイ経由限定という VM 設計原則による面";

    /// <summary>登録済み全 intrinsic / バインドキーの分類表 (唯一の真実源)。</summary>
    public static readonly CoreLibSurfaceEntry[] Delegations = BuildDelegations();

    /// <summary>キーを分類する (未載 = null。監査テスト / 診断用)。
    /// 段階照合: ①パラメータ数が特定のエントリ (最も精密な分類) → ②統合面 (ParamCount null)。
    /// 特定エントリ (例: 影の残置 legacy キー) が統合面 (バインド用) に呑まれないようにするため。</summary>
    public static CoreLibSurfaceEntry? Classify(string typeFullName, string methodName, bool hasThis, int? paramCount) {
        foreach (var requireSpecificParams in new[] { true, false }) {
            var match = Delegations.FirstOrDefault(e =>
                e.Type == typeFullName &&
                e.Method == methodName &&
                (e.HasThis is null || e.HasThis == hasThis) &&
                (requireSpecificParams
                    ? e.ParamCount is not null && paramCount is { } actual && e.ParamCount == actual
                    : e.ParamCount is null));
            if (match.Type is not null)
                return match;
        }
        return null;
    }

    private static CoreLibSurfaceEntry[] BuildDelegations() {
        var entries = new List<CoreLibSurfaceEntry>();
        void Add(string type, string method, CoreLibSurfaceKind kind, string justification,
            bool? hasThis = null, int? paramCount = null) =>
            entries.Add(new CoreLibSurfaceEntry(type, method, kind, justification, hasThis, paramCount));
        void AddRange(IEnumerable<string> types, string method, CoreLibSurfaceKind kind, string justification,
            bool hasThis, int? paramCount = null) {
            foreach (var t in types)
                Add(t, method, kind, justification, hasThis, paramCount);
        }

        var integer4 = new[] { "System.Int32", "System.UInt32", "System.Int64", "System.UInt64" };

        // ---- CoreLibBindings: 書式付きプリミティブ ToString (culture 書式面) ----
        // C5.5 Wave 2 で整数 8 型 + Boolean/Char、Wave 3 で Single/Double の全 ToString 面
        // (無引数 / format / format+IFormatProvider / IFormatProvider) を Faces 置換面 (b) へ
        // 移行済み (VmCoreLibSurfaces.Faces → DotnetVM.CoreLib FormatSpecifiers /
        // DoubleFormatting。format を無視するバインドは廃止済み)

        // ---- CoreLibBindings: 構築ジェネリック インターフェース (プリミティブ実体化面) ----
        var comparableJ = RuntimeRepresentation +
            "。プリミティブ実体化の IComparable`1/IEquatable`1 面 (box 経由の実装詳細に依存)。CoreLib IL 内の制約付き callvirt を受ける";
        Add("System.IComparable`1", "CompareTo", CoreLibSurfaceKind.RuntimeInternal, comparableJ, hasThis: true, paramCount: 1);
        Add("System.IEquatable`1", "Equals", CoreLibSurfaceKind.RuntimeInternal, comparableJ, hasThis: true, paramCount: 1);

        // ---- CoreLibBindings: Enum / Object / Monitor ----
        Add("System.Enum", "HasFlag", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (IL は GetRawData + Unsafe.As の生ポインタ演算。VM は生ビット AND で同等提供)", hasThis: true, paramCount: 1);
        // Enum の IConvertible EII (Convert.ToXxx(GetValue()) 形) が依存する internal-call リーフ。
        // EII 本体の IL は実在 CoreLib のまま実行される (C5.5 Wave 1)
        Add("System.Enum", "GetValue", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (IL は GetRawData の生ポインタ読み。VM は box の生値スロットを基底型で再ボックス化)", hasThis: true, paramCount: 0);
        Add("System.Enum", "InternalGetCorElementType", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (IL は GetMethodTable → MethodTable 内部表現アクセス。VM は value__ 型から同一要素型コードを返す)", hasThis: true, paramCount: 0);
        Add("System.Object", "GetType", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (CoreLib IL は GetMethodTable → MethodTable 内部表現へ直接アクセス)", hasThis: true, paramCount: 0);

        // ---- CoreLibBindings: System.Decimal (C5.5 探査継続) ----
        // 本家 IL 本体は Decimal ↔ DecCalc の Unsafe.As 参照再解釈 (同一ビット列の型視点差し替え) で
        // 構成されるため VM のオブジェクト表現 (VmStructValue スロット列) では IL 実行にできない。
        // 実 CLR も JIT intrinsic / ランタイム内部で処理する面と同型のため、同一意味論の
        // ホスト BCL 実装へ委譲し、戻り値は VM の System.Decimal 構造体値に正規化
        var decJ = RuntimeRepresentation +
            " (本家 IL は Decimal ↔ DecCalc の Unsafe.As 参照再解釈で構成され VM のオブジェクト表現では IL 実行にできない。実 CLR も JIT intrinsic / 内部面として処理。ホスト同一意味論へ委譲、結果は VM 構造体値に正規化。ToString 書式面は (b) DecimalFormatting)";
        Add("System.Decimal", "Parse", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "TryParse", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Addition", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Subtraction", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Multiply", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Division", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Remainder", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_UnaryNegation", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Add", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Subtract", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Multiply", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Divide", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Equality", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Inequality", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_GreaterThan", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_LessThan", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_GreaterThanOrEqual", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_LessThanOrEqual", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Compare", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Implicit", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "op_Explicit", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Round", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Truncate", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Floor", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);
        Add("System.Decimal", "Ceiling", CoreLibSurfaceKind.RuntimeInternal, decJ, hasThis: false);

        // ---- CoreLibBindings: System.Math (拡張 overload / JIT intrinsic 面) ----
        // 本家 IL は double 丸め機構 (ModF InternalCall / fixed バッファ) と JIT intrinsic で
        // 構成されるため VM の表現境界。ホスト同一意味論へ委譲し結果は VM スロットに正規化。
        // 基本算術面 (Abs / Sqrt 等) は既存の一般経路 (② IL 実行 / ③ legacy) のまま
        var mathJ = RuntimeRepresentation +
            " (本家 IL は ModF InternalCall / fixed バッファの丸め核と JIT intrinsic で構成 = VM 表現境界。ホスト同一意味論へ委譲)";
        Add("System.Math", "ModF", CoreLibSurfaceKind.RuntimeInternal,
            InternalCall + " (CoreLib IL の double 丸め核が呼ぶ InternalCall 面。本家筐体は QCall 相当の double* 署名で呼ぶため両形状を登録。戻り = 整数部 / out 参照先 = 小数部)", hasThis: false);
        Add("System.Math", "Round", CoreLibSurfaceKind.RuntimeInternal, mathJ, hasThis: false);
        Add("System.Math", "Truncate", CoreLibSurfaceKind.RuntimeInternal, mathJ, hasThis: false);
        Add("System.Math", "BigMul", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + "。ホスト同一面 (64bit 積) で提供", hasThis: false);
        Add("System.Math", "ILogB", CoreLibSurfaceKind.RuntimeInternal, mathJ, hasThis: false);
        Add("System.Math", "ScaleB", CoreLibSurfaceKind.RuntimeInternal, mathJ, hasThis: false);
        Add("System.Math", "Sign", CoreLibSurfaceKind.RuntimeInternal, mathJ, hasThis: false);
        Add("System.Math", "CopySign", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + "。ホスト同一面で提供", hasThis: false);
        Add("System.Math", "MaxMagnitude", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + "。ホスト同一面で提供", hasThis: false);
        Add("System.Math", "FusedMultiplyAdd", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + "。ホスト同一面 (IEEE 754 FMA) で提供", hasThis: false);
        Add("System.Math", "DivRem", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " ( managed 戻り ValueTuple 面。VM は ValueTuple`2 構造体値を構築して返す)", hasThis: false);

        // ---- CoreLibBindings: string 整形 / SCI overload 面 (C5.5 探査継続) ----
        // 本家 IL は Span / fixed char* 内部 (PadLeft/PadRight の SpanFill、Remove の
        // Substring+InnerAlloc 連鎖) と SCI 抽象化面 (SIMD ignore-case = ISimdVector static
        // abstract = VM 表現境界) で構成される。整形面はホスト ordinal 同意味論、SCI overload 群は
        // culture 面 (下記) と同じ不変カルチャ写像で提供する
        var cultureFaceJ = CultureOutOfScope +
            "。本家 IL は CompareInfo (culture 機構) で構成され IL 移植対象外のため、不変カルチャ固定のホスト BCL 委譲を継続する (C5.5 Wave 5 で CurrentCulture から不変へ修正)";
        var stringFill = RuntimeRepresentation +
            " (本家 IL は SpanFill / InnerAlloc の fixed char* 内部 = VM 表現境界。ホスト ordinal 同意味論へ委譲)";
        Add("System.String", "Remove", CoreLibSurfaceKind.RuntimeInternal, stringFill, hasThis: true);
        Add("System.String", "PadLeft", CoreLibSurfaceKind.RuntimeInternal, stringFill, hasThis: true);
        Add("System.String", "PadRight", CoreLibSurfaceKind.RuntimeInternal, stringFill, hasThis: true);
        Add("System.String", "StartsWith", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: true);
        Add("System.String", "EndsWith", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: true);
        Add("System.String", "LastIndexOf", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: true);
        Add("System.String", "Join", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (連結本体は wstrcpy 生ポインタコピー面。要素の ToString は VM の暗黙 ToString フック経由で正規化)", hasThis: false);

        // ---- CoreLibBindings: System.TimeSpan / System.DateTime 面 (C5.5 探査テスト継続) ----
        // culture 依存 IL 面の不変カルチャ委譲 (TimeSpan.Parse/ToString, DateTime.Parse/ToString/
        // AddDays/AddYears/DateToTicks/DayOfWeek.ToString): 本家 IL は CultureInfo /
        // DateTimeFormatInfo / CompareInfo / Calendar (DaysToMonth365/366 FieldRVA static array +
        // RuntimeHelpers.CreateSpan + RuntimeFieldHandle.m_ptr) の culture 機構全面を辿るため
        // VM 表現境界。VM 規約 (文化は不変カルチャ固定) と CLR の統合文化構成 (文化面の
        // InvariantCulture 委譲) でクラッチ均衡する
        var timeCultureJ = CultureOutOfScope +
            "。本家 IL は CultureInfo / DateTimeFormatInfo / Calendar (DaysToMonth365/366 の FieldRVA static array + RuntimeHelpers.CreateSpan 面 +" +
            "RuntimeFieldHandle.m_ptr ByRef 読み) の culture 機構全面を辿るため、不変カルチャ固定のホスト BCL 委譲を継続する";
        Add("System.TimeSpan", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 0);
        Add("System.TimeSpan", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 1);
        Add("System.TimeSpan", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 2);
        Add("System.TimeSpan", "Parse", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: false, paramCount: 1);
        Add("System.TimeSpan", "Parse", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: false, paramCount: 2);
        Add("System.DateTime", "Parse", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: false, paramCount: 1);
        Add("System.DateTime", "Parse", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: false, paramCount: 2);
        Add("System.DateTime", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 0);
        Add("System.DateTime", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 1);
        Add("System.DateTime", "ToString", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 2);
        Add("System.DateTime", "AddDays", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 1);
        Add("System.DateTime", "AddYears", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: true, paramCount: 1);
        Add("System.DateTime", "DateToTicks", CoreLibSurfaceKind.RuntimeInternal, timeCultureJ, hasThis: false, paramCount: 3);

        // ---- CoreLibBindings: System.Type / Enum / Unsafe / RuntimeHelpers runtime-representation 面 ----
        // Type 面 (culture 機構 IL が辿る RuntimeType 判定群): 本家 IL は RuntimeType 内部表現 (IL なし = ランタイム intrinsic) を辿るため VM 型モデル
        var typeRuntimeJ = RuntimeRepresentation +
            " (本家 IL は RuntimeType 内部表現へ直接アクセス (culture 機構 / Dictionary cache IL から依存))。VM 型モデル (VmType) から同一要素を提示する";
        Add("System.Type", "get_BaseType", CoreLibSurfaceKind.RuntimeInternal, typeRuntimeJ, hasThis: true, paramCount: 0);
        Add("System.Type", "get_TypeHandle", CoreLibSurfaceKind.RuntimeInternal, typeRuntimeJ, hasThis: true, paramCount: 0);
        Add("System.Type", "IsValueTypeImpl", CoreLibSurfaceKind.RuntimeInternal, typeRuntimeJ, hasThis: true, paramCount: 0);
        Add("System.Type", "get_IsValueType", CoreLibSurfaceKind.RuntimeInternal, typeRuntimeJ, hasThis: true, paramCount: 0);
        Add("System.Type", "IsSubclassOf", CoreLibSurfaceKind.RuntimeInternal, typeRuntimeJ, hasThis: true, paramCount: 1);
        // Enum ToString (culture-dependent type-face IL): enum Face文化 CultureData文化 culture IL CultureCulture文化 representation
        Add("System.Enum", "ToString", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (本家 IL は FormatFeatures / CultureInfo 面を辿る culture 機構 depende の G 書式面。VM enum box の value__ 基底型生値をホストの同名 enum (CoreLib typedef 同名照合) で文字列化)", hasThis: true, paramCount: 0);
        // Unsafe.CopyBlockUnaligned 群: jit intrinsic (byte copy 裏の memmove 面)
        Add("System.Runtime.CompilerServices.Unsafe", "CopyBlockUnaligned", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (実 IL はダミー throw = JIT intrinsic。String / Span IL が バイト実体コピー (byteCount) に Talent → Buffer.Memmove 相当の memmove で同等提供)", hasThis: false);
        // RuntimeHelpers の判別面 (SpanHelpers / DateToTicks IL が辿る generic 判定面)
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "IsBitwiseEquatable", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (実 IL はダミー throw = JIT intrinsic。T を メソッド型実引数で判別: 数値 / char / bool 系 true)", hasThis: false);
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "IsKnownConstant", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (実 IL はダミー throw = JIT intrinsic。VM は JIT 定数畳み込みを持たないため false を返し恒常経路へ誘導)", hasThis: false);
        // IUtfChar<T> の jit intrinsic 面 (string/span char genericIL が辿る T(char) 再解釈面)
        Add("System.IUtfChar`1", "CastFrom", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (実 IL はダミー throw = JIT intrinsic。T(char) 系の i4 スロット再解釈で透過)", hasThis: false);

        // ---- CoreLibBindings: System.SR (CoreLib 内部リソース文字列) ----
        // 実 IL が例外生成時に SR.Overflow_Int32 等を辿る先。culture 機構 (ResourceManager) 依存
        var srJ = CultureOutOfScope + "。CoreLib の例外既定文言は ResourceManager (culture 機構) 依存のためホスト CoreLib から同一キー資源を取得";
        Add("System.SR", "GetResourceString", CoreLibSurfaceKind.RuntimeInternal, srJ, hasThis: false, paramCount: 1);
        Add("System.SR", "InternalGetResourceString", CoreLibSurfaceKind.RuntimeInternal, srJ, hasThis: false, paramCount: 1);
        Add("System.SR", "UsingResourceKeys", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + "。AppContext スイッチ面 (culture 機構) のため false 固定で提供", hasThis: false, paramCount: 0);

        var monitorJ = InternalCall + "。VM は単一スレッド実行のため競合なしの暫定ファサード (C6 ゲストスレッドで真の競合を実装予定)";
        Add("System.Threading.Monitor", "TryEnter_FastPath", CoreLibSurfaceKind.RuntimeInternal, monitorJ, hasThis: false);
        Add("System.Threading.Monitor", "TryEnter_FastPath_WithTimeout", CoreLibSurfaceKind.RuntimeInternal, monitorJ, hasThis: false);
        Add("System.Threading.Monitor", "Exit_FastPath", CoreLibSurfaceKind.RuntimeInternal, monitorJ, hasThis: false);
        Add("System.Threading.Monitor", "IsEnteredNative", CoreLibSurfaceKind.RuntimeInternal, monitorJ, hasThis: false);

        // ---- CoreLibBindings: String culture 面 (C5.5 Wave 5 確定) ----
        // culture 相当必須の面 (Compare / IndexOf(string) / LastIndexOf(string) /
        // StartsWith / EndsWith / 大文字小文字 / CompareTo) は本家 IL が CultureInfo /
        // CompareInfo / TextInfo (culture 機構) で構成され IL 移植対象外のため、
        // VM 規約の不変カルチャ固定をホスト BCL 委譲 (① バインド) で提供する。
        // ordinal 近似の IL 移植候補との ASCII+非 ASCII ファズ突合で差分が証明済み
        // (インバリアントの ß/ss 等の比較等価は ordinal では再現できない)。
        // 対して ordinal 面 (CompareOrdinal / IndexOf(char) / LastIndexOf(char) /
        // Contains / Replace) と Format / Split は (b) 置換面 (VmCoreLibSurfaces.Faces →
        // DotnetVM.CoreLib.StringOrdinalOps / StringFormatting) に移行済み。
        // 各エントリは「ロード時 (b) 置換面 / 未ロード時 ③ legacy キー」の代替経路を併記する
        Add("System.String", "Concat", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (連結本体は実 IL の wstrcpy 生ポインタコピー / 正確な容量計算面。要素の ToString 面は Wave 4 で実 IL 化済み)", hasThis: false, paramCount: 1);
        Add("System.String", "Compare", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: false, paramCount: 2);
        // StringComparison overload 群: 本家 IL は ordinal 面と CompareInfo / SpanHelpers SIMD
        // 抽象化面 (EqualsIgnoreCase_Vector は ISimdVector static abstract = VM 表現境界) に
        // 分かれるため culture 面と同じホスト BCL 委譲。CurrentCulture 系 SCI は不変へ写像
        Add("System.String", "Compare", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ +
            "。StringComparison 分岐後 ordinal 面は String.CompareOrdinal 直呼びに置換されるが SIMD ignore-case 面が culture / GSJA 抽象化に依存するため委譲に統一", hasThis: false, paramCount: 3);
        Add("System.String", "Equals", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ +
            "。OrdinalIgnoreCase は SpanHelpers SIMD 抽象化 (ISimdVector static abstract) を辿るため委譲に統一", hasThis: false, paramCount: 3);
        Add("System.String", "Equals", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ +
            "。OrdinalIgnoreCase は SpanHelpers SIMD 抽象化 (ISimdVector static abstract) を辿るため委譲に統一", hasThis: true, paramCount: 2);
        Add("System.String", "CompareOrdinal", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は fixed byte* 比較。ordinal。ロード時は (b) 置換面 StringOrdinalOps、未ロード時この legacy キー)", hasThis: false, paramCount: 2);
        Add("System.String", "IndexOf", CoreLibSurfaceKind.RuntimeInternal,
            cultureFaceJ + "。string 面は不変カルチャ委譲の ① バインド、char 面は (b) 置換面 (ロード時)。未ロード時この legacy キーが受ける", hasThis: true, paramCount: 1);
        Add("System.String", "IndexOf", CoreLibSurfaceKind.RuntimeInternal,
            cultureFaceJ + "。IndexOf(string, StringComparison) overload (SCI 面を不変カルチャ写像)。本家 IL は SpanHelpers SIMD ignore-case 抽象化を辿るため委譲", hasThis: true, paramCount: 2);
        Add("System.String", "LastIndexOf", CoreLibSurfaceKind.RuntimeInternal,
            cultureFaceJ + "。string 面は不変カルチャ委譲の ① バインド、char 面は (b) 置換面 (ロード時)。未ロード時この legacy キーが受ける", hasThis: true, paramCount: 1);
        Add("System.String", "LastIndexOf", CoreLibSurfaceKind.RuntimeInternal,
            cultureFaceJ + "。LastIndexOf(string, StringComparison) overload (SCI 面を不変カルチャ写Map)。SIMD 抽象化のため委譲", hasThis: true, paramCount: 2);
        Add("System.String", "Contains", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は SpanHelpers SIMD intrinsic 面。ordinal。ロード時は (b) 置換面 StringOrdinalOps、未ロード時この legacy キー)", hasThis: true, paramCount: 1);
        Add("System.String", "StartsWith", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: true, paramCount: 1);
        Add("System.String", "EndsWith", CoreLibSurfaceKind.RuntimeInternal, cultureFaceJ, hasThis: true, paramCount: 1);
        Add("System.String", "Replace", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は Span ベース表現。ordinal。ロード時は (b) 置換面 StringOrdinalOps、未ロード時この legacy キー)", hasThis: true, paramCount: 2);
        Add("System.String", "Format", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は StringBuilder チャンク + Span 解析。ロード時は (b) 置換面 StringFormatting、未ロード時この legacy キー)", hasThis: false);
        Add("System.String", "Split", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (SpanHelpers 依存の表現境界面。ロード時は (b) 置換面 StringOrdinalOps、未ロード時この legacy キー)", hasThis: true);

        // ---- CoreLibBindings: System.Char culture 面 (C5.5 Wave 5 継続) ----
        var charCultureJ = CultureOutOfScope +
            "。本家 IL は CultureInfo.CurrentCulture.TextInfo (culture 機構) を辿るため不変カルチャ規約どおりホストの不変面へ委譲";
        Add("System.Char", "ToUpper", CoreLibSurfaceKind.RuntimeInternal, charCultureJ, hasThis: false, paramCount: 1);
        Add("System.Char", "ToLower", CoreLibSurfaceKind.RuntimeInternal, charCultureJ, hasThis: false, paramCount: 1);
        Add("System.Char", "GetNumericValue", CoreLibSurfaceKind.RuntimeInternal,
            InternalCall + "。Unicode 数字値 (ネイティブ Unicode テーブル)。ホスト同一面で提供", hasThis: false, paramCount: 1);

        // ---- CoreLibBindings: String 内部 / Buffer / Unsafe / MemoryMarshal ----
        Add("System.String", "FastAllocateString", CoreLibSurfaceKind.RuntimeInternal,
            InternalCall + "。実 CLR の確保点と同じ位置で VmHeap 会計つき確保する", hasThis: false);
        Add("System.String", "CreateFromChar", CoreLibSurfaceKind.RuntimeInternal,
            InternalCall + "。1 文字の VmString 生成 (char.ToString() の実 IL が辿る面)", hasThis: false);
        Add("System.Buffer", "Memmove", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (バイト実体 / スロット列間の memmove として同等提供)", hasThis: false);
        var unsafeJ = JitIntrinsic + " (実 IL はダミー throw か JIT 置換。バイト実体 / スロット列参照で同等提供)";
        Add("System.Runtime.CompilerServices.Unsafe", "Add", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "AddByteOffset", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "As", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "AreSame", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "AsRef", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "SizeOf", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.CompilerServices.Unsafe", "BitCast", CoreLibSurfaceKind.RuntimeInternal, unsafeJ, hasThis: false);
        Add("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (配列データ先頭スロットへの VmByRef で同等提供)", hasThis: false);

        // ---- CoreLibBindings: RuntimeHelpers ----
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "GetMethodTable", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (実 IL はダミー自己再帰、実 CLR も JIT が置換)", hasThis: false, paramCount: 1);
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "IsReferenceOrContainsReferences", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (VM 型モデルで参照含有を再帰判定)", hasThis: false, paramCount: 1);
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "IsReferenceOrContainsReferences", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (ジェネリック ラッパー面。メソッド型実引数から判定)", hasThis: false);

        // ---- CoreLibBindings: Vector64/128/256/512 ----
        // VM は SIMD 値型の inline 表現を持たないため true にすると SpanHelpers 等が
        // SIMD 面で fail-closed に落ちる。false を返して scalar フォールバック IL へ誘導
        // (string SCI overload 群は同一結果を得る)。直接観測時の実 x64 CLR (true) との
        // 既知差異 = SIMD 表現境界
        var vectorJ = RuntimeRepresentation +
            " (実 IL はダミー自己再帰、実 CLR も JIT がハードウェア判定へ置換。VM は SIMD 値型表現を持たないため false を返し scalar フォールバックへ誘導)";
        foreach (var t in new[] { "System.Runtime.Intrinsics.Vector64", "System.Runtime.Intrinsics.Vector128", "System.Runtime.Intrinsics.Vector256", "System.Runtime.Intrinsics.Vector512" }) {
            Add(t, "get_IsHardwareAccelerated", CoreLibSurfaceKind.RuntimeInternal, vectorJ, hasThis: false, paramCount: 0);
        }

        // ---- CoreLibBindings: 環境 / Marshal lastError / GlobalizationMode (C5.5 探査テスト継続) ----
        var marshalLastErrorJ = InternalCall +
            "。実 CLR も last-error TLS スロットの取得/設定 (ECall 面)。VM はネイティブ呼びを持たないため同一スロットの set/get 対として成立";
        Add("System.Runtime.InteropServices.Marshal", "SetLastSystemError", CoreLibSurfaceKind.RuntimeInternal, marshalLastErrorJ, hasThis: false, paramCount: 1);
        Add("System.Runtime.InteropServices.Marshal", "GetLastSystemError", CoreLibSurfaceKind.RuntimeInternal, marshalLastErrorJ, hasThis: false, paramCount: 0);
        // SystemError/PInvokeError は実 CLR でも同一スロットの alias 面
        Add("System.Runtime.InteropServices.Marshal", "SetLastPInvokeError", CoreLibSurfaceKind.RuntimeInternal, marshalLastErrorJ, hasThis: false, paramCount: 1);
        Add("System.Runtime.InteropServices.Marshal", "GetLastPInvokeError", CoreLibSurfaceKind.RuntimeInternal, marshalLastErrorJ, hasThis: false, paramCount: 0);
        Add("Interop+Kernel32", "GetEnvironmentVariable", CoreLibSurfaceKind.RuntimeInternal,
            "pinvoke-replacement: Kernel32 P/Invoke の代替実装をホスト環境変数取得へ委譲 (面の再現 + プロキシ委譲規約。ネイティブ実行はしない)", hasThis: false, paramCount: 3);
        Add("System.Globalization.GlobalizationMode+Settings", "get_Invariant", CoreLibSurfaceKind.RuntimeInternal,
            InternalCall + "。本家もネイティブ状態参照。VM 規約 (culture 不変固定) により true 固定 = DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 起動と同一意味論", hasThis: false, paramCount: 0);

        // ---- DefaultIntrinsics: DefaultInterpolatedStringHandler ----
        var dishJ = RuntimeRepresentation +
            "。状態は VmIntrinsicCarrier + ホスト StringBuilder。実 CLR も Span 内部 + [Intrinsic] 最適化面";
        var dish = "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";
        Add(dish, ".ctor", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 2);
        Add(dish, ".ctor", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 3);
        Add(dish, "AppendLiteral", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 1);
        Add(dish, "AppendFormatted", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 1);
        Add(dish, "AppendFormatted", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 2);
        Add(dish, "AppendFormatted", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 3);
        Add(dish, "ToStringAndClear", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 0);
        Add(dish, "ToString", CoreLibSurfaceKind.RuntimeInternal, dishJ, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Type / MemberInfo / MethodBase ----
        var typeJ = RuntimeRepresentation +
            "。実体が VmRuntimeObject / VmRuntimeMethod (VM 型系ファサード) で CoreLib IL の対象データが VM に存在しない";
        Add("System.Type", "GetTypeFromHandle", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: false, paramCount: 1);
        Add("System.Type", "op_Equality", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: false, paramCount: 2);
        Add("System.Type", "op_Inequality", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: false, paramCount: 2);
        foreach (var t in new[] { "System.Type", "System.Reflection.MemberInfo" }) {
            Add(t, "get_Name", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
            Add(t, "get_FullName", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
            Add(t, "get_UnderlyingSystemType", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
            Add(t, "ToString", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
            Add(t, "Equals", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 1);
        }
        Add("System.Reflection.MethodBase", "GetMethodFromHandle", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: false, paramCount: 1);
        Add("System.Reflection.MethodBase", "GetCurrentMethod", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: false, paramCount: 0);
        Add("System.Reflection.MethodBase", "get_Name", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
        Add("System.Reflection.MethodBase", "get_DeclaringType", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);
        Add("System.Reflection.MethodBase", "ToString", CoreLibSurfaceKind.RuntimeInternal, typeJ, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: 仮想 I/O 面 (デバイス / ゲートウェイ) ----
        AddRange(new[] { "System.IO.File" }, "Exists", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 1);
        Add("System.IO.File", "ReadAllBytes", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 1);
        Add("System.IO.File", "WriteAllBytes", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 2);
        Add("System.IO.File", "ReadAllText", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 1);
        Add("System.IO.File", "WriteAllText", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 2);
        Add("System.IO.File", "Delete", CoreLibSurfaceKind.Device, DeviceFace, hasThis: false, paramCount: 1);
        Add("System.Net.WebClient", ".ctor", CoreLibSurfaceKind.Device, DeviceFace, hasThis: true, paramCount: 0);
        Add("System.Net.WebClient", "DownloadData", CoreLibSurfaceKind.Device, DeviceFace, hasThis: true, paramCount: 1);
        Add("System.Net.WebClient", "DownloadString", CoreLibSurfaceKind.Device, DeviceFace, hasThis: true, paramCount: 1);
        Add("System.Net.WebClient", "UploadData", CoreLibSurfaceKind.Device, DeviceFace, hasThis: true, paramCount: 2);
        Add("System.IDisposable", "Dispose", CoreLibSurfaceKind.Device,
            DeviceFace + " (ゲスト実装が無い場合の no-op 既定。仮想ディスパッチが優先される)", hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Delegate ----
        var delegateJ = RuntimeRepresentation +
            " (MulticastDelegate の _invocationList 内部表現依存。VM の呼出リスト モデルで同等提供)";
        Add("System.Delegate", "Combine", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 2);
        Add("System.Delegate", "Combine", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 3);
        Add("System.Delegate", "Remove", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 2);
        Add("System.Delegate", "RemoveAll", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 2);
        Add("System.Delegate", "op_Equality", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 2);
        Add("System.Delegate", "op_Inequality", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: false, paramCount: 2);
        Add("System.Delegate", "Equals", CoreLibSurfaceKind.RuntimeInternal, delegateJ, hasThis: true, paramCount: 1);

        // ---- DefaultIntrinsics: Interlocked (計算 + バリア) ----
        var interlockedJ = JitIntrinsic + " (単一スレッド逐次実行で計算面は CLR 同一、バリアは no-op)";
        // ① バインド (CoreLibBindings.RegisterInterlockedBindings) は .NET 10 既知署名の列挙面。
        // 本家 IL 本体が JIT intrinsic ダミー (typeof(T); throw) のため ② IL 実行に落ちないよう
        // ① で受ける必要がある (探査テストで発掘: CultureInfo::get_InvariantCulture 経路)
        Add("System.Threading.Interlocked", "CompareExchange", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "Exchange", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "Add", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "Increment", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "Decrement", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "And", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "Or", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "MemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "ReadMemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        Add("System.Threading.Interlocked", "WriteMemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: null);
        // 以下は legacy intrinsic (③) 残置の特定エントリ (IL 本体なし経路)
        Add("System.Threading.Interlocked", "CompareExchange", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 3);
        Add("System.Threading.Interlocked", "CompareExchange", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 4);
        Add("System.Threading.Interlocked", "Exchange", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 2);
        Add("System.Threading.Interlocked", "Add", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 2);
        Add("System.Threading.Interlocked", "Increment", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 1);
        Add("System.Threading.Interlocked", "Decrement", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 1);
        Add("System.Threading.Interlocked", "And", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 2);
        Add("System.Threading.Interlocked", "Or", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 2);
        Add("System.Threading.Interlocked", "MemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 0);
        Add("System.Threading.Interlocked", "ReadMemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 0);
        Add("System.Threading.Interlocked", "WriteMemoryBarrier", CoreLibSurfaceKind.RuntimeInternal, interlockedJ, hasThis: false, paramCount: 0);

        // ---- DefaultIntrinsics: RuntimeHelpers.InitializeArray ----
        Add("System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray", CoreLibSurfaceKind.RuntimeInternal,
            JitIntrinsic + " (FieldRVA 初期データをリトルエンディアンで展開)", hasThis: false, paramCount: 2);

        // ---- DefaultIntrinsics: プリミティブ ToString() 無パラメータ面 (残置 shadow) ----
        // 整数 4 型: ② IlPreferred + Faces 置換 (②' choke point) が常に先。CoreLib 未ロード時の
        // 代替経路として永続残置 (未ロード時のゲスト整数 ToString() をこの legacy キーが受ける)
        var int4Shadow = "shadowed-legacy (ロード時は ② IlPreferred + 置換面 choke point が常に先。CoreLib 未ロード時の代替経路として永続残置)";
        foreach (var t in integer4)
            Add(t, "ToString", CoreLibSurfaceKind.RealCoreLibIl, int4Shadow, hasThis: true, paramCount: 0);
        // Wave 2 移行済み 6 型 (Byte/SByte/Int16/UInt16/Char/Boolean): ① バインド廃止に伴い
        // ②' Faces 置換面 (b) が常に先 → この legacy キーは到達しない
        var wave2Done = new[] { "System.Int16", "System.UInt16", "System.SByte", "System.Byte",
            "System.Char", "System.Boolean" };
        var wave2Shadow = "shadowed-legacy (②' VmCoreLibSurfaces Faces 置換面 (b) が常に先に解決するため到達しない。C5.5 Wave 2 で ① バインドを廃止済み)";
        foreach (var t in wave2Done)
            Add(t, "ToString", CoreLibSurfaceKind.RealCoreLibIl, wave2Shadow, hasThis: true, paramCount: 0);
        // Single/Double: ②' Faces 置換面 (b) が常に先 → この legacy キーは到達しない
        var floatShadow = "shadowed-legacy (②' VmCoreLibSurfaces Faces 置換面 (b) が常に先に解決するため到達しない。C5.5 Wave 3 で ① バインドを廃止済み)";
        Add("System.Single", "ToString", CoreLibSurfaceKind.RealCoreLibIl, floatShadow, hasThis: true, paramCount: 0);
        Add("System.Double", "ToString", CoreLibSurfaceKind.RealCoreLibIl, floatShadow, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Char ----
        Add("System.Char", "IsWhiteSpace", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (Unicode プロパティ表依存の面をホスト char.IsWhiteSpace と同一意味論で委譲)", hasThis: false, paramCount: 1);

        // ---- DefaultIntrinsics: Object ----
        // C5.5 Wave 4 確定: Object の既定面 (ToString() / Equals ×2 / .ctor) は本家 managed IL
        // 本体のみで構成 (ToString は GetType() 呼び / Equals は参照比較 + 仮想呼び / .ctor は
        // 空本体) → Object は IlPreferred に上げ、ロード時は ② IL 本体実行。
        // これらの legacy intrinsic は CoreLib 未ロード時の代替経路として残置
        // (VM ランタイム オブジェクト向けの特殊化 — VmExceptionObject の「型名: メッセージ」等 — を含む)
        var objectShadow = "shadowed-legacy (CoreLib 未ロード時の代替経路。ロード時は ② IL 本体 (C5.5 Wave 4 で逆アセンブル確認済み) が常に先に実行される)";
        Add("System.Object", ".ctor", CoreLibSurfaceKind.RealCoreLibIl, objectShadow, hasThis: true, paramCount: 0);
        Add("System.Object", "ToString", CoreLibSurfaceKind.RealCoreLibIl, objectShadow, hasThis: true, paramCount: 0);
        Add("System.Object", "Equals", CoreLibSurfaceKind.RealCoreLibIl, objectShadow, hasThis: true, paramCount: 1);
        Add("System.Object", "Equals", CoreLibSurfaceKind.RealCoreLibIl, objectShadow, hasThis: false, paramCount: 2);
        // GetHashCode: 本体 IL は TryGetHashCode (InternalCall) → GetHashCodeSlow (QCall ネイティブ)
        // を辿り identity hash の発行自体がランタイム内部 (実 CLR も IL を実行しない面)。
        // P/Invoke 代替の原則で ① バインド (CoreLibBindings.RegisterObject) が IdentityHash を優先提供。
        // 未ロード時はこの legacy intrinsic が受ける
        Add("System.Object", "GetHashCode", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (identity hash は QCall ネイティブ面 = ObjectNative_GetHashCodeSlow。① バインドが IdentityHash を優先提供)", hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Exception ----
        var exceptionJ = RuntimeRepresentation +
            " (例外実体は VM 例外表で管理。既定文言はホスト CLR 例外型への委譲 = culture スコープ外)";
        Add("System.Exception", ".ctor", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);
        Add("System.Exception", ".ctor", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 1);
        Add("System.Exception", "get_Message", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);
        Add("System.Exception", "ToString", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: String (IlPreferred = ② IL 実行が基本。影の残置キー) ----
        // IL 本体を持つ面の legacy intrinsic はロード時 ② が常に先。未ロード時の代替経路として永続残置
        var stringIlShadow = "shadowed-legacy (String は IlPreferred のため managed IL 本体を持つ面は ② IL 実行が常に先。CoreLib 未ロード時の代替経路として永続残置)";
        var stringSubstituteShadow = "shadowed-legacy (ロード時は ② IL 解決後の choke point で (b) 置換面に差し替え。未ロード時は ③ legacy intrinsic が受ける)";
        // culture 面 (C5.5 Wave 5 確定): 本家 IL は CultureInfo.CurrentCulture.TextInfo /
        // CompareInfo (culture 機構) を辿るため ② IL 実行では fail-closed になる。
        // 不変カルチャ規約の同一結果を ① バインドで優先提供し、未ロード時はこの legacy キーが受ける
        var stringCultureJ = CultureOutOfScope +
            " (本家 IL は culture 機構依存のため不変カルチャ固定のホスト委譲を ① バインドで優先提供。未ロード時この legacy キー)";
        Add("System.String", ".ctor", CoreLibSurfaceKind.RuntimeInternal,
            "shadowed-legacy (newobj string 構築は ObjectEngine の NewStringFromCtor が処理するため到達しない)", hasThis: true, paramCount: 0);
        foreach (var (method, hasThis, pc, kind, j) in new[] {
            ((string)"get_Length", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("get_Chars", true, 0 + 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Substring", true, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Substring", true, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Equals", true, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("CompareTo", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("Contains", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("StartsWith", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("EndsWith", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("IndexOf", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("LastIndexOf", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("Replace", true, 2, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("ToUpper", true, 0, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("ToLower", true, 0, CoreLibSurfaceKind.RuntimeInternal, stringCultureJ),
            ("Trim", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("ToString", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 3, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 4, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Format", false, 2, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Format", false, 3, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Format", false, 4, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Format", false, 5, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Split", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Split", true, 2, CoreLibSurfaceKind.RuntimeInternal, stringSubstituteShadow),
            ("Join", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Equals", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("op_Equality", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("op_Inequality", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("IsNullOrEmpty", false, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("IsNullOrWhiteSpace", false, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Intern", false, 1, CoreLibSurfaceKind.RuntimeInternal,
                "shadowed-legacy (本体があれば ② IL が先、InternalCall の場合はこのキーが受ける — いずれも VM 文字列プールで同等)"),
        })
            Add("System.String", method, kind, j, hasThis, pc);
        // ToUpperInvariant / ToLowerInvariant (C5.5 Wave 5 新設の ① バインド)。本家 IL は
        // CultureInfo.InvariantCulture.TextInfo (culture 機構) を辿るため culture スコープ外。
        // 不変カルチャ規約ではホストの不変大文字小文字化と同一結果
        Add("System.String", "ToUpperInvariant", CoreLibSurfaceKind.RuntimeInternal, stringCultureJ, hasThis: true, paramCount: 0);
        Add("System.String", "ToLowerInvariant", CoreLibSurfaceKind.RuntimeInternal, stringCultureJ, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Math (IlPreferred。IL 本体が証明済みの面は影、演画面は委譲) ----
        // IL 本体を持つ面の legacy intrinsic はロード時 ② が常に先。未ロード時の代替経路として永続残置
        var mathIlShadow = "shadowed-legacy (Math は IlPreferred のため managed IL 本体を持つ面は ② IL 実行が常に先。CLR 突合 + tracer で IL 実行証明済み。CoreLib 未ロード時の代替経路として永続残置)";
        Add("System.Math", "Abs", CoreLibSurfaceKind.RealCoreLibIl, mathIlShadow, hasThis: false, paramCount: 1);
        Add("System.Math", "Max", CoreLibSurfaceKind.RealCoreLibIl, mathIlShadow, hasThis: false, paramCount: 2);
        Add("System.Math", "Min", CoreLibSurfaceKind.RealCoreLibIl, mathIlShadow, hasThis: false, paramCount: 2);
        Add("System.Math", "Sign", CoreLibSurfaceKind.RealCoreLibIl, mathIlShadow, hasThis: false, paramCount: 1);
        var mathFacilityJ = InternalCall +
            " (実 CLR も JIT / ランタイムが提供する浮動小数点演画面。IL 本体があれば ② が先、無ければこのキーが受ける)";
        foreach (var m in new[] { "Sqrt", "Pow", "Floor", "Ceiling", "Round", "Truncate",
            "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Exp", "Log", "Log10" })
            Add("System.Math", m, CoreLibSurfaceKind.RuntimeInternal, mathFacilityJ, hasThis: false);

        // ---- DefaultIntrinsics: Convert (C5.5 Wave 1 以降の実態) ----
        // CoreLib ロード時: object 経由面は面単位 IL 優先 (IlPreferredFaces) で実 IL →
        // IConvertible EII、対応面 (string / 整数) は Faces 置換面 (b) が ②/②' で先に解決。
        // この legacy キーは CoreLib 未ロード時の代替経路と、置換面の無い overload
        // (ToByte(int) 等の primitive 直呼出) を受ける
        var convertFaceJ = RuntimeInternalKindNote() +
            "。CoreLib ロード時は object 面が IlPreferredFaces (実 IL) / string・整数面が Faces 置換面 (b) で先に解決。このキーは CoreLib 未ロード時と残 overload を受ける";
        Add("System.Convert", "ToInt32", CoreLibSurfaceKind.RuntimeInternal, convertFaceJ, hasThis: false, paramCount: 1);
        Add("System.Convert", "ToInt64", CoreLibSurfaceKind.RuntimeInternal, convertFaceJ, hasThis: false, paramCount: 1);
        Add("System.Convert", "ToString", CoreLibSurfaceKind.RuntimeInternal, convertFaceJ, hasThis: false, paramCount: 1);
        Add("System.Convert", "ToBoolean", CoreLibSurfaceKind.RuntimeInternal, convertFaceJ, hasThis: false, paramCount: 1);
        Add("System.Convert", "ToChar", CoreLibSurfaceKind.RuntimeInternal, convertFaceJ, hasThis: false, paramCount: 1);
        var convertJ = RuntimeInternalKindNote() +
            "。実 CLR は IConvertible 経由の managed IL。文字列→浮動小数点 (ToDouble/ToSingle) の解析面は C5.5 Wave 3 で Faces 置換面 (b) (DotnetVM.CoreLib.DoubleParsing) に移行済み。このキーは CoreLib 未ロード時の代替経路と残 overload を受ける";
        Add("System.Convert", "ToDouble", CoreLibSurfaceKind.RuntimeInternal, convertJ, hasThis: false, paramCount: 1);
        Add("System.Convert", "ToSingle", CoreLibSurfaceKind.RuntimeInternal, convertJ, hasThis: false, paramCount: 1);
        foreach (var m in new[] { "ToByte", "ToSByte", "ToInt16", "ToUInt16", "ToUInt32", "ToUInt64" })
            Add("System.Convert", m, CoreLibSurfaceKind.RuntimeInternal, convertJ, hasThis: false, paramCount: 1);

        // ---- DefaultIntrinsics: Console (デバイス面 + Device バインド併存) ----
        // Device バインド (① で常に先に当たる全引数一致面)
        var deviceBindingJ = DeviceFace + " (メソッド名ごとの全引数一致面。オーバーロード判別は実行時の宣言パラメータ型名)";
        Add("System.Console", "Write", CoreLibSurfaceKind.Device, deviceBindingJ, hasThis: false);
        Add("System.Console", "WriteLine", CoreLibSurfaceKind.Device, deviceBindingJ, hasThis: false);
        Add("System.Console", "ReadLine", CoreLibSurfaceKind.Device, deviceBindingJ, hasThis: false);
        // legacy intrinsic 側 (① Device バインドが常に先に当たるため到達しない残置 = shadowed-legacy)
        var consoleShadow = "shadowed-legacy。" + DeviceFace + " (① Device バインドが常に先に解決するため到達しない)";
        foreach (var arity in new[] { 1, 2, 3, 4, 5 }) {
            Add("System.Console", "Write", CoreLibSurfaceKind.Device, consoleShadow, hasThis: false, paramCount: arity);
            Add("System.Console", "WriteLine", CoreLibSurfaceKind.Device, consoleShadow, hasThis: false, paramCount: arity);
        }
        Add("System.Console", "WriteLine", CoreLibSurfaceKind.Device, consoleShadow, hasThis: false, paramCount: 0);
        Add("System.Console", "ReadLine", CoreLibSurfaceKind.Device, consoleShadow, hasThis: false, paramCount: 0);

        return [.. entries];
    }

    private static string RuntimeInternalKindNote() => RuntimeRepresentation.Split('。')[0];
}
