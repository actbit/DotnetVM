using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    // ---- System.Convert ----

    private static void RegisterConvert(IntrinsicRegistry r) {
        const string T = "System.Convert";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        S("ToBoolean", 1, static (ctx, a) => {
            var s = new Args(a);
            // CLR は char → bool を変換不可 (IConvertible.ToBoolean が InvalidCastException)。
            // i4 スロットに統合される char はパラメータ型名で判別して同じ例外にする
            if (s[0].Kind == StackKind.Int32 && ctx.ParamAt(0) == "System.Char")
                throw new UnhandledGuestException("System.InvalidCastException", null);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(s.Int32(0) != 0 ? 1 : 0),
                StackKind.Int64 => StackSlot.OfInt32(s.Int64(0) != 0 ? 1 : 0),
                StackKind.Float => StackSlot.OfInt32(s.Float(0) != 0 ? 1 : 0),
                // ボックス化された enum/プリミティブ (Convert.ToInt32((Enum)…) 経由) は
                // 基底値スロットで判定する (CLR も IConvertible で基底値に落とす)
                StackKind.Object when s[0].ObjectValue is VmBoxedValue boxed =>
                    StackSlot.OfInt32(boxed.Fields[0].Int64Value != 0 ? 1 : 0),
                _ => StackSlot.OfInt32(bool.Parse(s.String(0).Value) ? 1 : 0),
            };
        });
        S("ToInt32", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfInt32(Checked(s.Int64(0))),
                StackKind.Float => StackSlot.OfInt32((int)Math.Round(s.Float(0), MidpointRounding.ToEven)),
                // ボックス化された enum は基底値 (int) に落としてから返す
                StackKind.Object when s[0].ObjectValue is VmBoxedValue boxed =>
                    StackSlot.OfInt32(Checked(boxed.Fields[0].Int64Value)),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfInt32(int.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToInt32 未対応の入力型: {s[0].Kind}"),
            };
            static int Checked(long v) {
                if (v is < int.MinValue or > int.MaxValue)
                    throw new UnhandledGuestException("System.OverflowException", null);
                return (int)v;
            }
        });
        S("ToInt64", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt64(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfInt64(s.Int64(0)),
                StackKind.Float => StackSlot.OfInt64((long)Math.Round(s.Float(0), MidpointRounding.ToEven)),
                StackKind.Object when s[0].ObjectValue is VmBoxedValue boxed =>
                    StackSlot.OfInt64(boxed.Fields[0].Int64Value),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfInt64(long.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToInt64 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToDouble", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfFloat(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfFloat(s.Int64(0)),
                StackKind.Float => StackSlot.OfFloat(s.Float(0)),
                StackKind.Object when s[0].ObjectValue is VmBoxedValue boxed =>
                    StackSlot.OfFloat(boxed.Fields[0].Kind == StackKind.Float
                        ? boxed.Fields[0].DoubleValue
                        : boxed.Fields[0].Int64Value),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfFloat(double.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToDouble 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToString", 1, static (ctx, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfObject(ctx.MakeString(s.Int32(0).ToString())),
                StackKind.Int64 => StackSlot.OfObject(ctx.MakeString(s.Int64(0).ToString())),
                StackKind.Float => StackSlot.OfObject(ctx.MakeString(s.Float(0).ToString())),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfObject(str),
                _ => throw new InvalidOperationException($"Convert.ToString 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToChar", 1, static (ctx, a) => {
            var s = new Args(a);
            // CLR は bool → char を変換不可 (IConvertible.ToChar が InvalidCastException)
            if (s[0].Kind == StackKind.Int32 && ctx.ParamAt(0) == "System.Boolean")
                throw new UnhandledGuestException("System.InvalidCastException", null);
            return StackSlot.OfInt32(s[0].Kind switch {
                StackKind.Int32 => checked((char)s.Int32(0)),
                StackKind.Int64 => checked((char)s.Int64(0)),
                _ => throw new InvalidOperationException($"Convert.ToChar 未対応の入力型: {s[0].Kind}"),
            });
        });
        S("ToByte", 1, static (_, a) => ToIntegral("ToByte", new Args(a), byte.MinValue, byte.MaxValue));
        S("ToSByte", 1, static (_, a) => ToIntegral("ToSByte", new Args(a), sbyte.MinValue, sbyte.MaxValue));
        S("ToInt16", 1, static (_, a) => ToIntegral("ToInt16", new Args(a), short.MinValue, short.MaxValue));
        S("ToUInt16", 1, static (_, a) => ToIntegral("ToUInt16", new Args(a), ushort.MinValue, ushort.MaxValue));
        S("ToUInt32", 1, static (_, a) => ToIntegral("ToUInt32", new Args(a), uint.MinValue, uint.MaxValue));
        S("ToUInt64", 1, static (_, a) => ToUnsigned64("ToUInt64", new Args(a)));
        S("ToSingle", 1, static (_, a) => {
            var s = new Args(a);
            var value = s[0].Kind switch {
                StackKind.Int32 => (float)s.Int32(0),
                StackKind.Int64 => (float)s.Int64(0),
                StackKind.Float => (float)s.Float(0),
                StackKind.Object when s[0].ObjectValue is VmString str => float.Parse(str.Value),
                _ => throw new InvalidOperationException($"Convert.ToSingle 未対応の入力型: {s[0].Kind}"),
            };
            return StackSlot.OfFloat(value);
        });
    }

    /// <summary>Convert.ToXxx の共通核。入力 (i4/i8/f/10 進文字列) を丸めて 64bit 整数にし、
    /// 範囲外ならゲスト例外 System.OverflowException として投げる (CLR の Convert と同じ)。</summary>
    private static StackSlot ToIntegral(string target, Args s, long min, long max) {
        var value = s[0].Kind switch {
            StackKind.Int32 => s.Int32(0),
            StackKind.Int64 => s.Int64(0),
            StackKind.Float => (long)Math.Round(s.Float(0), MidpointRounding.ToEven),
            StackKind.Object when s[0].ObjectValue is VmString str => long.Parse(str.Value),
            _ => throw new InvalidOperationException($"Convert.{target} 未対応の入力型: {s[0].Kind}"),
        };
        if (value < min || value > max)
            throw new UnhandledGuestException("System.OverflowException", null);
        // i4 スロットには bit パターンを格納する (u4 値は (int) でラップ。読み手が u4 として解釈)
        return StackSlot.OfInt32((int)value);
    }

    /// <summary>Convert.ToUInt64 の共通核 (戻り値のみ i8 スロット)。入力が負整数なら CLR 同様 OverflowException。</summary>
    private static StackSlot ToUnsigned64(string target, Args s) {
        if (s[0].Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt &&
            (s[0].Kind == StackKind.Int64 ? s.Int64(0) : s.Int32(0)) < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        var value = s[0].Kind switch {
            StackKind.Int32 => (ulong)(long)s.Int32(0),
            StackKind.Int64 or StackKind.NativeInt => (ulong)s.Int64(0),
            StackKind.Float => (ulong)Math.Round(s.Float(0), MidpointRounding.ToEven), // 負/範囲外の浮動小数は変換時点で例外
            StackKind.Object when s[0].ObjectValue is VmString str => ulong.Parse(str.Value),
            _ => throw new InvalidOperationException($"Convert.{target} 未対応の入力型: {s[0].Kind}"),
        };
        return StackSlot.OfInt64((long)value);
    }
}
