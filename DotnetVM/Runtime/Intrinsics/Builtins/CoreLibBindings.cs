using System.Buffers.Binary;
using System.Reflection;
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
        RegisterComparableInterfaces(r);
        RegisterPrimitiveToString(r);
        RegisterDecimalBindings(r);
        RegisterTimeCultureFaces(r);
        RegisterMathBindings(r);
        RegisterSystemSr(r);
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
                    byref.Slot = StackSlot.OfValueType(MakeDecimalStruct(ctx, ok ? value : 0m));
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
            return x.DoubleValue == y.DoubleValue;
        var lx = x.Kind == StackKind.Int64 ? x.Int64Value : (long)x.AsInt32;
        var ly = y.Kind == StackKind.Int64 ? y.Int64Value : (long)y.AsInt32;
        return lx == ly;
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
        // CoreLib の IL は to 内 Culture 太字異 (FormatFeatures / CultureInfo) を辿り culture 機構
        // 全面に落ちるため表現境界。VM enum box 型 (value__ 基底型生値) をホストの同名 enum
        // (typeof(object).Assembly 同一名照合) で再構成し、G 書式名をホスト文化自立の文字列化で返す
        // (DayOfWeek.ToString 等の F10 / Enum 書式 IL が依存するリーフ、culture-out-of-scope)
        r.RegisterBinding(BindingKey.Instance("System.Enum", "ToString"),
            static (ctx, a) => {
                var box = a[0].ObjectValue as VmBoxedValue
                    ?? throw new UnhandledGuestException("System.InvalidCastException", null);
                var raw = box.Fields[0];
                object hostValue = box.Type.FullName switch {
                    "System.DayOfWeek" => (System.DayOfWeek)raw.AsInt32,
                    "System.Boolean" => (bool)(raw.AsInt32 != 0),
                    _ => raw.Kind == StackKind.Int64
                        ? (box.Type.FullName == "System.Int64" ? (object)(long)raw.Int64Value : (object)(ulong)raw.Int64Value)
                        : raw.AsInt32,
                };
                return StackSlot.OfObject(ctx.MakeString(hostValue.ToString()!));
            },
            BindingOrigin.InternalCall);
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
            byref.Slot = StackSlot.OfFloat(a[0].DoubleValue - intPart);
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
        // static abstract char IUtfChar<T>.CastFrom(T)  ([Intrinsic]: 実 IL はダミー throw。
        // String/span IL が T(char)→char の面を経由するため、char 系 T の値を i4 スロットで透過)
        r.RegisterBinding(BindingKey.StaticAnyParams("System.IUtfChar`1", "CastFrom"),
            static (_, a) => StackSlot.OfInt32(a[0].AsInt32),
            BindingOrigin.InternalCall);
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

    // ---- System.Threading.Monitor (VM は単一スレッド実行のため競合なしのロック面) ----

    private static void RegisterThreading(IntrinsicRegistry r) {
        // CoreLib Monitor の InternalCall 面 4 件 (Enter/Exit の IL から呼ばれる)。
        // VM にはスレッドがなく同期ブロックもないため、常に即時取得・競合なしで即解放する:
        //   TryEnter_FastPath(obj)              → true (競合しないので FastPath で取得成功。
        //                                                  bool 返し (Enter IL が brtrue で分岐))
        //   TryEnter_FastPath_WithTimeout(...)  → true (待ちなしで取得成功)
        //   Exit_FastPath(obj)                  → void (保持解除は no-op)
        //   IsEnteredNative(obj)                → false (ロックを保持しないモデルと整合)
        // パラメータ構成は CoreLib のバージョンで揺れうるため、面名ごとの全引数一致キーにする
        const string T = "System.Threading.Monitor";
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "TryEnter_FastPath"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "TryEnter_FastPath_WithTimeout"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Exit_FastPath"),
            static (_, _) => null, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "IsEnteredNative"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
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
        // internal static extern string String.FastAllocateString(int charCount)
        // CoreLib の InternalCall 面。実 CLR の確保点 (FastAllocateString) と同じ位置で
        // VmString の可変 char バッファを VmHeap 会計つきで確保する
        r.RegisterBinding(BindingKey.StaticAnyParams("System.String", "FastAllocateString"),
            static (ctx, a) => StackSlot.OfObject(ctx.Strings.Allocate(a[0].AsInt32)),
            BindingOrigin.InternalCall);
        // static void Buffer.Memmove<T>(ref T destination, ref T source, nuint elementCount)
        // String 構築 IL (InternalSubString 等) が ref char で呼ぶ。実 CLR では JIT intrinsic だが
        // VM はバイト実体 (VmString.Bytes / VmLocallocMemory) 間の memmove として同等意味論を提供する
        r.RegisterBinding(BindingKey.StaticAnyParams("System.Buffer", "Memmove"),
            static (ctx, a) => MemmoveImpl(ctx, a), BindingOrigin.InternalCall);
        // Unsafe.* ([Intrinsic]: 実 CLR も JIT が IL を置き換える面。CoreLib IL 内では
        // ref 値の byte 単位のアドレス演算として現れるため、バイト実体ポインタで同等に提供する)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "Add"),
            static (ctx, a) => AddImpl(ctx, a, elementStride: true), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AddByteOffset"),
            static (ctx, a) => AddImpl(ctx, a, elementStride: false), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "As"),
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
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AreSame"),
            static (_, a) => StackSlot.OfInt32(AreSameImpl(a) ? 1 : 0), BindingOrigin.InternalCall);
        // ref TTo Unsafe.AsRef<T>(void*) / ref readonly T Unsafe.AsRef<T>(in T)
        // ([Intrinsic]: ReadOnlySpan(in T&) ctor の IL が呼ぶ。実 IL はダミーで
        // PlatformNotSupportedException を投げるため、参照をそのまま透過させる同等意味論を提供する)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "AsRef"),
            static (_, a) =>
                a[0].Kind == StackKind.ByRef && a[0].ObjectValue is VmByRef byRef
                    ? StackSlot.OfByRef(byRef)
                    : throw new InvalidOperationException(
                        $"Unsafe.AsRef の引数をスロット列参照として解釈できませんでした ({a[0].Kind})。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "SizeOf"),
            static (ctx, _) => StackSlot.OfInt32(SlotStride(ctx.ParamAt(0))), BindingOrigin.InternalCall);
        // static TTo Unsafe.BitCast<TFrom, TTo>(TFrom from)
        // ([Intrinsic]: 実 IL はダミー throw。same-size 値型のビット再解釈として同等意味論を
        // 提供する。Math.Abs(double) の IL が BitConverter.DoubleToUInt64Bits 経由で呼ぶ)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "BitCast"),
            static (ctx, a) => BitCastImpl(ctx.ParamAt(0), a[0]), BindingOrigin.InternalCall);
        // static void Unsafe.CopyBlockUnaligned(ref byte destination, ref byte source, nuint byteCount)
        // ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic。String / Span IL がバイト実体コ
        // ピーに使うため Buffer.Memmove 相当の memmove で同等意味論を提供する)
        r.RegisterBinding(BindingKey.StaticAnyParams(UnsafeType, "CopyBlockUnaligned"),
            static (ctx, a) => MemmoveImpl(ctx, a, strideOverride: 1), BindingOrigin.InternalCall);
        // ref T MemoryMarshal.GetArrayDataReference<T>(T[] array) / ref byte (Array array)
        // ([Intrinsic]: 実 IL は配列データ先頭へのランタイム内部参照。IL を実行させると
        // 同名オーバーロードへの自己再帰に落ちるため、VM は要素格納列の先頭スロットへの
        // VmByRef で同等意味論を提供する (Span<T> 構築 IL の GetArrayDataReference 面用)
        r.RegisterBinding(BindingKey.StaticAnyParams("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference"),
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
            var target = outer.Slot; // 指し先スロットの値
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
            if (target < 0 || target >= slotRef.Container.Length)
                throw new InvalidOperationException(
                    $"Unsafe.Add の結果がスロット列の範囲外です (index {target}, 要素数 {slotRef.Container.Length})。");
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
        // 正確なキーが先に照合され、この AnyParams 面は generic ラッパー (キー不一致) のみ受ける
        r.RegisterBinding(BindingKey.StaticAnyParams(RuntimeHelpersType, "IsReferenceOrContainsReferences"),
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
        // 早抜け経路に誘導。非 primitive は false = 比較 delegate 経路にフォールバック)
        r.RegisterBinding(BindingKey.StaticAnyParams(RuntimeHelpersType, "IsBitwiseEquatable"),
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
        // (未定義でも呼出 IL の fallback 分岐が動くが、false 固定にして恒常経路に誘導する)
        r.RegisterBinding(BindingKey.StaticAnyParams(RuntimeHelpersType, "IsKnownConstant"),
            static (_, _) => StackSlot.OfInt32(0),
            BindingOrigin.InternalCall);
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
    }

    // ---- System.Runtime.InteropServices.Marshal / Interop+Kernel32 (環境取得起動面) ----

    /// <summary>VM 代替の last system error (Marshal.SetLastSystemError / GetLastSystemError 面)。
    /// 実 CLR の per-thread TLS スロットの代わりにスレッドローカルの単一値で模倣する。
    /// CultureInfo.InvariantCulture 初期化 (GlobalizationMode → GetEnvironmentVariableCore)
    /// が GetLastSystemError() &lt; ERROR_ENVVAR_NOT_FOUND(203) で成功判定に使う。
    /// (タスク 2 hardening: これらの面は TrustedCoreLib domain に属し、trusted CoreLib IL
    /// からの呼出のみ照合される。)</summary>
    [ThreadStatic]
    private static int _lastSystemError;

    /// <summary>VM ごとの仮想環境変数ストア (タスク 2 hardening)。
    /// Kernel32.GetEnvironmentVariable 面は host の Environment.GetEnvironmentVariable を
    /// 直接呼ばず、この VM ごとの仮想環境のみを参照する (host 環境の読み替えを遮断し、
    /// trusted CoreLib domain 呼出でのみ到達する特権面に)。
    /// 既定は空 (GlobalizationMode::get_Invariant を true 固定にする呼出経路が
    /// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT の実環境読み取りに依存しない)。</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> VirtualEnvironment =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本家 CultureInfo::.cctor → CultureData.get_Invariant → GlobalizationMode の
    /// IL は AppContextConfigHelper → Environment.GetEnvironmentVariableCore を辿り、その
    /// Kernel32.GetEnvironmentVariable(name, buffer, size) が P/Invoke 面。
    /// タスク 2 hardening: この面は trusted CoreLib (TrustedCoreLib domain) からの呼出のみ
    /// 到達する特権面 (BindingDomain.TrustedCoreLib) とし、VM ごとの仮想環境変数ストアを
    /// 読む (host Environment.GetEnvironmentVariable への直接委譲を廃止)。
    /// バッファへは Win32 規約 (戻り = 終端 null 除くコピー文字数 / 不足時は終端含む
    /// 必要文字数を返すのみ、未定義なら 0 + lastError = 203) で書き込む。
    /// Marshal の 4 面は IL 実体が下請け P/Invoke shim 呼びのみのため
    /// internal-call リーフで VM lastError に代替する (trusted CoreLib 限定)。</summary>
    private static void RegisterEnvironmentAndMarshal(IntrinsicRegistry r) {
        const string MarshalType = "System.Runtime.InteropServices.Marshal";
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastSystemError", "System.Int32"),
            static (_, a) => {
                _lastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastSystemError"),
            static (_, _) => StackSlot.OfInt32(_lastSystemError),
            BindingOrigin.InternalCall);
        // SystemError/PInvokeError は実 CLR でも同一 TLS スロットの alias 面
        // (SetLastSystemError/GetLastSystemError の IL 実体が呼ぶ下請け)
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastPInvokeError", "System.Int32"),
            static (_, a) => {
                _lastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastPInvokeError"),
            static (_, _) => StackSlot.OfInt32(_lastSystemError),
            BindingOrigin.InternalCall);
        // Kernel32.GetEnvironmentVariable は trusted CoreLib (GlobalizationMode 経路) からの
        // 起動面としてのみ有効な特権面。BindingDomain.TrustedCoreLib で鍵化し、トレース
        // 時 (CallEngine の callerDomain 判定) は trusted CoreLib IL からの呼出のみ照合する
        r.RegisterBinding(BindingKey.TrustedStatic("Interop+Kernel32", "GetEnvironmentVariable",
                "System.String", "System.Char&", "System.UInt32"),
            static (_, a) => GetEnvironmentVariableImpl(a),
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
    /// VM は単一スレッドで走るため比較と交換は逐次実行で競合なし (意味論は CLR と同一)。</summary>
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
                succeeded.Slot = StackSlot.OfInt32(equal ? 1 : 0);
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
            static (_, _) => null, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ReadMemoryBarrier", "System.Void", []),
            static (_, _) => null, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "WriteMemoryBarrier", "System.Void", []),
            static (_, _) => null, BindingOrigin.InternalCall);
    }

    private static StackSlot GetEnvironmentVariableImpl(StackSlot[] a) {
        var name = (a[0].ObjectValue as VmString)?.Value;
        var (native, slotRef) = ResolvePointerBase(a[1], "Interop+Kernel32.GetEnvironmentVariable");
        if (name is null || (native is null && slotRef is null))
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // host Environment への直接委譲を廃止: VM ごとの仮想環境変数ストアを読む
        var value = VirtualEnvironment.GetValueOrDefault(name);
        _lastSystemError = value is null ? 203 /* ERROR_ENVVAR_NOT_FOUND */ : 0;
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

