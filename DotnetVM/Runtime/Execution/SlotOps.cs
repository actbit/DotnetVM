using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>スタックスロットに対する純粋演算群 (状態を持たない)。
/// コピー意味論 (値型は Clone)、真値判定、比較 (ceq/cgt/clt 系 + 分岐 shim)、
/// 算術 (int32/int64/float + ovf)、変換 (conv 系 + ovf)、スロット記述。</summary>
internal static class SlotOps {

    /// <summary>値の読み出し/書き込みコピー (値型は Clone、参照はそのまま)。</summary>
    public static StackSlot PushCopyOfValue(in StackSlot slot) {
        if (slot.Kind == StackKind.ValueType && slot.ObjectValue is VmStructValue sv)
            return StackSlot.OfValueType(sv.Clone());
        return slot;
    }

    public static StackSlot StoreCopyOfValue(in StackSlot value) => PushCopyOfValue(value);

    public static string Describe(in StackSlot slot) => slot.ObjectValue switch {
        null => "null",
        VmClassInstance ci => ci.ClassType.FullName,
        DotnetVM.Runtime.Objects.VmExceptionObject e => e.Type.FullName,
        VmString => "System.String",
        VmArray a => a.ArrayType.FullName,
        VmBoxedValue b => b.Type.FullName,
        VmStructValue sv => sv.StructType.FullName,
        VmDelegate d => d.DeclaredType.FullName,
        _ => slot.ObjectValue.GetType().Name,
    };

    // ---- 分岐/比較の共通評価 ----

    public static bool IsTrue(in StackSlot slot) => slot.Kind switch {
        StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt => slot.Int64Value != 0,
        StackKind.Float => slot.DoubleValue != 0, // NaN は true (ECMA-335: brtrue は non-zero)
        StackKind.Object or StackKind.ByRef => slot.ObjectValue is not null,
        _ => throw new InvalidOperationException($"分岐条件に使えないスタック型です: {slot.Kind}"),
    };

    private static bool CompareNativePointer(ILOp op, in StackSlot left, in StackSlot right) {
        var lp = left.ObjectValue as VmNativePointer;
        var rp = right.ObjectValue as VmNativePointer;
        if (lp is null && rp is null)
            return false;
        // 片側が null (native int 0): VM のポインタは常に有効 → p != null は常に真
        if (lp is null || rp is null) {
            var intSide = lp is null ? right : left;
            if (intSide.Kind is not (StackKind.NativeInt or StackKind.Int32) || intSide.Int64Value != 0)
                throw new InvalidOperationException("ポインタと null 以外の整数の比較は対応していません。");
            return op is ILOp.Cgt or ILOp.Cgt_Un; // eq/lt は false (非 null), gt 系は true
        }
        if (!ReferenceEquals(lp.Bytes, rp.Bytes))
            throw new InvalidOperationException("別のメモリブロックを指すポインタ同士の順序比較は未定義動作のため対応していません。");
        return op switch {
            ILOp.Ceq => lp.ByteOffset == rp.ByteOffset,
            ILOp.Cgt or ILOp.Cgt_Un => lp.ByteOffset > rp.ByteOffset,
            ILOp.Clt or ILOp.Clt_Un => lp.ByteOffset < rp.ByteOffset,
            CgeShim or CgeUnShim => lp.ByteOffset >= rp.ByteOffset,
            CleShim or CleUnShim => lp.ByteOffset <= rp.ByteOffset,
            _ => false,
        };
    }

    public static bool CompareBranch(ILOp op, in StackSlot left, in StackSlot right) => op switch {
        ILOp.Beq or ILOp.Beq_S => Compare(ILOp.Ceq, left, right),
        ILOp.Bne_Un or ILOp.Bne_Un_S => !Compare(ILOp.Ceq, left, right),
        ILOp.Bge or ILOp.Bge_S => Compare(CgeShim, left, right),
        ILOp.Bgt or ILOp.Bgt_S => Compare(ILOp.Cgt, left, right),
        ILOp.Ble or ILOp.Ble_S => Compare(CleShim, left, right),
        ILOp.Blt or ILOp.Blt_S => Compare(ILOp.Clt, left, right),
        ILOp.Bge_Un or ILOp.Bge_Un_S => Compare(CgeUnShim, left, right),
        ILOp.Bgt_Un or ILOp.Bgt_Un_S => Compare(ILOp.Cgt_Un, left, right),
        ILOp.Ble_Un or ILOp.Ble_Un_S => Compare(CleUnShim, left, right),
        ILOp.Blt_Un or ILOp.Blt_Un_S => Compare(ILOp.Clt_Un, left, right),
        _ => throw new InvalidOperationException($"比較分岐でない命令 {op} が渡されました。"),
    };

    // cge/cle は IL に無いので比較関数内部でのみ使う疑似コード
    public const ILOp CgeShim = (ILOp)0xFF01;
    public const ILOp CleShim = (ILOp)0xFF02;
    public const ILOp CgeUnShim = (ILOp)0xFF03;
    public const ILOp CleUnShim = (ILOp)0xFF04;

    /// <summary>ceq/cgt/clt 系 (shim を含む) の共通比較。数値は統一 (i4/i8/native/float)、オブジェクトは参照比較。</summary>
    public static bool Compare(ILOp op, in StackSlot left, in StackSlot right) {
        // unmanaged ポインタの比較 (C# の p != null は cgt.un、p == q は ceq にコンパイルされる)
        if (left.ObjectValue is VmNativePointer || right.ObjectValue is VmNativePointer)
            return CompareNativePointer(op, left, right);
        if (op == ILOp.Ceq) {
            if (left.Kind is StackKind.Object or StackKind.ByRef || right.Kind is StackKind.Object or StackKind.ByRef)
                return ReferenceEquals(left.ObjectValue, right.ObjectValue);
            if (left.Kind == StackKind.Float || right.Kind == StackKind.Float)
                return ToFloat(left) == ToFloat(right);
            return ToLong(left) == ToLong(right);
        }
        // 参照系スロットの順序比較: cgt.un は「同一インスタンスでなければ真」を返す
        // (ECMA-335 III.2。C# の x != null は cgt.un にコンパイルされるため、非 null vs null
        // が真になる必要がある)。他の順序命令が参照に使われることは検証可能な IL では無いため
        // 数値比較への誤落下を避けて fail-closed にする
        if (left.Kind is StackKind.Object or StackKind.ByRef || right.Kind is StackKind.Object or StackKind.ByRef) {
            if (op == ILOp.Cgt_Un)
                return !ReferenceEquals(left.ObjectValue, right.ObjectValue);
            throw new InvalidOperationException($"比較命令 {op} は参照スロットに適用できません。");
        }
        if (left.Kind == StackKind.Float || right.Kind == StackKind.Float) {
            var a = ToFloat(left);
            var b = ToFloat(right);
            return op switch {
                ILOp.Cgt => a > b,
                ILOp.Clt => a < b,
                // un 系は「無順序 (NaN) も真」: blt.un/bge.un 等のセマンティクス
                CgeShim => a >= b,
                CleShim => a <= b,
                ILOp.Cgt_Un => double.IsNaN(a) || double.IsNaN(b) || a > b,
                ILOp.Clt_Un => double.IsNaN(a) || double.IsNaN(b) || a < b,
                CgeUnShim => double.IsNaN(a) || double.IsNaN(b) || a >= b,
                CleUnShim => double.IsNaN(a) || double.IsNaN(b) || a <= b,
                _ => throw new InvalidOperationException($"比較命令 {op} は数値比較に対応しません。"),
            };
        }

        // 整数系。un 系は符号なし比較
        if (op is ILOp.Cgt_Un or ILOp.Clt_Un or CgeUnShim or CleUnShim) {
            if (left.Kind == StackKind.Int64 || left.Kind == StackKind.NativeInt) {
                var la = (ulong)left.Int64Value;
                var lb = (ulong)right.Int64Value;
                return op switch {
                    ILOp.Cgt_Un => la > lb,
                    ILOp.Clt_Un => la < lb,
                    CgeUnShim => la >= lb,
                    _ => la <= lb,
                };
            }
            var a = (uint)left.Int64Value;
            var b = (uint)right.Int64Value;
            return op switch {
                ILOp.Cgt_Un => a > b,
                ILOp.Clt_Un => a < b,
                CgeUnShim => a >= b,
                _ => a <= b,
            };
        }

        var li = left.Int64Value;
        var ri = right.Int64Value;
        return op switch {
            ILOp.Cgt => li > ri,
            ILOp.Clt => li < ri,
            CgeShim => li >= ri,
            CleShim => li <= ri,
            _ => throw new InvalidOperationException($"比較命令 {op} は整数比較に対応しません。"),
        };
    }

    private static long ToLong(in StackSlot slot) {
        if (slot.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt)
            return slot.Int64Value;
        throw new InvalidOperationException($"比較に使えないスタック型です: {slot.Kind}");
    }

    private static double ToFloat(in StackSlot slot) => slot.Kind switch {
        StackKind.Float => slot.DoubleValue,
        StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt => slot.Int64Value,
        _ => throw new InvalidOperationException($"比較に使えないスタック型です: {slot.Kind}"),
    };

    // ---- 算術 ----

    public static StackSlot BinaryArithmetic(ILOp op, in StackSlot left, in StackSlot right) {
        // 浮動小数演算
        if (left.Kind == StackKind.Float || right.Kind == StackKind.Float) {
            var a = ToFloat(left);
            var b = ToFloat(right);
            return StackSlot.OfFloat(op switch {
                ILOp.Add or ILOp.Add_Ovf or ILOp.Add_Ovf_Un => a + b,
                ILOp.Sub or ILOp.Sub_Ovf or ILOp.Sub_Ovf_Un => a - b,
                ILOp.Mul or ILOp.Mul_Ovf or ILOp.Mul_Ovf_Un => a * b,
                ILOp.Div or ILOp.Div_Un => a / b,
                ILOp.Rem or ILOp.Rem_Un => a % b,
                _ => throw new InvalidOperationException($"浮動小数に適用できない算術命令です: {op}"),
            });
        }

        var is64 = left.Kind is StackKind.Int64 or StackKind.NativeInt
            || right.Kind is StackKind.Int64 or StackKind.NativeInt;
        if (!is64) {
            var a = (int)left.Int64Value;
            var b = (int)right.Int64Value;
            return IsUnsigned(op)
                ? StackSlot.OfInt32((int)UnsignedInt32(op, (uint)a, (uint)b))
                : StackSlot.OfInt32(SignedInt32(op, a, b));
        }

        var x = left.Int64Value;
        var y = right.Int64Value;
        var resultKind = left.Kind == StackKind.NativeInt || right.Kind == StackKind.NativeInt
            ? StackKind.NativeInt : StackKind.Int64;
        if (IsUnsigned(op)) {
            var ux = (ulong)x;
            var uy = (ulong)y;
            return OfKind(resultKind, (long)UnsignedInt64(op, ux, uy));
        }
        return OfKind(resultKind, SignedInt64(op, x, y));
    }

    private static StackSlot OfKind(StackKind kind, long value) => kind switch {
        StackKind.NativeInt => StackSlot.OfNativeInt(value),
        _ => StackSlot.OfInt64(value),
    };

    private static bool IsUnsigned(ILOp op) =>
        op is ILOp.Div_Un or ILOp.Rem_Un or ILOp.Add_Ovf_Un or ILOp.Sub_Ovf_Un or ILOp.Mul_Ovf_Un
            or ILOp.Shr_Un;

    /// <summary>整数算術の実体 (int32)。div/rem のゼロ除算・Overflow はゲスト例外に変換。</summary>
    private static int SignedInt32(ILOp op, int a, int b) {
        switch (op) {
            case ILOp.Add: return a + b;
            case ILOp.Sub: return a - b;
            case ILOp.Mul: return a * b;
            case ILOp.Div:
                if (b == 0) ThrowDivideByZero();
                if (a == int.MinValue && b == -1) ThrowOverflow();
                return a / b;
            case ILOp.Rem:
                if (b == 0) ThrowDivideByZero();
                // 実在 CLR と同じく min % -1 も OverflowException (ECMA の「0 を返す」と異なる点に注意)
                if (a == int.MinValue && b == -1) ThrowOverflow();
                return a % b;
            case ILOp.And: return a & b;
            case ILOp.Or: return a | b;
            case ILOp.Xor: return a ^ b;
            case ILOp.Shl: return a << (b & 31);
            case ILOp.Shr: return a >> (b & 31);
            case ILOp.Add_Ovf:
            case ILOp.Sub_Ovf:
            case ILOp.Mul_Ovf:
                try {
                    return op switch {
                        ILOp.Add_Ovf => checked(a + b),
                        ILOp.Sub_Ovf => checked(a - b),
                        _ => checked(a * b),
                    };
                } catch (OverflowException) {
                    ThrowOverflow();
                    return 0;
                }
            default:
                throw new InvalidOperationException($"符号付き int32 算術でない命令です: {op}");
        }
    }

    private static uint UnsignedInt32(ILOp op, uint a, uint b) {
        switch (op) {
            case ILOp.Div_Un:
                if (b == 0) ThrowDivideByZero();
                return a / b;
            case ILOp.Rem_Un:
                if (b == 0) ThrowDivideByZero();
                return a % b;
            case ILOp.Shr_Un: return a >> (int)(b & 31);
            case ILOp.Add_Ovf_Un:
                var s = a + b;
                if (s < a) ThrowOverflow();
                return s;
            case ILOp.Sub_Ovf_Un:
                if (b > a) ThrowOverflow();
                return a - b;
            case ILOp.Mul_Ovf_Un:
                var p = a * b;
                if (a != 0 && p / a != b) ThrowOverflow();
                return p;
            default:
                throw new InvalidOperationException($"符号なし int32 算術でない命令です: {op}");
        }
    }

    private static long SignedInt64(ILOp op, long a, long b) {
        switch (op) {
            case ILOp.Add: return a + b;
            case ILOp.Sub: return a - b;
            case ILOp.Mul: return a * b;
            case ILOp.Div:
                if (b == 0) ThrowDivideByZero();
                if (a == long.MinValue && b == -1) ThrowOverflow();
                return a / b;
            case ILOp.Rem:
                if (b == 0) ThrowDivideByZero();
                if (a == long.MinValue && b == -1) ThrowOverflow();
                return a % b;
            case ILOp.And: return a & b;
            case ILOp.Or: return a | b;
            case ILOp.Xor: return a ^ b;
            case ILOp.Shl: return a << (int)(b & 63);
            case ILOp.Shr: return a >> (int)(b & 63);
            case ILOp.Add_Ovf:
            case ILOp.Sub_Ovf:
            case ILOp.Mul_Ovf:
                try {
                    return op switch {
                        ILOp.Add_Ovf => checked(a + b),
                        ILOp.Sub_Ovf => checked(a - b),
                        _ => checked(a * b),
                    };
                } catch (OverflowException) {
                    ThrowOverflow();
                    return 0;
                }
            default:
                throw new InvalidOperationException($"符号付き int64 算術でない命令です: {op}");
        }
    }

    private static ulong UnsignedInt64(ILOp op, ulong a, ulong b) {
        switch (op) {
            case ILOp.Div_Un:
                if (b == 0) ThrowDivideByZero();
                return a / b;
            case ILOp.Rem_Un:
                if (b == 0) ThrowDivideByZero();
                return a % b;
            case ILOp.Shr_Un: return a >> (int)(b & 63);
            case ILOp.Add_Ovf_Un: {
                var s = a + b;
                if (s < a) ThrowOverflow();
                return s;
            }
            case ILOp.Sub_Ovf_Un:
                if (b > a) ThrowOverflow();
                return a - b;
            case ILOp.Mul_Ovf_Un: {
                var p = a * b;
                if (a != 0 && p / a != b) ThrowOverflow();
                return p;
            }
            default:
                throw new InvalidOperationException($"符号なし int64 算術でない命令です: {op}");
        }
    }

    public static void ThrowDivideByZero() =>
        throw new UnhandledGuestException("System.DivideByZeroException", null);
    public static void ThrowOverflow() =>
        throw new UnhandledGuestException("System.OverflowException", null);

    public static StackSlot UnaryArithmetic(ILOp op, in StackSlot value) {
        if (value.Kind == StackKind.Float)
            return StackSlot.OfFloat(op == ILOp.Neg ? -value.DoubleValue
                : throw new InvalidOperationException("浮動小数に not は適用できません。"));
        if (value.Kind == StackKind.Int32)
            return op == ILOp.Neg ? StackSlot.OfInt32(-(int)value.Int64Value)
                : StackSlot.OfInt32(~(int)value.Int64Value);
        return op == ILOp.Neg ? StackSlot.OfInt64(-value.Int64Value)
            : StackSlot.OfInt64(~value.Int64Value);
    }

    // ---- 変換 ----

    public static StackSlot ConvertValue(ILOp op, in StackSlot value) {
        // unmanaged ポインタの恒等変換 (p + n の add 前後に出る conv.i / conv.u)
        if (op is ILOp.Conv_I or ILOp.Conv_U && value.ObjectValue is VmNativePointer)
            return value;
        // マネージポインタ (ByRef: fixed / ldloca 由来) の conv.i / conv.u も参照を
        // 維持して素通しする (整数化で null 化すると指し先が失われ後続 stind 等が NRE に
        // 落ちる。参照先アクセスは ldind/stind/Add の ByRef 対応で受ける)
        if (op is ILOp.Conv_I or ILOp.Conv_U && value.Kind == StackKind.ByRef &&
            value.ObjectValue is VmByRef)
            return value;
        // ソース値を i8 (または f8) に統一してから切り詰める
        var isFloatSrc = value.Kind == StackKind.Float;
        var f = isFloatSrc ? value.DoubleValue : 0.0;
        // R8 → 整数への非検査変換はゼロ方向切捨て (C5.5 Wave 3 修正: 以前は
        // float ソースで i を 0 に落としており conv.i1〜i8 / u1〜u8 が常に 0 を返した)。
        // x64 cvttsd2si 規約に合わせ NaN は 0、範囲外は 0x8000... パターン
        var i = isFloatSrc ? TruncateFloatToInt64(f) : value.Int64Value;

        switch (op) {
            // 無限精度の拡張/縮小 (ラップする)
            case ILOp.Conv_I1: return StackSlot.OfInt32((sbyte)i);
            case ILOp.Conv_I2: return StackSlot.OfInt32((short)i);
            case ILOp.Conv_I4: return StackSlot.OfInt32(isFloatSrc ? TruncateFloatToInt32(f) : (int)i);
            case ILOp.Conv_I8: return StackSlot.OfInt64(i);
            case ILOp.Conv_U1: return StackSlot.OfInt32((byte)i);
            case ILOp.Conv_U2: return StackSlot.OfInt32((ushort)i);
            case ILOp.Conv_U4: return StackSlot.OfInt32((int)(uint)i);
            // conv.u8/conv.u はゼロ拡張。i4 スロットの Int64Value は既に符号拡張済みなので Kind で判断する
            case ILOp.Conv_U8:
                return StackSlot.OfInt64(value.Kind == StackKind.Int32
                    ? (long)(uint)value.Int64Value
                    : (long)(ulong)value.Int64Value);
            case ILOp.Conv_I: return StackSlot.OfNativeInt(i);
            case ILOp.Conv_U:
                return StackSlot.OfNativeInt(value.Kind == StackKind.Int32
                    ? (long)(uint)value.Int64Value
                    : (long)(ulong)value.Int64Value);
            case ILOp.Conv_R4: return StackSlot.OfFloat(isFloatSrc ? (double)(float)f : (double)(float)i);
            case ILOp.Conv_R8: return StackSlot.OfFloat(isFloatSrc ? f : i);
            case ILOp.Conv_R_Un:
                if (isFloatSrc) return StackSlot.OfFloat(f);
                return value.Kind == StackKind.Int64
                    ? StackSlot.OfFloat((double)(ulong)value.Int64Value)
                    : StackSlot.OfFloat((double)(uint)value.Int64Value);

            // オーバーフロー検査つき
            case ILOp.Conv_Ovf_I1: return Checked32((sbyte)CheckedSigned(op, i, isFloatSrc, f,
                sbyte.MinValue, sbyte.MaxValue, 128.0));
            case ILOp.Conv_Ovf_I2: return Checked32((short)CheckedSigned(op, i, isFloatSrc, f,
                short.MinValue, short.MaxValue, 32768.0));
            case ILOp.Conv_Ovf_I4: return Checked32((int)CheckedSigned(op, i, isFloatSrc, f,
                int.MinValue, int.MaxValue, 2147483648.0));
            case ILOp.Conv_Ovf_I8: return StackSlot.OfInt64(CheckedSigned(op, i, isFloatSrc, f,
                long.MinValue, long.MaxValue, 9223372036854775808.0));
            case ILOp.Conv_Ovf_U1: return Checked32((byte)CheckedUnsigned(op, i, isFloatSrc, f,
                byte.MaxValue, 256.0));
            case ILOp.Conv_Ovf_U2: return Checked32((ushort)CheckedUnsigned(op, i, isFloatSrc, f,
                ushort.MaxValue, 65536.0));
            case ILOp.Conv_Ovf_U4: return Checked32((int)(uint)CheckedUnsigned(op, i, isFloatSrc, f,
                uint.MaxValue, 4294967296.0));
            case ILOp.Conv_Ovf_U8: return StackSlot.OfInt64((long)CheckedU64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I: return StackSlot.OfNativeInt(CheckedSigned(op, i, isFloatSrc, f,
                long.MinValue, long.MaxValue, 9223372036854775808.0));
            case ILOp.Conv_Ovf_U: return StackSlot.OfNativeInt((long)CheckedU64(op, i, isFloatSrc, f));
            case ILOp.Conv_Ovf_I1_Un or ILOp.Conv_Ovf_I2_Un or ILOp.Conv_Ovf_I4_Un or ILOp.Conv_Ovf_I8_Un
                or ILOp.Conv_Ovf_U1_Un or ILOp.Conv_Ovf_U2_Un or ILOp.Conv_Ovf_U4_Un or ILOp.Conv_Ovf_U8_Un
                or ILOp.Conv_Ovf_I_Un or ILOp.Conv_Ovf_U_Un:
                return ConvertOvfUnsigned(op, value);
            default:
                throw new InvalidOperationException($"変換命令でない命令です: {op}");
        }
    }

    /// <summary>R8 → int64 への非検査切捨て変換 (conv.i8 系の float ソース)。
    /// ホスト RyuJIT 規約 (ゼロ方向切捨て + 飽和): NaN → 0、範囲外は各極値へ飽和。</summary>
    private static long TruncateFloatToInt64(double f) {
        if (double.IsNaN(f)) return 0;
        if (f >= 9223372036854775808.0) return long.MaxValue;
        if (f < -9223372036854775808.0) return long.MinValue;
        return (long)f;
    }

    /// <summary>R8 → int32 への非検査切捨て変換 (conv.i4 の float ソースは 64bit 経由でなく
    /// 直接 32bit に切るため別経路)。ホスト RyuJIT 規約: NaN → 0、範囲外は各極値へ飽和。</summary>
    private static int TruncateFloatToInt32(double f) {
        if (double.IsNaN(f)) return 0;
        if (f >= 2147483648.0) return int.MaxValue;
        if (f < -2147483648.0) return int.MinValue;
        return (int)f;
    }

    /// <summary>conv.ovf.*.un: ソースを符号なし整数とみなしてターゲット範囲を検査する。</summary>
    private static StackSlot ConvertOvfUnsigned(ILOp op, in StackSlot value) {
        if (value.Kind == StackKind.Float) {
            var f = value.DoubleValue;
            if (double.IsNaN(f) || f < 0 || f >= 18446744073709551615.0)
                ThrowOverflow();
            var u = (ulong)f;
            return ConvertOvfFromU64(op, u);
        }
        // i4 は符号拡張済みだが .un はビット列を符号なしとして扱う
        var raw = value.Kind == StackKind.Int32 ? (uint)value.Int64Value : (ulong)value.Int64Value;
        return ConvertOvfFromU64(op, raw);
    }

    private static StackSlot ConvertOvfFromU64(ILOp op, ulong value) {
        switch (op) {
            case ILOp.Conv_Ovf_I1_Un: if (value > (ulong)sbyte.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((sbyte)value);
            case ILOp.Conv_Ovf_I2_Un: if (value > (ulong)short.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((short)value);
            case ILOp.Conv_Ovf_I4_Un: if (value > int.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((int)value);
            case ILOp.Conv_Ovf_I8_Un: if (value > long.MaxValue) ThrowOverflow(); return StackSlot.OfInt64((long)value);
            case ILOp.Conv_Ovf_U1_Un: if (value > byte.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((byte)value);
            case ILOp.Conv_Ovf_U2_Un: if (value > ushort.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((ushort)value);
            case ILOp.Conv_Ovf_U4_Un: if (value > uint.MaxValue) ThrowOverflow(); return StackSlot.OfInt32((int)(uint)value);
            case ILOp.Conv_Ovf_U8_Un: return StackSlot.OfInt64((long)value);
            case ILOp.Conv_Ovf_I_Un: if (value > long.MaxValue) ThrowOverflow(); return StackSlot.OfNativeInt((long)value);
            case ILOp.Conv_Ovf_U_Un: return StackSlot.OfNativeInt((long)value);
            default:
                throw new InvalidOperationException($"conv.ovf.*.un でない命令です: {op}");
        }
    }

    private static long CheckedSigned(ILOp op, long i, bool isFloat, double f,
        long min, long max, double maxExclusive) {
        if (isFloat) {
            if (double.IsNaN(f) || f < min || f >= maxExclusive)
                ThrowOverflow();
            return (long)f;
        }
        if (i < min || i > max)
            ThrowOverflow();
        return i;
    }

    private static ulong CheckedUnsigned(ILOp op, long i, bool isFloat, double f,
        ulong max, double maxExclusive) {
        if (isFloat) {
            if (double.IsNaN(f) || f < 0 || f >= maxExclusive)
                ThrowOverflow();
            return (ulong)f;
        }
        if (i < 0)
            ThrowOverflow();
        var value = (ulong)i;
        if (value > max)
            ThrowOverflow();
        return value;
    }

    private static ulong CheckedU64(ILOp op, long i, bool isFloat, double f) {
        if (isFloat) {
            if (double.IsNaN(f) || f < 0 || f >= 18446744073709551615.0)
                ThrowOverflow();
            return (ulong)f;
        }
        if (i < 0)
            ThrowOverflow();
        return (ulong)i;
    }

    private static StackSlot Checked32(int value) => StackSlot.OfInt32(value);

    // ---- 呼出補助 (純粋判定) ----

    public static bool IsNullReference(in StackSlot slot) =>
        slot.Kind is StackKind.Object or StackKind.ByRef && slot.ObjectValue is null;

    /// <summary>レシーバ (インスタンス/ボックス/構造体値、ByRef レシーバも可) が持つジェネリック実引数を得る。</summary>
    public static bool TryGetReceiverTypeArguments(in StackSlot receiver, int count, out VmType[] args) {
        args = [];
        if (count == 0)
            return false;
        var value = receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef
            ? byRef.Slot
            : receiver;
        VmType[]? found = value.Kind switch {
            StackKind.Object => value.ObjectValue switch {
                VmClassInstance ci => ci.TypeArguments,
                VmBoxedValue bv => bv.Type is VmConstructedType boxedCt ? boxedCt.TypeArguments : null,
                _ => null,
            },
            StackKind.ValueType => value.ObjectValue is VmStructValue sv ? sv.TypeArguments : null,
            _ => null,
        };
        if (found is { Length: > 0 } && found.Length == count) {
            args = found;
            return true;
        }
        return false;
    }

    public static bool SignatureReturnsValue(MethodSignature signature) =>
        signature.ReturnType.Kind != SigKind.Void;
}
