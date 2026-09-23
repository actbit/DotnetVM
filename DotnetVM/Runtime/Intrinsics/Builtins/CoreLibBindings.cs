using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// CoreLib 画像 (System.Private.CoreLib) の面に対するランタイムバインド。
/// fail-driven に発見した InternalCall 面 / IL 実行が表現境界で保留されている面を
/// 署名キー + BindingOrigin 付きで登録する。すべての実装は intrinsic 契約
/// (VM オブジェクトモデル正規化 / ヒープ計上 / デバイス・ゲートウェイ経由 I/O) に従う。
/// ジェネリック メソッドは開いたキー (!!n / !n) で 1 件登録し、実引数は
/// IntrinsicContext.ParameterTypeNames 経由で判別する。
/// </summary>
internal static class CoreLibBindings {
    private const string RuntimeHelpersType = "System.Runtime.CompilerServices.RuntimeHelpers";

    public static void RegisterAll(IntrinsicRegistry r) {
        RegisterString(r);
        RegisterStringInternals(r);
        RegisterRuntimeHelpers(r);
        RegisterVectorIntrinsics(r);
        RegisterEnvironmentAndMarshal(r);
        RegisterInterlockedBindings(r);
        RegisterObject(r);
        RegisterEnum(r);
        RegisterThreading(r);
        RegisterTaskBindings(r);
        RegisterComparableInterfaces(r);
        RegisterPrimitiveToString(r);
        RegisterDecimalBindings(r);
        RegisterTimeCultureFaces(r);
        RegisterMathBindings(r);
        RegisterSystemSr(r);
        RegisterThrowHelpers(r);
        RegisterArrayBindings(r);
        RegisterActivator(r);
        RegisterEqualityComparer(r);
        RegisterSpanFormattable(r);
        RegisterArrayPool(r);
        RegisterConvertBinary(r);
        RegisterGuid(r);
        RegisterAssembly(r);
        AssemblyLoadContextRuntime.RegisterBindings(r);
        RegisterExpressionTrees(r);
        RegisterReflectionEmit(r);
    }

    
    // ---- host 側 CPU コストの面共費 (タスク 2 hardening #7)。culture 比較 / TextInfo 書式面 /
    //      number formatting が intrinsic 内で走る際に、文字列の文字数近似での host work 予算
    //      (HostWorkBudget) を消費させる。budget 超過は MemoryQuotaExceededException (ゲスト外)。
    private static void ChargeHostWork(this IntrinsicContext ctx, long costUnits) =>
        ctx.Heap.ChargeHostWork(costUnits);

    // ---- host 側 CPU コストの計上 (タスク 2 hardening #7/#9)。culture / 書式面の
    // 重いホスト演算 (CompareInfo / TextInfo / Number.Formatting) を HostWorkBudget
    // (VmHeap.ChargeHostWork) に計上する (符号の作業量を文字数近似での計上)。
    // charge を 1 箇所で集中管理し、従来「intrinsic 毎に Sculptor」だった経路を統一。
    private static long HostWorkChars(string? a, string? b) => (long)a?.Length + b?.Length ?? 0;
// ---- System.Decimal (演算・変換・解析面) ----

    /// <summary>decimal の演算 / 変換 / 解析面。
    /// 本家 IL 本体は Decimal 構造体と内部 DecCalc 構造体の Unsafe.As 参照再解釈
    /// (同一ビット列の型視点差し替え) で構成されるため、VM のオブジェクト表現
    /// (VmStructValue のフィールドスロット列) では IL 実行にできない。
    /// 実 CLR もこれらの面を JIT intrinsic / ランタイム内部として処理することと同型であるため、
    /// 同一意味論のホスト BCL 実装へ委譲し、戻り値は VM の System.Decimal 構造体値に正規化する。
    /// ToString 書式面は VmCoreLibSurfaces 経由の DotnetVM.CoreLib.DecimalFormatting
    /// (不変カルチャ固定) が担当 (Faces 置換面が先に解決されるため共存は競合しない)。</summary>
    private static void RegisterDecimalBindings(IntrinsicRegistry r) {
        const string T = "System.Decimal";
        const string Ret = "System.Decimal";
        const string Styles = "System.Globalization.NumberStyles";
        const string Midpoint = "System.MidpointRounding";

        static string? S(StackSlot[] a, int i) => (a[i].ObjectValue as VmString)?.Value;

        // ---- 解析面 (Parse / TryParse): 不変カルチャ規約固定 (VM 規約)。
        //      Parse 失敗は本家と同じ FormatException / OverflowException (ゲスト例外化) を投げる
        static StackSlot ParseImpl(IntrinsicContext ctx, StackSlot[] a, int styles) {
            try {
                return StackSlot.OfValueType(MakeDecimalStruct(ctx,
                    decimal.Parse(S(a, 0) ?? "", (System.Globalization.NumberStyles)styles,
                        System.Globalization.CultureInfo.InvariantCulture)));
            } catch (FormatException) {
                throw new UnhandledGuestException("System.FormatException", null);
            } catch (OverflowException) {
                throw new UnhandledGuestException("System.OverflowException", null);
            }
        }
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", Ret, ["System.String"]),
            static (ctx, a) => ParseImpl(ctx, a, (int)System.Globalization.NumberStyles.Number),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", Ret, ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => ParseImpl(ctx, a, (int)System.Globalization.NumberStyles.Number),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", Ret, ["System.String", Styles]),
            static (ctx, a) => ParseImpl(ctx, a, a[1].AsInt32),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", Ret, ["System.String", Styles, "System.IFormatProvider"]),
            static (ctx, a) => ParseImpl(ctx, a, a[1].AsInt32),
            BindingOrigin.Managed);
        // TryParse (out decimal& を VmByRef 経由で書き込む)
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryParse", "System.Boolean", ["System.String", "System.Decimal&"]),
            static (ctx, a) => {
                decimal value = 0m;
                bool ok;
                try {
                    value = decimal.Parse(S(a, 0) ?? "", System.Globalization.CultureInfo.InvariantCulture);
                    ok = true;
                } catch (FormatException) {
                    ok = false;
                } catch (OverflowException) {
                    ok = false;
                }
                if (a[1].ObjectValue is VmByRef byref)
                    byref.Write(StackSlot.OfValueType(MakeDecimalStruct(ctx, ok ? value : 0m)));
                return StackSlot.OfInt32(ok ? 1 : 0);
            },
            BindingOrigin.Managed);

        // ---- 96 ビット演算面 (ホスト BCL 委譲、結果は VM 構造体値に正規化)。
        //      0 除算 = DivideByZeroException / 上限超過 = OverflowException (本家 DecCalc と同一)
        static StackSlot BinOp(IntrinsicContext ctx, StackSlot[] a, char op) {
            try {
                var x = ToDecimalValue(a[0]);
                var y = ToDecimalValue(a[1]);
                var value = op switch {
                    '+' => x + y,
                    '-' => x - y,
                    '*' => x * y,
                    '/' => x / y,
                    _ => x % y,
                };
                return StackSlot.OfValueType(MakeDecimalStruct(ctx, value));
            } catch (DivideByZeroException) {
                throw new UnhandledGuestException("System.DivideByZeroException", null);
            } catch (OverflowException) {
                throw new UnhandledGuestException("System.OverflowException", null);
            }
        }
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Addition", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '+'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Subtraction", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '-'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Multiply", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '*'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Division", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '/'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Remainder", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '%'), BindingOrigin.Managed);
        // 公開ヘルパー面 (decimal.Add 等は本家 IL が op_* を呼ぶ形のため同一実装で受ける)
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Add", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '+'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Subtract", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '-'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Multiply", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '*'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Divide", Ret, ["System.Decimal", "System.Decimal"]),
            static (ctx, a) => BinOp(ctx, a, '/'), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_UnaryNegation", Ret, ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, -ToDecimalValue(a[0]))), BindingOrigin.Managed);

        // ---- 比較群 (戻り System.Boolean / パラメータ (dec, dec))。戻り型はワイルドカードで統一
        r.RegisterBinding(BindingKey.Static(T, "op_Equality", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) == ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "op_Inequality", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) != ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "op_GreaterThan", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) > ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "op_LessThan", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) < ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "op_GreaterThanOrEqual", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) >= ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "op_LessThanOrEqual", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]) <= ToDecimalValue(a[1]) ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Compare", ["System.Decimal", "System.Decimal"]),
            static (_, a) => StackSlot.OfInt32(ToDecimalValue(a[0]).CompareTo(ToDecimalValue(a[1]))), BindingOrigin.Managed);

        // ---- 変換群: パラメータ列が同一で戻り型のみ異なる面 (op_Implicit / op_Explicit) は
        //      BindingKey の戻り型名 (C5.5 継続で追加) で区別する
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.Int32"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.Int64"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, a[0].Int64Value)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.UInt32"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (uint)a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.Byte"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (byte)a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.SByte"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (sbyte)a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.Int16"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (short)a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.UInt16"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (ushort)a[0].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Implicit", Ret, ["System.UInt64"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, (ulong)a[0].Int64Value)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Int32", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32(decimal.ToInt32(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Double", ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfFloat(decimal.ToDouble(ToDecimalValue(a[0]))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Single", ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfFloat(decimal.ToSingle(ToDecimalValue(a[0]))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Byte", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32(decimal.ToByte(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.SByte", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32(decimal.ToSByte(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Int16", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32(decimal.ToInt16(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.UInt16", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32(decimal.ToUInt16(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.UInt32", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt32((int)decimal.ToUInt32(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.UInt64", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt64((long)decimal.ToUInt64(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Explicit", "System.Int64", ["System.Decimal"]),
            static (ctx, a) => OverflowToGuest("System.OverflowException",
                () => StackSlot.OfInt64(decimal.ToInt64(ToDecimalValue(a[0])))), BindingOrigin.Managed);

        // ---- 丸め系 (Truncate / Round / Floor / Ceiling)。本家 IL は
        //      DecCalc::InternalRound / VarDec* (Decimal と DecCalc の Unsafe.As 参照再解釈)
        //      を辿るため IL 実行にできず、ホスト同一意味論 (中点規約 = 5 丸めの away-from-zero
        //      (Decimal.Round(decimal) / (_, digits) の CLR 契約)) を委譲する
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", Ret, ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, decimal.Round(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", Ret, ["System.Decimal", "System.Int32"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, decimal.Round(ToDecimalValue(a[0]), a[1].AsInt32))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", Ret, ["System.Decimal", Midpoint]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx,
                decimal.Round(ToDecimalValue(a[0]), (System.MidpointRounding)a[1].AsInt32))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", Ret, ["System.Decimal", "System.Int32", Midpoint]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx,
                decimal.Round(ToDecimalValue(a[0]), a[1].AsInt32, (System.MidpointRounding)a[2].AsInt32))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Truncate", Ret, ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, decimal.Truncate(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Floor", Ret, ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, decimal.Floor(ToDecimalValue(a[0])))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Ceiling", Ret, ["System.Decimal"]),
            static (ctx, a) => StackSlot.OfValueType(MakeDecimalStruct(ctx, decimal.Ceiling(ToDecimalValue(a[0])))), BindingOrigin.Managed);
    }

    /// <summary>decimal の解析面 (不変カルチャ固定)。Parse 失敗は本家と同じ FormatException /
    /// OverflowException (ゲスト例外化) を投げる。</summary>
    private static StackSlot DecimalParse(IntrinsicContext ctx, string? s, int styles) {
        try {
            return StackSlot.OfValueType(MakeDecimalStruct(ctx,
                decimal.Parse(s ?? "", (System.Globalization.NumberStyles)styles,
                    System.Globalization.CultureInfo.InvariantCulture)));
        } catch (FormatException) {
            throw new UnhandledGuestException("System.FormatException", null);
        } catch (OverflowException) {
            throw new UnhandledGuestException("System.OverflowException", null);
        }
    }

    /// <summary>Divide / Remainder の例外ラップ (0 除算 = DivideByZeroException、
    /// 上限超過 = OverflowException。本家 DecCalc と同一)。</summary>
    private static StackSlot DecimalWrap(IntrinsicContext ctx, Func<decimal> op) {
        try {
            return StackSlot.OfValueType(MakeDecimalStruct(ctx, op()));
        } catch (DivideByZeroException) {
            throw new UnhandledGuestException("System.DivideByZeroException", null);
        } catch (OverflowException) {
            throw new UnhandledGuestException("System.OverflowException", null);
        }
    }

    /// <summary>ホスト演算の OverflowException をゲスト例外に変換する共通部。</summary>
    private static StackSlot OverflowToGuest(string typeName, Func<StackSlot> op) {
        try {
            return op();
        } catch (OverflowException) {
            throw new UnhandledGuestException(typeName, null);
        }
    }

    /// <summary>VM の System.Decimal 構造体値 (VmStructValue) を構築する。
    /// ホスト decimal.GetBits のビット列を CoreLib (宣言ローダ) の System.Decimal 構造体の
    /// フィールドスロット (_flags int32 / _hi32 uint32 / _lo64 uint64 本家 .NET 10 レイアウト) に
    /// 配置する。スロットの Kind は VM のフィールド署名型 (i4 / i8 スロット) に合わせる。</summary>
    private static VmStructValue MakeDecimalStruct(IntrinsicContext ctx, decimal value) {
        var cls = FindCoreLibDecimalType(ctx)
            ?? throw new InvalidOperationException("System.Decimal (CoreLib 実型) がロードされていません。");
        var bits = decimal.GetBits(value);
        var lo64 = (ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        // bits[3] = CLR flags ビット列 (sign<<31 | scale<<16) がそのまま i4 スロット値
        var fields = new StackSlot[InstanceFieldCount(cls)];
        var index = 0;
        foreach (var field in cls.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            fields[index++] = field.Name switch {
                "_flags" => StackSlot.OfInt32(bits[3]),
                "_hi32" => StackSlot.OfInt32(bits[2]),
                "_lo64" => StackSlot.OfInt64((long)lo64),
                // CoreLib レイアウトの未知フィールドはゼロ埋め (現行ホスト CoreLib には無い)
                _ => SlotDefaultZero(field),
            };
        }
        return new VmStructValue(cls, fields);
    }

    /// <summary>unknown フィールドのゼロスロット (フィールド型が i8/u8 なら i8 スロット)。</summary>
    private static StackSlot SlotDefaultZero(VmField field) {
        var typeName = field.FieldType?.FullName ?? "";
        return typeName is "System.UInt64" or "System.Int64" ? StackSlot.OfInt64(0) : StackSlot.OfInt32(0);
    }

    /// <summary>宣言ローダ (CoreLib) の System.Decimal 実型を探す。バインド呼出側の
    /// ctx.Types は「呼出フレームのローダ」 (ゲスト画像等 CoreLib でないことがある) のため、
    /// Context に登録された全画像から CoreLib の TypeDef/System.Decimal を探す。
    /// 複数画像に同名型がある場合は System.Private.CoreLib を優先する (C2 型ユニフィケーション
    /// の実型統一規約と同じ優先順)。</summary>
    private static VmClassType? FindCoreLibDecimalType(IntrinsicContext ctx) =>
        FindCoreLibType(ctx, "System.Decimal");

    /// <summary>CoreLib (System.Private.CoreLib.dll 実装側) の TypeDef を探す一般化面。</summary>
    private static VmClassType? FindCoreLibType(IntrinsicContext ctx, string fullName) {
        VmClassType? fromContext = null;
        foreach (var loader in ctx.Types.Context?.Loaders ?? [ctx.Types]) {
            if (loader.FindTypeByFullName(fullName) is not VmClassType cls)
                continue;
            if (loader.Image.SourcePath?.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) == true)
                return cls;
            fromContext ??= cls;
        }
        // Context 未接続/接続画像に無い場合のフォールバック (呼出ローダ自体の検索)
        return fromContext ?? ctx.Types.FindTypeByFullName(fullName);
    }

    /// <summary>CoreLib (宣言ローダ) 値型のインスタンスフィールド数 (this を含まないスロット幅)。</summary>
    private static int InstanceFieldCount(VmClassType cls) {
        var count = 0;
        foreach (var f in cls.Fields)
            if (!f.IsStatic && !f.IsLiteral)
                count++;
        return count;
    }

    /// <summary>VM 構造体値 / box スロット (System.Decimal) → ホスト decimal。
    /// "new decimal(...)" の IL が入れたフィールドレイアウト (_flags / _hi32 / _lo64) を読む。
    /// u8 フィールドのスロットは Int64 ビット列で生ビットを保持するため符号は無関係。</summary>
    private static decimal ToDecimalValue(in StackSlot slot) {
        var sv = slot.ObjectValue switch {
            VmStructValue direct => direct,
            VmBoxedValue boxed => new VmStructValue(boxed.Type, boxed.Fields, boxed.Type is VmConstructedType ct ? ct.TypeArguments : null),
            _ => throw new InvalidOperationException($"decimal のスロットを期待しましたが {SlotOps.Describe(slot)} が来ました。"),
        };
        var cls = (VmClassType)sv.StructType;
        var iFlags = InstanceFieldSlotIndex(cls, "_flags");
        var iHi = InstanceFieldSlotIndex(cls, "_hi32");
        var iLo = InstanceFieldSlotIndex(cls, "_lo64");
        if (iFlags < 0 || iHi < 0 || iLo < 0)
            throw new InvalidOperationException($"decimal 構造体のフィールドレイアウト (_flags/_hi32/_lo64) を解決できません: {cls.FullName}");
        var flags = (int)sv.Fields[iFlags].Int64Value;
        var hi = (uint)sv.Fields[iHi].Int64Value;
        var lo64 = (ulong)sv.Fields[iLo].Int64Value;
        var scale = (byte)((flags >> 16) & 0xFF);
        var isNegative = flags < 0;
        return new decimal((int)(uint)lo64, (int)(uint)(lo64 >> 32), (int)hi, isNegative, scale);
    }

    /// <summary>宣言順のインスタンスフィールドスロットインデ克斯を名前で解決する (VmField 解決辞書無しで済む補助)。
    /// 既存の ObjectModel.GetLayout と同一の走査規約 (継承チェーン / 静的除去)。</summary>
    private static int InstanceFieldSlotIndex(VmClassType cls, string fieldName) {
        var index = 0;
        foreach (var field in cls.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            if (field.Name == fieldName)
                return index;
            index++;
        }
        return -1;
    }
    // ---- プリミティブ instance ToString (ボックス化仮想呼出面) ----

    /// <summary>プリミティブの instance ToString バインド。String.Concat(object, object)
    /// 等、CoreLib IL 内の `boxedPrimitive?.ToString()` の callvirt が culture 機構依存の
    /// managed IL に仮想解決されるのを受ける面。
    /// C5.5 Wave 2 で整数 8 型 + Boolean/Char、Wave 3 で Single/Double の全 ToString 面
    /// (無引数 / format / format+IFormatProvider / IFormatProvider) を VmCoreLibSurfaces
    /// (DotnetVM.CoreLib FormatSpecifiers / DoubleFormatting の managed IL) に置換したため、
    /// ここには載せない (載せると ① が ②'/置換面を塞いでしまう)。</summary>
    private static void RegisterPrimitiveToString(IntrinsicRegistry r) {
    }

    // ---- 構築ジェネリック インターフェース (プリミティブ実体化面) ----

    /// <summary>System.IComparable`1&lt;T&gt;::CompareTo と System.IEquatable`1&lt;T&gt;::Equals の
    /// プリミティブ実体化面。ArgumentOutOfRangeException.ThrowIfGreaterThan 等、CoreLib IL の
    /// T: IComparable&lt;T&gt; / IEquatable&lt;T&gt; 制約付き callvirt から呼ばれる。
    /// CLR と同じく T の型で符号 / 浮動小数点の比較規約を替える。</summary>
    private static void RegisterComparableInterfaces(IntrinsicRegistry r) {
        const string comparable = "System.IComparable`1";
        const string equatable = "System.IEquatable`1";
        // (実体化 T 名, 符号なし, 浮動小数点)
        var instantiations = new[] {
            ("System.Int32", false, false), ("System.UInt32", true, false),
            ("System.Int64", false, false), ("System.UInt64", true, false),
            ("System.Int16", false, false), ("System.UInt16", true, false),
            ("System.SByte", false, false), ("System.Byte", true, false),
            ("System.Char", true, false), ("System.Boolean", true, false),
            ("System.Single", false, true), ("System.Double", false, true),
        };
        foreach (var (typeName, unsigned, floating) in instantiations) {
            r.RegisterBinding(BindingKey.Instance(comparable, "CompareTo", typeName),
                (_, a) => StackSlot.OfInt32(ComparePrimitives(a[0], a[1], unsigned, floating)),
                BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance(equatable, "Equals", typeName),
                (_, a) => StackSlot.OfInt32(EqualsPrimitives(a[0], a[1], floating) ? 1 : 0),
                BindingOrigin.Managed);
        }
    }

    private static int ComparePrimitives(in StackSlot x, in StackSlot y, bool unsigned, bool floating) {
        if (floating)
            return x.DoubleValue.CompareTo(y.DoubleValue);
        if (unsigned) {
            var ux = x.Kind == StackKind.Int64 ? (ulong)x.Int64Value : (uint)x.AsInt32;
            var uy = y.Kind == StackKind.Int64 ? (ulong)y.Int64Value : (uint)y.AsInt32;
            return ux.CompareTo(uy);
        }
        var sx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var sy = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return sx.CompareTo(sy);
    }

    private static bool EqualsPrimitives(in StackSlot x, in StackSlot y, bool floating) {
        if (floating)
            return x.DoubleValue.Equals(y.DoubleValue);
        var lx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var ly = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return lx == ly;
    }

    // ---- ISpanFormattable.TryFormat (JoinCore 等の要素書式面) ----

    /// <summary>ISpanFormattable.TryFormat(span, out int, format, provider) の同等意味論。
    /// string.JoinCore 等が要素書式に辿る。本家 IL は Number.TryFormat* (char* / culture 機構)
    /// のため VM 表現境界。ホストの不変カルチャ書式で文字列化し、出力 span へ書き込む。
    /// provider は不変カルチャ規約で無視する (全書式面と同一方針)。長さ不足は false
    /// (例外なし。Try パターン規約)。不正書式は CLR どおり FormatException。
    /// 整数 4 型 + 浮動小数点 2 型を列挙する (他は fail-closed のまま)。</summary>
    private static void RegisterSpanFormattable(IntrinsicRegistry r) {
        const string SpanChar = "System.Span`1<System.Char>";
        const string ReadOnlySpanChar = "System.ReadOnlySpan`1<System.Char>";
        const string OutInt = "System.Int32&";
        const string Provider = "System.IFormatProvider";
        static void Face(IntrinsicRegistry reg, string type, Func<StackSlot, string?, string> format) =>
            reg.RegisterBinding(
                BindingKey.Instance(type, "TryFormat", SpanChar, OutInt, ReadOnlySpanChar, Provider),
                (ctx, a) => TryFormatImpl(ctx, a, format),
                BindingOrigin.Managed);
        static string Inv(int v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Unv(uint v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Lng(long v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Ulng(ulong v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Flt(float v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        static string Dbl(double v, string? f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        Face(r, "System.Int32", static (v, f) => Inv((int)v.Int64Value, f));
        Face(r, "System.UInt32", static (v, f) => Unv((uint)v.Int64Value, f));
        Face(r, "System.Int64", static (v, f) => Lng(v.Int64Value, f));
        Face(r, "System.UInt64", static (v, f) => Ulng((ulong)v.Int64Value, f));
        Face(r, "System.Single", static (v, f) => Flt((float)v.DoubleValue, f));
        Face(r, "System.Double", static (v, f) => Dbl(v.DoubleValue, f));
    }

    private static StackSlot? TryFormatImpl(IntrinsicContext ctx, StackSlot[] a, Func<StackSlot, string?, string> format) {
        // 書式 span (空 = null 書式相当)
        var formatText = ReadCharSpanOrEmpty(a[3]);
        string text;
        try {
            text = format(a[0], formatText);
        } catch (FormatException) {
            throw new UnhandledGuestException("System.FormatException", null);
        }
        // 出力 span の容量検査と書込
        var dest = ReadSpanParts(a[1]);
        if (text.Length > dest.Length) {
            WriteWritten(a[2], 0);
            return StackSlot.OfInt32(0);
        }
        if (text.Length > 0)
            WriteChars(dest.Reference, text);
        WriteWritten(a[2], text.Length);
        return StackSlot.OfInt32(1);
    }

    /// <summary>ReadOnlySpan&lt;char&gt; 構造体値から文字列を読む (空 span は "")。</summary>
    private static string ReadCharSpanOrEmpty(in StackSlot span) {
        var (reference, length) = ReadSpanParts(span);
        if (length == 0)
            return "";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)ReadCharAt(reference, i);
        return new string(chars);
    }

    private static (StackSlot Reference, int Length) ReadSpanParts(in StackSlot span) {
        if (span.ObjectValue is not VmStructValue sv)
            throw new InvalidOperationException($"span ではありません ({span.Kind})。");
        var definition = sv.StructType is VmConstructedType constructed
            ? constructed.Definition as VmClassType
            : sv.StructType as VmClassType;
        if (definition is null)
            throw new InvalidOperationException("span の型を解決できません。");
        // 配置は GetLayout (正規レイアウト) で引く (StaticFieldIndex は静的専用のため不可)
        var layout = new ObjectModel().GetLayout(definition);
        StackSlot reference = default;
        var length = 0;
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            if (!layout.TryGetValue(field, out var index) || index >= sv.Fields.Length)
                throw new InvalidOperationException($"span のフィールド {field.Name} の配置を解決できません。");
            if (field.Name == "_reference")
                reference = sv.Fields[index];
            else if (field.Name == "_length")
                length = sv.Fields[index].AsInt32;
        }
        return (reference, length);
    }

    private static int ReadCharAt(in StackSlot reference, int index) {
        if (reference.Kind == StackKind.ByRef && reference.ObjectValue is VmByRef byRef) {
            if (index < 0 || byRef.Index + index >= byRef.Container.Length)
                throw new InvalidOperationException($"span の範囲外を読みます (index={index})。");
            return byRef.Container[byRef.Index + index].AsInt32;
        }
        if (reference.ObjectValue is VmNativePointer native) {
            var offset = native.ByteOffset + index * 2;
            if (offset < 0 || offset + 2 > native.Bytes.Length)
                throw new InvalidOperationException($"span の範囲外を読みます (index={index})。");
            return native.Bytes[offset] | native.Bytes[offset + 1] << 8;
        }
        throw new InvalidOperationException($"span の参照を解釈できません ({reference.Kind})。");
    }

    private static void WriteChars(in StackSlot reference, string text) {
        if (reference.Kind == StackKind.ByRef && reference.ObjectValue is VmByRef byRef) {
            if (byRef.Index + text.Length > byRef.Container.Length)
                throw new InvalidOperationException("span の範囲外に書きます。");
            for (var i = 0; i < text.Length; i++)
                byRef.Container[byRef.Index + i] = StackSlot.OfInt32(text[i]);
            return;
        }
        if (reference.ObjectValue is VmNativePointer native) {
            if ((long)native.ByteOffset + text.Length * 2 > native.Bytes.Length)
                throw new InvalidOperationException("span の範囲外に書きます。");
            for (var i = 0; i < text.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                    native.Bytes.AsSpan(native.ByteOffset + i * 2, 2), text[i]);
            return;
        }
        throw new InvalidOperationException($"span の参照を解釈できません ({reference.Kind})。");
    }

    private static void WriteWritten(in StackSlot slot, int value) {
        if (slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef)
            byRef.Write(StackSlot.OfInt32(value));
        else
            throw new InvalidOperationException("TryFormat の out 引数が参照ではありません。");
    }

    /// <summary>Assembly.Load(byte[]) は CLR にロードせず、必ず同じ VM の PE loader に通す。</summary>
    private static void RegisterAssembly(IntrinsicRegistry r) {
        const string T = "System.Reflection.Assembly";
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Byte[]"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Byte[]", "System.Byte[]"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Reflection.AssemblyName"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "get_FullName"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(
                ((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.Identity.ToString())), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "get_Location"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(
                ((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.SourcePath ?? "")), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetType", "System.String"),
            static (ctx, a) => {
                var assembly = (VmAssemblyObject)a[0].ObjectValue!;
                var name = (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                return assembly.Loader.FindTypeByFullName(name) is { } type
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, type) : StackSlot.Null;
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetType", "System.String", "System.Boolean"),
            static (ctx, a) => {
                var assembly = (VmAssemblyObject)a[0].ObjectValue!;
                var name = (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                if (assembly.Loader.FindTypeByFullName(name) is { } type)
                    return DefaultIntrinsics.MakeRuntimeObject(ctx, type);
                if (a[2].AsInt32 != 0)
                    throw new UnhandledGuestException("System.TypeLoadException", $"型 '{name}' が見つかりません。");
                return StackSlot.Null;
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetTypes"),
            static (ctx, a) => {
                var loader = ((VmAssemblyObject)a[0].ObjectValue!).Loader;
                var values = new List<StackSlot>();
                for (var rid = 1; rid <= loader.Image.Tables.GetRowCount(TableKind.TypeDef); rid++) {
                    var type = loader.GetTypeDef(rid);
                    if (type.FullName != "<Module>")
                        values.Add(DefaultIntrinsics.MakeRuntimeObject(ctx, type));
                }
                var elementType = (VmType?)ctx.Types.FindTypeByFullName("System.Type")
                    ?? ctx.Types.FindIntrinsicType("System.Type")
                    ?? throw new InvalidOperationException("System.Type が解決できません。");
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(
                    new VmArrayType { ElementType = elementType }, values.ToArray())));
            }, BindingOrigin.Managed);
    }

    /// <summary>Expression.Compile は小さな式ノードを VM 側で評価する delegate として返す。</summary>
    private static void RegisterExpressionTrees(IntrinsicRegistry r) {
        const string T = "System.Linq.Expressions.Expression";
        static VmExpressionObject Node(StackSlot slot) => slot.ObjectValue as VmExpressionObject
            ?? throw new UnhandledGuestException("System.ArgumentException", "引数は VM 式木ノードである必要があります。");
        static VmType TargetType(StackSlot slot) => slot.ObjectValue is VmRuntimeObject runtimeType
            ? runtimeType.Target
            : throw new UnhandledGuestException("System.ArgumentException", "Type 引数は VM の型情報である必要があります。");
        static StackSlot ConstantValue(StackSlot slot) => slot.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length == 1
            ? boxed.Fields[0] : slot;

        r.RegisterBinding(BindingKey.Static(T, "Constant", "System.Object"),
            static (ctx, a) => {
                var value = ConstantValue(a[0]);
                var type = value.ObjectValue is null
                    ? ctx.Types.FindIntrinsicType("System.Object")!
                    : DefaultIntrinsics.RuntimeTypeOf(ctx, value);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.Constant, Constant = value, ResultType = type,
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Constant", "System.Object", "System.Type"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Constant, Constant = ConstantValue(a[0]), ResultType = TargetType(a[1]),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Parameter", "System.Type"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Parameter", "System.Type", "System.String"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]),
            })), BindingOrigin.Managed);

        void Binary(string name, VmExpressionKind kind) =>
            r.RegisterBinding(BindingKey.Static(T, name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression"),
                (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
                })), BindingOrigin.Managed);
        Binary("Add", VmExpressionKind.Add);
        Binary("Subtract", VmExpressionKind.Subtract);
        Binary("Multiply", VmExpressionKind.Multiply);
        Binary("Divide", VmExpressionKind.Divide);
        foreach (var (name, kind) in new[] {
            ("AddChecked", VmExpressionKind.Add), ("SubtractChecked", VmExpressionKind.Subtract),
            ("MultiplyChecked", VmExpressionKind.Multiply), ("Modulo", VmExpressionKind.Modulo),
            ("And", VmExpressionKind.And), ("Or", VmExpressionKind.Or),
            ("ExclusiveOr", VmExpressionKind.ExclusiveOr), ("AndAlso", VmExpressionKind.AndAlso),
            ("OrElse", VmExpressionKind.OrElse), ("Equal", VmExpressionKind.Equal),
            ("NotEqual", VmExpressionKind.NotEqual), ("GreaterThan", VmExpressionKind.GreaterThan),
            ("GreaterThanOrEqual", VmExpressionKind.GreaterThanOrEqual), ("LessThan", VmExpressionKind.LessThan),
            ("LessThanOrEqual", VmExpressionKind.LessThanOrEqual),
        })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) =>
                StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
                })), BindingOrigin.Managed);
        foreach (var (name, kind) in new[] {
            ("Negate", VmExpressionKind.Negate), ("NegateChecked", VmExpressionKind.Negate),
            ("Not", VmExpressionKind.Not),
        })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) =>
                StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), ResultType = Node(a[0]).ResultType,
                })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Convert"), (ctx, a) => {
            var node = Node(a[0]);
            var target = a.Length > 1 && a[1].ObjectValue is VmRuntimeObject type ? type.Target : node.ResultType;
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Convert, Left = node, ResultType = target,
            }));
        }, BindingOrigin.Managed);
        foreach (var (name, kind) in new[] { ("TypeIs", VmExpressionKind.TypeIs), ("TypeAs", VmExpressionKind.TypeAs) })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                var operand = Node(a[0]);
                var target = TargetType(a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = operand, NewType = target,
                    ResultType = kind == VmExpressionKind.TypeIs ? ctx.Types.FindIntrinsicType("System.Boolean") : target,
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayIndex"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayLength"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayLength, Left = Node(a[0]), ResultType = ctx.Types.FindIntrinsicType("System.Int32"),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Condition"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Conditional, Left = Node(a[0]), IfTrue = Node(a[1]), IfFalse = Node(a[2]), ResultType = Node(a[1]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Assign"), (ctx, a) => {
            var target = Node(a[0]);
            var value = Node(a[1]);
            if (target.Kind != VmExpressionKind.Parameter)
                throw new UnhandledGuestException("System.ArgumentException", "Expression.Assign の左辺は ParameterExpression である必要があります。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Assign, Left = target, Right = value, ResultType = value.ResultType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Variable"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]), IsVariable = true,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Default"), (ctx, a) => {
            var type = TargetType(a[0]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Default, ResultType = type, NewType = type,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Quote"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Quote, Object = Node(a[0]), ResultType = Node(a[0]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayAccess"), (ctx, a) => {
            var indexes = Nodes(a[^1]);
            if (indexes.Length != 1)
                throw new UnhandledGuestException("System.NotSupportedException", "式木の多次元配列アクセスは未対応です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = indexes[0],
                ResultType = Node(a[0]).ResultType,
            }));
        }, BindingOrigin.Managed);
        static VmExpressionObject[] Nodes(StackSlot slot) => slot.ObjectValue switch {
            VmArray array => array.Elements.Select(Node).ToArray(),
            null => [],
            _ => throw new UnhandledGuestException("System.ArgumentException", "式木引数配列が不正です。"),
        };
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Block"), (ctx, a) => {
            var expressions = a.Length == 0 ? [] : Nodes(a[^1]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Block, Expressions = expressions,
                ResultType = expressions.Length == 0 ? ctx.Types.FindIntrinsicType("System.Void") : expressions[^1].ResultType,
            }));
        }, BindingOrigin.Managed);
        foreach (var name in new[] { "NewArrayInit", "NewArrayBounds" })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                var elementType = TargetType(a[0]);
                var values = a.Length > 1 ? Nodes(a[^1]) : [];
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.NewArray, NewType = elementType, Arguments = values,
                    NewArrayBounds = name == "NewArrayBounds",
                    ResultType = new VmArrayType { ElementType = elementType },
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Call"), (ctx, a) => {
            VmExpressionObject? receiver = null;
            if (a[0].ObjectValue is not VmRuntimeMethod) receiver = Node(a[0]);
            var methodSlot = receiver is null ? a[0] : a[1];
            var method = methodSlot.ObjectValue is VmRuntimeMethod runtimeMethod
                ? runtimeMethod.Target : throw new UnhandledGuestException("System.ArgumentException", "MethodInfo が必要です。");
            var firstArgs = receiver is null ? (a.Length > 1 ? Nodes(a[^1]) : []) : (a.Length > 2 ? Nodes(a[^1]) : []);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Call, Object = receiver, Method = method, Arguments = firstArgs,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "New"), (ctx, a) => {
            var ctor = a[0].ObjectValue is VmRuntimeMethod runtimeMethod
                ? runtimeMethod.Target
                : throw new UnhandledGuestException("System.ArgumentException", "ConstructorInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.New, Method = ctor, NewType = ctor.DeclaringType,
                Arguments = a.Length > 1 ? Nodes(a[^1]) : [], ResultType = ctor.DeclaringType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Invoke"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Invoke, Object = Node(a[0]), Arguments = a.Length > 1 ? Nodes(a[^1]) : [],
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Field"), (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var field = a[^1].ObjectValue is VmRuntimeField runtimeField
                ? runtimeField.Target
                : ownerType is { } owner && a[^1].ObjectValue is VmString fieldName
                    ? (DefaultIntrinsics.MakeFieldObject(ctx, owner, fieldName.Value).ObjectValue as VmRuntimeField)?.Target
                    : null;
            if (field is null)
                throw new UnhandledGuestException("System.ArgumentException", "FieldInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Field = field, ResultType = field.FieldType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Property"), (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var property = a[^1].ObjectValue as VmRuntimeProperty;
            if (property is null && a[^1].ObjectValue is VmString propertyName && ownerType is { } owner)
                property = DefaultIntrinsics.MakePropertyObject(ctx, owner, propertyName.Value).ObjectValue as VmRuntimeProperty;
            if (property is null)
                throw new UnhandledGuestException("System.ArgumentException", "PropertyInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Property = property,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        }, BindingOrigin.Managed);

        r.RegisterBinding(BindingKey.Static(T, "Lambda", "System.Linq.Expressions.Expression", "System.Linq.Expressions.ParameterExpression[]"),
            static (ctx, a) => {
                if (ctx.MethodTypeArguments.Length != 1)
                    throw new UnhandledGuestException("System.ArgumentException", "Lambda<TDelegate> は delegate 型引数が必要です。");
                var body = Node(a[0]);
                var parameters = a[1].ObjectValue switch {
                    VmArray array => array.Elements.Select(Node).ToArray(),
                    null => [],
                    _ => throw new UnhandledGuestException("System.ArgumentException", "parameters は ParameterExpression[] である必要があります。"),
                };
                if (parameters.Any(p => p.Kind != VmExpressionKind.Parameter))
                    throw new UnhandledGuestException("System.ArgumentException", "lambda の引数は ParameterExpression である必要があります。");
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                    DelegateType = ctx.MethodTypeArguments[0], ResultType = ctx.MethodTypeArguments[0],
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Lambda"), static (ctx, a) => {
            var genericDelegate = ctx.MethodTypeArguments.Length == 1 ? ctx.MethodTypeArguments[0] : null;
            var explicitDelegate = genericDelegate is null && a.Length > 0 && a[0].ObjectValue is VmRuntimeObject
                ? TargetType(a[0]) : null;
            var delegateType = genericDelegate ?? explicitDelegate
                ?? throw new UnhandledGuestException("System.ArgumentException", "Lambda には delegate 型が必要です。");
            var bodyIndex = explicitDelegate is null ? 0 : 1;
            var body = Node(a[bodyIndex]);
            var parameters = a.Length > bodyIndex + 1 ? Nodes(a[^1]) : [];
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                DelegateType = delegateType, ResultType = delegateType,
            }));
        }, BindingOrigin.Managed);

        StackSlot Compile(IntrinsicContext ctx, StackSlot[] args) {
            var lambda = Node(args[0]);
            if (lambda.Kind != VmExpressionKind.Lambda || lambda.Body is null || lambda.DelegateType is null)
                throw new UnhandledGuestException("System.InvalidOperationException", "式木 Lambda が不正です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmDelegate {
                DeclaredType = lambda.DelegateType, ExpressionLambda = lambda,
            }));
        }
        foreach (var type in new[] { "System.Linq.Expressions.LambdaExpression", "System.Linq.Expressions.Expression`1" }) {
            r.RegisterBinding(BindingKey.Instance(type, "Compile"), (ctx, args) => Compile(ctx, args), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance(type, "Compile", "System.Boolean"), (ctx, args) => Compile(ctx, args), BindingOrigin.Managed);
        }
    }

    private static void RegisterReflectionEmit(IntrinsicRegistry r) {
        const string dynamicMethod = "System.Reflection.Emit.DynamicMethod";
        const string generator = "System.Reflection.Emit.ILGenerator";
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "GetILGenerator"),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "GetILGenerator", "System.Int32"),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "CreateDelegate", "System.Type"),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "CreateDelegate", "System.Type", "System.Object"),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DefineLabel"),
            (ctx, args) => ReflectionEmitRuntime.DefineLabel(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "MarkLabel", "System.Reflection.Emit.Label"),
            (ctx, args) => ReflectionEmitRuntime.MarkLabel(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DeclareLocal", "System.Type"),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DeclareLocal", "System.Type", "System.Boolean"),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode"),
            (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        foreach (var operand in new[] { "System.Byte", "System.SByte", "System.Int16", "System.Int32", "System.Int64", "System.Single", "System.Double" })
            r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode", operand),
                (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        foreach (var operand in new[] { "System.Reflection.Emit.Label", "System.Reflection.Emit.Label[]", "System.Reflection.Emit.LocalBuilder", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo", "System.Reflection.FieldInfo", "System.Type", "System.String" })
            r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode", operand),
                (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "EmitCall", "System.Reflection.Emit.OpCode", "System.Reflection.MethodInfo", "System.Type[]"),
            (ctx, args) => ReflectionEmitRuntime.EmitCall(ctx, args), BindingOrigin.Managed);
    }

    // ---- System.Enum ([Intrinsic] 面: 実 CLR も JIT が IL を置き換えるため VM もバインドで提供) ----

    private static void RegisterEnum(IntrinsicRegistry r) {
        // public bool Enum.HasFlag(Enum flag)
        // CoreLib の IL は GetRawData() (boxed 値への生ポインタ) と Unsafe.As によるビット演算で、
        // VM のオブジェクト表現では実行できない。実 CLR でも HasFlag は [Intrinsic] として JIT が
        // 丸ごと置き換える面であり、VM は同一意味論 (両者の生データのビットごと AND) をバインドで
        // 提供する。符号拡張は両辺で同一に行われるため AND 一致判定は生データ比較と等価
        r.RegisterBinding(BindingKey.Instance("System.Enum", "HasFlag", "System.Enum"),
            static (_, a) => StackSlot.OfInt32(HasFlagImpl(a) ? 1 : 0),
            BindingOrigin.Managed);
        // internal CorElementType Enum.InternalGetCorElementType()
        // CoreLib の IL は RuntimeHelpers.GetMethodTable(this) → MethodTable::GetPrimitiveCorElementType()
        // というランタイム内部表現 (MethodTable ポインタ) への直接アクセスで構成されるため実行できない。
        // Enum.IConvertible.GetTypeCode 等の実 IL が依存するリーフとして、box の型 (enum の
        // value__ フィールド型) から同一の CorElementType 値 (ECMA-335 の要素型コード) を返す
        r.RegisterBinding(BindingKey.Instance("System.Enum", "InternalGetCorElementType"),
            static (_, a) => StackSlot.OfInt32(CorElementTypeCodeOf(a[0])),
            BindingOrigin.InternalCall);
        // internal object Enum.GetValue()
        // CoreLib の IL は GetRawData() (box への生ポインタ) を Unsafe.As で基底型幅ごとに読むため
        // 実行できない。Enum の IConvertible EII (Convert.ToXxx(GetValue()) 形) が依存するリーフ。
        // VM では box の生値スロット (Fields[0]) を基底型で再ボックス化して返す (同一意味論)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "GetValue"),
            static (ctx, a) => GetEnumValueImpl(ctx, a[0]),
            BindingOrigin.InternalCall);
        // public override string Enum.ToString()
        // CoreLib の IL は FormatFeatures / CultureInfo 面を辿る culture 機構依存のため
        // 表現境界。リテラル表 (Constant テーブル) から G 書式名を解決する
        // (DayOfWeek 等の BCL enum もゲスト enum も同一経路。culture-out-of-scope)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format: null, ctx)));
            },
            BindingOrigin.InternalCall);
        // public string Enum.ToString(string? format)
        // 実 IL は castclass RuntimeType + 生ポインタ + ジェネリックヘルパーのため
        // VM 表現境界。リテラル表から G/D/X/F 書式を解決する。不正書式は FormatException
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString", "System.String"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                var format = (a[1].ObjectValue as VmString)?.Value;
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format, ctx)));
            },
            BindingOrigin.InternalCall);
        // string IFormattable.ToString(string?, IFormatProvider?) の明示的実装面
        // (短名 ToString の 2 引数 overload として解決される)。
        // string.Join 等が要素書式に辿る。EII 経路の ① 照合で受ける (ToString(string) と同一意味論)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString",
                "System.String", "System.IFormatProvider"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                var format = (a[1].ObjectValue as VmString)?.Value;
                return StackSlot.OfObject(ctx.MakeString(
                    EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format, ctx)));
            },
            BindingOrigin.InternalCall);
        // public static T Enum.Parse<T>(string) / (string, bool)
        // 実 IL は RuntimeType リフレクション (GetFields) のため VM 表現境界。
        // リテラル表の名前照合 (ignoreCase 対応) + 数値面で同一意味論を提供する。
        // T はメソッド型実引数で判別する
        r.RegisterBinding(BindingKey.StaticWithReturn("System.Enum", "Parse", "!!0", ["System.String"]),
            static (ctx, a) => EnumParseImpl(ctx, a[0], ignoreCase: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn("System.Enum", "Parse", "!!0",
                ["System.String", "System.Boolean"]),
            static (ctx, a) => EnumParseImpl(ctx, a[0], a[1].AsInt32 != 0),
            BindingOrigin.Managed);
        // public static bool Enum.TryParse<T>(string?, out T) / (string?, bool, out T)
        r.RegisterBinding(BindingKey.Static("System.Enum", "TryParse", "System.String", "!!0&"),
            static (ctx, a) => EnumTryParseImpl(ctx, a, ignoreCase: false),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "TryParse", "System.String", "System.Boolean", "!!0&"),
            static (ctx, a) => EnumTryParseImpl(ctx, a, a[1].AsInt32 != 0),
            BindingOrigin.Managed);
        // public static string[] Enum.GetNames<T>() / T[] Enum.GetValues<T>()
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetNames"),
            static (ctx, a) => {
                _ = a;
                var literals = EnumLiteralsOf(ResolveEnumType(ctx, null));
                return StackSlot.OfObject(ctx.MakeStringArray(literals.Select(l => l.Name).ToList()));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetValues"),
            static (ctx, a) => {
                _ = a;
                var enumType = ResolveEnumType(ctx, null);
                var literals = EnumLiteralsOf(enumType);
                var elementType = (VmType)enumType;
                var arrayType = new VmArrayType { ElementType = elementType };
                try {
                    arrayType.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
                } catch {
                }
                var elements = new StackSlot[literals.Count];
                for (var i = 0; i < literals.Count; i++)
                    elements[i] = StackSlot.OfObject(ctx.Heap.Allocate(
                        new VmBoxedValue(enumType, [RawSlot(literals[i].Bits, enumType)])));
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(arrayType, elements)));
            },
            BindingOrigin.Managed);
        // public static bool Enum.IsDefined(Type, object?) / (Type, string?)
        r.RegisterBinding(BindingKey.Static("System.Enum", "IsDefined", "System.Type", "System.Object"),
            static (_, a) => StackSlot.OfInt32(EnumIsDefined(a[0], a[1]) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Enum", "IsDefined", "System.Type", "System.String"),
            static (_, a) => StackSlot.OfInt32(EnumIsDefined(a[0], a[1]) ? 1 : 0),
            BindingOrigin.Managed);
        // public static string? Enum.GetName(Type, object?)
        r.RegisterBinding(BindingKey.Static("System.Enum", "GetName", "System.Type", "System.Object"),
            static (ctx, a) => EnumGetName(a[0], a[1]) is { } name
                ? StackSlot.OfObject(ctx.MakeString(name)) : StackSlot.Null,
            BindingOrigin.Managed);
    }

    /// <summary>enum box の生ビット列 (ulong 正規化)。</summary>
    private static ulong RawBitsOf(VmBoxedValue box) => box.Fields[0].Kind switch {
        StackKind.Int64 => (ulong)box.Fields[0].Int64Value,
        StackKind.Float => (ulong)BitConverter.DoubleToInt64Bits(box.Fields[0].DoubleValue),
        _ => (ulong)(uint)box.Fields[0].AsInt32,
    };

    /// <summary>ジェネリック Enum 面の対象 enum 型 (メソッド型実引数 T)。
    /// 引数で直接型が渡る面 (Type, object) は typeSlot から取る。</summary>
    private static VmClassType ResolveEnumType(IntrinsicContext ctx, StackSlot? typeSlot) {
        VmType? target = null;
        if (typeSlot is { } slot && slot.ObjectValue is VmRuntimeObject runtime)
            target = runtime.Target;
        else {
            var name = ctx.MethodTypeArgAt(0);
            if (!string.IsNullOrEmpty(name) && name is not "!!0")
                target = FindAnyType(ctx, name);
        }
        if (target is VmClassType cls && (cls.IsEnum || cls.FullName == "System.Enum"))
            return cls;
        throw new UnhandledGuestException("System.ArgumentException", null);
    }

    private sealed record EnumLiteral(string Name, ulong Bits);

    /// <summary>enum 型のリテラル表 (名前 + 生ビット)。Constant テーブルから読む。</summary>
    private static List<EnumLiteral> EnumLiteralsOf(VmClassType? enumType) {
        var result = new List<EnumLiteral>();
        if (enumType is null)
            return result;
        var image = enumType.Image;
        var rowCount = image.Tables.GetRowCount(TableKind.Constant);
        foreach (var field in enumType.Fields) {
            if (!field.IsLiteral)
                continue;
            for (var rid = 1; rid <= rowCount; rid++) {
                var parent = image.Tables.DecodeCoded(TableKind.Constant, rid, 2, CodedIndexKind.HasConstant);
                if (parent.Table != TableKind.Field || parent.Rid != field.FieldRid)
                    continue;
                var blob = image.GetBlob(image.Tables.GetRowIndex(TableKind.Constant, rid, 3));
                result.Add(new EnumLiteral(field.Name, ReadConstantBits(blob)));
                break;
            }
        }
        return result;
    }

    private static ulong ReadConstantBits(System.ReadOnlySpan<byte> blob) => blob.Length switch {
        1 => blob[0],
        2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob),
        4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob),
        _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob),
    };

    /// <summary>enum 基底型の (バイト幅, 符号付き)。</summary>
    private static (int Size, bool Signed) EnumLayout(VmClassType? enumType) {
        var name = enumType?.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral)?.FieldType?.FullName;
        return name switch {
            "System.SByte" => (1, true),
            "System.Byte" or "System.Boolean" => (1, false),
            "System.Int16" => (2, true),
            "System.UInt16" or "System.Char" => (2, false),
            "System.Int32" => (4, true),
            "System.UInt32" => (4, false),
            "System.Int64" => (8, true),
            _ => (8, false),
        };
    }

    private static bool HasFlagsAttribute(VmClassType? enumType) {
        if (enumType is null)
            return false;
        var image = enumType.Image;
        var count = image.Tables.GetRowCount(TableKind.CustomAttribute);
        for (var rid = 1; rid <= count; rid++) {
            var parent = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 0, CodedIndexKind.HasCustomAttribute);
            if (parent.Table != TableKind.TypeDef || parent.Rid != enumType.TypeDefRid)
                continue;
            var ctor = image.Tables.DecodeCoded(TableKind.CustomAttribute, rid, 1, CodedIndexKind.CustomAttributeType);
            string? ownerName = null;
            if (ctor.Table == TableKind.MemberRef) {
                // 属性 .ctor の MemberRef (親は TypeRef / TypeDef のいずれか)
                var memberParent = image.Tables.DecodeCoded(
                    TableKind.MemberRef, ctor.Rid, 0, CodedIndexKind.MemberRefParent);
                if (memberParent.Table == TableKind.TypeRef) {
                    var (ns, n, _) = image.GetTypeRefName(memberParent.Rid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                } else if (memberParent.Table == TableKind.TypeDef) {
                    var (ns, n) = image.GetTypeDefName(memberParent.Rid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
            } else if (ctor.Table == TableKind.MethodDef) {
                // 同一画像の属性 .ctor 直接参照
                var ownerRid = FindMethodOwner(image, ctor.Rid);
                if (ownerRid != 0) {
                    var (ns, n) = image.GetTypeDefName(ownerRid);
                    ownerName = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
            }
            if (ownerName == "System.FlagsAttribute")
                return true;
        }
        return false;
    }

    private static int FindMethodOwner(AssemblyImage image, int methodRid) {
        var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var methodCount = image.Tables.GetRowCount(TableKind.MethodDef);
            _ = methodCount;
            // MethodList 範囲で所有 TypeDef を特定する (簡易走査)
            var start = image.Tables.GetRowIndex(TableKind.TypeDef, typeRid, 5);
            var end = typeRid < typeDefs
                ? image.Tables.GetRowIndex(TableKind.TypeDef, typeRid + 1, 5)
                : image.Tables.GetRowCount(TableKind.MethodDef) + 1;
            if (methodRid >= start && methodRid < end)
                return typeRid;
        }
        return 0;
    }

    /// <summary>enum 値の書式化 (G/D/X/F。provider は不変カルチャ規約で無視)。</summary>
    private static string EnumToString(VmClassType? enumType, ulong bits, bool? signed, string? format, IntrinsicContext ctx) {
        _ = ctx;
        var (size, underlyingSigned) = EnumLayout(enumType);
        var isSigned = signed ?? underlyingSigned;
        var fmt = string.IsNullOrEmpty(format) ? "G" : format!;
        if (fmt.Length != 1)
            throw new UnhandledGuestException("System.FormatException", null);
        return fmt[0] switch {
            'G' or 'g' or 'F' or 'f' => EnumNameOrNumber(enumType, bits, size, isSigned),
            'D' or 'd' => isSigned ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString(),
            'X' or 'x' => TruncateBits(bits, size, false).ToString(fmt[0] == 'X' ? "X" + size * 2 : "x" + size * 2),
            _ => throw new UnhandledGuestException("System.FormatException", null),
        };
    }

    /// <summary>文字列連結に渡された boxed enum の既定 (G) 書式。</summary>
    internal static string FormatEnumForConcat(VmBoxedValue box) =>
        EnumToString(box.Type as VmClassType, RawBitsOf(box), signed: null, format: null, ctx: null!);

    private static ulong TruncateBits(ulong bits, int size, bool signed) => (size, signed) switch {
        (1, true) => (ulong)(long)(sbyte)(byte)bits,
        (1, false) => (byte)bits,
        (2, true) => (ulong)(long)(short)(ushort)bits,
        (2, false) => (ushort)bits,
        (4, true) => (ulong)(int)(uint)bits,
        (4, false) => (uint)bits,
        _ => bits,
    };

    private static string EnumNameOrNumber(VmClassType? enumType, ulong bits, int size, bool signed) {
        var literals = EnumLiteralsOf(enumType);
        foreach (var literal in literals) {
            if (TruncateBits(literal.Bits, size, signed) == TruncateBits(bits, size, signed))
                return literal.Name;
        }
        if (enumType is not null && HasFlagsAttribute(enumType))
            return FormatFlags(literals, bits, size, signed);
        return signed ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString();
    }

    private static string FormatFlags(List<EnumLiteral> literals, ulong bits, int size, bool signed) {
        if (bits == 0) {
            foreach (var literal in literals) {
                if (TruncateBits(literal.Bits, size, signed) == 0)
                    return literal.Name;
            }
            return "0";
        }
        var parts = new List<string>();
        var remaining = TruncateBits(bits, size, signed);
        foreach (var literal in literals
                     .Select(l => (l.Name, Value: TruncateBits(l.Bits, size, signed)))
                     .OrderByDescending(p => p.Value)) {
            if (literal.Value == 0 || (remaining & literal.Value) != literal.Value)
                continue;
            parts.Add(literal.Name);
            remaining &= ~literal.Value;
        }
        if (remaining != 0)
            parts.Add(signed ? ((long)remaining).ToString() : remaining.ToString());
        return parts.Count == 0
            ? signed ? ((long)TruncateBits(bits, size, true)).ToString() : TruncateBits(bits, size, false).ToString()
            : string.Join(", ", parts);
    }

    private static StackSlot RawSlot(ulong bits, VmClassType enumType) {
        var (size, _) = EnumLayout(enumType);
        return size == 8 ? StackSlot.OfInt64((long)bits) : StackSlot.OfInt32((int)(uint)bits);
    }

    private static StackSlot EnumParseImpl(IntrinsicContext ctx, in StackSlot nameSlot, bool ignoreCase) {
        var enumType = ResolveEnumType(ctx, null);
        var raw = (nameSlot.ObjectValue as VmString)?.Value?.Trim() ?? "";
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (string.Equals(literal.Name, raw, comparison))
                return RawSlot(literal.Bits, enumType);
        }
        // 数値面 ("2" 等。範囲内なら未定義値でも box 化するのが CLR 規約)
        var (size, signed) = EnumLayout(enumType);
        if (signed) {
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sparsed) &&
                FitsIn(sparsed, size, true))
                return size == 8 ? StackSlot.OfInt64(sparsed) : StackSlot.OfInt32((int)sparsed);
        } else {
            if (ulong.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var uparsed) &&
                FitsInU(uparsed, size))
                return size == 8 ? StackSlot.OfInt64((long)uparsed) : StackSlot.OfInt32((int)(uint)uparsed);
        }
        throw new UnhandledGuestException("System.ArgumentException", null);
    }

    private static bool FitsIn(long value, int size, bool signed) => (size, signed) switch {
        (1, true) => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        (1, false) => value is >= byte.MinValue and <= byte.MaxValue,
        (2, true) => value is >= short.MinValue and <= short.MaxValue,
        (2, false) => value is >= ushort.MinValue and <= ushort.MaxValue,
        (4, true) => value is >= int.MinValue and <= int.MaxValue,
        (4, false) => value is >= uint.MinValue and <= (long)uint.MaxValue,
        _ => true,
    };

    private static StackSlot EnumTryParseImpl(IntrinsicContext ctx, StackSlot[] a, bool ignoreCase) {
        // out T は ByRef (a[^1])。成功時は box せず生スロットを書く
        // (enum ローカルは i4/i8 スロット表現のため)
        var enumType = ResolveEnumType(ctx, null);
        var (size, signed) = EnumLayout(enumType);
        var outRef = a[^1].ObjectValue as VmByRef
            ?? throw new InvalidOperationException("Enum.TryParse の out 引数が参照ではありません。");
        var nameIndex = a.Length == 3 ? 1 : 0;
        var raw = ((a[nameIndex].ObjectValue as VmString)?.Value ?? "").Trim();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (string.Equals(literal.Name, raw, comparison)) {
            outRef.Write(RawSlot(literal.Bits, enumType));
                return StackSlot.OfInt32(1);
            }
        }
        var parsedOk = false;
        ulong parsedBits = 0;
        if (signed) {
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sparsed) &&
                FitsIn(sparsed, size, true)) {
                parsedOk = true;
                parsedBits = unchecked((ulong)sparsed);
            }
        } else {
            if (ulong.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var uparsed) &&
                FitsInU(uparsed, size)) {
                parsedOk = true;
                parsedBits = uparsed;
            }
        }
        if (parsedOk) {
            outRef.Write(RawSlot(parsedBits, enumType));
            return StackSlot.OfInt32(1);
        }
        outRef.Write(size == 8 ? StackSlot.OfInt64(0) : StackSlot.OfInt32(0));
        return StackSlot.OfInt32(0);
    }

    private static bool FitsInU(ulong value, int size) => size switch {
        1 => value <= byte.MaxValue,
        2 => value <= ushort.MaxValue,
        4 => value <= uint.MaxValue,
        _ => true,
    };

    private static bool EnumIsDefined(in StackSlot typeSlot, in StackSlot valueSlot) {
        if (typeSlot.ObjectValue is not VmRuntimeObject runtime || runtime.Target is not VmClassType enumType ||
            (!enumType.IsEnum && enumType.FullName != "System.Enum"))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var literals = EnumLiteralsOf(enumType);
        var (size, signed) = EnumLayout(enumType);
        if (valueSlot.ObjectValue is VmString name) {
            return literals.Any(l => l.Name == name.Value);
        }
        if (valueSlot.ObjectValue is VmBoxedValue box) {
            var bits = TruncateBits(RawBitsOf(box), size, signed);
            return literals.Any(l => TruncateBits(l.Bits, size, signed) == bits);
        }
        // 基底型の生値スロット (int 等が直接来る形)
        if (valueSlot.Kind is StackKind.Int32 or StackKind.Int64) {
            var bits = valueSlot.Kind == StackKind.Int64
                ? (ulong)valueSlot.Int64Value : (ulong)(uint)valueSlot.AsInt32;
            return literals.Any(l => TruncateBits(l.Bits, size, signed) == TruncateBits(bits, size, signed));
        }
        return false;
    }

    private static string? EnumGetName(in StackSlot typeSlot, in StackSlot valueSlot) {
        if (typeSlot.ObjectValue is not VmRuntimeObject runtime || runtime.Target is not VmClassType enumType ||
            (!enumType.IsEnum && enumType.FullName != "System.Enum"))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var (size, signed) = EnumLayout(enumType);
        ulong bits;
        if (valueSlot.ObjectValue is VmBoxedValue box)
            bits = TruncateBits(RawBitsOf(box), size, signed);
        else if (valueSlot.Kind is StackKind.Int32 or StackKind.Int64)
            bits = valueSlot.Kind == StackKind.Int64
                ? TruncateBits((ulong)valueSlot.Int64Value, size, signed)
                : TruncateBits((ulong)(uint)valueSlot.AsInt32, size, signed);
        else
            throw new UnhandledGuestException("System.ArgumentException", null);
        foreach (var literal in EnumLiteralsOf(enumType)) {
            if (TruncateBits(literal.Bits, size, signed) == bits)
                return literal.Name;
        }
        return null;
    }

    /// <summary>ECMA-335 の CorElementType コード (enum の許容基底型のみ)。</summary>
    private const int CorElementBoolean = 0x02, CorElementChar = 0x03, CorElementI1 = 0x04,
        CorElementU1 = 0x05, CorElementI2 = 0x06, CorElementU2 = 0x07, CorElementI4 = 0x08,
        CorElementU4 = 0x09, CorElementI8 = 0x0a, CorElementU8 = 0x0b;

    private static int CorElementTypeCodeOf(in StackSlot receiver) {
        var underlying = EnumUnderlyingType(receiver);
        return underlying?.FullName switch {
            "System.Boolean" => CorElementBoolean,
            "System.Char" => CorElementChar,
            "System.SByte" => CorElementI1,
            "System.Byte" => CorElementU1,
            "System.Int16" => CorElementI2,
            "System.UInt16" => CorElementU2,
            "System.Int32" => CorElementI4,
            "System.UInt32" => CorElementU4,
            "System.Int64" => CorElementI8,
            "System.UInt64" => CorElementU8,
            // value__ フィールドが未解決の異常画像はスロット幅でフォールバック
            _ => SlotIs64(receiver) ? CorElementI8 : CorElementI4,
        };
    }

    private static bool SlotIs64(in StackSlot slot) {
        var value = slot.ObjectValue is VmBoxedValue box ? box.Fields[0] : default(StackSlot?);
        return value is { Kind: StackKind.Int64 };
    }

    private static StackSlot GetEnumValueImpl(IntrinsicContext ctx, in StackSlot receiver) {
        var box = receiver.ObjectValue as VmBoxedValue ??
            throw new UnhandledGuestException("System.InvalidCastException", null);
        var underlying = EnumUnderlyingType(receiver) ??
            ctx.Types.FindIntrinsicType(SlotIs64(receiver) ? "System.Int64" : "System.Int32") ??
            throw new InvalidOperationException("enum の基底型が解決できません (value__ フィールドが解決不能な画像です)。");
        return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(underlying, [box.Fields[0]])));
    }

    /// <summary>enum box の基底型 (value__ フィールドの型) を辿る。</summary>
    private static VmType? EnumUnderlyingType(in StackSlot receiver) {
        if (receiver.ObjectValue is not VmBoxedValue box)
            return null;
        for (var t = box.Type; t is not null; t = t.BaseType) {
            var field = t.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral);
            if (field?.FieldType is { } fieldType)
                return fieldType;
        }
        return null;
    }
    // ---- System.Math (拡張 overload / JIT intrinsic 面) ----

    /// <summary>Math の拡張 overload 面。
    /// 本家 IL は double 丸め機構 (ModF InternalCall / fixed バッファ) と JIT intrinsic
    /// (BigMul / FusedMultiplyAdd 等) で構成されるため VM の表現境界。ホストの同一意味論
    /// へ委譲し、結果は VM スロットに正規化する。基本算術面 (Abs / Sqrt 等) は既存の
    /// 一般経路 (② IL 実行 / ③ legacy) のまま。</summary>
    private static void RegisterMathBindings(IntrinsicRegistry r) {
        const string T = "System.Math";
        const string Double = "System.Double";
        const string Midpoint = "System.MidpointRounding";

        // ModF(double, out double&): 丸め核 (RoundNumber 内部 IL が呼ぶ InternalCall 面)。
        // 戻り = 整数部、out 参照先 = 小数部 (本家と同一の呼び出し規約)。
        // 本家筐体は QCall 相当の double* 署名で呼ぶため両形状を登録する
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ModF", "System.Double", ["System.Double", "System.Double&"]),
            static (_, a) => ModFImpl(a), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ModF", "System.Double", ["System.Double", "System.Double*"]),
            static (_, a) => ModFImpl(a), BindingOrigin.InternalCall);

        // Round 系 overload (4 形状)。Math.Round(double, int) の既定中点規約 = AwayFromZero。
        // 第 2 引数が MidpointRounding か digits かはバインドキーのパラメータ型名 (IntrinsicContext) で判別
        static StackSlot RoundFaces(IntrinsicContext ctx, StackSlot[] a) => a.Length switch {
            1 => StackSlot.OfFloat(Math.Round(a[0].DoubleValue)),
            2 when ctx.ParamAt(1) == "System.MidpointRounding" =>
                StackSlot.OfFloat(Math.Round(a[0].DoubleValue, (System.MidpointRounding)a[1].AsInt32)),
            2 => StackSlot.OfFloat(Math.Round(a[0].DoubleValue, a[1].AsInt32)),
            _ => StackSlot.OfFloat(Math.Round(a[0].DoubleValue, a[1].AsInt32,
                (System.MidpointRounding)a[2].AsInt32)),
        };
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double"]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", "System.Int32"]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", Midpoint]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", "System.Int32", Midpoint]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        // Truncate は managed IL が TruncateNative (InternalCall) を辿るため直接ホスト面で受ける
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Truncate", "System.Double", ["System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.Truncate(a[0].DoubleValue)), BindingOrigin.Managed);

        // JIT intrinsic / 内部面の残り (ホスト同一意味論)
        r.RegisterBinding(BindingKey.Static(T, "BigMul", ["System.Int32", "System.Int32"]),
            static (_, a) => StackSlot.OfInt64(Math.BigMul(a[0].AsInt32, a[1].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "ILogB", ["System.Double"]),
            static (_, a) => StackSlot.OfInt32(Math.ILogB(a[0].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "ScaleB", ["System.Double", "System.Int32"]),
            static (_, a) => StackSlot.OfFloat(Math.ScaleB(a[0].DoubleValue, a[1].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Sign", ["System.Double"]),
            static (_, a) => StackSlot.OfInt32(Math.Sign(a[0].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "CopySign", ["System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.CopySign(a[0].DoubleValue, a[1].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "MaxMagnitude", ["System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.MaxMagnitude(a[0].DoubleValue, a[1].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "FusedMultiplyAdd", ["System.Double", "System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.FusedMultiplyAdd(a[0].DoubleValue, a[1].DoubleValue, a[2].DoubleValue)), BindingOrigin.Managed);
        // DivRem (左, 右) → (商, 余り) ValueTuple`2 面 (戻り = VM 構造体値)
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "DivRem", "System.ValueTuple`2", ["System.Int32", "System.Int32"]),
            static (ctx, a) => Tuple2Int32(ctx, a[0].AsInt32, a[1].AsInt32), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "DivRem", "System.ValueTuple`2", ["System.Int64", "System.Int64"]),
            static (ctx, a) => Tuple2Int64(ctx, a[0].Int64Value, a[1].Int64Value), BindingOrigin.Managed);
    }

    private static StackSlot ModFImpl(StackSlot[] a) {
        var intPart = Math.Truncate(a[0].DoubleValue);
        if (a[1].ObjectValue is VmByRef byref)
            byref.Write(StackSlot.OfFloat(a[0].DoubleValue - intPart));
        return StackSlot.OfFloat(intPart);
    }

    /// <summary>Math.DivRem の (商, 余り) 戻り面。System.ValueTuple`2&lt;System.Int32,System.Int32&gt; の
    /// 構造体値 (Fields = Item1 / Item2 スロット) を構築して返す。</summary>
    private static StackSlot Tuple2Int32(IntrinsicContext ctx, int item1, int item2) {
        var (def, args) = Tuple2Type(ctx, "System.Int32")
            ?? throw new InvalidOperationException("System.ValueTuple`2 (CoreLib 実型) がロードされていません。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [args, args] };
        var fields = new StackSlot[2];
        fields[0] = StackSlot.OfInt32(item1);
        fields[1] = StackSlot.OfInt32(item2);
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }

    /// <summary>Math.DivRem(long,long) の (商, 余り) 戻り面 (i8 スロット)。</summary>
    private static StackSlot Tuple2Int64(IntrinsicContext ctx, long item1, long item2) {
        var (def, args) = Tuple2Type(ctx, "System.Int64")
            ?? throw new InvalidOperationException("System.ValueTuple`2 (CoreLib 実型) がロードされていません。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [args, args] };
        var fields = new StackSlot[2];
        fields[0] = StackSlot.OfInt64(item1);
        fields[1] = StackSlot.OfInt64(item2);
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }

    /// <summary>ValueTuple`2 の定義型と実引数型 (確認。値型統合済み型) を解決する。</summary>
    private static (VmClassType Def, VmType Arg)? Tuple2Type(IntrinsicContext ctx, string argTypeName) {
        if (ctx.Types.FindTypeByFullName("System.ValueTuple`2") is not VmClassType def)
            return null;
        var arg = ctx.Types.FindTypeByFullName(argTypeName)
            ?? ctx.Types.ResolveWellKnownType(argTypeName)
            ?? throw new InvalidOperationException($"{argTypeName} が解決できません。");
        return (def, arg);
    }

    // ---- System.SR (CoreLib 内部リソース文字列: 例外既定文言の culture インフラ) ----

    // CoreLib の実 IL は例外生成時に SR.Overflow_Int32 等のリソース getter を辿る
    // (GetResourceString → ResourceManager → culture 機構)。VM では例外文言の culture 機構は
    // スコープ外 (不変カルチャ規約) のため、ホストが動いている同一 CoreLib から同一キーの
    // 資源文字列を取得して委譲する (ゲストに文字列として渡すだけ = I/O なし)

    private static void RegisterSystemSr(IntrinsicRegistry r) {
        const string T = "System.SR";
        r.RegisterBinding(BindingKey.Static(T, "GetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "InternalGetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        // リソースキー直返しモードのスイッチ値 (AppContext 機構には依存しない = false 固定)
        r.RegisterBinding(BindingKey.Static(T, "UsingResourceKeys"),
            static (_, _) => StackSlot.OfInt32(0),
            BindingOrigin.Managed);
    }

    private static readonly Func<string, string?> HostCoreLibResource = CreateHostResourceLookup();

    private static Func<string, string?> CreateHostResourceLookup() {
        var sr = typeof(object).Assembly.GetType("System.SR", throwOnError: false);
        var method = sr?.GetMethod("GetResourceString",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (method is null)
            return _ => null; // CoreLib 未配置環境ではキー直返し (UsingResourceKeys 相当) にフォールバック
        return key => {
            try {
                return method.Invoke(null, [key]) as string;
            } catch {
                return null;
            }
        };
    }

    private static StackSlot ResourceString(IntrinsicContext ctx, StackSlot[] a) {
        var key = a[0].ObjectValue as VmString ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
        var text = HostCoreLibResource(key.Value) ?? key.Value;
        return StackSlot.OfObject(ctx.MakeString(text));
    }

    // ---- System.ArgumentOutOfRangeException.ThrowIfNegative (ジェネリック検証面) ----

    /// <summary>検証ヘルパー ThrowIfNegative&lt;T&gt;(T[, string?]) の同等意味論。
    /// 本家 IL は constrained T + INumberBase&lt;T&gt;.IsNegative (static abstract) を辿るが、
    /// VM には static abstract ディスパッチ機構がなく、抽象宣言に着地して fail-closed になる。
    /// 検証の観測意味論 (負なら ArgumentOutOfRangeException) を直接提供する。
    /// -0.0 の符号ビットも CLR どおり負と判定する (単なる &lt; 0 比較では再現できない)。
    /// .NET 10 の既知署名は (T, string?) の 1 形状 (1 引数版は存在しない)。</summary>
    private static void RegisterThrowHelpers(IntrinsicRegistry r) {
        const string T = "System.ArgumentOutOfRangeException";
        r.RegisterBinding(BindingKey.Static(T, "ThrowIfNegative", "!!0", "System.String"),
            static (_, a) => {
                if (IsNegativeValue(a[0]))
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                return null;
            },
            BindingOrigin.InternalCall);
    }

    private static bool IsNegativeValue(in StackSlot slot) => slot.Kind switch {
        StackKind.Int32 => slot.Int64Value < 0,
        StackKind.Int64 => slot.Int64Value < 0,
        StackKind.NativeInt => slot.Int64Value < 0,
        StackKind.Float => BitConverter.DoubleToInt64Bits(slot.DoubleValue) < 0,
        _ => throw new InvalidOperationException(
            $"ThrowIfNegative の引数型 ({slot.Kind}) は数値検証に対応していません。"),
    };

    // ---- System.Array (コア面: 全 overload がここへ集約される) ----

    /// <summary>Array のコア面 (Sort 5 引数 / Reverse 3 引数 / IndexOf 4 引数 / Copy 5 引数)。
    /// 本家は全 overload をここへ集約する (List.Sort / Array.Sort(T[]) 等の実 IL が辿る先)。
    /// 実 IL は introsort / 比較子生成 (CreateArraySortHelper) / MethodTable 内部表現
    /// (CopyImpl) で構成され VM 表現境界のため、同一意味論を直接提供する:
    /// 既定順序はプリミティブ数値 + 文字列 (不変カルチャ規約) のホスト比較。カスタム
    /// IComparer がある面はゲスト委譲機構が無いため fail-closed (ホスト例外)。
    /// 多次元配列は RankException (SZArray のみ対応)。</summary>
    private static void RegisterArrayBindings(IntrinsicRegistry r) {
        const string T = "System.Array";
        r.RegisterBinding(BindingKey.Static(T, "Sort",
                "System.Array", "System.Array", "System.Int32", "System.Int32", "System.Collections.IComparer"),
            static (_, a) => { SortImpl(a[0], a[1], a[2].AsInt32, a[3].AsInt32, a[4]); return null; }, BindingOrigin.Managed);
        // ジェネリック Sort (List.Sort / Array.Sort(T[]) 等が辿る実面。比較子生成の
        // ランタイム内部 (CreateArraySortHelper) を迂回し既定順序を直接提供する)
        r.RegisterBinding(BindingKey.Static(T, "Sort", "!!0[]"),
            static (_, a) => { SortImpl(a[0], StackSlot.Null, 0, RequireSzArray(a[0], "Array.Sort").Length, StackSlot.Null); return null; }, BindingOrigin.Managed);
        // Sort(T[], int, int, IComparer<T>): 呼出元の文脈で型変数の綴り (!!0 / !0) が
        // 変わるため両形を登録する (実引数は実行時に判別する)
        foreach (var comparerParam in new[] {
            "System.Collections.Generic.IComparer`1<!!0>",
            "System.Collections.Generic.IComparer`1<!0>",
        })
            r.RegisterBinding(BindingKey.Static(T, "Sort", "!!0[]", "System.Int32", "System.Int32", comparerParam),
                static (_, a) => { SortImpl(a[0], StackSlot.Null, a[1].AsInt32, a[2].AsInt32, a[3]); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Reverse",
                "System.Array", "System.Int32", "System.Int32"),
            static (_, a) => { ReverseImpl(a); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "IndexOf",
                "System.Array", "System.Object", "System.Int32", "System.Int32"),
            static (_, a) => StackSlot.OfInt32(IndexOfImpl(a)), BindingOrigin.Managed);
        // ジェネリック IndexOf<T>(T[], T, int, int) (List<T>.Contains/IndexOf 等が辿る実面。
        // SpanHelpers の SIMD/static-abstract 依存を迂回し同一意味論を直接提供する)
        r.RegisterBinding(BindingKey.Static(T, "IndexOf",
                "!!0[]", "!!0", "System.Int32", "System.Int32"),
            static (_, a) => StackSlot.OfInt32(IndexOfImpl(a)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Copy",
                "System.Array", "System.Int32", "System.Array", "System.Int32", "System.Int32"),
            static (_, a) => { CopyImpl(a); return null; }, BindingOrigin.Managed);
    }

    private static VmArray RequireSzArray(in StackSlot slot, string face) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is null)
            throw new UnhandledGuestException("System.ArgumentNullException", null);
        return slot.ObjectValue is VmArray array
            ? array
            : throw new UnhandledGuestException("System.RankException",
                $"{face} は 1 次元配列 (SZArray) のみ対応しています。");
    }

    private static void CheckRange(int index, int length, int arrayLength, string face) {
        if (index < 0 || length < 0)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
        if (index + length > arrayLength)
            throw new UnhandledGuestException("System.ArgumentException",
                $"{face} の範囲 (index={index}, length={length}) が配列長 {arrayLength} を超えています。");
    }

    private static void SortImpl(in StackSlot keysSlot, in StackSlot itemsSlot, int index, int length, in StackSlot comparerSlot) {
        var keys = RequireSzArray(keysSlot, "Array.Sort");
        VmArray? items = itemsSlot.ObjectValue is null ? null : RequireSzArray(itemsSlot, "Array.Sort");
        CheckRange(index, length, keys.Length, "Array.Sort");
        if (items is not null)
            CheckRange(index, length, items.Length, "Array.Sort");
        if (comparerSlot.ObjectValue is not null)
            throw new NotSupportedException(
                "Array.Sort のカスタム IComparer 面は未対応です (ゲスト比較子への委譲機構が無いため fail-closed)。");
        if (length <= 1)
            return;
        var elementType = keys.ArrayType.ElementType.FullName;
        var order = new int[length];
        for (var i = 0; i < length; i++)
            order[i] = i;
        var elements = keys.Elements;
        Array.Sort(order, (x, y) => CompareElement(elements[index + x], elements[index + y], elementType));
        var sortedKeys = new StackSlot[length];
        for (var i = 0; i < length; i++)
            sortedKeys[i] = elements[index + order[i]];
        for (var i = 0; i < length; i++)
            elements[index + i] = sortedKeys[i];
        if (items is not null) {
            var itemElements = items.Elements;
            var sortedItems = new StackSlot[length];
            for (var i = 0; i < length; i++)
                sortedItems[i] = itemElements[index + order[i]];
            for (var i = 0; i < length; i++)
                itemElements[index + i] = sortedItems[i];
        }
    }

    private static void ReverseImpl(StackSlot[] a) {
        var array = RequireSzArray(a[0], "Array.Reverse");
        var index = a[1].AsInt32;
        var length = a[2].AsInt32;
        CheckRange(index, length, array.Length, "Array.Reverse");
        var elements = array.Elements;
        for (var i = 0; i < length / 2; i++)
            (elements[index + i], elements[index + length - 1 - i]) =
                (elements[index + length - 1 - i], elements[index + i]);
    }

    private static int IndexOfImpl(StackSlot[] a) {
        var array = RequireSzArray(a[0], "Array.IndexOf");
        var start = a[2].AsInt32;
        var count = a[3].AsInt32;
        CheckRange(start, count, array.Length, "Array.IndexOf");
        var elements = array.Elements;
        for (var i = 0; i < count; i++) {
            if (SlotsEqual(elements[start + i], a[1]))
                return start + i;
        }
        return -1;
    }

    private static void CopyImpl(StackSlot[] a) {
        var src = RequireSzArray(a[0], "Array.Copy");
        var srcIndex = a[1].AsInt32;
        var dst = RequireSzArray(a[2], "Array.Copy");
        var dstIndex = a[3].AsInt32;
        var length = a[4].AsInt32;
        CheckRange(srcIndex, length, src.Length, "Array.Copy");
        CheckRange(dstIndex, length, dst.Length, "Array.Copy");
        var srcName = src.ArrayType.ElementType.FullName;
        var dstName = dst.ArrayType.ElementType.FullName;
        if (VmPrimitiveTypes.IsSlotPrimitive(srcName) || VmPrimitiveTypes.IsSlotPrimitive(dstName)) {
            // プリミティブ配列は完全一致のみ (int[]→uint[] 等の同一幅も CLR は拒否する)
            if (!string.Equals(srcName, dstName, StringComparison.Ordinal))
                throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                    $"Array.Copy の要素型が一致しません ({srcName}[] → {dstName}[])。");
            Array.Copy(src.Elements, srcIndex, dst.Elements, dstIndex, length);
            return;
        }
        // 参照配列は要素ごとに共変検査 (不一致はその場で ArrayTypeMismatch)
        for (var i = 0; i < length; i++) {
            var value = src.Elements[srcIndex + i];
            if (value.ObjectValue is not null &&
                !TypeChecks.IsAssignableToType(value.ObjectValue, dst.ArrayType.ElementType))
                throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                    $"Array.Copy の要素 {i} を {dstName} に格納できません。");
        }
        Array.Copy(src.Elements, srcIndex, dst.Elements, dstIndex, length);
    }

    private static bool IsNullSlot(in StackSlot slot) =>
        slot.Kind == StackKind.Object && slot.ObjectValue is null;

    /// <summary>既定順序の要素比較 (CLR の既定比較子と同一順序)。
    /// 数値は符号どおり、浮動小数点は host CompareTo (NaN 順序を含む)、文字列は
    /// 不変カルチャ規約。box 化プリミティブは開いて比較する。構造体等は fail-closed。</summary>
    private static int CompareElement(in StackSlot x, in StackSlot y, string elementType) {
        var (xv, xn) = UnwrapForCompare(x, elementType);
        var (yv, yn) = UnwrapForCompare(y, elementType);
        if (IsNullSlot(xv) || IsNullSlot(yv)) {
            if (IsNullSlot(xv) && IsNullSlot(yv))
                return 0;
            return IsNullSlot(xv) ? -1 : 1; // null は先頭 (CLR 規約)
        }
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys)
            return string.Compare(xs.Value, ys.Value, StringComparison.InvariantCulture);
        if (xv.Kind is StackKind.Object or StackKind.ValueType or StackKind.ByRef ||
            yv.Kind is StackKind.Object or StackKind.ValueType or StackKind.ByRef)
            throw new InvalidOperationException(
                $"Array.Sort の要素 ({SlotKindName(xv)}, {SlotKindName(yv)}) は既定順序に対応していません。");
        var name = xn ?? yn ?? elementType;
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue.CompareTo(yv.DoubleValue);
        var signed = name is not ("System.UInt32" or "System.UInt64");
        if (!signed)
            return ((ulong)xv.Int64Value).CompareTo((ulong)yv.Int64Value);
        return xv.Int64Value.CompareTo(yv.Int64Value);
    }

    private static (StackSlot Slot, string? TypeName) UnwrapForCompare(in StackSlot slot, string elementType) {
        if (slot.ObjectValue is VmBoxedValue box)
            return (box.Fields[0], box.Type.FullName);
        return (slot, elementType);
    }

    private static bool SlotsEqual(in StackSlot x, in StackSlot y) {
        var (xv, _) = UnwrapForCompare(x, "");
        var (yv, _) = UnwrapForCompare(y, "");
        if (IsNullSlot(xv) || IsNullSlot(yv))
            return IsNullSlot(xv) && IsNullSlot(yv);
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys)
            return string.Equals(xs.Value, ys.Value, StringComparison.Ordinal);
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue.Equals(yv.DoubleValue);
        if (xv.Kind == StackKind.Object || yv.Kind == StackKind.Object)
            return ReferenceEquals(xv.ObjectValue, yv.ObjectValue);
        return xv.Int64Value == yv.Int64Value;
    }

    private static string SlotKindName(in StackSlot slot) =>
        slot.Kind + (slot.ObjectValue is null ? "" : ":" + slot.ObjectValue.GetType().Name);

    /// <summary>== 演算子の観測意味論 (IEqualityOperators.op_Equality 用)。
    /// box 化値は開いて比較する。浮動小数点は == (NaN ペアは false)、文字列は ordinal。</summary>
    private static bool OperatorEqual(in StackSlot x, in StackSlot y) {
        var (xv, _) = UnwrapForCompare(x, "");
        var (yv, _) = UnwrapForCompare(y, "");
        if (IsNullSlot(xv) || IsNullSlot(yv))
            return IsNullSlot(xv) && IsNullSlot(yv);
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys)
            return string.Equals(xs.Value, ys.Value, StringComparison.Ordinal);
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue == yv.DoubleValue;
        if (xv.Kind == StackKind.Object || yv.Kind == StackKind.Object ||
            xv.Kind == StackKind.ValueType || yv.Kind == StackKind.ValueType ||
            xv.Kind == StackKind.ByRef || yv.Kind == StackKind.ByRef)
            return ReferenceEquals(xv.ObjectValue, yv.ObjectValue);
        return xv.Int64Value == yv.Int64Value;
    }

    private static bool HasFlagImpl(StackSlot[] a) {
        var thisBox = a[0].ObjectValue as VmBoxedValue;
        var flagBox = a[1].ObjectValue as VmBoxedValue;
        // CLR の型検査 (GetType() != flag.GetType() で ArgumentException) に対応。
        // ボックス化されていない面の呼び出しは形状違反として同様に扱う
        if (thisBox is null || flagBox is null ||
            !string.Equals(thisBox.Type.FullName, flagBox.Type.FullName, StringComparison.Ordinal))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var thisBits = RawBits(thisBox);
        var flagBits = RawBits(flagBox);
        return (thisBits & flagBits) == flagBits;
    }

    private static ulong RawBits(VmBoxedValue box) =>
        box.Fields[0].Kind switch {
            StackKind.Int64 => (ulong)box.Fields[0].Int64Value,
            _ => (ulong)box.Fields[0].AsInt32,
        };

    // ---- System.Object (CoreLib managed IL が内部表現依存面を辿る面の代替) ----

    private static void RegisterObject(IntrinsicRegistry r) {
        // public extern Type Object.GetType()
        // CoreLib の managed IL は GetMethodTable → MethodTable::AuxiliaryData という
        // ランタイム内部表現への直接アクセスを辿るため、VM では実行時型ファサード
        // (VmRuntimeObject) を返すバインドで優先提供する (① ランタイムバインドが ② IL より先)
        r.RegisterBinding(BindingKey.Instance("System.Object", "GetType"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.Managed);
        // public virtual int Object.GetHashCode()
        // C5.5 Wave 4 で監査確定: 本体 IL (RuntimeHelpers.GetHashCode 呼び) は
        // TryGetHashCode (InternalCall) → GetHashCodeSlow (QCall ネイティブ =
        // ObjectNative_GetHashCodeSlow) を辿り identity hash の発行自体がランタイム内部。
        // P/Invoke 代替の原則で identity hash リーフとして ① バインドで優先提供する
        // (Object は IlPreferred に上がったため ① が無いと ② がこの面を辿ってしまう)
        r.RegisterBinding(BindingKey.Instance("System.Object", "GetHashCode"),
            static (ctx, a) => StackSlot.OfInt32(ctx.IdentityHash(a[0].ObjectValue)),
            BindingOrigin.Managed);

        // ---- System.Type の runtime-representation 面 (culture 機構 / Enum 書式 IL が辿る Type 面) ----
        // 本家 Type の get_* 面は RuntimeType 内部表現 (IL なし = ランタイム intrinsic) を辿るため
        // ② IL 実行に落ちず、VM 型モデルから同一要素を提示する (監査表 (c) runtime-representation)
        // get_BaseType: VM 型モデル (VmType.BaseType) の判定で返す (System.Object の親 = null)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_BaseType"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt && rt.Target.BaseType is { } baseType
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, baseType)
                    : null, // BaseType 無し (System.Object 等) = CLR と同一の null
            BindingOrigin.InternalCall);
        // Type::get_TypeHandle: 本家は RuntimeTypeHandle (runtime representation)。
        // VM 型への参照 (VmTypeHandle) を返す (GetCultureInfo 機構 IL 内の静的リテラル type handle 面)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_TypeHandle"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt
                    ? StackSlot.OfObject(ctx.Heap.Allocate(new VmTypeHandle { Target = rt.Target }))
                    : throw new InvalidOperationException("Type::get_TypeHandle の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        // Type 面の runtime-representation 補完 (culture 機構 IL / Dictionary cache ctor が辿る判定面):
        // IsValueTypeImpl / IsSubclassOf は RuntimeType 内部表現 (IL なし) を辿るため VM 型モデルで
        // 同一判定を提供する。IsValueType = IsValueTypeImpl と同値 (プリミティブは ValueType 派生)
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsValueTypeImpl"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt
                    ? StackSlot.OfInt32(rt.Target.IsValueType ? 1 : 0)
                    : throw new InvalidOperationException("Type::IsValueTypeImpl の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_IsValueType"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt2
                    ? StackSlot.OfInt32(rt2.Target.IsValueType ? 1 : 0)
                    : throw new InvalidOperationException("Type::get_IsValueType の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsSubclassOf", "System.Type"),
            static (ctx, a) => {
                var self = a[0].ObjectValue is VmRuntimeObject s ? s.Target : null;
                var other = a[1].ObjectValue is VmRuntimeObject o ? o.Target : null;
                if (self is null || other is null)
                    throw new InvalidOperationException("IsSubclassOf の引数が Type ファサードではありません。");
                for (VmType? t = self.BaseType; t is not null;) {
                    if (t.FullName == other.FullName)
                        return StackSlot.OfInt32(1);
                    t = t.BaseType;
                }
                return StackSlot.OfInt32(0);
            },
            BindingOrigin.InternalCall);
        // Type::IsAssignableFrom: 本家は RuntimeType 内部表現を辿るため VM 型モデルで提供する。
        // null 引数は false (CLR 規約)。変性込みの構築型判定は VmType 側に委譲する
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsAssignableFrom", "System.Type"),
            static (_, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject self)
                    throw new InvalidOperationException("Type::IsAssignableFrom の this が Type ファサードではありません。");
                if (a[1].ObjectValue is not VmRuntimeObject other)
                    return StackSlot.OfInt32(0);
                return StackSlot.OfInt32(other.Target.IsAssignableTo(self.Target) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // Type::get_IsInterface: VM 型モデルの Flags 判定 (IEnumerable`1 等の構築型は定義側を見る)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_IsInterface"),
            static (_, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? StackSlot.OfInt32(rt.Target.IsInterface ? 1 : 0)
                : throw new InvalidOperationException("Type::get_IsInterface の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethod", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, name.Value, null)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethod", "System.String", "System.Type[]"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, name.Value, a[2])
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetConstructor", "System.Type[]"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, ".ctor", a[1])
                : throw new InvalidOperationException("Type::GetConstructor の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethods"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeMethodArray(ctx, rt.Target)
                : throw new InvalidOperationException("Type::GetMethods の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetField", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeFieldObject(ctx, rt.Target, name.Value)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetProperty", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakePropertyObject(ctx, rt.Target, name.Value)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetFields"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeFieldArray(ctx, rt.Target)
                : throw new InvalidOperationException("Type::GetFields の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        // Enum書式などで Type.GetEnumUnderlyingType が本家 GetFields 実装へ降りるが、
        // RuntimeType のフィールド反映は VM の Type ファサードに存在しないため、
        // enum 定義の value__ フィールドから基底型を直接返す。
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetEnumUnderlyingType"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject { Target: VmClassType enumType } || !enumType.IsEnum)
                    throw new UnhandledGuestException("System.ArgumentException", null);
                var underlying = enumType.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral)?.FieldType
                    ?? throw new InvalidOperationException($"enum {enumType.FullName} の基底型が解決できません。");
                return DefaultIntrinsics.MakeRuntimeObject(ctx, underlying);
            },
            BindingOrigin.InternalCall);
        // Type::GetType(string): 型名から VM 型を解決するランタイム面。
        // アセンブリ修飾名は指定アセンブリ内で、単純名は trusted CoreLib を先に
        // (fake BCL 名の混入防止)・次に全ロード画像で解決する。未解決は null
        // (throwOnError なし overload の CLR 規約。呼出側の !. は NRE になる)
        r.RegisterBinding(BindingKey.Static("System.Type", "GetType", "System.String"),
            static (ctx, a) => {
                var name = (a[0].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                return ResolveTypeName(ctx, name) is { } type
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, type)
                    : StackSlot.Null;
            },
            BindingOrigin.InternalCall);
        // IEqualityOperators<TSelf,TOther,TResult>.op_Equality (static abstract)。
        // GenericEqualityComparer<T>.Equals 等が constrained 呼出で辿る。本家 IL は
        // ダミー抽象に着地するため、観測意味論 (==) を直接提供する。NaN は == 規約
        // (Equals と異なり NaN ペアは false) どおり比較する
        r.RegisterBinding(BindingKey.Static("System.Numerics.IEqualityOperators`3", "op_Equality", "!0", "!1"),
            static (_, a) => StackSlot.OfInt32(OperatorEqual(a[0], a[1]) ? 1 : 0),
            BindingOrigin.InternalCall);
        // op_Inequality (== の否定。Guid 比較等が辿る)
        r.RegisterBinding(BindingKey.Static("System.Numerics.IEqualityOperators`3", "op_Inequality", "!0", "!1"),
            static (_, a) => StackSlot.OfInt32(OperatorEqual(a[0], a[1]) ? 0 : 1),
            BindingOrigin.InternalCall);
        // static abstract char IUtfChar<T>.CastFrom(T)  ([Intrinsic]: 実 IL はダミー throw。
        // String/span IL が T(char)→char の面を経由するため、char 系 T の値を i4 スロットで透過)
        // .NET 10 の既知署名 (IUtfChar<TSelf>.CastFrom の 5 overload) を列挙する
        foreach (var castFromParam in new[] {
            "System.Byte", "System.Char", "System.Int32", "System.UInt32", "System.UInt64",
        })
            r.RegisterBinding(BindingKey.Static("System.IUtfChar`1", "CastFrom", castFromParam),
                static (_, a) => StackSlot.OfInt32(a[0].AsInt32),
                BindingOrigin.InternalCall);
        // static uint IUtfChar<TSelf>.CastToUInt32(TSelf value) ([Intrinsic]:
        // Guid 16 進解析等が T(char) → uint の再解釈に使う。クラス変数 (!0) の開いた
        // キーで 1 件登録し、実引数は実行時の具体名で判別する)
        r.RegisterBinding(BindingKey.Static("System.IUtfChar`1", "CastToUInt32", "!0"),
            static (_, a) => StackSlot.OfInt32(unchecked((int)(a[0].Kind == StackKind.Int64
                ? (uint)a[0].Int64Value : (uint)a[0].AsInt32))),
            BindingOrigin.InternalCall);
    }

    // ---- System.Collections.Generic.EqualityComparer<T> (等値比較子面) ----

    /// <summary>EqualityComparer&lt;T&gt;.get_Default の同等意味論。
    /// 本家は .cctor → ComparerHelpers.CreateDefaultEqualityComparer (RuntimeType 内部表現・
    /// MakeGenericType・未初期化実体化の連鎖) を辿るため、VM 型モデルで直接振り分ける:
    /// string → StringEqualityComparer、enum → EnumEqualityComparer&lt;T&gt;、
    /// Nullable → NullableEqualityComparer&lt;T&gt;、その他 → GenericEqualityComparer&lt;T&gt;
    /// (本家と同一の振分順序)。実体は公開無引数 .ctor を NewInstanceHook で実行する。
    /// T はクラス型実引数 (!0) で判別する (値パラメータ 0 個のため)。</summary>
    private static void RegisterEqualityComparer(IntrinsicRegistry r) {
        r.RegisterBinding(BindingKey.Static("System.Collections.Generic.EqualityComparer`1", "get_Default"),
            static (ctx, a) => {
                _ = a;
                var tName = ctx.ClassTypeArgAt(0);
                if (string.IsNullOrEmpty(tName) || tName is "!!0" or "!0")
                    throw new InvalidOperationException(
                        "EqualityComparer<T>.get_Default の型引数 T を判別できませんでした。");
                var t = FindAnyType(ctx, tName);
                // 本家 ComparerHelpers.CreateDefaultEqualityComparer と同一の振分順序:
                // string → IEquatable<T> 実装 → Nullable<T> → enum → 既定
                string comparerBase;
                if (tName == "System.String") {
                    comparerBase = "System.Collections.Generic.StringEqualityComparer";
                } else if (t is not null && IsEquatableImpl(ctx, t)) {
                    comparerBase = "System.Collections.Generic.GenericEqualityComparer`1";
                } else if (t is VmConstructedType nullable &&
                           nullable.Definition.FullName == "System.Nullable`1") {
                    comparerBase = "System.Collections.Generic.NullableEqualityComparer`1";
                } else if (t is not null && t.IsEnum) {
                    comparerBase = "System.Collections.Generic.EnumEqualityComparer`1";
                } else {
                    comparerBase = "System.Collections.Generic.GenericEqualityComparer`1";
                }
                var def = FindAnyType(ctx, comparerBase) as VmClassType
                    ?? throw new InvalidOperationException(
                        $"等値比較子 {comparerBase} が CoreLib に見つかりません。");
                GenericContext? classContext = null;
                VmType[] typeArgs = [];
                if (def.GenericParamCount > 0) {
                    if (t is null)
                        throw new InvalidOperationException(
                            $"等値比較子の型引数 {tName} を解決できません。");
                    typeArgs = [t];
                    classContext = new GenericContext { ClassArgs = typeArgs };
                }
                var ctor = def.Methods.FirstOrDefault(m =>
                    m.Name == ".ctor" && !m.IsStatic && m.Signature.ParamTypes.Length == 0 && m.Body is not null);
                if (ctor is null)
                    throw new InvalidOperationException($"等値比較子 {comparerBase} に無引数 .ctor がありません。");
                var hook = ctx.NewInstanceHook
                    ?? throw new InvalidOperationException("等値比較子の生成フックが設定されていません。");
                var instance = typeArgs.Length > 0
                    ? hook(new VmConstructedType { Definition = def, TypeArguments = typeArgs }, ctor, [], classContext)
                    : hook(def, ctor, [], null);
                return StackSlot.OfObject(instance);
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>VM 型を全ロード画像から探す (trusted 優先)。ゲスト型の比較子等、
    /// 既知型に無い型引数の解決に使う。</summary>
    private static VmType? FindAnyType(IntrinsicContext ctx, string fullName) {
        try {
            if (ctx.Types.TryResolveTrustedUnifiedType(fullName) is { } trusted)
                return trusted;
        } catch {
        }
        var context = ctx.Types.Context;
        if (context is null) {
            try {
                return ctx.Types.FindTypeByFullName(fullName);
            } catch {
                return null;
            }
        }
        foreach (var loader in context.Loaders) {
            try {
                if (loader.FindTypeByFullName(fullName) is { } found)
                    return found;
            } catch {
                continue;
            }
        }
        try {
            return ctx.Types.ResolveWellKnownType(fullName);
        } catch {
            return null;
        }
    }

    private static bool IsEquatableImpl(IntrinsicContext ctx, VmType t) {
        var equatableDef = FindAnyType(ctx, "System.IEquatable`1");
        if (equatableDef is null)
            return false;
        var constructed = new VmConstructedType { Definition = equatableDef, TypeArguments = [t] };
        try {
            return t.IsAssignableTo(constructed);
        } catch {
            return false;
        }
    }

    // ---- System.Convert (Base64) / System.BitConverter (バイト列変換面) ----

    /// <summary>Base64 / BitConverter の純粋変換面。
    /// 本家 IL は byte* 生ポインタ + Span 内部 (ConvertToBase64Array 等) で構成され
    /// VM のスロット表現に落ちないため、ホストの同一意味論へ委譲する。
    /// 不正入力の FormatException 分類も CLR と同一 (ゲスト例外化する)。</summary>
    private static void RegisterConvertBinary(IntrinsicRegistry r) {
        const string Convert = "System.Convert";
        r.RegisterBinding(BindingKey.Static(Convert, "ToBase64String", "System.Byte[]"),
            static (ctx, a) => {
                if (a[0].ObjectValue is null)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    System.Convert.ToBase64String(ctx.ReadByteArray(a[0]))));
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(Convert, "FromBase64String", "System.String"),
            static (ctx, a) => {
                var s = (a[0].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                try {
                    return StackSlot.OfObject(ctx.MakeByteArray(System.Convert.FromBase64String(s)));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        const string BitConverter = "System.BitConverter";
        static StackSlot GetBytes(IntrinsicContext ctx, byte[] data) =>
            StackSlot.OfObject(ctx.MakeByteArray(data));
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Boolean"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].AsInt32 != 0)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Char"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((char)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int16"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((short)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt16"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((ushort)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int32"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt32"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((uint)a[0].AsInt32)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Int64"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].Int64Value)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.UInt64"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((ulong)a[0].Int64Value)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Single"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes((float)a[0].DoubleValue)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "GetBytes", "System.Double"),
            static (ctx, a) => GetBytes(ctx, System.BitConverter.GetBytes(a[0].DoubleValue)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(BitConverter, "ToString", "System.Byte[]"),
            static (ctx, a) => {
                if (a[0].ObjectValue is null)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                return StackSlot.OfObject(ctx.MakeString(
                    System.BitConverter.ToString(ctx.ReadByteArray(a[0]))));
            },
            BindingOrigin.Managed);
    }

    // ---- System.Buffers.ArrayPool<T> (ValueStringBuilder 等の作業域プール) ----

    /// <summary>ArrayPool&lt;T&gt;.Shared / Rent / Return の同等意味論。
    /// 本家は TlsOverPerCoreLockedStacksArrayPool (スレッド局所スタック + EventSource 診断)
    /// で構成され、初回利用で ArrayPoolEventSource (ETW) の静的初期化まで辿るため
    /// VM 表現境界。プールは性能機構であり意味論は「要求長以上の新規配列」なので、
    /// 要求長どおりの新規確保で代替する (再利用しないため Return は no-op。
    /// 再利用の有無は観測不能)。Shared 実体は不透明なプレースホルダ (Rent/Return は
    /// バインドが状態を見ずに処理するため .ctor 不実行で足りる)。</summary>
    private static void RegisterArrayPool(IntrinsicRegistry r) {
        const string T = "System.Buffers.ArrayPool`1";
        r.RegisterBinding(BindingKey.Static(T, "get_Shared"),
            static (ctx, a) => {
                _ = a;
                var tName = ctx.ClassTypeArgAt(0);
                if (string.IsNullOrEmpty(tName) || tName is "!!0" or "!0")
                    throw new InvalidOperationException(
                        "ArrayPool<T>.Shared の型引数 T を判別できませんでした。");
                var t = FindAnyType(ctx, tName)
                    ?? throw new InvalidOperationException(
                        $"ArrayPool<T>.Shared の型引数 {tName} を解決できません。");
                var def = FindAnyType(ctx, T) as VmClassType
                    ?? throw new InvalidOperationException(
                        "System.Buffers.ArrayPool`1 がロードされていません。");
                var storage = new ObjectModel().CreateInstanceStorage(def, ctx.Types,
                    new GenericContext { ClassArgs = [t] });
                return StackSlot.OfObject(ctx.Heap.Allocate(
                    new VmClassInstance(def, storage, [t])));
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(T, "Rent", "System.Int32"),
            static (ctx, a) => {
                var length = a[1].AsInt32;
                if (length < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var element = RentElementType(a[0]);
                var elements = new StackSlot[length];
                var fill = element is not null && element.IsValueType
                    ? new ObjectModel().DefaultForType(element, ctx.Types)
                    : StackSlot.Null;
                for (var i = 0; i < length; i++)
                    elements[i] = fill;
                var arrayType = new VmArrayType {
                    ElementType = element ?? ctx.Types.ResolveWellKnownType("System.Object"),
                };
                try {
                    arrayType.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
                } catch {
                }
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(arrayType, elements)));
            },
            BindingOrigin.InternalCall);
        // クラスジェネリック面のため開いたキーはクラス変数 (!0) 形
        // (メソッド変数 !!0 ではない。TryInvokeOpenGenericBinding が !0 で再照合する)
        r.RegisterBinding(BindingKey.Instance(T, "Return", "!0[]", "System.Boolean"),
            static (_, _) => null,
            BindingOrigin.InternalCall);
        // .NET 10 追加面: Return(T[], int lengthToClear) (使用済み範囲のみクリアして返却。
        // ValueStringBuilder.Dispose が辿る。プール再利用しないため検証のみで no-op)。
        // instance 面のため a[0] はレシーバ (プール実体) で a[1]/a[2] が引数
        r.RegisterBinding(BindingKey.Instance(T, "Return", "!0[]", "System.Int32"),
            static (_, a) => {
                if (a[1].ObjectValue is not VmArray array)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                var lengthToClear = a[2].AsInt32;
                if (lengthToClear < 0 || lengthToClear > array.Length)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                return null;
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>Rent 呼出のレシーバ (ArrayPool&lt;T&gt; 実体) から要素型を取り出す。
    /// Shared プレースホルダは構築型の実引数を保持している。不明時は null。</summary>
    private static VmType? RentElementType(in StackSlot receiver) {
        if (receiver.ObjectValue is VmClassInstance instance && instance.TypeArguments.Length > 0)
            return instance.TypeArguments[0];
        return null;
    }

    // ---- System.Guid (解析・書式・比較面) ----

    /// <summary>Guid の Parse / TryParse / ToString / Equals / CompareTo / GetHashCode 面。
    /// 本家 IL は span 16 進解析 (IUtfChar / RawData 生ポインタ) + SIMD フォーマットで
    /// VM のスロット表現に落ちないため、ホストの同一意味論へ委譲する。
    /// Guid 書式 (N/D/B/P/X) に culture 要素は無く完全一致する。
    /// VM 内表現は CoreLib の Guid 構造体 (_a.._k の 11 フィールド) に正規化する
    /// (フィールド名で対応付け、宣言順依存にしない)。不正入力の FormatException 分類も同一。
    /// new Guid(string) の .ctor 実 IL は Parse へ委譲するためここで受ける。</summary>
    private static void RegisterGuid(IntrinsicRegistry r) {
        const string T = "System.Guid";
        static StackSlot MakeGuidStruct(IntrinsicContext ctx, Guid value) {
            var cls = FindCoreLibType(ctx, T)
                ?? throw new InvalidOperationException("System.Guid (CoreLib 実型) がロードされていません。");
            var bytes = value.ToByteArray();
            var fields = new StackSlot[InstanceFieldCount(cls)];
            var index = 0;
            foreach (var field in cls.Fields) {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                fields[index++] = field.Name switch {
                    "_a" => StackSlot.OfInt32(BitConverter.ToInt32(bytes, 0)),
                    "_b" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 4)),
                    "_c" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 6)),
                    "_d" => StackSlot.OfInt32(bytes[8]),
                    "_e" => StackSlot.OfInt32(bytes[9]),
                    "_f" => StackSlot.OfInt32(bytes[10]),
                    "_g" => StackSlot.OfInt32(bytes[11]),
                    "_h" => StackSlot.OfInt32(bytes[12]),
                    "_i" => StackSlot.OfInt32(bytes[13]),
                    "_j" => StackSlot.OfInt32(bytes[14]),
                    "_k" => StackSlot.OfInt32(bytes[15]),
                    _ => SlotDefaultZero(field),
                };
            }
            return StackSlot.OfValueType(new VmStructValue(cls, fields));
        }
        static Guid GuidOf(in StackSlot slot) {
            if (slot.ObjectValue is not VmStructValue sv || sv.StructType.FullName != T)
                throw new InvalidOperationException(
                    $"Guid 面の引数が CoreLib Guid 構造体値ではありません ({slot.Kind})。");
            var bytes = new byte[16];
            foreach (var (field, index) in FieldSlots(sv)) {
                var value = sv.Fields[index].AsInt32;
                switch (field) {
                    case "_a":
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), value);
                        break;
                    case "_b":
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4, 2), (short)value);
                        break;
                    case "_c":
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6, 2), (short)value);
                        break;
                    case "_d": bytes[8] = (byte)value; break;
                    case "_e": bytes[9] = (byte)value; break;
                    case "_f": bytes[10] = (byte)value; break;
                    case "_g": bytes[11] = (byte)value; break;
                    case "_h": bytes[12] = (byte)value; break;
                    case "_i": bytes[13] = (byte)value; break;
                    case "_j": bytes[14] = (byte)value; break;
                    case "_k": bytes[15] = (byte)value; break;
                }
            }
            return new Guid(bytes);
        }
        static string? S(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value;
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Parse", T, ["System.String"]),
            static (ctx, a) => {
                try {
                    return MakeGuidStruct(ctx, Guid.Parse(S(a[0])
                        ?? throw new UnhandledGuestException("System.ArgumentNullException", null)));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryParse", "System.Boolean",
                ["System.String", "System.Guid&"]),
            static (ctx, a) => {
                var ok = Guid.TryParse(S(a[0]) ?? "", out var value);
                if (a[1].ObjectValue is VmByRef byRef)
                    byRef.Write(MakeGuidStruct(ctx, value));
                else
                    throw new InvalidOperationException("Guid.TryParse の out 引数が参照ではありません。");
                return StackSlot.OfInt32(ok ? 1 : 0);
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String", []),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString())),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String", ["System.String"]),
            static (ctx, a) => {
                try {
                    return StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString(S(a[1]))));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "ToString", "System.String",
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => {
                try {
                    return StackSlot.OfObject(ctx.MakeString(GuidOf(a[0]).ToString(S(a[1]))));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "Equals", "System.Boolean", ["System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "CompareTo", "System.Int32", ["System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).CompareTo(GuidOf(a[1]))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(T, "GetHashCode", "System.Int32", []),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).GetHashCode()),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Equality", "System.Boolean", ["System.Guid", "System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "op_Inequality", "System.Boolean", ["System.Guid", "System.Guid"]),
            static (_, a) => StackSlot.OfInt32(GuidOf(a[0]).Equals(GuidOf(a[1])) ? 0 : 1),
            BindingOrigin.Managed);
    }

    /// <summary>構造体値の (フィールド名, インデックス) 列挙 (レイアウト順)。</summary>
    private static System.Collections.Generic.IEnumerable<(string Name, int Index)> FieldSlots(VmStructValue sv) {
        var definition = sv.StructType is VmConstructedType constructed
            ? constructed.Definition as VmClassType
            : sv.StructType as VmClassType;
        if (definition is null)
            yield break;
        var layout = new ObjectModel().GetLayout(definition);
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            if (layout.TryGetValue(field, out var index) && index < sv.Fields.Length)
                yield return (field.Name, index);
        }
    }

    // ---- System.Activator (インスタンス生成面) ----    /// <summary>Activator.CreateInstance(Type) の同等意味論。
    /// 本家 IL は RuntimeType.CreateInstanceDefaultCtor → ActivatorCache (ランタイム内部の
    /// メソッドテーブルキャッシュ) を辿るため VM 表現境界。値型は既定値の box、参照型は
    /// 公開無引数 .ctor を NewInstanceHook (ObjectEngine 実体。確保＋初期化＋IL 実行) で
    /// 実行する。抽象・インターフェース・.ctor 無しは CLR と同一分類のゲスト例外。</summary>
    private static void RegisterActivator(IntrinsicRegistry r) {
        r.RegisterBinding(BindingKey.Static("System.Activator", "CreateInstance", "System.Type"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject runtime)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                var target = runtime.Target;
                if (target.IsValueType) {
                    var def = new ObjectModel().DefaultForType(target, ctx.Types);
                    if (def.Kind == StackKind.ValueType && def.ObjectValue is VmStructValue sv)
                        return StackSlot.OfObject(ctx.Heap.Allocate(
                            new VmBoxedValue(sv.StructType is VmClassType cls ? cls : target, sv.Fields)));
                    return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(target, [def])));
                }
                if (target.IsInterface)
                    throw new UnhandledGuestException("System.MissingMethodException", null);
                VmClassType def2;
                GenericContext? classContext = null;
                if (target is VmConstructedType constructed) {
                    if (constructed.Definition is not VmClassType constructedDef)
                        throw new UnhandledGuestException("System.ArgumentException", null);
                    def2 = constructedDef;
                    classContext = new GenericContext { ClassArgs = constructed.TypeArguments };
                } else if (target is VmClassType simple) {
                    def2 = simple;
                } else {
                    // 配列は MissingMethod (CLR 規約: 要素数なしでは構築不可)、
                    // ポインタ/参照/ジェネリックパラメータは ArgumentException
                    throw new UnhandledGuestException(
                        target is VmArrayType or VmMultiDimArrayType
                            ? "System.MissingMethodException" : "System.ArgumentException", null);
                }
                if ((def2.Flags & 0x80) != 0)
                    throw new UnhandledGuestException("System.MemberAccessException", null);
                var ctor = def2.Methods.FirstOrDefault(m =>
                    m.Name == ".ctor" && !m.IsStatic && m.Signature.ParamTypes.Length == 0 &&
                    m.IsPublic && m.Body is not null);
                if (ctor is null)
                    throw new UnhandledGuestException("System.MissingMethodException", null);
                var hook = ctx.NewInstanceHook
                    ?? throw new InvalidOperationException("Activator のインスタンス生成フックが設定されていません。");
                return StackSlot.OfObject(hook(target, ctor, [], classContext));
            },
            BindingOrigin.Managed);
        // RuntimeTypeHandle.CreateInstanceForAnotherGenericParameter(Type, RuntimeType):
        // ArraySortHelper<T>.CreateArraySortHelper 等が比較子 (GenericComparer<T> 等) の
        // 未初期化実体を得る面。本家はランタイム内部だが、VM では構築型の確保 (ctor 不実行 =
        // GetUninitializedObject と同一) が同一意味論。stateless な比較子型が使う。
        r.RegisterBinding(BindingKey.Static("System.RuntimeTypeHandle", "CreateInstanceForAnotherGenericParameter",
                "System.RuntimeType", "System.RuntimeType"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject genericType)
                    throw new InvalidOperationException(
                        "CreateInstanceForAnotherGenericParameter の第 1 引数が Type ファサードではありません。");
                if (genericType.Target is not VmConstructedType constructed ||
                    constructed.Definition is not VmClassType def)
                    throw new InvalidOperationException(
                        "CreateInstanceForAnotherGenericParameter は構築ジェネリック型にのみ対応しています。");
                var storage = new ObjectModel().CreateInstanceStorage(def, ctx.Types,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
                return StackSlot.OfObject(ctx.Heap.Allocate(
                    new VmClassInstance(def, storage, constructed.TypeArguments)));
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>Type.GetType(string) の型名解決 (アセンブリ修飾・ジェネリック・配列/参照/ポインタ)。
    /// アセンブリ指定ありはその画像内のみ、単純名は trusted CoreLib を先に (fake BCL 防止)・
    /// 次に全ロード画像で探す。解釈不能・未解決は null (CLR の null 返却規約)。</summary>
    private static VmType? ResolveTypeName(IntrinsicContext ctx, string name) {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        SplitTopLevel(name, ',', out var typePart, out var assemblyPart);
        typePart = typePart.Trim();
        TypeLoader? ambient = null;
        if (!string.IsNullOrEmpty(assemblyPart)) {
            var comma = assemblyPart.IndexOf(',');
            var simpleName = (comma < 0 ? assemblyPart : assemblyPart[..comma]).Trim();
            ambient = ctx.Types.Context?.FindBySimpleName(simpleName);
            if (ambient is null)
                return null;
        }
        return ResolveTypePart(ctx, typePart, ambient);
    }

    private static VmType? ResolveTypePart(IntrinsicContext ctx, string typePart, TypeLoader? ambient) {
        // 末尾の [] / & / * を剥がす (多重対応)
        var suffixes = new List<char>();
        var core = typePart.TrimEnd();
        while (core.EndsWith("[]", StringComparison.Ordinal) || core.EndsWith("&", StringComparison.Ordinal) ||
               (core.EndsWith("*", StringComparison.Ordinal) && !core.EndsWith("**", StringComparison.Ordinal))) {
            if (core.EndsWith("[]", StringComparison.Ordinal)) {
                suffixes.Add('a');
                core = core[..^2].TrimEnd();
            } else if (core.EndsWith("&", StringComparison.Ordinal)) {
                suffixes.Add('&');
                core = core[..^1].TrimEnd();
            } else {
                suffixes.Add('*');
                core = core[..^1].TrimEnd();
            }
        }
        VmType? resolved;
        var bracket = core.IndexOf('[');
        if (bracket < 0) {
            resolved = FindNamedType(ctx, core, ambient);
        } else {
            // ジェネリック実体化: Def`N[[arg],[arg]]
            var defName = core[..bracket].Trim();
            var argsSection = core[bracket..].Trim();
            if (!argsSection.StartsWith("[", StringComparison.Ordinal) || !argsSection.EndsWith("]", StringComparison.Ordinal))
                return null;
            var inner = argsSection[1..^1];
            var argTexts = SplitTopLevelAll(inner, ',');
            var def = FindNamedType(ctx, defName, ambient);
            if (def is not VmClassType defClass || defClass.GenericParamCount != argTexts.Count)
                return null;
            var typeArgs = new VmType[argTexts.Count];
            for (var i = 0; i < argTexts.Count; i++) {
                var argText = argTexts[i].Trim();
                // 引数は [修飾名] 形で包まれる (包みが無ければそのまま)
                if (argText.StartsWith("[", StringComparison.Ordinal) && argText.EndsWith("]", StringComparison.Ordinal))
                    argText = argText[1..^1];
                var argType = ResolveTypeName(ctx, argText);
                if (argType is null)
                    return null;
                typeArgs[i] = argType;
            }
            resolved = new VmConstructedType { Definition = defClass, TypeArguments = typeArgs };
        }
        if (resolved is null)
            return null;
        foreach (var suffix in suffixes) {
            resolved = suffix switch {
                'a' => ArrayWithKnownBase(ctx, resolved),
                '&' => new VmByRefType { ElementType = resolved },
                // ポインタは ByRef と同様に扱う (署名解決の既存規約)
                _ => new VmByRefType { ElementType = resolved },
            };
        }
        return resolved;
    }

    private static VmType ArrayWithKnownBase(IntrinsicContext ctx, VmType element) {
        var array = new VmArrayType { ElementType = element };
        try {
            array.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
        } catch {
            // System.Array 未解決時は基底なし (稀。キャスト判定のみ弱くなる)
        }
        return array;
    }

    private static VmType? FindNamedType(IntrinsicContext ctx, string fullName, TypeLoader? ambient) {
        if (ambient is not null) {
            try {
                return ambient.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                return null;
            }
        }
        // 単純名: trusted 実型を先に (同名 fake 型の混入防止)、次に全ロード画像
        try {
            if (ctx.Types.TryResolveTrustedUnifiedType(fullName) is { } trusted)
                return trusted;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
        }
        var context = ctx.Types.Context;
        if (context is null) {
            try {
                return ctx.Types.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                return null;
            }
        }
        foreach (var loader in context.Loaders) {
            try {
                if (loader.FindTypeByFullName(fullName) is { } found)
                    return found;
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                continue;
            }
        }
        return null;
    }

    /// <summary>トップレベルの区切り ([]) ネストを無視して最初に分割する。</summary>
    private static void SplitTopLevel(string text, char separator, out string left, out string? right) {
        var depth = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == separator && depth == 0) {
                left = text[..i];
                right = text[(i + 1)..];
                return;
            }
        }
        left = text;
        right = null;
    }

    private static List<string> SplitTopLevelAll(string text, char separator) {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == separator && depth == 0) {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    // ---- System.TimeSpan / System.DateTime (culture 依存 IL 面の不変カルチャ委譲) ----
    // Time 面の Parse / ToString / AddDays / AddYears は本家 IL が CultureInfo / CultureData /
    // TextInfo (culture 機構) の全面を辿る (Dictionary cache / CompareInfo / Calendar 面で
    // VM 表現境界に落ちる)。C5.5 Wave 5 と同じ culture-out-of-scope の ① バインドで、
    // VM 規約の不変カルチャ (CultureInfo.InvariantCulture) 相当のホスト BCL 委譲で受ける。
    // VM 内表現は CoreLib の DateTime(_dateData: ulong) / TimeSpan(_ticks: long) 構造体 (単一スロット)
    // で正規化する。

    private static void RegisterTimeCultureFaces(IntrinsicRegistry r) {
        // VM System.TimeSpan 構造体値の構築 (ホスト TimeSpan.Ticks → CoreLib TimeSpan._ticks)
        static StackSlot MakeTimeSpanStruct(IntrinsicContext ctx, TimeSpan ts) {
            var cls = FindCoreLibType(ctx, "System.TimeSpan");
            var fields = new StackSlot[InstanceFieldCount(cls)];
            var index = 0;
            foreach (var field in cls.Fields) {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                fields[index++] = field.Name switch {
                    "_ticks" => StackSlot.OfInt64(ts.Ticks),
                    _ => SlotDefaultZero(field),
                };
            }
            return StackSlot.OfValueType(new VmStructValue(cls, fields));
        }
        // VM System.DateTime 構造体値の構築 (_dateData = ticks | kind<<62 本家レイアウト。
        // Invariant 解析は Unspecified (kind=0) 相当で ticks のみ)
        static StackSlot MakeDateTimeStruct(IntrinsicContext ctx, DateTime dt) {
            var cls = FindCoreLibType(ctx, "System.DateTime");
            var fields = new StackSlot[InstanceFieldCount(cls)];
            var index = 0;
            foreach (var field in cls.Fields) {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                fields[index++] = field.Name switch {
                    "_dateData" => StackSlot.OfInt64((long)dt.Ticks), // kind=0 (Unspecified) のビット列
                    _ => SlotDefaultZero(field),
                };
            }
            return StackSlot.OfValueType(new VmStructValue(cls, fields));
        }
        // VM VmStructValue → ホスト TimeSpan (CoreLib _ticks スロットを読む。CoreLib 未ロードの
        // legacy 経路では VmStructValue 以外が来るため fail-closed)
        static TimeSpan TimeSpanOf(in StackSlot slot) {
            if (slot.ObjectValue is not VmStructValue sv || sv.StructType.FullName != "System.TimeSpan")
                throw new InvalidOperationException(
                    $"TimeSpan 面の引数が CoreLib TimeSpan 構造体値ではありません ({slot.Kind})。");
            return new TimeSpan(sv.Fields[0].Int64Value);
        }
        static DateTime DateTimeOf(in StackSlot slot) {
            if (slot.ObjectValue is not VmStructValue sv || sv.StructType.FullName != "System.DateTime")
                throw new InvalidOperationException(
                    $"DateTime 面の引数が CoreLib DateTime 構造体値ではありません ({slot.Kind})。");
            return new DateTime(sv.Fields[0].Int64Value);
        }
        static string? S(in StackSlot slot) => (slot.ObjectValue as VmString)?.Value;
        static VmString? ResultString(IntrinsicContext ctx, string value) => ctx.MakeString(value);

        var timeSpanT = "System.TimeSpan";
        // TimeSpan.ToString (無引数 / format / format,provider): 本家 IL は Span 解析 + culture 機構
        // (TimeSpanFormat / TimeSpanParse は Number.Formatting 系 + IFormatProvider) で構成され、
        // Span 表現境界のため IL 実行にできない。不変カルチャ固定のホスト委譲で提供
        r.RegisterBinding(BindingKey.InstanceWithReturn(timeSpanT, "ToString", "System.String",
                Array.Empty<string>()),
            static (ctx, a) => ctx.MakeString(TimeSpanOf(a[0]).ToString(null, System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(timeSpanT, "ToString", "System.String",
                ["System.String"]),
            static (ctx, a) => ctx.MakeString(TimeSpanOf(a[0]).ToString(S(a[1]), System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(timeSpanT, "ToString", "System.String",
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => ctx.MakeString(TimeSpanOf(a[0]).ToString(S(a[1]), System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        // TimeSpan.Parse (string[, provider]): 不変カルチャ規約固定。FormatException / OverflowException は本家と同一分類
        r.RegisterBinding(BindingKey.StaticWithReturn(timeSpanT, "Parse", timeSpanT, ["System.String"]),
            static (ctx, a) => {
                try {
                    return MakeTimeSpanStruct(ctx, TimeSpan.Parse(S(a[0]) ?? "", System.Globalization.CultureInfo.InvariantCulture));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                } catch (OverflowException) {
                    throw new UnhandledGuestException("System.OverflowException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(timeSpanT, "Parse", timeSpanT,
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => {
                try {
                    return MakeTimeSpanStruct(ctx, TimeSpan.Parse(S(a[0]) ?? "", System.Globalization.CultureInfo.InvariantCulture));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                } catch (OverflowException) {
                    throw new UnhandledGuestException("System.OverflowException", null);
                }
            },
            BindingOrigin.Managed);
        // TimeSpan 掛け算系 (乘演算 = op_Multiply は本家 IL が TimeSpan.ValueTuple で構成されるため
        // IL 実行可能のため inert (② で走る))

        var dateTimeT = "System.DateTime";
        // DateTime.Parse (string[, provider]): 本家 IL は culture 機構 (CultureInfo.GetCultureInfo →
        // Dictionary cache → Type face 判定群) の全面を辿るため culture-out-of-scope で委譲
        r.RegisterBinding(BindingKey.StaticWithReturn(dateTimeT, "Parse", dateTimeT, ["System.String"]),
            static (ctx, a) => {
                try {
                    return MakeDateTimeStruct(ctx, DateTime.Parse(S(a[0]) ?? "", System.Globalization.CultureInfo.InvariantCulture));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                } catch (Exception ex) when (ex is OverflowException or ArgumentException) {
                    throw new UnhandledGuestException(ex.GetType().Name.Replace("Exception", "Exception") == "OverflowException"
                        ? "System.OverflowException" : "System.ArgumentException", null);
                }
            },
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(dateTimeT, "Parse", dateTimeT,
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => {
                try {
                    return MakeDateTimeStruct(ctx, DateTime.Parse(S(a[0]) ?? "", System.Globalization.CultureInfo.InvariantCulture));
                } catch (FormatException) {
                    throw new UnhandledGuestException("System.FormatException", null);
                }
            },
            BindingOrigin.Managed);
        // DateTime.ToString (無引数 / format / format,provider): 本家 IL は DateTimeFormatInfo 機構を辿る
        // ため culture-out-of-scope で不変カルチャ固定のホスト委譲
        r.RegisterBinding(BindingKey.InstanceWithReturn(dateTimeT, "ToString", "System.String", Array.Empty<string>()),
            static (ctx, a) => ctx.MakeString(DateTimeOf(a[0]).ToString(null, System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(dateTimeT, "ToString", "System.String", ["System.String"]),
            static (ctx, a) => ctx.MakeString(DateTimeOf(a[0]).ToString(S(a[1]), System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(dateTimeT, "ToString", "System.String",
                ["System.String", "System.IFormatProvider"]),
            static (ctx, a) => ctx.MakeString(DateTimeOf(a[0]).ToString(S(a[1]), System.Globalization.CultureInfo.InvariantCulture))
                is { } s ? StackSlot.OfObject(s) : null,
            BindingOrigin.Managed);
        // 日付演算 (AddDays / AddYears): 本家 IL は Calendar (DaysToMonth366 FieldRVA static array +
        // RuntimeHelpers.CreateSpan 面) を辿るため表現境界。不変 Calendar (Gregorian) のホスト演算を委譲
        r.RegisterBinding(BindingKey.InstanceWithReturn(dateTimeT, "AddDays", dateTimeT, ["System.Double"]),
            static (ctx, a) => MakeDateTimeStruct(ctx, DateTimeOf(a[0]).AddDays(a[1].DoubleValue)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.InstanceWithReturn(dateTimeT, "AddYears", dateTimeT, ["System.Int32"]),
            static (ctx, a) => MakeDateTimeStruct(ctx, DateTimeOf(a[0]).AddYears(a[1].AsInt32)),
            BindingOrigin.Managed);
        // internal static long DateTime.DateToTicks(int year, int month, int day)
        // 本家 DateToTicks IL は DaysToMonth366 (FieldRVA 静的配列) の
        // RuntimeHelpers.CreateSpan / RuntimeFieldHandle::m_ptr 面を辿るため VM 表現境界。
        // Gregorian 不変 Calendar のホスト演算 (new DateTime(y, m, d).Ticks) を委譲する
        // (DateTime::ctor(int, int, int) IL はこの面と DateTime::.ctor(long) IL (単純フィールド代入)
        // で構成されるため、ここだけ受けることでゲスト new DateTime(y, m, d) 全体が VM で通る)
        // private static ulong DateTime.DateToTicks(int year, int month, int day)
        // 本家 DateToTicks IL は DaysToMonth365/366 (FieldRVA 静的配列) の
        // RuntimeHelpers.CreateSpan / GetSpanDataFrom / RuntimeFieldHandle::IsNullHandle (m_ptr
        // ByRef 読み) 面を辿るため VM 表現境界。Gregorian 不変 Calendar のホスト演算
        // ((ulong)new DateTime(y, m, d).Ticks) を委譲する (DateTime::.ctor(int, int, int) IL は
        // この面と DateTime::.ctor(long) IL (単純フィールド代入) で構成されるため、
        // ここだけ受けることでゲスト new DateTime(y, m, d) 全体が VM で通る)
        r.RegisterBinding(BindingKey.StaticWithReturn(dateTimeT, "DateToTicks", "System.UInt64",
                ["System.Int32", "System.Int32", "System.Int32"]),
            static (_, a) => StackSlot.OfInt64(unchecked((long)new DateTime(
                a[0].AsInt32, a[1].AsInt32, a[2].AsInt32).Ticks)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(dateTimeT, "DateToTicks", "System.Int64",
                ["System.Int32", "System.Int32", "System.Int32"]),
            static (_, a) => StackSlot.OfInt64(new DateTime(
                a[0].AsInt32, a[1].AsInt32, a[2].AsInt32).Ticks),
            BindingOrigin.Managed);
    }

    // ---- System.Threading.Thread / Monitor ----

    private static void RegisterThreading(IntrinsicRegistry r) {
        // Thread は VM delegate をホスト worker thread 上で実行する。ゲスト IL への再入は
        // Interpreter のスレッド別フレームと共有 quota / heap を通る。
        const string thread = "System.Threading.Thread";
        static VmObject Receiver(StackSlot[] a) => a[0].ObjectValue as VmObject
            ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        static VmDelegate StartDelegate(StackSlot[] a, int index) => a[index].ObjectValue as VmDelegate
            ?? throw new UnhandledGuestException("System.ArgumentNullException", "start");
        static bool Start(IntrinsicContext ctx, StackSlot[] a, bool hasState) {
            var run = ctx.RunGuestThreadDelegate ?? throw new InvalidOperationException("Guest Thread runner が初期化されていません。");
            ctx.Shared.GuestThreads.Start(Receiver(a), hasState ? a[1] : default, run, hasState);
            return true;
        }
        static bool Join(IntrinsicContext ctx, StackSlot[] a, int timeout) {
            var completed = false;
            SuspendHostWait(ctx, () => completed = ctx.Shared.GuestThreads.Join(Receiver(a), timeout));
            return completed;
        }
        r.RegisterBinding(BindingKey.Instance(thread, ".ctor", "System.Threading.ThreadStart"),
            static (ctx, a) => {
                ctx.Shared.GuestThreads.Configure(Receiver(a), StartDelegate(a, 1), parameterized: false);
                return null;
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, ".ctor", "System.Threading.ParameterizedThreadStart"),
            static (ctx, a) => {
                ctx.Shared.GuestThreads.Configure(Receiver(a), StartDelegate(a, 1), parameterized: true);
                return null;
            }, BindingOrigin.InternalCall);
        r.Register(new IntrinsicKey(thread, ".ctor", 2, true), static (ctx, a) => {
            var start = StartDelegate(a, 1);
            ctx.Shared.GuestThreads.Configure(Receiver(a), start,
                parameterized: start.DeclaredType.FullName == "System.Threading.ParameterizedThreadStart");
            return null;
        });
        r.RegisterBinding(BindingKey.Instance(thread, "Start"),
            static (ctx, a) => { Start(ctx, a, hasState: false); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Start", 0), static (ctx, a) => { Start(ctx, a, hasState: false); return null; });
        r.RegisterBinding(BindingKey.Instance(thread, "Start", "System.Object"),
            static (ctx, a) => { Start(ctx, a, hasState: true); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Start", 1), static (ctx, a) => { Start(ctx, a, hasState: true); return null; });
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", []),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, Timeout.Infinite) ? 1 : 0), BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Join", 0), static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, Timeout.Infinite) ? 1 : 0));
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")) ? 1 : 0), BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Join", 1), static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")) ? 1 : 0));
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", ["System.TimeSpan"]),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(TimeSpanMilliseconds(a[1]), "timeout")) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, "get_IsAlive"),
            static (ctx, a) => StackSlot.OfInt32(ctx.Shared.GuestThreads.IsAlive(Receiver(a)) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, "get_ManagedThreadId"),
            static (ctx, a) => StackSlot.OfInt32(ctx.Shared.GuestThreads.ManagedThreadId(Receiver(a))), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.Threading.Thread", "Sleep", "System.Int32"),
            static (ctx, a) => { var timeout = ValidateTimeout(a[0].AsInt32, "millisecondsTimeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Static("System.Threading.Thread", "Sleep", 1), static (ctx, a) => { var timeout = ValidateTimeout(a[0].AsInt32, "millisecondsTimeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; });
        r.RegisterBinding(BindingKey.Static("System.Threading.Thread", "Sleep", "System.TimeSpan"),
            static (ctx, a) => { var timeout = ValidateTimeout(TimeSpanMilliseconds(a[0]), "timeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; }, BindingOrigin.InternalCall);

        // Monitor はゲストオブジェクト identity ごとの CLR Monitor を同期ブロックとして持つ。
        // Enter の lockTaken overload も直接受け、CoreLib fast path / slow path に依存しない。
        const string T = "System.Threading.Monitor";
        r.RegisterBinding(BindingKey.Static(T, "TryEnter_FastPath", "System.Object"),
            static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return StackSlot.OfInt32(1); }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(T, "TryEnter_FastPath_WithTimeout", "System.Object", "System.Int32"),
                static (ctx, a) => SuspendHostWait(ctx, () => Monitor.TryEnter(
                    ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")))
                ? StackSlot.OfInt32(1) : StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Exit_FastPath", "System.Object"),
            static (ctx, a) => { ExitMonitor(ctx, a[0]); return StackSlot.OfInt32(0); }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "IsEnteredNative", "System.Object"),
            static (ctx, a) => StackSlot.OfInt32(Monitor.IsEntered(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);

        r.RegisterBinding(BindingKey.Static(T, "Enter", "System.Object"),
            static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Enter", "System.Object", "System.Boolean&"),
            static (ctx, a) => {
                SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)));
                WriteByRef(a[1], StackSlot.OfInt32(1));
                return null;
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Exit", "System.Object"),
            static (ctx, a) => { ExitMonitor(ctx, a[0]); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx,
                () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Boolean&"]),
            static (ctx, a) => {
                var entered = Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue));
                WriteByRef(a[1], StackSlot.OfInt32(entered ? 1 : 0));
                return StackSlot.OfInt32(entered ? 1 : 0);
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Int32", "System.Boolean&"]),
            static (ctx, a) => {
                var entered = SuspendHostWait(ctx,
                    () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")));
                WriteByRef(a[2], StackSlot.OfInt32(entered ? 1 : 0));
                return StackSlot.OfInt32(entered ? 1 : 0);
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Wait", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => Monitor.Wait(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Wait", "System.Boolean", ["System.Object", "System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => Monitor.Wait(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Pulse", "System.Object"),
            static (ctx, a) => { Monitor.Pulse(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "PulseAll", "System.Object"),
            static (ctx, a) => { Monitor.PulseAll(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "IsEntered", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(Monitor.IsEntered(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);

        // Fallback facade 経路 (LoadHostCoreLib=false) の legacy key 群。CoreLib ロード時は
        // 上の署名バインドが優先される。
        r.Register(IntrinsicKey.Static(T, "Enter", 1), static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return null; });
        r.Register(IntrinsicKey.Static(T, "Enter", 2), static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); WriteByRef(a[1], StackSlot.OfInt32(1)); return null; });
        r.Register(IntrinsicKey.Static(T, "Exit", 1), static (ctx, a) => { ExitMonitor(ctx, a[0]); return null; });
        r.Register(IntrinsicKey.Static(T, "TryEnter", 1), static (ctx, a) => StackSlot.OfInt32(Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0));
        r.Register(IntrinsicKey.Static(T, "TryEnter", 2), static (ctx, a) => {
            var sync = ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue);
            var taken = a[1].Kind == StackKind.ByRef
                ? Monitor.TryEnter(sync)
                : SuspendHostWait(ctx, () => Monitor.TryEnter(sync, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")));
            if (a[1].Kind == StackKind.ByRef)
                WriteByRef(a[1], StackSlot.OfInt32(taken ? 1 : 0));
            return StackSlot.OfInt32(taken ? 1 : 0);
        });
        r.Register(IntrinsicKey.Static(T, "TryEnter", 3), static (ctx, a) => { var taken = SuspendHostWait(ctx, () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))); WriteByRef(a[2], StackSlot.OfInt32(taken ? 1 : 0)); return StackSlot.OfInt32(taken ? 1 : 0); });
        r.Register(IntrinsicKey.Static(T, "Pulse", 1), static (ctx, a) => { Monitor.Pulse(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; });
        r.Register(IntrinsicKey.Static(T, "PulseAll", 1), static (ctx, a) => { Monitor.PulseAll(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; });
    }

    // ---- System.Threading.Tasks.Task / async state machines ----

    private static void RegisterTaskBindings(IntrinsicRegistry r) {
        const string task = "System.Threading.Tasks.Task";
        const string taskOfT = "System.Threading.Tasks.Task`1";
        const string awaiter = "System.Runtime.CompilerServices.TaskAwaiter";
        const string awaiterOfT = "System.Runtime.CompilerServices.TaskAwaiter`1";
        const string builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder";
        const string builderOfT = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1";

        static VmTaskObject AsTask(in StackSlot slot) => slot.ObjectValue as VmTaskObject
            ?? throw new UnhandledGuestException("System.InvalidOperationException", "Task の VM 実体がありません。");
        static VmType FindType(IntrinsicContext ctx, string name) =>
            (ctx.Types.IsTrustedCoreLib
                ? (VmType?)ctx.Types.FindTypeByFullName(name) ?? ctx.Types.FindIntrinsicType(name)
                : (VmType?)ctx.Types.FindIntrinsicType(name) ?? ctx.Types.FindTypeByFullName(name))
            ?? throw new InvalidOperationException($"{name} type が見つかりません。");
        static VmTaskObject NewTask(IntrinsicContext ctx, bool generic, VmType? resultType = null, bool completed = false,
            StackSlot result = default) {
            var taskDefinition = FindType(ctx, generic ? taskOfT : task);
            VmType taskType = generic
                ? new VmConstructedType {
                    Definition = taskDefinition,
                    TypeArguments = [resultType ?? ctx.ClassTypeArguments.FirstOrDefault()
                        ?? ctx.Types.FindIntrinsicType("System.Object")!],
                }
                : taskDefinition;
            return ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(taskType, completed, result));
        }
        static VmType AwaiterType(IntrinsicContext ctx, bool generic, VmType? resultType) {
            var definition = FindType(ctx, generic ? awaiterOfT : awaiter);
            return generic
                ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                : definition;
        }
        static VmTaskObject AwaitedTask(in StackSlot slot) {
            var awaiterValue = slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
                ? byRef.Read() : slot;
            if (awaiterValue.Kind != StackKind.ValueType || awaiterValue.ObjectValue is not VmStructValue value || value.Fields.Length == 0)
                throw new UnhandledGuestException("System.InvalidOperationException", "Task awaiter が初期化されていません。");
            return AsTask(value.Fields[0]);
        }
        static VmStructValue BuilderValue(in StackSlot slot) {
            var value = slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
                ? byRef.Read() : slot;
            return value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue builderValue
                ? builderValue : throw new UnhandledGuestException("System.InvalidOperationException", "Async builder が初期化されていません。");
        }
        static VmTaskObject EnsureBuilderTask(IntrinsicContext ctx, StackSlot[] args) {
            var builderValue = BuilderValue(args[0]);
            if (builderValue.Fields.Length > 0 && builderValue.Fields[0].ObjectValue is VmTaskObject taskObject)
                return taskObject;
            var generic = builderValue.StructType is VmConstructedType { Definition.FullName: builderOfT } ||
                builderValue.StructType.FullName == builderOfT;
            var resultType = builderValue.TypeArguments.FirstOrDefault() ?? ctx.ClassTypeArguments.FirstOrDefault();
            var newTask = NewTask(ctx, generic, resultType);
            if (builderValue.Fields.Length == 0) {
                var replacement = new VmStructValue(builderValue.StructType, [StackSlot.OfObject(newTask)], builderValue.TypeArguments);
                if (args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef emptyDestination)
                    emptyDestination.Write(StackSlot.OfValueType(replacement));
                return newTask;
            } else {
                builderValue.Fields[0] = StackSlot.OfObject(newTask);
            }
            if (args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef destination)
                destination.Write(StackSlot.OfValueType(builderValue));
            return newTask;
        }
        static StackSlot TaskResult(IntrinsicContext ctx, VmTaskObject taskObject) {
            if (!taskObject.IsCompleted)
                SuspendHostWait(ctx, taskObject.Wait);
            var result = taskObject.Snapshot();
            if (result.GuestException.Kind != StackKind.Empty) {
                var exceptionObject = result.GuestException.ObjectValue;
                var typeName = exceptionObject switch {
                    VmExceptionObject guestException => guestException.ExceptionType.FullName,
                    VmClassInstance guestException => guestException.ClassType.FullName,
                    VmIntrinsicInstance guestException => guestException.InstanceType.FullName,
                    _ => "System.Exception",
                };
                throw new UnhandledGuestException(typeName, null);
            }
            if (result.HostException is not null)
                throw result.HostException;
            return result.Result;
        }
        static VmType? ResultType(IntrinsicContext ctx, bool fromMethod) => fromMethod
            ? ctx.MethodTypeArguments.FirstOrDefault()
            : ctx.ClassTypeArguments.FirstOrDefault();
        static void RegisterBinding(IntrinsicRegistry registry, BindingKey key, IntrinsicImpl impl) =>
            registry.RegisterBinding(key, impl, BindingOrigin.Managed);

        static void RegisterTaskType(IntrinsicRegistry registry, string typeName, bool generic) {
            RegisterBinding(registry, BindingKey.InstanceWithReturn(typeName, "get_IsCompleted", "System.Boolean", []),
                static (_, a) => StackSlot.OfInt32(AsTask(a[0]).IsCompleted ? 1 : 0));
            RegisterBinding(registry, BindingKey.Instance(typeName, "GetAwaiter"),
                (ctx, a) => {
                    var objectTask = AsTask(a[0]);
                    var resultType = generic ? ResultType(ctx, fromMethod: false) : null;
                    return StackSlot.OfValueType(new VmStructValue(AwaiterType(ctx, generic, resultType),
                        [StackSlot.OfObject(objectTask)], generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "Wait"),
                static (ctx, a) => { SuspendHostWait(ctx, AsTask(a[0]).Wait); return null; });
            RegisterBinding(registry, BindingKey.InstanceWithReturn(typeName, "Wait", "System.Boolean", ["System.Int32"]),
                (ctx, a) => {
                    var target = AsTask(a[0]);
                    var milliseconds = a[1].AsInt32;
                    if (milliseconds < Timeout.Infinite)
                        throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "timeout");
                    return StackSlot.OfInt32(SuspendHostWait(ctx, () => target.Wait(milliseconds)) ? 1 : 0);
                });
            RegisterBinding(registry, BindingKey.InstanceWithReturn(typeName, "Wait", "System.Boolean", ["System.TimeSpan"]),
                (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => AsTask(a[0]).Wait(
                    ValidateTimeout(TimeSpanMilliseconds(a[1]), "timeout"))) ? 1 : 0));
        }

        static StackSlot StartTaskWorker(IntrinsicContext ctx, StackSlot[] a, VmType? resultType, bool generic) {
            if (a[0].ObjectValue is not VmDelegate guestDelegate)
                throw new UnhandledGuestException("System.ArgumentNullException", "function");
            var running = NewTask(ctx, generic, resultType);
            var invoke = ctx.InvokeGuestDelegate ?? throw new InvalidOperationException("guest delegate runner が初期化されていません。");
            ctx.Shared.GuestTasks.Run(running, [StackSlot.OfObject(running), a[0]], () => {
                var result = invoke(guestDelegate, [StackSlot.OfObject(guestDelegate)]) ?? default;
                if (result.ObjectValue is VmTaskObject nestedTask) {
                    nestedTask.Wait();
                    var nested = nestedTask.Snapshot();
                    if (nested.GuestException.Kind != StackKind.Empty)
                        throw new UnhandledGuestException("System.Exception", null);
                    if (nested.HostException is not null)
                        throw nested.HostException;
                    return nested.Result;
                }
                return result;
            });
            return StackSlot.OfObject(running);
        }

        RegisterTaskType(r, task, generic: false);
        RegisterTaskType(r, taskOfT, generic: true);
        RegisterBinding(r, BindingKey.Instance(taskOfT, "get_Result"),
            static (ctx, a) => TaskResult(ctx, AsTask(a[0])));
        RegisterBinding(r, BindingKey.StaticWithReturn(task, "get_CompletedTask", task, []),
            static (ctx, _) => StackSlot.OfObject(NewTask(ctx, generic: false, completed: true)));
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.Int32"),
            static (ctx, a) => {
                var milliseconds = a[0].AsInt32;
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.Delay(delayed, milliseconds);
                return StackSlot.OfObject(delayed);
            });
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.TimeSpan"),
            static (ctx, a) => {
                var milliseconds = TimeSpanMilliseconds(a[0]);
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.Delay(delayed, milliseconds);
                return StackSlot.OfObject(delayed);
            });
        RegisterBinding(r, BindingKey.Static(task, "FromResult", "!!0"),
            static (ctx, a) => {
                return StackSlot.OfObject(NewTask(ctx, generic: true,
                    ResultType(ctx, fromMethod: true), completed: true, result: a[0]));
            });
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Action"),
            static (ctx, a) => StartTaskWorker(ctx, a, resultType: null, generic: false));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<System.Threading.Tasks.Task>"),
            static (ctx, a) => StartTaskWorker(ctx, a, resultType: null, generic: false));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<!!0>"),
            static (ctx, a) => {
                var resultType = ResultType(ctx, fromMethod: true);
                var generic = resultType is not null;
                return StartTaskWorker(ctx, a, resultType, generic);
            });

        foreach (var typeName in new[] { awaiter, awaiterOfT }) {
            RegisterBinding(r, BindingKey.InstanceWithReturn(typeName, "get_IsCompleted", "System.Boolean", []),
                static (_, a) => StackSlot.OfInt32(AwaitedTask(a[0]).IsCompleted ? 1 : 0));
            RegisterBinding(r, BindingKey.Instance(typeName, "GetResult"),
                static (ctx, a) => TaskResult(ctx, AwaitedTask(a[0])));
            RegisterBinding(r, BindingKey.Instance(typeName, "OnCompleted", "System.Action"),
                static (_, _) => null);
            RegisterBinding(r, BindingKey.Instance(typeName, "UnsafeOnCompleted", "System.Action"),
                static (_, _) => null);
        }

        RegisterBuilderType(r, builder, generic: false);
        RegisterBuilderType(r, builderOfT, generic: true);

        static void RegisterBuilderType(IntrinsicRegistry registry, string typeName, bool generic) {
            RegisterBinding(registry, BindingKey.Static(typeName, "Create"),
                (ctx, _) => {
                    var resultType = generic ? ResultType(ctx, fromMethod: false) : null;
                    var definition = FindType(ctx, typeName);
                    VmType builderType = generic
                        ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                        : definition;
                    return StackSlot.OfValueType(new VmStructValue(builderType, [default], generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "get_Task"),
                (ctx, a) => StackSlot.OfObject(EnsureBuilderTask(ctx, a)));
            RegisterBinding(registry, generic
                    ? BindingKey.Instance(typeName, "SetResult", "!0")
                    : BindingKey.Instance(typeName, "SetResult"),
                (ctx, a) => {
                    var target = EnsureBuilderTask(ctx, a);
                    var result = generic && a.Length > 1 ? a[1] : default;
                    ctx.Shared.GuestTasks.Complete(target, result);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "SetException", "System.Exception"),
                (ctx, a) => {
                    var target = EnsureBuilderTask(ctx, a);
                    ctx.Shared.GuestTasks.CompleteGuestException(target, a.Length > 1 ? a[1] : default);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "Start", "!!0&"),
                (ctx, a) => {
                    var run = ctx.RunGuestStateMachine ?? throw new InvalidOperationException("guest state machine runner が初期化されていません。");
                    run(a[1]);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "AwaitOnCompleted", "!!0&", "!!1&"),
                (ctx, a) => RegisterContinuation(ctx, a));
            RegisterBinding(registry, BindingKey.Instance(typeName, "AwaitUnsafeOnCompleted", "!!0&", "!!1&"),
                (ctx, a) => RegisterContinuation(ctx, a));
            RegisterBinding(registry, BindingKey.Instance(typeName, "SetStateMachine",
                    "System.Runtime.CompilerServices.IAsyncStateMachine"),
                static (_, _) => null);
        }

        static StackSlot? RegisterContinuation(IntrinsicContext ctx, StackSlot[] a) {
            var awaited = AwaitedTask(a[1]);
            var resume = ctx.RunGuestStateMachine ?? throw new InvalidOperationException("guest state machine runner が初期化されていません。");
            ctx.Shared.GuestTasks.ScheduleContinuation(awaited, a[2], resume);
            return null;
        }
    }

    private static int TimeSpanMilliseconds(StackSlot slot) {
        var value = slot.ObjectValue switch {
            VmStructValue vm => vm,
            VmByRef byRef when byRef.Read().ObjectValue is VmStructValue vm => vm,
            _ => throw new UnhandledGuestException("System.ArgumentException", "TimeSpan value is not available."),
        };
        long ticks = 0;
        if (value.Fields.Length > 0)
            ticks = value.Fields[0].Int64Value;
        if (ticks == -TimeSpan.TicksPerMillisecond)
            return Timeout.Infinite;
        if (ticks < 0)
            return -2;
        return (int)Math.Min(int.MaxValue, (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
    }

    private static int ValidateTimeout(int milliseconds, string parameterName) => milliseconds >= Timeout.Infinite
        ? milliseconds
        : throw new UnhandledGuestException("System.ArgumentOutOfRangeException", parameterName);

    private static void SuspendHostWait(IntrinsicContext context, Action wait) {
        if (context.SuspendExecution is { } suspend)
            suspend(wait);
        else
            wait();
    }

    private static T SuspendHostWait<T>(IntrinsicContext context, Func<T> wait) {
        T result = default!;
        SuspendHostWait(context, () => { result = wait(); });
        return result;
    }

    private static void WriteByRef(StackSlot byRefSlot, StackSlot value) {
        if (byRefSlot.ObjectValue is VmByRef byRef)
            byRef.Write(value);
        else
            throw new UnhandledGuestException("System.ArgumentException", "A writable byref argument is required.");
    }

    private static void ExitMonitor(IntrinsicContext context, StackSlot target) {
        try {
            Monitor.Exit(context.Shared.Monitors.SyncRoot(target.ObjectValue));
        } catch (SynchronizationLockException) {
            throw new UnhandledGuestException("System.Threading.SynchronizationLockException", null);
        }
    }

    // ---- System.String (culture 依存面のバインド: 署名キーで ② IL より先に解決される) ----

    private static void RegisterString(IntrinsicRegistry r) {
        const string T = "System.String";
        // 4 項以上の連結は Roslyn が String.Concat(params string[]) / (params object[]) に
        // コンパイルする。要素は CLR 規約どおりフォーマットする (null は空文字列)。
        // 連結本体は実 IL の wstrcpy 生ポインタコピー面のため委譲継続 (C5.5 Wave 5 確定)
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.String[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Concat", "System.Object[]"),
            static (ctx, a) => ConcatArray(ctx, a), BindingOrigin.Managed);
        // ---- culture 相当必須面 (C5.5 Wave 5 確定): 本家 IL は CultureInfo /
        //      CompareInfo (culture 機構) で構成され culture スコープ外のため、
        //      VM 規約の不変カルチャ固定をホスト BCL の InvariantCulture 面で委譲する
        //      (不変カルチャ比較 IL 移植候補とのファズ突合で非 ASCII 差分が証明済み —
        //      ordinal 近似では ß/ss 等のインバリアント等価が再現できない)。
        //      対応する ordinal / 置換面 (CompareOrdinal / IndexOf(char) / Contains /
        //      Replace / Split / Format) は VmCoreLibSurfaces の (b) 置換面に移行済みで
        //      ここには載せない (載せると ① が ②'/置換面を塞ぐ退行)。
        //      null 許容の静的比較面のみ本家どおり null を通す (Compare(null, x) = 負)
        static string? Ns(StackSlot[] a, int i) => (a[i].ObjectValue as VmString)?.Value;
        static VmString Str(StackSlot[] a, int i) =>
            a[i].ObjectValue as VmString ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        r.RegisterBinding(BindingKey.Static(T, "Compare", "System.String", "System.String"),
            static (ctx, a) => {
                ctx.Heap.ChargeHostWork(HostWorkChars(Ns(a, 0), Ns(a, 1)));
                return StackSlot.OfInt32(string.Compare(Ns(a, 0), Ns(a, 1), StringComparison.InvariantCulture));
            },
            BindingOrigin.Managed);
        // Equals(String, String) (op_Equality の呼び先): 本家 IL 末尾が SpanHelpers.SequenceEqual
        // (ref byte, ref byte, nuint) で、その scalar フォールバック自体が GSJA 抽象化
        // (ISimdVector static abstract = Vector128<T> inline 表現依存) のため VM 表現境界。
        // ordinal 等価は culture 無関当面のためホスト ordinal 等価へ委譲する
        r.RegisterBinding(BindingKey.Static(T, "Equals", "System.String", "System.String"),
            static (_, a) => StackSlot.OfInt32(string.Equals(Ns(a, 0), Ns(a, 1), StringComparison.Ordinal) ? 1 : 0),
            BindingOrigin.Managed);
        // StringComparison overload 群 (Equals static / Equals instance / Compare SCI):
        // 本家 IL は StringComparison 分岐後、ordinal 面 (String.EqualsFast / String.CompareOrdinal)
        // を直接辿る面と culture 機構面 (CompareInfo / SpanHelpers SIMD 抽象化 —
        // EqualsIgnoreCase_Vector は ISimdVector<TVector,T> static abstract = GSJA 抽象化で
        // Vector128<T> inline 表現依存) に分かれる。SIMD 抽象化面は VM 表現境界のため、
        // SCI 面 3 overload を上記 culture 面と同じホスト BCL 委譲に統一する。
        // CurrentCulture / CurrentCultureIgnoreCase は VM 規約 (不変カルチャ固定) どおり
        // InvariantCulture / InvariantCultureIgnoreCase へ写像し、Ordinal 系はそのまま通す
        static StringComparison InvariantOf(int comparison) => comparison switch {
            0 => StringComparison.InvariantCulture,            // CurrentCulture
            1 => StringComparison.InvariantCultureIgnoreCase, // CurrentCultureIgnoreCase
            2 => StringComparison.InvariantCulture,
            3 => StringComparison.InvariantCultureIgnoreCase,
            4 => StringComparison.Ordinal,
            _ => StringComparison.OrdinalIgnoreCase,
        };
        r.RegisterBinding(BindingKey.Static(T, "Equals", "System.String", "System.String", "System.StringComparison"),
            static (_, a) => StackSlot.OfInt32(string.Equals(Ns(a, 0), Ns(a, 1), InvariantOf(a[2].AsInt32)) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "Equals", "System.String", "System.StringComparison"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.Equals(Str(a, 1).Value, InvariantOf(a[2].AsInt32)) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Compare", "System.String", "System.String", "System.StringComparison"),
            static (_, a) => StackSlot.OfInt32(string.Compare(Ns(a, 0), Ns(a, 1), InvariantOf(a[2].AsInt32))),
            BindingOrigin.Managed);
        // CompareTo (instance): 本家 IL も CurrentCulture の Compare を呼ぶ文化面。
        // ① が ② IL より先に解決されるため、無いと IL 実行時に CompareInfo で fail-closed になる
        r.RegisterBinding(BindingKey.Instance(T, "CompareTo", "System.String"),
            static (_, a) => StackSlot.OfInt32(string.Compare(Str(a, 0).Value, Str(a, 1).Value, StringComparison.InvariantCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.IndexOf(Str(a, 1).Value, StringComparison.InvariantCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.LastIndexOf(Str(a, 1).Value, StringComparison.InvariantCulture)),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "StartsWith", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.StartsWith(Str(a, 1).Value, StringComparison.InvariantCulture) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "EndsWith", "System.String"),
            static (_, a) => StackSlot.OfInt32(Str(a, 0).Value.EndsWith(Str(a, 1).Value, StringComparison.InvariantCulture) ? 1 : 0),
            BindingOrigin.Managed);
        // 大文字小文字面: 本家 IL は CultureInfo.CurrentCulture.TextInfo (culture 機構 +
        // InternalCall) を辿る。不変カルチャ規約ではホストの不変大文字小文字化と同一結果。
        // ToUpperInvariant / ToLowerInvariant も本家 IL は TextInfo を辿るため同様に委譲する
        r.RegisterBinding(BindingKey.Instance(T, "ToUpper"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.ToUpperInvariant())),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToLower"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.ToLowerInvariant())),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToUpperInvariant"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.ToUpperInvariant())),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "ToLowerInvariant"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.ToLowerInvariant())),
            BindingOrigin.Managed);
        // 1 文字の文字列生成 (InternalCall 面。char.ToString() の実 IL が string.CreateFromChar
        // を辿る — VmString 生成は既存の文字列内部面と同一経路)
        r.RegisterBinding(BindingKey.Static(T, "CreateFromChar", "System.Char"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Ch(a, 0).ToString())),
            BindingOrigin.InternalCall);
        // ---- System.Char culture 面 (C5.5 Wave 5 継続): 本家 IL は
        //      CultureInfo.CurrentCulture.TextInfo (culture 機構 + InternalCall) を辿るため
        //      不変カルチャ規約どおりホストの不変面へ委譲 (1 引数面のみ。CultureInfo 引数の
        //      overload は呼び出し側 IL が CultureInfo 構築を必要とするため当面未対応)。
        //      GetNumericValue は Unicode 数字値 (ネイティブ Unicode テーブル InternalCall)
        r.RegisterBinding(BindingKey.Static("System.Char", "ToUpper", "System.Char"),
            static (_, a) => StackSlot.OfInt32(char.ToUpperInvariant(Ch(a, 0))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Char", "ToLower", "System.Char"),
            static (_, a) => StackSlot.OfInt32(char.ToLowerInvariant(Ch(a, 0))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static("System.Char", "GetNumericValue", "System.Char"),
            static (_, a) => StackSlot.OfFloat(char.GetNumericValue(Ch(a, 0))),
            BindingOrigin.InternalCall);

        // ---- 文字整形・部分文字列面 (C5.5 探査継続):
        //      本家 IL は Span / fixed char* 内部 (PadLeft の SpanFill、Remove の Substring
        //      InnerAlloc 連鎖、EndsWith/IndexOf の StringComparison 抽象化面) を辿るため、
        //      VM の表現境界として同一意味論のホスト ordinal / 不変面へ委譲する。
        //      Join (params object[]) も CLR 同一規約 (null → 空、要素はフォーマット相当)
        //      でホスト面を通す (要素の ToString は VM の暗黙 ToString フック経由で正規化)。
        r.RegisterBinding(BindingKey.Instance(T, "Remove", ["System.Int32", "System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.Remove(a[1].AsInt32, a[2].AsInt32))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadLeft", ["System.Int32", "System.Char"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadLeft(a[1].AsInt32, Ch(a, 2)))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadLeft", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadLeft(a[1].AsInt32))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadRight", ["System.Int32", "System.Char"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadRight(a[1].AsInt32, Ch(a, 2)))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "PadRight", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(Str(a, 0).Value.PadRight(a[1].AsInt32))),
            BindingOrigin.Managed);
        // —— SCI overload 群 (IndexOf(string[, SCI]) / EndsWith / StartsWith):
        //      Ordinal 面 (IndexOf(Char, SCI) の被評価 IL は string.IndexOf(char) や
        //      SpanHelpers を辿る。SIMD ignore-case 抽象化 = GSJA 抽象化のため culture 無関
        //      … ordinal 等価は文化に依存しない = ordinal のホスト面に渡す
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String", ["System.String", "System.String[]"]),
            static (ctx, a) => JoinedFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String", ["System.String", "System.Object[]"]),
            static (ctx, a) => JoinedFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Join", "System.String",
                ["System.String", "System.Collections.Generic.IEnumerable`1<!!0>"]),
            static (ctx, a) => JoinedEnumerableFace(ctx, Ns(a, 0), a[1]),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "StartsWith", ["System.String", "System.StringComparison"]),
            static (_, a) => StackSlot.OfInt32(
                Str(a, 0).Value.StartsWith(Str(a, 1).Value, InvariantOf(a[2].AsInt32)) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "EndsWith", ["System.String", "System.StringComparison"]),
            static (_, a) => StackSlot.OfInt32(
                Str(a, 0).Value.EndsWith(Str(a, 1).Value, InvariantOf(a[2].AsInt32)) ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "IndexOf", ["System.String", "System.StringComparison"]),
            static (_, a) => StackSlot.OfInt32(
                Str(a, 0).Value.IndexOf(Str(a, 1).Value, InvariantOf(a[2].AsInt32))),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "LastIndexOf", ["System.String", "System.StringComparison"]),
            static (_, a) => StackSlot.OfInt32(
                Str(a, 0).Value.LastIndexOf(Str(a, 1).Value, InvariantOf(a[2].AsInt32))),
            BindingOrigin.Managed);
    }

    /// <summary>string.Join の要素の ToString 面 (VM オブジェクトを正規化して連結する)。</summary>
    private static StackSlot? JoinedFace(IntrinsicContext ctx, string? separator, StackSlot valuesSlot) {
        if (valuesSlot.ObjectValue is not VmArray array)
            throw new InvalidOperationException("string.Join の第 2 引数が配列ではありません。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator ?? string.Empty, parts)));
    }

    /// <summary>string.Join&lt;T&gt;(string, IEnumerable&lt;T&gt;)。配列由来の列挙面は VM 配列を
    /// 直接走査し、要素の ToString は暗黙 ToString フックへ渡す。</summary>
    private static StackSlot? JoinedEnumerableFace(IntrinsicContext ctx, string? separator, StackSlot valuesSlot) {
        if (valuesSlot.ObjectValue is null)
            return StackSlot.OfObject(ctx.MakeString(string.Empty));
        if (valuesSlot.ObjectValue is not VmArray array)
            throw new NotSupportedException(
                $"string.Join<T> actual={valuesSlot.ObjectValue.GetType().Name}/{valuesSlot.Kind}; VM 配列の IEnumerable<T> 面のみ対応しています。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator ?? string.Empty, parts)));
    }

    private static char Ch(StackSlot[] a, int i) => (char)a[i].AsInt32;

    private static StackSlot? ConcatArray(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].Kind != StackKind.Object || a[0].ObjectValue is not VmArray array)
            throw new InvalidOperationException("String.Concat の引数が配列ではありません。");
        var parts = new string[array.Length];
        for (var i = 0; i < array.Length; i++)
            parts[i] = DefaultIntrinsics.ConcatFormat(ctx, array.Elements[i]);
        return StackSlot.OfObject(ctx.MakeString(string.Concat(parts)));
    }

    // ---- System.String / System.Buffer / System.Runtime.CompilerServices.Unsafe
    //      (String IL 実行の全面化で CoreLib IL が呼ぶランタイム内部面) ----

    private const string UnsafeType = "System.Runtime.CompilerServices.Unsafe";

    private static void RegisterStringInternals(IntrinsicRegistry r) {
        // internal static string String.FastAllocateString(nint charCount)
        // CoreLib の InternalCall 面。実 CLR の確保点 (FastAllocateString) と同じ位置で
        // VmString の可変 char バッファを VmHeap 会計つきで確保する。
        // .NET 10 の既知署名 2 件を列挙する (単引数面と MethodTable 付き面。後者の長さは第 2 引数)
        r.RegisterBinding(BindingKey.Static("System.String", "FastAllocateString", "System.IntPtr"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[0].AsInt32)),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.String", "FastAllocateString",
                "System.Runtime.CompilerServices.MethodTable*", "System.IntPtr"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[1].AsInt32)),
            BindingOrigin.InternalCall);
        // static void Buffer.Memmove<T>(ref T destination, ref T source, nuint elementCount)
        // String 構築 IL (InternalSubString 等) が ref char で呼ぶ。実 CLR では JIT intrinsic だが
        // VM はバイト実体 (VmString.Bytes / VmLocallocMemory) 間の memmove として同等意味論を提供する。
        // ジェネリック面のため開いたキー (!!0) で 1 件登録し、T は実行時の具体名で判別する
        r.RegisterBinding(BindingKey.Static("System.Buffer", "Memmove", "!!0&", "!!0&", "System.UIntPtr"),
            static (ctx, a) => MemmoveImpl(ctx, a), BindingOrigin.InternalCall);
        // Unsafe.* ([Intrinsic]: 実 CLR も JIT が IL を置き換える面。CoreLib IL 内では
        // ref 値の byte 単位のアドレス演算として現れるため、バイト実体ポインタで同等に提供する)。
        // .NET 10 の既知署名を列挙する (ジェネリック面は開いたキー !!0 で 1 件ずつ)。
        // 呼出側の具体名 (例: System.Char&) は CallEngine の開いたキー照合で解決される
        foreach (var addParams in new[] {
            new[] { "!!0&", "System.Int32" },
            new[] { "!!0&", "System.IntPtr" },
            new[] { "!!0&", "System.UIntPtr" },
            new[] { "System.Void*", "System.Int32" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "Add", addParams),
                static (ctx, a) => AddImpl(ctx, a, elementStride: true), BindingOrigin.InternalCall);
        foreach (var addByteOffsetParams in new[] {
            new[] { "!!0&", "System.IntPtr" },
            new[] { "!!0&", "System.UIntPtr" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "AddByteOffset", addByteOffsetParams),
                static (ctx, a) => AddImpl(ctx, a, elementStride: false), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "As", "System.Object"),
            // static T Unsafe.As<T>(object source): 参照の型視点再解釈 (アドレス不変)。
            // SZArrayHelper.GetEnumerator 等が this (配列実体) を T[] 視点で受け取る面。
            // 検証は呼出側 (ldflda/ldobj/castclass) に委ね、ここでは素通しする
            // (box も unbox せずそのまま渡す。GetRawData 経路の ldflda は
            // GetRawData バインド側で受ける)
            static (_, a) => a[0],
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "As", "!!0&"),
            // static TTo Unsafe.As<TFrom, TTo>(ref TFrom source): 参照の型視点再解釈
            // (アドレス不変)。バイト実体ポインタは素通り、スロット列参照 (string 内部 char
            // 配列等の配列データ面) も同一アドレスとして素通りさせる — decimal 演算面の
            // scalar IL が Unsafe.As<char, byte>(ref char) をスロット列参照で呼ぶ
            static (_, a) => {
                var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.As");
                if (native is not null)
                    return StackSlot.OfObject(native);
                return StackSlot.OfByRef(slotRef!);
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "AreSame", "!!0&", "!!0&"),
            static (_, a) => StackSlot.OfInt32(AreSameImpl(a) ? 1 : 0), BindingOrigin.InternalCall);
        // ref T Unsafe.AsRef<T>(in T) ([Intrinsic]: ReadOnlySpan(in T&) ctor の IL が呼ぶ。
        // 実 IL はダミーで PlatformNotSupportedException を投げるため、参照をそのまま透過させる同等意味論を提供する)。
        // .NET 10 の既知署名 2 件 (ref 面と void* 面) を列挙する。
        // void* 面には NativeInt スロット (NullRef() の ldc.i4.0+conv.u 等の null ポインタ) が
        // 来る: 0 なら null 参照 (空コンテナ ByRef。未初期化 ByRef と同一の「null byref 相当」
        // 表現。未参照経路では触れられず、参照時は境界検査で fail-closed) を返し、
        // 非 0 (実アドレスの整数化 — VM に実体なし) は fail-closed。
        foreach (var asRefParams in new[] {
            new[] { "!!0&" },
            new[] { "System.Void*" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "AsRef", asRefParams),
                static (_, a) =>
                    a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef
                        ? StackSlot.OfByRef(byRef)
                        : a[0].Kind == StackKind.Object && a[0].ObjectValue is VmNativePointer native
                            ? StackSlot.OfObject(native)
                            : a[0].Kind == StackKind.NativeInt && a[0].Int64Value == 0
                                ? StackSlot.OfByRef(new VmByRef([], 0))
                                : throw new InvalidOperationException(
                                    $"Unsafe.AsRef の引数をスロット列参照として解釈できませんでした ({a[0].Kind})。"),
                BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(UnsafeType, "SizeOf"),
            static (ctx, _) => StackSlot.OfInt32(SlotStride(ctx.ParamAt(0))), BindingOrigin.InternalCall);
        // static bool Unsafe.IsNullRef<T>(ref T source)
        // ([Intrinsic]: 本家 IL は AsPointer との比較で構成されるが AsPointer 自体が
        // ダミー throw のため VM では null 参照表現 (空コンテナ ByRef) の直接判定で提供する。
        // 未初期化 ByRef ローカル (null byref 相当) も null と判定する
        r.RegisterBinding(BindingKey.Static(UnsafeType, "IsNullRef", "!!0&"),
            static (_, a) => StackSlot.OfInt32(
                a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef &&
                byRef.Container.Length == 0 ? 1 : 0),
            BindingOrigin.InternalCall);
        // static TTo Unsafe.BitCast<TFrom, TTo>(TFrom from)
        // ([Intrinsic]: 実 IL はダミー throw。same-size 値型のビット再解釈として同等意味論を
        // 提供する。Math.Abs(double) の IL が BitConverter.DoubleToUInt64Bits 経由で呼ぶ)。
        // ジェネリック面のため開いたキー (!!0) で登録する
        r.RegisterBinding(BindingKey.Static(UnsafeType, "BitCast", "!!0"),
            static (ctx, a) => BitCastImpl(ctx.ParamAt(0), a[0]), BindingOrigin.InternalCall);
        // static void Unsafe.CopyBlockUnaligned(ref byte destination, ref byte source, uint byteCount)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。String / Span IL がバイト実体コ
        // ピーに使うため Buffer.Memmove 相当の memmove で同等意味論を提供する)。
        // .NET 10 の既知署名 2 件 (ref byte 面と void* 面) を列挙する
        foreach (var copyParams in new[] {
            new[] { "System.Byte&", "System.Byte&", "System.UInt32" },
            new[] { "System.Void*", "System.Void*", "System.UInt32" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "CopyBlockUnaligned", copyParams),
                static (ctx, a) => MemmoveImpl(ctx, a, strideOverride: 1), BindingOrigin.InternalCall);
        // ref T MemoryMarshal.GetArrayDataReference<T>(T[] array) / ref byte (Array array)
        // ([Intrinsic]: 実 IL は配列データ先頭へのランタイム内部参照。IL を実行させると
        // 同名オーバーロードへの自己再帰に落ちるため、VM は要素格納列の先頭スロットへの
        // VmByRef で同等意味論を提供する (Span<T> 構築 IL の GetArrayDataReference 面用))。
        // .NET 10 の既知署名 2 件 (ジェネリック面は開いたキー !!0[] で登録)
        r.RegisterBinding(BindingKey.Static("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "System.Array"),
            static (_, a) => {
                // 配列は通常 Object スロットで来るが、呼出側が配列ローカルを ldarga する
                // 形 (ByRef スロット経由) もあるため展開して受ける
                var value = a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef ? byRef.Slot : a[0];
                return value.ObjectValue is VmArray array
                    ? StackSlot.OfByRef(new VmByRef(array.Elements, 0))
                    : throw new InvalidOperationException(
                        $"MemoryMarshal.GetArrayDataReference の引数が配列ではありません ({value.Kind})。");
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "!!0[]"),
            static (_, a) => {
                var value = a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef ? byRef.Slot : a[0];
                return value.ObjectValue is VmArray array
                    ? StackSlot.OfByRef(new VmByRef(array.Elements, 0))
                    : throw new InvalidOperationException(
                        $"MemoryMarshal.GetArrayDataReference の引数が配列ではありません ({value.Kind})。");
            },
            BindingOrigin.InternalCall);
        // static void Unsafe.WriteUnaligned<T>(ref byte destination, T value) / (void*, T)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。BitConverter.TryWriteBytes 等が
        // Span へのプリミティブ書込に使う。Read/Write 対称で同等意味論を提供する)
        // .NET 10 の既知署名 2 件ずつ (T は開いたキー !!0 で 1 件ずつ)
        foreach (var writeParams in new[] {
            new[] { "System.Byte&", "!!0" },
            new[] { "System.Void*", "!!0" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "WriteUnaligned", writeParams),
                static (ctx, a) => WriteUnalignedImpl(ctx, a), BindingOrigin.InternalCall);
        // static T Unsafe.ReadUnaligned<T>(ref byte source) / (void*)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。対称面)
        foreach (var readParams in new[] {
            new[] { "System.Byte&" },
            new[] { "System.Void*" },
        })
            r.RegisterBinding(BindingKey.Static(UnsafeType, "ReadUnaligned", readParams),
                static (ctx, a) => ReadUnalignedImpl(ctx, a), BindingOrigin.InternalCall);
        // static void Unsafe.SkipInit<T>(out T value)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。実 CLR も何もしない
        // (初期化のスキップ) ため no-op が同一意味論)
        r.RegisterBinding(BindingKey.Static(UnsafeType, "SkipInit", "!!0&"),
            static (_, _) => null, BindingOrigin.InternalCall);
    }

    /// <summary>Read/WriteUnaligned の T の要素型名。メソッド型実引数 (具体名) を優先し、
    /// 無い場合のみ宣言パラメータから推定する。ポインタ自体 (void*) や byte 参照は
    /// T ではないため候補から除く (誤った stride での無音破壊より fail-closed を優先)。</summary>
    private static string UnalignedElementName(IntrinsicContext ctx) {
        var methodArg = ctx.MethodTypeArgAt(0);
        if (!string.IsNullOrEmpty(methodArg) && methodArg is not "!!0")
            return methodArg;
        foreach (var i in new[] { 1, 0 }) {
            var name = ctx.ParamAt(i);
            if (string.IsNullOrEmpty(name) || name is "!!0" or "!!0&" or "System.Void*" or "System.Byte&")
                continue;
            return name.EndsWith("&", StringComparison.Ordinal) ? name[..^1] : name;
        }
        throw new InvalidOperationException(
            "Unaligned 面のジェネリック型引数 T を判別できませんでした。");
    }

    private static StackSlot? WriteUnalignedImpl(IntrinsicContext ctx, StackSlot[] a) {
        var elementName = UnalignedElementName(ctx);
        var stride = SlotStride(elementName);
        var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.WriteUnaligned");
        if (slotRef is not null) {
            // スロット列への T 丸ごと書込 (1 要素 = 1 スロット。Span<byte> 以外の
            // Span<T> 参照が来る形。部分重なりは単一スロット代入で正確)
            if (slotRef.Index < 0 || slotRef.Index >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.WriteUnaligned がスロット列の範囲外を参照します (index={slotRef.Index}, 要素数 {slotRef.Container.Length})。");
            slotRef.Container[slotRef.Index] = a[1];
            return null;
        }
        if ((long)native!.ByteOffset + stride > native.Bytes.Length)
            throw new InvalidOperationException(
                $"Unsafe.WriteUnaligned がブロック外を参照します (offset={native.ByteOffset}, {stride} バイト, ブロック {native.Bytes.Length} バイト)。");
        WriteNativeElement(native, native.ByteOffset, a[1], elementName);
        return null;
    }

    private static StackSlot ReadUnalignedImpl(IntrinsicContext ctx, StackSlot[] a) {
        var elementName = UnalignedElementName(ctx);
        var stride = SlotStride(elementName);
        var (native, slotRef) = ResolvePointerBase(a[0], "Unsafe.ReadUnaligned");
        if (slotRef is not null) {
            if (slotRef.Index < 0 || slotRef.Index >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.ReadUnaligned がスロット列の範囲外を参照します (index={slotRef.Index}, 要素数 {slotRef.Container.Length})。");
            return slotRef.Container[slotRef.Index];
        }
        if ((long)native!.ByteOffset + stride > native.Bytes.Length)
            throw new InvalidOperationException(
                $"Unsafe.ReadUnaligned がブロック外を参照します (offset={native.ByteOffset}, {stride} バイト, ブロック {native.Bytes.Length} バイト)。");
        return ReadNativeElement(native, native.ByteOffset, elementName);
    }

    /// <summary>ref 引数のスロットから「ポインタの指し先」を解決する。byref 引数は 2 つの
    /// 形で来る (① byref 値そのもの: Span 構造体の byref フィールドを ldfld で取り出した形、
    /// ② byref が格納されたスロットのアドレス: 同フィールドを ldflda した形)。指し先スロットの
    /// 値がさらに byref / ネイティブポインタを包むなら 1 段降りて解釈する。
    /// VmNativePointer (バイト実体) と VmByRef (スロット列: VmArray.Elements 等の
    /// 配列データ面) の両方を返しうる。</summary>
    private static (VmNativePointer? Native, VmByRef? SlotRef) ResolvePointerBase(in StackSlot slot, string face) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmNativePointer directNative)
            return (directNative, null);
        if (slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef outer) {
            var target = outer.Read(); // 指し先スロットの値
            if (target.Kind == StackKind.ByRef && target.ObjectValue is VmByRef inner)
                return (null, inner); // ② アドレス先スロットに格納された byref 値
            if (target.Kind == StackKind.Object && target.ObjectValue is VmNativePointer innerNative)
                return (innerNative, null); // ② アドレス先スロットに格納されたネイティブポインタ
            return (null, outer); // ① byref 値そのもの (外側の参照 = ポインタ)
        }
        throw new InvalidOperationException($"面 {face} の参照引数をポインタとして解釈できませんでした: {slot.Kind}");
    }

    /// <summary>ref 引数のスロットをバイト実体ポインタに読み替える (バイト実体を持つ面用)。
    /// VmNativePointer (ldflda 結果 / localloc ブロック) を素通りさせ、
    /// VmByRef 中に VmNativePointer を包む形も展開する。それ以外は fail-closed。</summary>
    private static VmNativePointer RequirePointer(in StackSlot slot, string face) {
        var (native, _) = ResolvePointerBase(slot, face);
        if (native is not null)
            return native;
        throw new InvalidOperationException(
            $"面 {face} の参照引数をバイト実体ポインタとして解釈できませんでした: {slot.Kind}");
    }

    /// <summary>static TTo Unsafe.BitCast&lt;TFrom, TTo&gt;(TFrom from) の同等意味論。
    /// TFrom (実引数型) のビット列をそのままのバイト幅で TTo として読み替える。スロット表現上
    /// float32/float64 は Float スロット、8 バイト整数は Int64 スロットに正規化されるため、
    /// TFrom 側の型名で出力スロットの種類を決める (TTo の読み手は IL 上のスロット種で解釈する)。</summary>
    private static StackSlot BitCastImpl(string fromTypeName, in StackSlot value) {
        // ByRef 署名 (System.Char& 等) の接尾辞と未実体化の型引数を剥がす
        var name = fromTypeName.EndsWith("&", StringComparison.Ordinal)
            ? fromTypeName[..^1]
            : fromTypeName;
        return name switch {
            "System.Double" => StackSlot.OfInt64(BitConverter.DoubleToInt64Bits(value.DoubleValue)),
            "System.Single" => StackSlot.OfInt32(
                BitConverter.SingleToInt32Bits((float)value.DoubleValue)),
            "System.Int64" or "System.UInt64" => StackSlot.OfFloat(
                BitConverter.Int64BitsToDouble(value.Int64Value)),
            "System.Int32" or "System.UInt32" => StackSlot.OfFloat(
                BitConverter.Int32BitsToSingle((int)value.Int64Value)),
            // 同一スロット表現の型 (bool/char/byte/enum 等) はビット再解釈なしで素通り
            _ => value,
        };
    }

    /// <summary>ジェネリック型引数のスロット上の要素サイズ。未対応の型は fail-closed
    /// (スロット表現に無い参照型 T 等の memmove / ポインタ演算は実行しない)。</summary>
    private static int SlotStride(string typeName) {
        // ByRef 署名 (System.Char& 等) の接尾辞と未実体化の型引数を剥がす
        var name = typeName.EndsWith("&", StringComparison.Ordinal)
            ? typeName[..^1]
            : typeName;
        return name switch {
            "System.Byte" or "System.SByte" or "System.Boolean" => 1,
            "System.Char" or "System.Int16" or "System.UInt16" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
            _ => throw new InvalidOperationException(
                $"面のジェネリック型引数 {typeName} は VM のスロット表現に対応していません (バイト幅が確定しません)。"),
        };
    }

    private static StackSlot? MemmoveImpl(IntrinsicContext ctx, StackSlot[] a, int? strideOverride = null) {
        // T の要素サイズ (elementCount は要素数) を宣言のパラメータ型から解決する。
        // 型名が解決できず呼出 VM 形状に乗らないものは fail-closed にする。
        // strideOverride は CopyBlockUnaligned 等の「byteCount リテラルとバイト長が一致する面」
        // (全型 1 バイト固定 stride) 用。
        var stride = strideOverride ?? SlotStride(ctx.ParamAt(0));
        var (dstNative, dstRef) = ResolvePointerBase(a[0], "Buffer.Memmove");
        var (srcNative, srcRef) = ResolvePointerBase(a[1], "Buffer.Memmove");
        var count = a[2].Kind == StackKind.Int64 ? a[2].Int64Value : a[2].AsInt32;
        if (count < 0)
            throw new InvalidOperationException("Buffer.Memmove の要素数が負です。");
        // スロット列 ↔ スロット列 (Span._reference がローカル/配列スロットを指す形):
        // 1 要素 = 1 スロットとして要素ごとにコピーする
        if (dstRef is not null && srcRef is not null) {
            if (dstRef.Index + count > dstRef.Container.Length ||
                srcRef.Index + count > srcRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (dst index={dstRef.Index}, src index={srcRef.Index}, {count} 要素)。" +
                    $"ブロック {dstRef.Container.Length} / {srcRef.Container.Length} スロット。");
            // memmove 意味論 (重なりがあっても正しく) のため送信側を退避してから書く
            var tmp = new StackSlot[count];
            for (var i = 0; i < count; i++)
                tmp[i] = srcRef.Container[srcRef.Index + i];
            for (var i = 0; i < count; i++)
                dstRef.Container[dstRef.Index + i] = tmp[i];
            return null;
        }
        // バイト実体 ↔ スロット列の混在 (Span<char> が VmString バッファとローカル char を繋ぐ形):
        // 要素をバイト列 LE とスロット値の間で変換する
        if (dstRef is not null || srcRef is not null) {
            var elementName = ctx.ParamAt(0).EndsWith("&", StringComparison.Ordinal)
                ? ctx.ParamAt(0)[..^1] : ctx.ParamAt(0);
            if (srcRef is not null && srcRef.Index + count > srcRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (src index={srcRef.Index}, {count} 要素, ブロック {srcRef.Container.Length} スロット)。");
            if (dstRef is not null && dstRef.Index + count > dstRef.Container.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がスロット列の範囲外を参照します (dst index={dstRef.Index}, {count} 要素, ブロック {dstRef.Container.Length} スロット)。");
            if (srcNative is not null && (long)srcNative.ByteOffset + count * stride > srcNative.Bytes.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がブロック外を参照します (src offset={srcNative.ByteOffset}, {count * stride} バイト, ブロック {srcNative.Bytes.Length} バイト)。");
            if (dstNative is not null && (long)dstNative.ByteOffset + count * stride > dstNative.Bytes.Length)
                throw new InvalidOperationException(
                    $"Buffer.Memmove がブロック外を参照します (dst offset={dstNative.ByteOffset}, {count * stride} バイト, ブロック {dstNative.Bytes.Length} バイト)。");
            for (var i = 0; i < count; i++) {
                if (dstRef is not null) {
                    // バイト実体 → スロット列
                    dstRef.Container[dstRef.Index + i] =
                        ReadNativeElement(srcNative!, srcNative.ByteOffset + i * stride, elementName);
                } else {
                    // スロット列 → バイト実体
                    WriteNativeElement(dstNative!, dstNative.ByteOffset + i * stride,
                        srcRef!.Container[srcRef.Index + i], elementName);
                }
            }
            return null;
        }
        var byteCount = count * stride;
        // 境界検査: 実 CLR では未定義動作になる参照先の越境は VM では拒否する
        if ((long)dstNative!.ByteOffset + byteCount > dstNative.Bytes.Length ||
            (long)srcNative!.ByteOffset + byteCount > srcNative.Bytes.Length)
            throw new InvalidOperationException(
                $"Buffer.Memmove がブロック外を参照します (dst offset={dstNative.ByteOffset}, src offset={srcNative.ByteOffset}, " +
                $"{byteCount} バイト, ブロック {dstNative.Bytes.Length} / {srcNative.Bytes.Length} バイト)。");
        // Array.Copy は同一配列内の重なりを memmove と同じく正しく扱う
        Array.Copy(srcNative.Bytes, srcNative.ByteOffset, dstNative.Bytes, dstNative.ByteOffset, byteCount);
        return null;
    }

    /// <summary>バイト実体から 1 要素分のスロット値を読む (LE)。</summary>
    private static StackSlot ReadNativeElement(VmNativePointer memory, int byteOffset, string elementName) {
        var bytes = memory.Bytes;
        return elementName switch {
            "System.Byte" => StackSlot.OfInt32(bytes[byteOffset]),
            "System.SByte" => StackSlot.OfInt32((sbyte)bytes[byteOffset]),
            "System.Boolean" => StackSlot.OfInt32(bytes[byteOffset] != 0 ? 1 : 0),
            "System.Char" or "System.Int16" or "System.UInt16" => StackSlot.OfInt32(
                bytes[byteOffset] | bytes[byteOffset + 1] << 8),
            "System.Int32" or "System.UInt32" or "System.Single" => StackSlot.OfInt32(
                bytes[byteOffset] | bytes[byteOffset + 1] << 8 | bytes[byteOffset + 2] << 16 | bytes[byteOffset + 3] << 24),
            "System.Int64" or "System.UInt64" or "System.Double" => StackSlot.OfInt64(
                BitConverter.ToInt64(bytes, byteOffset)),
            "System.IntPtr" or "System.UIntPtr" => StackSlot.OfNativeInt(BitConverter.ToInt64(bytes, byteOffset)),
            _ => throw new InvalidOperationException(
                $"Buffer.Memmove の要素型 {elementName} はバイト実体 ↔ スロット列の変換に対応していません。"),
        };
    }

    /// <summary>スロット値を 1 要素分バイト実体へ書く (LE)。</summary>
    private static void WriteNativeElement(VmNativePointer memory, int byteOffset, in StackSlot slot, string elementName) {
        var bytes = memory.Bytes;
        switch (elementName) {
            case "System.Byte":
                bytes[byteOffset] = (byte)slot.Int64Value;
                break;
            case "System.SByte":
                bytes[byteOffset] = unchecked((byte)(sbyte)slot.Int64Value);
                break;
            case "System.Boolean":
                bytes[byteOffset] = slot.Int64Value != 0 ? (byte)1 : (byte)0;
                break;
            case "System.Char":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((ushort)slot.Int64Value >> 8);
                break;
            case "System.Int16":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((short)slot.Int64Value >> 8);
                break;
            case "System.UInt16":
                bytes[byteOffset] = (byte)slot.Int64Value;
                bytes[byteOffset + 1] = (byte)((ushort)slot.Int64Value >> 8);
                break;
            case "System.Int32":
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(byteOffset), (int)slot.Int64Value);
                break;
            case "System.UInt32":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(byteOffset), (uint)slot.Int64Value);
                break;
            case "System.Single":
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(byteOffset), (float)slot.DoubleValue);
                break;
            case "System.Int64":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(byteOffset), slot.Int64Value);
                break;
            case "System.UInt64":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(byteOffset), (ulong)slot.Int64Value);
                break;
            case "System.Double":
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(byteOffset), slot.DoubleValue);
                break;
            case "System.IntPtr" or "System.UIntPtr":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(byteOffset), slot.Int64Value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Buffer.Memmove の要素型 {elementName} はバイト実体 ↔ スロット列の変換に対応していません。");
        }
    }

    private static StackSlot AddImpl(IntrinsicContext ctx, StackSlot[] a, bool elementStride) {
        var (native, slotRef) = ResolvePointerBase(a[0], elementStride ? "Unsafe.Add" : "Unsafe.AddByteOffset");
        var offset = a[1].Kind == StackKind.Int64 ? a[1].Int64Value : a[1].AsInt32;
        // スロット列ベース (VmArray.Elements 等の配列データ面): 要素加算はスロット
        // インデックスの移動として表現する (1 要素 = 1 スロット)。バイトオフセット面
        // (AddByteOffset) はスロット列では表現できないため fail-closed
        if (slotRef is not null) {
            if (!elementStride)
                throw new InvalidOperationException(
                    "Unsafe.AddByteOffset はバイト実体を持たないスロット列参照には対応していません。");
            var target = slotRef.Index + (int)offset;
            // Unsafe.Add の pointer arithmetic では、ループ終端の比較用に配列末尾の 1 つ先を
            // 作ることがある。形成だけ許可し、実アクセス時の範囲検査は読み書き面に委ねる。
            if (target < 0 || target > slotRef.Container.Length) {
                System.IO.File.AppendAllText(@"C:\Users\Binary_number\AppData\Local\Temp\opencode\frames.log",
                    $"ADDBOOM base0kind={slotRef.Container[0].Kind} baselen={slotRef.Container.Length} index={slotRef.Index} offset={offset} p0={ctx.ParamAt(0)}\n");
                throw new InvalidOperationException(
                    $"Unsafe.Add の結果がスロット列の範囲外です (index {target}, 要素数 {slotRef.Container.Length})。");
            }
            return StackSlot.OfByRef(new VmByRef(slotRef.Container, target));
        }
        var stride = elementStride ? SlotStride(ctx.ParamAt(0)) : 1;
        return StackSlot.OfObject(new VmNativePointer {
            Memory = native!.Memory,
            ByteOffset = native.ByteOffset + (int)(offset * stride),
        });
    }

    private static bool AreSameImpl(StackSlot[] a) {
        var (leftNative, leftRef) = ResolvePointerBase(a[0], "Unsafe.AreSame");
        var (rightNative, rightRef) = ResolvePointerBase(a[1], "Unsafe.AreSame");
        if (leftNative is not null && rightNative is not null)
            return ReferenceEquals(leftNative.Bytes, rightNative.Bytes) &&
                leftNative.ByteOffset == rightNative.ByteOffset;
        if (leftRef is not null && rightRef is not null)
            return ReferenceEquals(leftRef.Container, rightRef.Container) && leftRef.Index == rightRef.Index;
        // バイト実体とスロット列は別の記憶域なので同一になることはない
        return false;
    }

    // ---- System.Runtime.CompilerServices.RuntimeHelpers (InternalCall 面) ----

    private static void RegisterRuntimeHelpers(IntrinsicRegistry r) {
        // internal static RuntimeType GetMethodTable(object obj)
        // CoreLib 上の IL は「ldarg.0; call 自分自身; ret」のダミー自己再帰本体で、実 CLR では
        // JIT が [Intrinsic] として置き換えるため決して実行されない。VM では実行時型ファサード
        // (VmRuntimeObject) を返すことで同意味論を提供する (未バインドだと無限再帰に落ちる)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetMethodTable", "System.Object"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.InternalCall);
        // internal static bool IsReferenceOrContainsReferences(RuntimeType type)
        // (ジェネリック ラッパー IsReferenceOrContainsReferences<T>() の IL から呼ばれる)。
        // 「参照型であるか、参照型フィールドを再帰的に含むか」を VM 型モデルで判定する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences", "System.RuntimeType"),
            static (_, a) => StackSlot.OfInt32(
                a[0].ObjectValue is VmRuntimeObject runtimeType && ContainsReferences(runtimeType.Target) ? 1 : 0),
            BindingOrigin.InternalCall);
        // ジェネリック ラッパー IsReferenceOrContainsReferences<T>() (MethodSpec 面)。
        // 実 IL がランタイム内部表現に直接依存するため IL 実行させず、T の実引数型名
        // (MethodSpec 解決で置換される。未解決なら開いた名 !!0) から VM 型モデルで判定する。
        // 値パラメータ 0 個のため無引数の正確キーで登録する (AnyParams 不要)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences"),
            static (ctx, _) => {
                // ジェネリック ラッパーは値パラメータ 0 個のため T はメソッド型実引数で来る
                var name = ctx.MethodTypeArgAt(0);
                var type = name is "" or "!!0" ? null
                    : (VmType?)ctx.Types.FindIntrinsicType(name) ?? ctx.Types.FindTypeByFullName(name);
                // 未解決は安全側 (参照を含む = 遅い経路を選ぶだけなので過大判定は安全)
                return StackSlot.OfInt32(type is null || ContainsReferences(type) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static T? IsBitwiseEquatable<T>()  ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic)。
        // SpanHelpers.SequenceEqual 等が T を bitwise 比較可能か判定する面 (typeof(T) の呼び出し
        // はメソッド型実引数で判別可能)。プリミティブ数値 / char / bool 系のみ true (Span IL の
        // 早抜け経路に誘導。非 primitive は false = 比較 delegate 経路にフォールバック)。
        // 値パラメータ 0 個のため無引数の正確キーで登録する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsBitwiseEquatable"),
            static (ctx, _) => {
                var name = ctx.MethodTypeArgAt(0);
                var primitive = name is "System.Byte" or "System.SByte" or "System.Char" or "System.Int16"
                    or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64"
                    or "System.UInt64" or "System.Boolean" or "System.IntPtr" or "System.UIntPtr"
                    or "System.Single" or "System.Double";
                return StackSlot.OfInt32(primitive ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static bool IsKnownConstant<T>(T value)  ([Intrinsic]):
        // 本家 IL はダミー throw の JIT intrinsic。VM は JIT 定数畳み込みを持たないため false を返す
        // (未定義でも呼出 IL の fallback 分岐が動くが、false 固定にして恒常経路に誘導する)。
        // .NET 10 の既知署名 4 件を列挙する (非ジェネリック 3 overload + ジェネリック面は開いたキー)
        foreach (var knownConstantParams in new[] {
            new[] { "System.Char" },
            new[] { "System.String" },
            new[] { "System.Type" },
            new[] { "!!0" },
        })
            r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsKnownConstant", knownConstantParams),
                static (_, _) => StackSlot.OfInt32(0),
                BindingOrigin.InternalCall);
        // static ReadOnlySpan<T> RuntimeHelpers.CreateSpan<T>(RuntimeFieldHandle fieldHandle)
        // 本家 IL は GetSpanDataFrom (MethodTable / ネイティブ型ハンドルのランタイム内部) を
        // 辿るため VM 表現境界。ldtoken Field の結果 (VmFieldRvaData の初期データ列) から
        // ReadOnlySpan<T> 構造体値 (_reference + _length) を直接構築する。
        // HashHelpers の素数表等の FieldRVA 静的テーブル面が使う。T はメソッド型実引数で判別する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "CreateSpan", "System.RuntimeFieldHandle"),
            static (ctx, a) => CreateSpanImpl(ctx, a),
            BindingOrigin.InternalCall);
        // static ref byte RuntimeHelpers.GetRawData(object obj)
        // 本家 IL は box への生ポインタを Unsafe.As で基底型幅ごとに読む。
        // VM では box / インスタンスの先頭フィールドへの ByRef、文字列はバイト実体で提供する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetRawData", "System.Object"),
            static (_, a) => a[0].ObjectValue switch {
                VmBoxedValue box => StackSlot.OfByRef(new VmByRef(box.Fields, 0)),
                VmClassInstance instance when instance.Fields.Length > 0 =>
                    StackSlot.OfByRef(new VmByRef(instance.Fields, 0)),
                VmString str => StackSlot.OfObject(new VmNativePointer {
                    Memory = str.PointerMemory,
                    ByteOffset = VmString.CharDataByteOffset,
                }),
                _ => throw new InvalidOperationException(
                    $"RuntimeHelpers.GetRawData の引数を生データ参照にできません ({a[0].Kind})。"),
            },
            BindingOrigin.InternalCall);
    }

    private static StackSlot CreateSpanImpl(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].ObjectValue is not VmFieldRvaData rva)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の引数がフィールド RVA データではありません ({a[0].Kind})。");
        var tName = ctx.MethodTypeArgAt(0);
        if (string.IsNullOrEmpty(tName) || tName is "!!0")
            throw new InvalidOperationException(
                "RuntimeHelpers.CreateSpan のジェネリック型引数 T を判別できませんでした。");
        var stride = SlotStride(tName);
        var bytes = rva.Data.ToArray();
        if (bytes.Length % stride != 0)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の初期データ ({bytes.Length} バイト) が要素幅 {stride} で割り切れません。");
        var t = FindAnyType(ctx, tName)
            ?? throw new InvalidOperationException($"RuntimeHelpers.CreateSpan の要素型 {tName} を解決できません。");
        var def = ctx.Types.FindTypeByFullName("System.ReadOnlySpan`1") as VmClassType
            ?? FindAnyType(ctx, "System.ReadOnlySpan`1") as VmClassType
            ?? throw new InvalidOperationException("System.ReadOnlySpan`1 がロードされていません。");
        if (def.GenericParamCount != 1)
            throw new InvalidOperationException("System.ReadOnlySpan`1 の型引数個数が不正です。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [t] };
        var native = new VmNativePointer {
            Memory = new VmLocallocMemory { Bytes = bytes },
            ByteOffset = 0,
        };
        // フィールド配置は GetLayout (正規レイアウト) に合わせる
        // (FieldLocation 等の読み側と一致させるため宣言順の独自採番はしない)
        var layout = new ObjectModel().GetLayout(def);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var field in def.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            fields[layout[field]] = field.Name switch {
                "_reference" => StackSlot.OfObject(native),
                "_length" => StackSlot.OfInt32(bytes.Length / stride),
                _ => throw new InvalidOperationException(
                    $"System.ReadOnlySpan`1 の未知フィールド {field.Name} があります。"),
            };
        }
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }

    // ---- System.Runtime.Intrinsics.Vector64/128/256/512 (JIT intrinsic 判定面) ----

    /// <summary>本家 get_IsHardwareAccelerated は [Intrinsic] 付きのダミー自己再帰 IL
    /// (return IsHardwareAccelerated;) で、実 CLR では JIT がハードウェア判定へ置換するため
    /// 決して実行されない (RuntimeHelpers.GetMethodTable と同型)。VM は SIMD 値型
    /// (Vector128&lt;T&gt; 等の 16/32/64 バイト inline 表現) を持たないため SIMD パスの IL を
    /// 実行できず、true を返すと SpanHelpers 等が SIMD 面で fail-closed に落ちる。
    /// そこで false を返して scalar フォールバック IL に誘導する (string SCI overload 群が
    /// 同一結果を得る経路)。SIMD 演算面自体は未バインド → fail-closed。ゲストがこの面を
    /// 直接観測した場合の実 x64 CLR (true) との既知差異は SIMD 表現境界として監査表に記載。</summary>
    private static void RegisterVectorIntrinsics(IntrinsicRegistry r) {
        foreach (var vectorType in new[] {
            "System.Runtime.Intrinsics.Vector64",
            "System.Runtime.Intrinsics.Vector128",
            "System.Runtime.Intrinsics.Vector256",
            "System.Runtime.Intrinsics.Vector512",
        }) {
            r.RegisterBinding(BindingKey.Static(vectorType, "get_IsHardwareAccelerated"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
        // X86.Sse2.get_IsSupported は同じくダミー自己再帰 IL (return IsSupported;) で、
        // SpanHelpers.IndexOfValueType 等が PackedSpanHelpers 経由で参照する。
        // false を返して scalar フォールバック IL に誘導する (Vector 面と同一方針)。
        // X86 の他面 (Sse41 等) は到達した面から順次追加する (fail-driven)。
        r.RegisterBinding(BindingKey.Static("System.Runtime.Intrinsics.X86.Sse2", "get_IsSupported"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        foreach (var x86Type in new[] {
            "System.Runtime.Intrinsics.X86.Avx2",
            "System.Runtime.Intrinsics.X86.Lzcnt",
        }) {
            r.RegisterBinding(BindingKey.Static(x86Type, "get_IsSupported"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
    }

    // ---- System.Runtime.InteropServices.Marshal / Interop+Kernel32 (環境取得起動面) ----

    /// <summary>VM 代替の last system error (Marshal.SetLastSystemError / GetLastSystemError 面)。
    /// 実 CLR の per-thread TLS スロットの代わりに VM ごとの共有状態 (VmSharedState) で模倣する。
    /// CultureInfo.InvariantCulture 初期化 (GlobalizationMode → GetEnvironmentVariableCore)
    /// が GetLastSystemError() &lt; ERROR_ENVVAR_NOT_FOUND(203) で成功判定に使う。
    /// (タスク 2 hardening: これらの面は TrustedCoreLib domain に属し、trusted CoreLib IL
    /// からの呼出のみ照合される。呼出元は caller loader 基準で判定する。)</summary>

    /// <summary>VM ごとの仮想環境変数ストアは VmSharedState.VirtualEnvironment を使う
    /// (static 共有にしない。VM ごとに分離する)。
    /// Kernel32.GetEnvironmentVariable 面は host の Environment.GetEnvironmentVariable を
    /// 直接呼ばず、この VM ごとの仮想環境のみを参照する (host 環境の読み替えを遮断し、
    /// trusted CoreLib domain 呼出でのみ到達する特権面に)。
    /// 既定は空 (GlobalizationMode::get_Invariant を true 固定にする呼出経路が
    /// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT の実環境読み取りに依存しない)。</summary>

    /// <summary>本家 CultureInfo::.cctor → CultureData.get_Invariant → GlobalizationMode の
    /// IL は AppContextConfigHelper → Environment.GetEnvironmentVariableCore を辿り、その
    /// Kernel32.GetEnvironmentVariable(name, buffer, size) が P/Invoke 面。
    /// タスク 2 hardening: この面は trusted CoreLib (TrustedCoreLib domain) からの呼出のみ
    /// 到達する特権面 (BindingDomain.TrustedCoreLib) とし、VM ごとの仮想環境変数ストアを
    /// 読む (host Environment.GetEnvironmentVariable への直接委譲を廃止)。
    /// バッファへは Win32 規約 (戻り = 終端 null 除くコピー文字数 / 不足時は終端含む
    /// 必要文字数を返すのみ、未定義なら 0 + lastError = 203) で書き込む。
    /// Marshal の 4 面は IL 実体が下請け P/Invoke shim 呼びのみのため
    /// internal-call リーフで VM lastError に代替する (trusted CoreLib 限定。
    /// 状態は ctx.Shared の VM インスタンス状態)。</summary>
    private static void RegisterEnvironmentAndMarshal(IntrinsicRegistry r) {
        const string MarshalType = "System.Runtime.InteropServices.Marshal";
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastSystemError", "System.Int32"),
            static (ctx, a) => {
                ctx.Shared.LastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastSystemError"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.LastSystemError),
            BindingOrigin.InternalCall);
        // SystemError/PInvokeError は実 CLR でも同一 TLS スロットの alias 面
        // (SetLastSystemError/GetLastSystemError の IL 実体が呼ぶ下請け)
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastPInvokeError", "System.Int32"),
            static (ctx, a) => {
                ctx.Shared.LastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastPInvokeError"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.LastSystemError),
            BindingOrigin.InternalCall);
        // Kernel32.GetEnvironmentVariable は trusted CoreLib (GlobalizationMode 経路) からの
        // 起動面としてのみ有効な特権面。BindingDomain.TrustedCoreLib で鍵化し、
        // 呼出元 loader 基準 (CallEngine.CallerDomainOf) で trusted CoreLib IL からの
        // 呼出のみ照合する
        r.RegisterBinding(BindingKey.TrustedStatic("Interop+Kernel32", "GetEnvironmentVariable",
                "System.String", "System.Char&", "System.UInt32"),
            static (ctx, a) => GetEnvironmentVariableImpl(ctx, a),
            BindingOrigin.PInvokeReplacement);
        // Interop+BCrypt.BCryptGenRandom (Marvin ハッシュ種等の乱数源 P/Invoke)。
        // ネイティブ実行はしない。VM 決定論規約 (仮想コンソールの決定的入力と同型) により
        // 決定論的ゼロ埋めで代替し STATUS_SUCCESS (0) を返す。CLR (プロセス毎ランダム) と
        // 種値は異なるが、ハッシュ利用面 (Dictionary 等) の観測意味論は同一。
        // ゲストからの直接呼出は TrustedCoreLib domain 遮断で拒否される
        r.RegisterBinding(BindingKey.TrustedStatic("Interop+BCrypt", "BCryptGenRandom",
                "System.IntPtr", "System.Byte*", "System.Int32", "System.Int32"),
            static (_, a) => {
                var count = a[2].AsInt32;
                if (count < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                if (count == 0)
                    return StackSlot.OfInt32(0);
                if (a[1].ObjectValue is VmNativePointer native) {
                    if ((long)native.ByteOffset + count > native.Bytes.Length)
                        throw new InvalidOperationException(
                            $"BCryptGenRandom がブロック外を参照します (offset={native.ByteOffset}, {count} バイト)。");
                    Array.Clear(native.Bytes, native.ByteOffset, count);
                    return StackSlot.OfInt32(0);
                }
                // マネージポインタ (stackalloc ulong 等のスロット列): 要素をゼロ化する
                // (Marvin.GenerateSeed の ulong 変数等。8 バイト = Int64 1 スロット)
                if (a[1].Kind == StackKind.ByRef && a[1].ObjectValue is VmByRef byRef) {
                    if (byRef.Container.Length == 0)
                        throw new UnhandledGuestException("System.NullReferenceException", null);
                    if (count == 8 && byRef.Index < byRef.Container.Length &&
                        byRef.Container[byRef.Index].Kind == StackKind.Int64) {
                        byRef.Container[byRef.Index] = StackSlot.OfInt64(0);
                        return StackSlot.OfInt32(0);
                    }
                    var slots = (count + 7) / 8;
                    if (byRef.Index < 0 || byRef.Index + slots > byRef.Container.Length)
                        throw new InvalidOperationException(
                            $"BCryptGenRandom がスロット列の範囲外を参照します (index={byRef.Index}, {count} バイト)。");
                    for (var i = 0; i < slots; i++)
                        byRef.Container[byRef.Index + i] = StackSlot.OfInt64(0);
                    return StackSlot.OfInt32(0);
                }
                throw new InvalidOperationException(
                    $"BCryptGenRandom のバッファがバイト実体ではありません ({a[1].Kind})。");
            },
            BindingOrigin.PInvokeReplacement);
        // GlobalizationMode+Settings::get_Invariant を true 固定にする (VM 規約: culture は
        // 不変カルチャ固定)。本家の DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 起動と同一意味論
        // で、本家 IL の分岐は CultureInfo::GetUserDefaultLocaleName 等の OS locale 取得
        // (Kernel32 P/Invoke) を通らない managed 経路に落ちる。OS locale 面自体は
        // culture 機構スコープ外のため代替実装を持たない (fail-closed を維持)
        r.RegisterBinding(BindingKey.TrustedStatic("System.Globalization.GlobalizationMode+Settings", "get_Invariant"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        // Type::GetTypeFromHandle: 本家 IL 本体は RuntimeType.GetTypeFromHandle (runtime
        // intrinsic = IL なし) の呼び出しを含むため ② IL 実行に落とせない (ldfld
        // RuntimeTypeHandle::m_type が VM 表現境界外)。実 CLR もこの面は IL を実行しない
        // (監査表 (c) runtime-representation)。VmTypeHandle → VmRuntimeObject ファサード変換
        // として同等面を提供する
        r.RegisterBinding(BindingKey.Static("System.Type", "GetTypeFromHandle", "System.RuntimeTypeHandle"),
            static (ctx, a) => a[0].ObjectValue is VmTypeHandle handle
                ? DefaultIntrinsics.MakeRuntimeObject(ctx, handle.Target)
                : throw new InvalidOperationException("GetTypeFromHandle の引数が RuntimeTypeHandle ではありません。"),
            BindingOrigin.InternalCall);
    }

    // ---- System.Threading.Interlocked (JIT intrinsic 面) ----

    /// <summary>本家 Interlocked は IL 本体が JIT intrinsic ダミー (typeof(T); throw new
    /// PlatformNotSupportedException()) のため、② IL 実行に落ちるとダミー本体が表出する。
    /// 実 CLR では JIT が命令列へ置換するため IL は決して実行されない (監査表 (c) jit-intrinsic)。
    /// legacy intrinsic (③) は IL 本体なしの面しか受けないため、ここに ① バインドで登録する。
    /// VM の共有 atomic gate と参照スロットロックで比較と交換を原子的に行う (CLR と同じ意味論)。</summary>
    private static void RegisterInterlockedBindings(IntrinsicRegistry r) {
        const string T = "System.Threading.Interlocked";
        static VmByRef Location(StackSlot slot, string method) =>
            slot.ObjectValue as VmByRef
            ?? throw new UnhandledGuestException("System.ArgumentException",
                $"Interlocked.{method} の第 1 引数は ref フィールド (ByRef) である必要があります。");
        // 比較対象の等価判定 (プリミティブは値、参照は同一性)
        static bool SlotEquals(in StackSlot x, in StackSlot y) {
            if (x.Kind != y.Kind)
                return false;
            return x.Kind switch {
                StackKind.Empty => true,
                StackKind.Object or StackKind.ByRef => ReferenceEquals(x.ObjectValue, y.ObjectValue),
                StackKind.Float => x.DoubleValue.Equals(y.DoubleValue),
                _ => x.Int64Value == y.Int64Value,
            };
        }
        // ---- Interlocked 面 (タスク 2 hardening: AnyParams → .NET 10 既知署名の列挙)。
        // 本家 CoreLib v10 の Interlocked 公開面を実署名列挙で登録する (wildecard 廃止)。
        // パラメータ型名は VM 表現と CoreLib 署名の実型 (統合後) 完全名で鍵化する。
        // i4 統合面 (byte/sbyte/short/ushort/bool/char/int が i4 スロットに載る) は 1 面で受け、
        // i8 / float / double / nint / nuint は別キーで鍵化
        static string[] I(string t) => [t];
        const string I4 = "System.Int32", I8 = "System.Int64", R4 = "System.Single", R8 = "System.Double";
        const string Ref1 = "&";
        void InstanceFace(string name, string[] paramTypes, IntrinsicImpl impl) =>
            r.RegisterBinding(BindingKey.StaticWithReturn(T, name, paramTypes[0].TrimEnd('&'), paramTypes), impl, BindingOrigin.InternalCall);

        // Increment(ref T) / Decrement(ref T): (ref int,int) と (ref long,long) の両面.
        foreach (var (ty, tyName) in new[] { (I4, "System.Int32"), (I8, "System.Int64") }) {
            var vt = tyName;
            r.RegisterBinding(BindingKey.StaticWithReturn(T, "Increment", vt, [vt + Ref1]), static (_, a) => {
                var loc = Location(a[0], "Increment");
                var updated = loc.Slot.Kind == StackKind.Int64
                    ? StackSlot.OfInt64(loc.Slot.Int64Value + 1)
                    : StackSlot.OfInt32((int)loc.Slot.Int64Value + 1);
                loc.Slot = updated;
                return updated;
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.StaticWithReturn(T, "Decrement", vt, [vt + Ref1]), static (_, a) => {
                var loc = Location(a[0], "Decrement");
                var updated = loc.Slot.Kind == StackKind.Int64
                    ? StackSlot.OfInt64(loc.Slot.Int64Value - 1)
                    : StackSlot.OfInt32((int)loc.Slot.Int64Value - 1);
                loc.Slot = updated;
                return updated;
            }, BindingOrigin.InternalCall);
        }
        // Exchange / CompareExchange / Add / And / Or: int / long / uint / ulong / float / double / nint / nuint 面水準
        static void RegExchangeFamily(IntrinsicRegistry reg, string type, IntrinsicImpl exchange, IntrinsicImpl compareExchange, IntrinsicImpl add)
        {
            foreach (var param in new[] {
                // ref int, int
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
                new[] { "System.Single&", "System.Single" },
                new[] { "System.Double&", "System.Double" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "Exchange", param[0].TrimEnd('&'), param), exchange, BindingOrigin.InternalCall);
            }
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32", "System.Int32" },
                new[] { "System.Int64&", "System.Int64", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64", "System.UInt64" },
                new[] { "System.Single&", "System.Single", "System.Single" },
                new[] { "System.Double&", "System.Double", "System.Double" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "CompareExchange", param[0].TrimEnd('&'), param), compareExchange, BindingOrigin.InternalCall);
            }
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "Add", param[0].TrimEnd('&'), param), add, BindingOrigin.InternalCall);
            }
        }
        var exchangeImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Exchange");
            var original = loc.Slot;
            loc.Slot = a[1];
            return original;
        });
        var compareExchange = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "CompareExchange");
            var original = loc.Slot;
            var equal = SlotEquals(original, a[2]);
            if (equal)
                loc.Slot = a[1];
            if (a.Length >= 4 && a[3].ObjectValue is VmByRef succeeded)
                succeeded.Write(StackSlot.OfInt32(equal ? 1 : 0));
            return original;
        });
        var addImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Add");
            StackSlot updated;
            if (loc.Slot.Kind == StackKind.Int64) {
                updated = StackSlot.OfInt64(loc.Slot.Int64Value + a[1].Int64Value);
            } else {
                int sum;
                try { sum = checked((int)loc.Slot.Int64Value + a[1].AsInt32); }
                catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
                updated = StackSlot.OfInt32(sum);
            }
            loc.Slot = updated;
            return updated;
        });
        RegExchangeFamily(r, T, exchangeImpl, compareExchange, addImpl);

        // 参照型 CompareExchange<T>/Exchange<T> ( CultureInfo 初期化等の lock-free 起動経路)。
        // 本家はジェネリック面のため開いたキー (!!0) で 1 件ずつ登録する。共有 atomic gate
        // の中で参照同一性 (SlotEquals の ReferenceEquals 側) による比較交換を行う。
        // 値型の具体面は上の正確キーが先に一致する
        r.RegisterBinding(BindingKey.Static(T, "Exchange", "!!0&", "!!0"),
            exchangeImpl, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "CompareExchange", "!!0&", "!!0", "!!0"),
            compareExchange, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "CompareExchange", "!!0&", "!!0", "!!0", "System.Boolean&"),
            compareExchange, BindingOrigin.InternalCall);

        static void RegAndOrFamily(IntrinsicRegistry reg, string type, string method, IntrinsicImpl impl)
        {
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, method, param[0].TrimEnd('&'), param), impl, BindingOrigin.InternalCall);
            }
        }
        var andImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "And");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value & a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value & a[1].AsInt32);
            return original;
        });
        var orImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Or");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value | a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value | a[1].AsInt32);
            return original;
        });
        RegAndOrFamily(r, T, "And", andImpl);
        RegAndOrFamily(r, T, "Or", orImpl);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "MemoryBarrier", "System.Void", Array.Empty<string>()),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ReadMemoryBarrier", "System.Void", []),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "WriteMemoryBarrier", "System.Void", []),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
    }

    private static StackSlot GetEnvironmentVariableImpl(IntrinsicContext ctx, StackSlot[] a) {
        var name = (a[0].ObjectValue as VmString)?.Value;
        var (native, slotRef) = ResolvePointerBase(a[1], "Interop+Kernel32.GetEnvironmentVariable");
        if (name is null || (native is null && slotRef is null))
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // host Environment への直接委譲を廃止: VM ごとの仮想環境変数ストア (ctx.Shared) を読む
        var value = ctx.Shared.VirtualEnvironment.TryGetValue(name, out var found) ? found : null;
        ctx.Shared.LastSystemError = value is null ? 203 /* ERROR_ENVVAR_NOT_FOUND */ : 0;
        if (string.IsNullOrEmpty(value))
            return StackSlot.OfInt32(0);
        // バッファ不足 (nSize <= 文字数): 書き込みは行わず終端含む必要文字数を返す
        // (lpBuffer 内容は Win32 規約上不定 — 呼び出し側の GetEnvironmentVariableCore は
        // この戻りで容量を確保して再試行する)
        if (value.Length + 1 > a[2].AsInt32)
            return StackSlot.OfInt32(value.Length + 1);
        if (native is not null) {
            var offset = native.ByteOffset;
            if (offset < 0 || (long)offset + value.Length * 2 + 2 > native.Bytes.Length)
                throw new InvalidOperationException(
                    $"Kernel32.GetEnvironmentVariable のバッファがブロック外を参照します (offset={offset}, 必要 {value.Length + 1} 文字, ブロック {native.Bytes.Length} バイト)。");
            var bytes = native.Bytes;
            for (var i = 0; i < value.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + i * 2, 2), (short)value[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + value.Length * 2, 2), (short)'\0');
        } else {
            for (var i = 0; i < value.Length; i++)
                slotRef!.Container[slotRef.Index + i] = StackSlot.OfInt32(value[i]);
            slotRef!.Container[slotRef.Index + value.Length] = StackSlot.OfInt32('\0');
        }
        return StackSlot.OfInt32(value.Length);
    }

    /// <summary>CLR の IsReferenceOrContainsReferences 意味論: 型が参照型であるか、
    /// 参照型フィールド (再帰的に) を含むか。プリミティブ / enum は false。
    /// 未解決フィールドは安全側 (true) に倒す (呼出側は遅い経路を選ぶだけなので過大判定は安全)。</summary>
    private static bool ContainsReferences(VmType type) {
        var definition = type is VmConstructedType constructed ? constructed.Definition : type;
        if (definition is VmArrayType or VmMultiDimArrayType or VmByRefType)
            return true;
        if (!definition.IsValueType)
            return true;
        if (definition.FullName == "System.Enum" || definition.IsEnum ||
            VmPrimitiveTypes.IsSlotPrimitive(definition.FullName))
            return false;
        var visited = new HashSet<VmType>();
        return ContainsFields(definition, visited);
    }

    private static bool ContainsFields(VmType definition, HashSet<VmType> visited) {
        if (!visited.Add(definition))
            return false; // 循環するフィールド構造 (異常画像) は打ち切り
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            var fieldType = field.FieldType;
            if (fieldType is null)
                return true; // 未解決フィールドは安全側に倒す
            // 構築ジェネリック型のフィールド (!0 等) は実引数で置換して判定する
            if (definition is VmConstructedType constructed)
                fieldType = GenericSubstitutor.Substitute(fieldType,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
            if (ContainsReferences(fieldType))
                return true;
        }
        return false;
    }
}

