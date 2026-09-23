using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
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
}
