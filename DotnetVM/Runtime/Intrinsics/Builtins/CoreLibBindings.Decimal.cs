using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
// ---- System.Decimal (演算・変換・解析面) ----

    /// <summary>decimal の演算 / 変換 / 解析面。
    /// 本家 IL 本体は Decimal 構造体と内部 DecCalc 構造体の Unsafe.As 参照再解釈
    /// (同一ビット列の型視点差し替え) で構成されるため、VM のオブジェクト表現
    /// (VmStructValue のフィールドスロット列) では IL 実行にできない。
    /// 実 CLR もこれらの面を JIT intrinsic / ランタイム内部として処理することと同型であるため、
    /// 同一意味論のホスト BCL 実装へ委譲し、戻り値は VM の System.Decimal 構造体値に正規化する。
    /// ToString 書式面は VmCoreLibSurfaces 経由の DotnetVM.CoreLib.DecimalFormatting
    /// (不変カルチャ固定) が担当 (Faces 置換面が先に解決されるため共存は競合しない)。Parse は VM 設定カルチャを使う。</summary>
    private static void RegisterDecimalBindings(IntrinsicRegistry r) {
        const string T = "System.Decimal";
        const string Ret = "System.Decimal";
        const string Styles = "System.Globalization.NumberStyles";
        const string Midpoint = "System.MidpointRounding";

        static string? S(StackSlot[] a, int i) => (a[i].ObjectValue as VmString)?.Value;

        // ---- 解析面 (Parse / TryParse): guest 呼出スコープの CurrentCulture を使う。
        //      Parse 失敗は本家と同じ FormatException / OverflowException (ゲスト例外化) を投げる
        static StackSlot ParseImpl(IntrinsicContext ctx, StackSlot[] a, int styles) {
            try {
                return StackSlot.OfValueType(MakeDecimalStruct(ctx,
                    decimal.Parse(S(a, 0) ?? "", (System.Globalization.NumberStyles)styles,
                        System.Globalization.CultureInfo.CurrentCulture)));
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
                    value = decimal.Parse(S(a, 0) ?? "", System.Globalization.CultureInfo.CurrentCulture);
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

    /// <summary>decimal の解析面 (guest 呼出スコープの CurrentCulture)。Parse 失敗は本家と同じ FormatException /
    /// OverflowException (ゲスト例外化) を投げる。</summary>
    private static StackSlot DecimalParse(IntrinsicContext ctx, string? s, int styles) {
        try {
            return StackSlot.OfValueType(MakeDecimalStruct(ctx,
                decimal.Parse(s ?? "", (System.Globalization.NumberStyles)styles,
                    System.Globalization.CultureInfo.CurrentCulture)));
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
}
