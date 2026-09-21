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
        var integer8 = new[] { "System.Int16", "System.UInt16", "System.SByte", "System.Byte",
            "System.Char", "System.Boolean", "System.Single", "System.Double" };

        // ---- CoreLibBindings: 書式付きプリミティブ ToString (culture 書式面) ----
        // Wave 2 (整数) / Wave 3 (Single/Double) で DotnetVM.CoreLib 置換面 (b) へ移行予定。
        // pc1 のうち (IFormatProvider) 面 (整数 4 型) は Convert.ToString(object) の実 IL が
        // IConvertible ディスパッチで辿るため Faces 置換面 (b) が先に解決される (provider は無視)
        var formatJ = CultureOutOfScope + "。書式エンジン未整備のため format を無視する暫定委譲 (整数は Wave 2、Single/Double は Wave 3 で置換面へ移行予定)。整数 4 型の ToString(IFormatProvider) は Faces 置換面 (b) が先に解決";
        foreach (var t in integer4) {
            Add(t, "ToString", CoreLibSurfaceKind.RuntimeInternal, formatJ, hasThis: true, paramCount: 1);
            Add(t, "ToString", CoreLibSurfaceKind.RuntimeInternal, formatJ, hasThis: true, paramCount: 2);
        }
        AddRange(integer8, "ToString", CoreLibSurfaceKind.RuntimeInternal, formatJ, hasThis: true);

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

        // ---- CoreLibBindings: String culture / 表現面 (Wave 5 で ordinal 分を (b) へ移行予定) ----
        Add("System.String", "Concat", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + "。要素の CLR 規約書式化が書式エンジン依存 (Wave 5 で再評価)", hasThis: false, paramCount: 1);
        Add("System.String", "Compare", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + " (CurrentCulture 比較。Wave 5 で不変比較 IL 実装へ移行予定)", hasThis: false, paramCount: 2);
        Add("System.String", "CompareOrdinal", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は fixed byte* 比較。Wave 5 で (b) 移行予定)", hasThis: false, paramCount: 2);
        Add("System.String", "IndexOf", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は SpanHelpers SIMD intrinsic 面。char は ordinal で Wave 5 (b) 移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "IndexOf", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + " (CurrentCulture 検索。Wave 5 で不変比較 IL 実装へ移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "LastIndexOf", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は SpanHelpers SIMD intrinsic 面。char は Wave 5 (b) 移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "LastIndexOf", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + " (CurrentCulture 検索。Wave 5 で移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "Contains", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は SpanHelpers SIMD intrinsic 面。ordinal。Wave 5 で (b) 移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "StartsWith", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + " (CurrentCulture 比較。Wave 5 で移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "EndsWith", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + " (CurrentCulture 比較。Wave 5 で移行予定)", hasThis: true, paramCount: 1);
        Add("System.String", "Replace", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (実 IL は Span ベース表現。ordinal。Wave 5 で (b) 移行予定)", hasThis: true, paramCount: 2);
        Add("System.String", "Format", CoreLibSurfaceKind.RuntimeInternal,
            CultureOutOfScope + "。複合書式が書式エンジン依存 (Wave 2/3 の書式エンジン整備後に (b) 移植予定)", hasThis: false);
        Add("System.String", "Split", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (SpanHelpers 依存の表現境界面。Wave 5 で再評価)", hasThis: true);

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
        // 整数 4 型: ② IlPreferred + Faces 置換 (②' choke point) が常に先 → この legacy キーは到達しない
        var int4Shadow = "shadowed-legacy (② IlPreferred + 置換面 choke point が常に先に解決するため到達しない。Wave 1+ で廃止)";
        foreach (var t in integer4)
            Add(t, "ToString", CoreLibSurfaceKind.RealCoreLibIl, int4Shadow, hasThis: true, paramCount: 0);
        // 残り 8 プリミティブ: ① InstanceAnyParams バインドが常に先 → 到達しない
        var prim8Shadow = "shadowed-legacy (① ToString 全引数一致バインドが常に先に解決するため到達しない。移行 Wave で廃止)";
        foreach (var t in integer8)
            Add(t, "ToString", CoreLibSurfaceKind.RealCoreLibIl, prim8Shadow, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Char ----
        Add("System.Char", "IsWhiteSpace", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (Unicode プロパティ表依存の面をホスト char.IsWhiteSpace と同一意味論で委譲)", hasThis: false, paramCount: 1);

        // ---- DefaultIntrinsics: Object ----
        var objectJ = RuntimeRepresentation + " (VM のオブジェクト モデルで同等提供。Wave 4 で実 IL 化の要否を再評価)";
        Add("System.Object", ".ctor", CoreLibSurfaceKind.RuntimeInternal, objectJ, hasThis: true, paramCount: 0);
        Add("System.Object", "ToString", CoreLibSurfaceKind.RuntimeInternal, objectJ, hasThis: true, paramCount: 0);
        Add("System.Object", "Equals", CoreLibSurfaceKind.RuntimeInternal, objectJ, hasThis: true, paramCount: 1);
        Add("System.Object", "Equals", CoreLibSurfaceKind.RuntimeInternal, objectJ, hasThis: false, paramCount: 2);
        Add("System.Object", "GetHashCode", CoreLibSurfaceKind.RuntimeInternal,
            RuntimeRepresentation + " (ランタイム ハッシュ プロバイダ依存。IdentityHash で同等提供)", hasThis: true, paramCount: 0);
        Add("System.Object", "GetType", CoreLibSurfaceKind.RuntimeInternal,
            "shadowed-legacy (① GetType バインドが常に先に解決するため到達しない)", hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: Exception ----
        var exceptionJ = RuntimeRepresentation +
            " (例外実体は VM 例外表で管理。既定文言はホスト CLR 例外型への委譲 = culture スコープ外)";
        Add("System.Exception", ".ctor", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);
        Add("System.Exception", ".ctor", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 1);
        Add("System.Exception", "get_Message", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);
        Add("System.Exception", "ToString", CoreLibSurfaceKind.RuntimeInternal, exceptionJ, hasThis: true, paramCount: 0);

        // ---- DefaultIntrinsics: String (IlPreferred = ② IL 実行が基本。影の残置キー) ----
        var stringIlShadow = "shadowed-legacy (String は IlPreferred のため managed IL 本体を持つ面は ② IL 実行が常に先。Wave 4 で廃止)";
        var stringBindingShadow = "shadowed-legacy (① culture / 表現面バインドが常に先に解決するため到達しない。Wave 5 の移行時に廃止)";
        Add("System.String", ".ctor", CoreLibSurfaceKind.RuntimeInternal,
            "shadowed-legacy (newobj string 構築は ObjectEngine の NewStringFromCtor が処理するため到達しない)", hasThis: true, paramCount: 0);
        foreach (var (method, hasThis, pc, kind, j) in new[] {
            ((string)"get_Length", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("get_Chars", true, 0 + 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Substring", true, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Substring", true, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Equals", true, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("CompareTo", true, 1, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Contains", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("StartsWith", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("EndsWith", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("IndexOf", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("LastIndexOf", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Replace", true, 2, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("ToUpper", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("ToLower", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Trim", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("ToString", true, 0, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 2, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 3, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Concat", false, 4, CoreLibSurfaceKind.RealCoreLibIl, stringIlShadow),
            ("Format", false, 2, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Format", false, 3, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Format", false, 4, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Format", false, 5, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Split", true, 1, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
            ("Split", true, 2, CoreLibSurfaceKind.RuntimeInternal, stringBindingShadow),
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

        // ---- DefaultIntrinsics: Math (IlPreferred。IL 本体が証明済みの面は影、演画面は委譲) ----
        var mathIlShadow = "shadowed-legacy (Math は IlPreferred のため managed IL 本体を持つ面は ② IL 実行が常に先。CLR 突合 + tracer で IL 実行証明済み。Wave 4 で廃止)";
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
            "。実 CLR は IConvertible 経由の managed IL。ToDouble/ToSingle は文字列入力の解析が Number.Formatting 依存 (Wave 3)、残 6 面 (整数 8bit/16bit/無符号系) は置換面ありの整合を Wave 2 で再評価予定";
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
