namespace DotnetVM.CoreLib;

/// <summary>
/// decimal の 96 ビット演算エンジン (実在 CoreLib の System.Decimal+DecCalc の移植)。
///
/// dotnet/runtime (MIT) v10.0.12 の System.Private.CoreLib (Decimal.DecCalc.cs:
/// DecAddSub / VarDecMul / VarDecDiv / VarDecMod / VarDecModFull / InternalRound /
/// VarR4FromDec / VarR8FromDec / ToInt32〜ToUInt64 経路 / ScaleResult / SearchScale /
/// OverflowUnscale / Unscale / Div96By32 / Div64By32 / Div96By64 / Div128By64 /
/// Div128By96 / IncreaseScale / Div96ByConst) の意味論を移植したもの。
///
/// 移植の制約 (VM の IL 実行モデルに合わせた置換):
/// - 本家 DecCalc は [StructLayout(LayoutKind.Explicit)] の明示的レイアウトで decimal と
///   メモリを共有する (Unsafe.As による参照再解釈 + ulomid/ulo/umid の union 重複)。
///   VM は型ごとのスロットレイアウトでフィールドを解決するためこの共有を再現できず、
///   96 ビット値を (low64, high, flags) のローカル変数組で保持する (Buf12/16/24/28 は
///   uint[] 配列へ、Buf12 の Low64/U2 は要素 0-1 (ulong) / 要素 2 (uint) に対応)
/// - Math.BigMul(ulong, ulong) (128 積) は Mul64x64 (32 ビットスライスの部分積) で置換
/// - BitOperations.LeadingZeroCount はビット走査ループで置換
/// - X86.X86Base / X86Base.X64 の SIMD 依存経路は移植対象外 (汎用 ulong 筆算経路のみ)
/// - Number.ThrowOverflowException は throw new OverflowException で置換 (例外型は本家同一)
///
/// 不変カルチャ規約固定 (culture 機構は decimal 演算に介在しないため本面は無関係)。
/// 正当性は VmCoreLibClrTests / CoreLibSurfaceGapProbeTests の CLR 突合で担保する。
/// </summary>
public static class DecimalCalc {
    // ---- 本家 DecCalc と同じ定数 ----
    private const uint SignMask = 0x80000000u;
    private const uint ScaleMask = 0x00FF0000u;
    private const int ScaleShift = 16;
    private const int DecScaleMax = 28;
    private const int MaxInt32Scale = 9;
    private const uint TenToPowerNine = 1000000000u;

    /// <summary>本家 UInt32Powers10 (10^0..10^9)。</summary>
    private static readonly uint[] UInt32Powers10 = [
        1u, 10u, 100u, 1000u, 10000u, 100000u, 1000000u, 10000000u, 100000000u, 1000000000u,
    ];

    /// <summary>本家 UInt64Powers10 (10^1..10^19)。添字 i は 10^(i+1) を表す
    /// (本家コメント "Fast access for 10^n where n is 1-19" のとおり。VarDecMul の
    /// scale 縮小が power = UInt64Powers10[scale] で 10^(scale+1) を期待する)。</summary>
    private static readonly ulong[] UInt64Powers10 = [
        10ul, 100ul, 1000ul, 10000ul, 100000ul, 1000000ul, 10000000ul, 100000000ul,
        1000000000ul, 10000000000ul, 100000000000ul, 1000000000000ul, 10000000000000ul,
        100000000000000ul, 1000000000000000ul, 10000000000000000ul, 100000000000000000ul,
        1000000000000000000ul, 10000000000000000000ul,
    ];

    /// <summary>本家 DoublePowers10 (10^0..10^28)。VarR8FromDec / VarR4FromDec 用。</summary>
    private static readonly double[] DoublePowers10 = [
        1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9,
        1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
        1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
    ];

    /// <summary>本家 PowerOvflValues の Hi 部 (SearchScale 用。2^32 / 10^n の整数部)。</summary>
    private static readonly uint[] PowerOvflHi = [
        429496729u, 42949672u, 4294967u, 429496u, 42949u, 4294u, 429u, 42u,
    ];

    /// <summary>本家 PowerOvflValues の MidLo 部 ((mid &lt;&lt; 32) + lo)。残りの分数部分 × 2^32。</summary>
    private static readonly ulong[] PowerOvflMidLo = [
        ((ulong)2576980377u << 32) + 2576980377u,   // 10^1 remainder 0.6
        ((ulong)4123168604u << 32) + 687194767u,    // 10^2 remainder 0.16
        ((ulong)1271310319u << 32) + 2645699854u,   // 10^3 remainder 0.616
        ((ulong)3133608139u << 32) + 694066715u,    // 10^4 remainder 0.1616
        ((ulong)2890341191u << 32) + 2216890319u,   // 10^5 remainder 0.51616
        ((ulong)4154504685u << 32) + 2369172679u,   // 10^6 remainder 0.551616
        ((ulong)2133437386u << 32) + 4102387834u,   // 10^7 remainder 0.9551616
        ((ulong)4078814305u << 32) + 410238783u,    // 10^8 remainder 0.09991616
    ];

    // ================================================================
    // 公開面 (VmCoreLibSurfaces.Faces の impl)。本家 System.Decimal の
    // 同名面の意味論をそのまま持つ (演算は 96 ビットパーツで実施)。
    // ================================================================

    /// <summary>本家 op_Addition / Add (DecAddSub sign: false)。</summary>
    public static decimal Add(decimal d1, decimal d2) {
        Split(d1, out var low64, out var high, out var flags);
        Split(d2, out var d2Low64, out var d2High, out var d2Flags);
        DecAddSub(ref low64, ref high, ref flags, d2Low64, d2High, d2Flags, sign: false);
        return Join(low64, high, flags);
    }

    /// <summary>本家 op_Subtraction / Subtract (DecAddSub sign: true)。</summary>
    public static decimal Subtract(decimal d1, decimal d2) {
        Split(d1, out var low64, out var high, out var flags);
        Split(d2, out var d2Low64, out var d2High, out var d2Flags);
        DecAddSub(ref low64, ref high, ref flags, d2Low64, d2High, d2Flags, sign: true);
        return Join(low64, high, flags);
    }

    /// <summary>本家 op_Multiply / Multiply (VarDecMul)。</summary>
    public static decimal Multiply(decimal d1, decimal d2) {
        Split(d1, out var low64, out var high, out var flags);
        Split(d2, out var d2Low64, out var d2High, out var d2Flags);
        VarDecMul(ref low64, ref high, ref flags, d2Low64, d2High, d2Flags);
        return Join(low64, high, flags);
    }

    /// <summary>本家 op_Division / Divide (VarDecDiv)。</summary>
    public static decimal Divide(decimal d1, decimal d2) {
        Split(d1, out var low64, out var high, out var flags);
        Split(d2, out var d2Low64, out var d2High, out var d2Flags);
        VarDecDiv(ref low64, ref high, ref flags, d2Low64, d2High, d2Flags);
        return Join(low64, high, flags);
    }

    /// <summary>本家 op_Modulus / Remainder (VarDecMod)。</summary>
    public static decimal Remainder(decimal d1, decimal d2) {
        Split(d1, out var low64, out var high, out var flags);
        Split(d2, out var d2Low64, out var d2High, out var d2Flags);
        VarDecMod(ref low64, ref high, ref flags, d2Low64, d2High, d2Flags);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Round(decimal) (ToEven)。</summary>
    public static decimal Round(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        InternalRound(ref low64, ref high, ref flags, (uint)((flags & ScaleMask) >> ScaleShift), MidpointRounding.ToEven);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Round(decimal, int) (ToEven)。</summary>
    public static decimal Round(decimal d, int decimals) {
        Split(d, out var low64, out var high, out var flags);
        RoundParts(ref low64, ref high, ref flags, decimals, MidpointRounding.ToEven);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Round(decimal, MidpointRounding)。</summary>
    public static decimal Round(decimal d, MidpointRounding mode) {
        Split(d, out var low64, out var high, out var flags);
        RoundParts(ref low64, ref high, ref flags, 0, mode);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Round(decimal, int, MidpointRounding)。</summary>
    public static decimal Round(decimal d, int decimals, MidpointRounding mode) {
        Split(d, out var low64, out var high, out var flags);
        RoundParts(ref low64, ref high, ref flags, decimals, mode);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Floor (InternalRound ToNegativeInfinity)。</summary>
    public static decimal Floor(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        if ((flags & ScaleMask) != 0)
            InternalRound(ref low64, ref high, ref flags, (flags & ScaleMask) >> ScaleShift, MidpointRounding.ToNegativeInfinity);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Ceiling (InternalRound ToPositiveInfinity)。</summary>
    public static decimal Ceiling(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        if ((flags & ScaleMask) != 0)
            InternalRound(ref low64, ref high, ref flags, (flags & ScaleMask) >> ScaleShift, MidpointRounding.ToPositiveInfinity);
        return Join(low64, high, flags);
    }

    /// <summary>本家 Truncate (InternalRound ToZero)。</summary>
    public static decimal Truncate(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        if ((flags & ScaleMask) != 0)
            InternalRound(ref low64, ref high, ref flags, (flags & ScaleMask) >> ScaleShift, MidpointRounding.ToZero);
        return Join(low64, high, flags);
    }

    /// <summary>本家 ToInt32 (Truncate + 範囲チェック)。</summary>
    public static int ToInt32(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        TruncateInPlace(ref low64, ref high, ref flags);
        if ((high | (uint)(low64 >> 32)) == 0) {
            var low = (int)(uint)low64;
            if ((flags & SignMask) == 0) {
                if (low >= 0)
                    return low;
            } else {
                low = -low;
                if (low <= 0)
                    return low;
            }
        }
        throw new OverflowException("Value was either too large or too small for an Int32.");
    }

    /// <summary>本家 ToInt64 (Truncate + 範囲チェック)。</summary>
    public static long ToInt64(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        TruncateInPlace(ref low64, ref high, ref flags);
        if (high == 0) {
            var low = (long)low64;
            if ((flags & SignMask) == 0) {
                if (low >= 0)
                    return low;
            } else {
                low = -low;
                if (low <= 0)
                    return low;
            }
        }
        throw new OverflowException("Value was either too large or too small for an Int64.");
    }

    /// <summary>本家 ToUInt32 (Truncate + 範囲チェック)。</summary>
    public static uint ToUInt32(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        TruncateInPlace(ref low64, ref high, ref flags);
        if ((high | (uint)(low64 >> 32)) == 0 && ((flags & SignMask) == 0 || low64 == 0))
            return (uint)low64;
        throw new OverflowException("Value was either too large or too small for a UInt32.");
    }

    /// <summary>本家 ToUInt64 (Truncate + 範囲チェック)。</summary>
    public static ulong ToUInt64(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        TruncateInPlace(ref low64, ref high, ref flags);
        if (high == 0 && ((flags & SignMask) == 0 || low64 == 0))
            return low64;
        throw new OverflowException("Value was either too large or too small for a UInt64.");
    }

    /// <summary>本家 ToSingle / VarR4FromDec。</summary>
    public static float ToSingle(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        // Value taken via reverse engineering the float that corresponds to 2^64.
        const double fs2to64 = 1.8446744073709552e+019;
        var dbl = ((double)low64 + (double)high * fs2to64) / DoublePowers10[(flags & ScaleMask) >> ScaleShift];
        if ((flags & SignMask) != 0)
            dbl = -dbl;
        return (float)dbl;
    }

    /// <summary>本家 ToDouble / VarR8FromDec (op_Explicit double の本家 IL が直接呼ぶ面)。</summary>
    public static double ToDouble(decimal d) {
        Split(d, out var low64, out var high, out var flags);
        // Value taken via reverse engineering the double that corresponds to 2^64. (oleaut32 has ds2to64)
        const double ds2to64 = 1.8446744073709552e+019;
        var dbl = ((double)low64 + (double)high * ds2to64) / DoublePowers10[(flags & ScaleMask) >> ScaleShift];
        if ((flags & SignMask) != 0)
            dbl = -dbl;
        return dbl;
    }

    // ================================================================
    // 96 ビットパーツ変換 (本家 DecCalc の union 共有の代替)。GetBits /
    // new decimal(int,int,int,bool,byte) は実在 CoreLib の managed IL として
    // VM の IL 実行で辿る。
    // ================================================================

    private static void Split(decimal d, out ulong low64, out uint high, out uint flags) {
        var bits = decimal.GetBits(d); // [lo, mid, hi, flags]
        low64 = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        high = (uint)bits[2];
        flags = (uint)bits[3];
    }

    private static decimal Join(ulong low64, uint high, uint flags) {
        return new decimal((int)(uint)low64, (int)(uint)(low64 >> 32), (int)high,
            (flags & SignMask) != 0, (byte)((flags & ScaleMask) >> ScaleShift));
    }

    // ================================================================
    // 本家 DecCalc の移植 (ローカル変数組 / uint[] 配列表現)
    // ================================================================

    /// <summary>本家 DecAddSub。d1 (low64/high/flags) に d2 を加減算する (結果は d1 へ in-place)。
    /// sign = 減算フラグ (本家 op_Subtraction は sign: true で呼ぶ)。</summary>
    private static void DecAddSub(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, uint d2flags, bool sign) {
        var xorflags = d2flags ^ flags;
        sign ^= (xorflags & SignMask) != 0;

        if ((xorflags & ScaleMask) != 0) {
            // Scale factors are not equal.  Assume that a larger scale
            // factor (more decimal places) is likely to mean that number
            // is smaller.  Start by guessing that the right operand has
            // the larger scale factor.  The result will have the larger
            // scale factor.
            //
            var d1flags = flags;
            flags = (d2flags & ScaleMask) | (flags & SignMask); // scale factor of "smaller", but sign of "larger"
            var scale = (int)(flags - d1flags) >> ScaleShift;

            if (scale < 0) {
                // Guessed scale factor wrong. Swap operands.
                scale = -scale;
                flags = d1flags;
                if (sign)
                    flags ^= SignMask;
                var tmpLow = d2Low64;
                var tmpHigh = d2High;
                d2Low64 = low64;
                d2High = high;
                low64 = tmpLow;
                high = tmpHigh;
            }

            // d1 will need to be multiplied by 10^scale so it will have the same
            // scale as d2.  We could be extending it to up to 192 bits of precision.

            // Scan for zeros in the upper words.
            //
            if (high == 0) {
                if (low64 <= uint.MaxValue) {
                    if ((uint)low64 == 0) {
                        // Left arg is zero, return right.
                        var signFlags = flags & SignMask;
                        if (sign)
                            signFlags ^= SignMask;
                        flags = d2flags & ScaleMask | signFlags;
                        low64 = d2Low64;
                        high = d2High;
                        return;
                    }

                    while (true) {
                        if (scale <= MaxInt32Scale) {
                            // 本家 Math.BigMul(uint, uint) → ulong (64 ビット積)。uint × uint は
                            // C# の規約で mod 2^32 になるため (ulong) 昇格が必須。
                            // 本家はここで goto AlignedAdd (スケール統一完了 = 64→96 ループに流れない)
                            low64 = (ulong)(uint)low64 * UInt32Powers10[scale];
                            AlignedAdd(ref low64, ref high, ref flags, d2Low64, d2High, sign);
                            return;
                        }
                        scale -= MaxInt32Scale;
                        low64 = (ulong)(uint)low64 * TenToPowerNine;
                        if (low64 > uint.MaxValue)
                            break;
                    }
                }

                if (low64 > uint.MaxValue) {
                    while (true) {
                        var power = TenToPowerNine;
                        if (scale < MaxInt32Scale)
                            power = UInt32Powers10[scale];
                        Mul64x64(low64, power, out var lo, out var hi);
                        low64 = lo;
                        high = (uint)hi;
                        scale -= MaxInt32Scale;
                        if (scale <= 0) {
                            // 本家: if ((scale -= MaxInt32Scale) <= 0) goto AlignedAdd
                            // (high の値にかかわらず 96 ビットに収まったので aligned add へ)
                            AlignedAdd(ref low64, ref high, ref flags, d2Low64, d2High, sign);
                            return;
                        }
                        if (high != 0)
                            break; // 96 ビットを超えた → 192 ビット経路へ (本家 while (high == 0) の脱出)
                    }
                } else {
                    AlignedAdd(ref low64, ref high, ref flags, d2Low64, d2High, sign);
                    return;
                }
            }

            while (true) {
                // Scaling won't make it larger than 4 uints
                var power = TenToPowerNine;
                if (scale < MaxInt32Scale)
                    power = UInt32Powers10[scale];
                Mul64x64(low64, power, out var lowLo, out var tmp64);
                Mul64x64(high, power, out var highLo, out var highHi);
                low64 = lowLo;
                tmp64 += highLo;
                highHi += (tmp64 < highLo) ? 1u : 0u;

                scale -= MaxInt32Scale;
                if (tmp64 > uint.MaxValue) {
                    // Have to scale by a bunch. Move the number to a buffer where it has
                    // room to grow as it's scaled.  (本家 Buf24 経路)
                    ScaleBigBuffer(ref low64, ref high, ref flags, d2Low64, d2High, sign, tmp64, scale);
                    return;
                }

                high = (uint)tmp64;
                // Result fits in 96 bits.  Use standard aligned add.
                if (scale <= 0) {
                    AlignedAdd(ref low64, ref high, ref flags, d2Low64, d2High, sign);
                    return;
                }
            }
        }

        // Scale factors are equal, no alignment necessary.
        AlignedAdd(ref low64, ref high, ref flags, d2Low64, d2High, sign);
    }

    /// <summary>本家 AlignedAdd (scale 同一時の加減算 + AlignedScale 丸め + SignFlip)。</summary>
    private static void AlignedAdd(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, bool sign) {
        ulong d1Low64 = low64;
        uint d1High = high;
        if (sign) {
            // Signs differ - subtract
            low64 = d1Low64 - d2Low64;
            high = d1High - d2High;

            // Propagate carry
            if (low64 > d1Low64) {
                high--;
                if (high >= d1High)
                    goto SignFlip;
            } else if (high > d1High) {
                goto SignFlip;
            }
        } else {
            // Signs are the same - add
            low64 = d1Low64 + d2Low64;
            high = d1High + d2High;

            // Propagate carry
            if (low64 < d1Low64) {
                high++;
                if (high <= d1High) {
                    // AlignedScale: The addition carried above 96 bits.
                    // Divide the value by 10, dropping the scale factor.
                    if ((flags & ScaleMask) == 0)
                        throw new OverflowException("Value was either too large or too small for a Decimal.");
                    flags -= 1u << ScaleShift;

                    const uint den = 10;
                    var num = high + (1ul << 32);
                    high = (uint)(num / den);
                    num = ((num - high * den) << 32) + (low64 >> 32);
                    var div = (uint)(num / den);
                    num = ((num - div * den) << 32) + (uint)low64;
                    low64 = div;
                    low64 <<= 32;
                    div = (uint)(num / den);
                    low64 += div;
                    div = (uint)num - div * den;

                    // See if we need to round up.
                    if (div >= 5 && (div > 5 || (low64 & 1) != 0)) {
                        if (++low64 == 0)
                            high++;
                    }
                }
            } else if (high < d1High) {
                // AlignedScale (same as above)
                if ((flags & ScaleMask) == 0)
                    throw new OverflowException("Value was either too large or too small for a Decimal.");
                flags -= 1u << ScaleShift;

                const uint den = 10;
                var num = high + (1ul << 32);
                high = (uint)(num / den);
                num = ((num - high * den) << 32) + (low64 >> 32);
                var div = (uint)(num / den);
                num = ((num - div * den) << 32) + (uint)low64;
                low64 = div;
                low64 <<= 32;
                div = (uint)(num / den);
                low64 += div;
                div = (uint)num - div * den;

                if (div >= 5 && (div > 5 || (low64 & 1) != 0)) {
                    if (++low64 == 0)
                        high++;
                }
            }
        }
        return;

        SignFlip:
        // Got negative result.  Flip its sign.
        flags ^= SignMask;
        high = ~high;
        low64 = (ulong)-(long)low64;
        if (low64 == 0)
            high++;
    }

    /// <summary>本家の Buf24 (192 ビット) 拡張加算経路 + ScaleResult。AlignedScale に届かなかった
    /// (10^n 倍が 192 ビットに広がる) 場合の加算。</summary>
    private static void ScaleBigBuffer(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, bool sign, ulong mid64, int scale) {
        // 本家: bufNum.Low64 = low64; bufNum.Mid64 = tmp64; uint hiProd = 3;
        // 本家の Buf24 は uint 6 個 (0..5)。Low64 = U0/U1、Mid64 = U2/U3、High64 = U4/U5。
        // hiProd は「最上位非ゼロ uint のインデックス」。
        var buf = new uint[6];
        buf[0] = (uint)low64;
        buf[1] = (uint)(low64 >> 32);
        buf[2] = (uint)mid64;
        buf[3] = (uint)(mid64 >> 32);
        var hiProd = 3u;

        // Scaling loop, up to 10^9 at a time. hiProd stays updated with index of highest non-zero uint.
        for (; scale > 0; scale -= MaxInt32Scale) {
            var power = TenToPowerNine;
            if (scale < MaxInt32Scale)
                power = UInt32Powers10[scale];
            var tmp64 = 0ul;
            // 本家: for (uint cur = 0; ;) { tmp64 += BigMul(rgulNum[cur], power);
            // rgulNum[cur] = (uint)tmp64; cur++; tmp64 >>= 32; if (cur > hiProd) break; }
            // cur の増分と判定の順序が意味を持つ (hiProd の要素まで処理してから脱出する)
            var cur = 0u;
            while (true) {
                tmp64 += (ulong)buf[cur] * power;
                buf[cur] = (uint)tmp64;
                cur++;
                tmp64 >>= 32;
                if (cur > hiProd)
                    break;
            }

            if ((uint)tmp64 != 0) {
                // We're extending the result by another uint.
                hiProd++;
                buf[hiProd] = (uint)tmp64;
            }
        }

        // Scaling complete, do the add.  Could be subtract if signs differ.
        var numLo = ((ulong)buf[0]) | ((ulong)buf[1] << 32);
        var numHigh = buf[2];
        if (sign) {
            // Signs differ, subtract.
            low64 = numLo - d2Low64;
            high = numHigh - d2High;

            // Propagate carry
            if (low64 > numLo) {
                high--;
                if (high < numHigh)
                    goto NoCarry; // 借りが上位に波及しない → ScaleResult へ (本家どおり)
            } else if (high <= numHigh) {
                goto NoCarry; // 借りが上位に波及しない → ScaleResult へ (本家どおり)
            }
            // Carry the subtraction into the higher bits.
            // (本家は if/else-if の外側で実行する。low64 <= numLo かつ high > numHigh の
            //  場合もここへ落ちる)
            {
                var cur = 3u;
                while (buf[cur]-- == 0) {
                    cur++;
                }
                if (buf[hiProd] == 0 && --hiProd <= 2)
                    goto ReturnResult;
            }
        } else {
            // Signs the same, add.
            low64 = numLo + d2Low64;
            high = numHigh + d2High;

            // Propagate carry
            if (low64 < numLo) {
                high++;
                if (high > numHigh)
                    goto NoCarry;
            } else if (high >= numHigh) {
                goto NoCarry;
            }

            for (var cur = 3u; ++buf[cur] == 0; cur++) {
                if (hiProd < cur) {
                    buf[cur] = 1;
                    hiProd = cur;
                    break;
                }
            }
        }

            NoCarry:
            buf[0] = (uint)low64;
            buf[1] = (uint)(low64 >> 32);
            buf[2] = high;
            var newScale = ScaleResult(buf, hiProd, (int)((flags & ScaleMask) >> ScaleShift));
        flags = (flags & ~ScaleMask) | ((uint)newScale << ScaleShift);
        low64 = ((ulong)buf[0]) | ((ulong)buf[1] << 32);
        high = buf[2];

        ReturnResult:
        return;
    }

    /// <summary>本家 VarDecMul。d1 (low64/high/flags) *= d2 (結果は d1 へ in-place)。</summary>
    private static void VarDecMul(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, uint d2flags) {
        var scale = (int)(byte)((flags + d2flags) >> ScaleShift);

        var bufProd = new uint[6];
        uint hiProd;

        if ((high | (uint)(low64 >> 32)) == 0) {
            var d1Low = (uint)low64;
            if ((d2High | (uint)(d2Low64 >> 32)) == 0) {
                // Upper 64 bits are zero.
                var d2Low = (uint)d2Low64;
                Mul64x64(d1Low, d2Low, out var prodLow, out var prodHigh);
                if (scale > DecScaleMax) {
                    // Result scale is too big.  Divide result by power of 10 to reduce it.
                    if (scale > DecScaleMax + 19)
                        goto ReturnZero;

                    scale -= DecScaleMax + 1;
                    var power = UInt64Powers10[scale];

                    var quotient = prodLow / power;
                    var remainder = prodLow % power;
                    prodLow = quotient;

                    // Round result.  See if remainder >= 1/2 of divisor.
                    // Divisor is a power of 10, so it is always even.
                    power >>= 1;
                    if (remainder >= power && (remainder > power || ((uint)prodLow & 1) > 0))
                        prodLow++;
                    scale = DecScaleMax;
                }
                low64 = prodLow;
                high = (uint)prodHigh;
                flags = ((d2flags ^ flags) & SignMask) | ((uint)scale << ScaleShift);
                return;
            } else {
                // Left value is 32-bit, result fits in 4 uints
                Mul64x64(d1Low, d2Low64, out var low, out var tmp);
                bufProd[0] = (uint)low;
                bufProd[1] = (uint)(low >> 32);

                if (d2High != 0) {
                    Mul64x64(d1Low, d2High, out var l2, out var h2);
                    tmp += l2;
                    if (tmp > uint.MaxValue) {
                        bufProd[2] = (uint)tmp;
                        bufProd[3] = (uint)(tmp >> 32);
                        hiProd = 3;
                        goto SkipScan;
                    }
                }
                bufProd[2] = (uint)tmp;
                hiProd = 2;
            }
        } else if ((d2High | (uint)(d2Low64 >> 32)) == 0) {
            // Right value is 32-bit, result fits in 4 uints
            var d2Low = (uint)d2Low64;
            Mul64x64(low64, d2Low, out var low, out var tmp);
            bufProd[0] = (uint)low;
            bufProd[1] = (uint)(low >> 32);

            if (high != 0) {
                Mul64x64(d2Low, high, out var l2, out var h2);
                tmp += l2;
                if (tmp > uint.MaxValue) {
                    bufProd[2] = (uint)tmp;
                    bufProd[3] = (uint)(tmp >> 32);
                    hiProd = 3;
                    goto SkipScan;
                }
            }
            bufProd[2] = (uint)tmp;
            hiProd = 2;
        } else {
            // At least one operand has bits set in the upper 64 bits.
            //
            // Compute and accumulate the 9 partial products into a 192-bit (3*64bit) result.
            //
            //                [l-hi][l-lo]   left high32, low64
            //             x  [r-hi][r-lo]   right high32, low64
            // -------------------------------
            //                [ 0-h][0-l ]   l-lo * r-lo => 64 + 64 bit result
            //          [ h*l][h*l ]         l-lo * r-hi => 32 + 64 bit result
            //          [ l*h][l*h ]         l-hi * r-lo => 32 + 64 bit result
            //          [ h*h]               l-hi * r-hi => 32 + 32 bit result
            // ------------------------------
            //          [Hi64][Mid64][Low64]   bufProd "array"
            //
            Mul64x64(low64, d2Low64, out var lowTmp, out var mid64);
            bufProd[0] = (uint)lowTmp;
            bufProd[1] = (uint)(lowTmp >> 32);

            if ((high | d2High) != 0) {
                // hi64 will never overflow since the result will always fit in 192 (2*96) bits
                // (本家 Math.BigMul(d1.High, d2.High) = uint × uint の 64 ビット積)
                Mul64x64(high, d2High, out var hh, out _);
                var hi64 = hh;

                // Do crosswise multiplications between upper 32bit and lower 64 bits
                Mul64x64(low64, d2High, out var lo1, out var hi1);
                hi64 += hi1;
                mid64 += lo1;
                if (mid64 < lo1)
                    hi64++;

                Mul64x64(d2Low64, high, out var lo2, out var hi2);
                hi64 += hi2;
                mid64 += lo2;
                if (mid64 < lo2)
                    hi64++;

                bufProd[2] = (uint)mid64;
                bufProd[3] = (uint)(mid64 >> 32);
                bufProd[4] = (uint)hi64;
                bufProd[5] = (uint)(hi64 >> 32);
                hiProd = 5;
            } else {
                bufProd[2] = (uint)mid64;
                bufProd[3] = (uint)(mid64 >> 32);
                hiProd = 3;
            }
        }

        // Check for leading zero uints on the product
        while (bufProd[hiProd] == 0) {
            if (hiProd == 0)
                goto ReturnZero;
            hiProd--;
        }

        SkipScan:
        if (hiProd > 2 || scale > DecScaleMax) {
            scale = ScaleResult(bufProd, hiProd, scale);
        }

        low64 = ((ulong)bufProd[0]) | ((ulong)bufProd[1] << 32);
        high = bufProd[2];
        flags = ((d2flags ^ flags) & SignMask) | ((uint)scale << ScaleShift);
        return;

        ReturnZero:
        low64 = 0;
        high = 0;
        flags = 0;
    }

    /// <summary>本家 ScaleResult。buf (96 ビット超の積) を 96 ビットに収める (丸めは half-even)。
    /// 戻り値は調整後の scale。hiRes は最上位非ゼロ uint のインデックス。</summary>
    private static int ScaleResult(uint[] result, uint hiRes, int scale) {
        // See if we need to scale the result.  The combined scale must
        // be <= DEC_SCALE_MAX and the upper 96 bits must be zero.
        //
        // Start by figuring a lower bound on the scaling needed to make
        // the upper 96 bits zero.  hiRes is the index into result[] of the highest non-zero uint.
        //
        var newScale = 0;
        if (hiRes > 2) {
            newScale = (int)hiRes * 32 - 64 - 1;
            newScale -= LeadingZeroCount(result[hiRes]);

            // Multiply bit position by log10(2) to figure its power of 10.
            // We scale the log by 256.  log(2) = .30103, * 256 = 77.
            newScale = ((newScale * 77) >> 8) + 1;

            // newScale = min scale factor to make high 96 bits zero, 0 - 29.
            if (newScale > scale)
                throw new OverflowException("Value was either too large or too small for a Decimal.");
        }

        // Make sure we scale by enough to bring the current scale factor into valid range.
        if (newScale < scale - DecScaleMax)
            newScale = scale - DecScaleMax;

        if (newScale != 0) {
            // Scale by the power of 10 given by newScale.  Note that this is
            // NOT guaranteed to bring the number within 96 bits -- it could be 1 power of 10 short.
            scale -= newScale;
            var sticky = 0u;
            var remainder = 0u;

            while (true) {
                sticky |= remainder; // record remainder as sticky bit

                uint power;
                uint quotient;
                // Scaling loop specialized for each power of 10 because division by constant is
                // an order of magnitude faster (especially for 64-bit division). (本家 switch どおり)
                switch (newScale) {
                    case 1:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 10);
                        break;
                    case 2:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 100);
                        break;
                    case 3:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 1000);
                        break;
                    case 4:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 10000);
                        break;
                    case 5:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 100000);
                        break;
                    case 6:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 1000000);
                        break;
                    case 7:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 10000000);
                        break;
                    case 8:
                        power = DivByConst(result, hiRes, out quotient, out remainder, 100000000);
                        break;
                    default:
                        power = DivByConst(result, hiRes, out quotient, out remainder, TenToPowerNine);
                        break;
                }
                result[hiRes] = quotient;
                // If first quotient was 0, update hiRes.
                if (result[hiRes] == 0 && hiRes != 0)
                    hiRes--;

                newScale -= MaxInt32Scale;
                if (newScale > 0)
                    continue; // scale some more

                // If we scaled enough, hiRes would be 2 or less.  If not, divide by 10 more.
                if (hiRes > 2) {
                    if (scale == 0)
                        throw new OverflowException("Value was either too large or too small for a Decimal.");
                    newScale = 1;
                    scale--;
                    continue; // scale by 10
                }

                // Round final result.  See if remainder >= 1/2 of divisor.
                // If remainder == 1/2 divisor, round up if odd or sticky bit set.
                power >>= 1;  // power of 10 always even
                if (power <= remainder && (power < remainder || ((result[0] & 1) | sticky) != 0)) {
                    if (++result[0] == 0) {
                        var cur = 0;
                        do {
                            cur++;
                            result[cur]++;
                        } while (result[cur] == 0);

                        if (cur > 2) {
                            // The rounding caused us to carry beyond 96 bits.
                            // Scale by 10 more.
                            if (scale == 0)
                                throw new OverflowException("Value was either too large or too small for a Decimal.");
                            hiRes = (uint)cur;
                            sticky = 0;    // no sticky bit
                            remainder = 0; // or remainder
                            newScale = 1;
                            scale--;
                            continue; // scale by 10
                        }
                    }
                }

                break;
            } // while (true)
        }
        return scale;
    }

    /// <summary>本家 DivByConst。result[0..hiRes-1] を power 除算列で割り、最上位桁の商と余りを
    /// out で返す (result[hiRes] 自体は書き換えない — 呼び出し側が result[hiRes] = quotient を行う)。
    /// 戻り値は power (丸め判定で半分値を取るため)。</summary>
    private static uint DivByConst(uint[] result, uint hiRes, out uint quotient, out uint remainder, uint power) {
        var high = result[hiRes];
        quotient = high / power;
        remainder = high % power;
        for (var i = hiRes - 1; (int)i >= 0; i--) {
            var num = result[i] + ((ulong)remainder << 32);
            var q = (uint)(num / power);
            remainder = (uint)num - q * power;
            result[i] = q;
        }
        return power;
    }

    /// <summary>本家 SearchScale。商 (96 ビット: low64 + u2) に 10^n を掛けても 96 ビットに収まる
    /// 最大の n (0..8。9 は早期 return) を返す。scale は現在の scale。</summary>
    private static int SearchScale(ulong resMidLo, uint resHi, int scale) {
        const uint OvflMax1Hi = 429496729u;
        const uint OvflMax9Hi = 4u;
        const ulong OvflMax9MidLo = 5441186219426131129ul;
        const uint OvflMax2Hi = 42949672u;
        const uint OvflMax3Hi = 4294967u;
        const uint OvflMax4Hi = 429496u;
        const uint OvflMax5Hi = 42949u;
        const uint OvflMax6Hi = 4294u;
        const uint OvflMax7Hi = 429u;
        const uint OvflMax8Hi = 42u;

        var curScale = 0;

        // Quick check to stop us from trying to scale any more.
        if (resHi > OvflMax1Hi)
            goto HaveScale;

        if (scale > DecScaleMax - 9) {
            // We can't scale by 10^9 without exceeding the max scale factor.
            // See if we can scale to the max.  If not, we'll fall into
            // standard search for scale factor.
            curScale = DecScaleMax - scale;
            if (resHi < PowerOvflHi[curScale - 1])
                goto HaveScale;
        } else if (resHi < OvflMax9Hi || (resHi == OvflMax9Hi && resMidLo <= OvflMax9MidLo)) {
            return 9;
        }

        // Search for a power to scale by < 9.  Do a binary search.
        if (resHi > OvflMax5Hi) {
            if (resHi > OvflMax3Hi) {
                curScale = 2;
                if (resHi > OvflMax2Hi)
                    curScale--;
            } else {
                curScale = 4;
                if (resHi > OvflMax4Hi)
                    curScale--;
            }
        } else {
            if (resHi > OvflMax7Hi) {
                curScale = 6;
                if (resHi > OvflMax6Hi)
                    curScale--;
            } else {
                curScale = 8;
                if (resHi > OvflMax8Hi)
                    curScale--;
            }
        }

        // In all cases, we already found we could not use the power one larger.
        // So if we can use this power, it is the biggest, and we're done.  If
        // we can't use this power, the one below it is correct for all cases
        // unless it's 10^1 -- we might have to go to 10^0 (no scaling).
        if (resHi == PowerOvflHi[curScale - 1] && resMidLo > PowerOvflMidLo[curScale - 1])
            curScale--;

        HaveScale:
        // curScale = largest power of 10 we can scale by without overflow,
        // curScale < 9.  See if this is enough to make scale factor positive if it isn't already.
        if (curScale + scale < 0)
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        return curScale;
    }

    /// <summary>本家 IncreaseScale (Buf12 版)。96 ビット値 × power、戻り値は溢れ上位ビット
    /// (0 以外 = 96 ビット溢れ)。</summary>
    private static uint IncreaseScale(uint[] bufNum, uint power) {
        var tmp = (ulong)bufNum[0] * power;
        bufNum[0] = (uint)tmp;
        tmp >>= 32;
        tmp += (ulong)bufNum[1] * power;
        bufNum[1] = (uint)tmp;
        tmp >>= 32;
        tmp += (ulong)bufNum[2] * power;
        bufNum[2] = (uint)tmp;
        return (uint)(tmp >> 32);
    }

    /// <summary>本家 IncreaseScale (Buf16 版)。128 ビット値 × power (溢れなし前提)。</summary>
    private static void IncreaseScale16(uint[] bufNum, uint power) {
        Mul64x64(((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32), power, out var low64, out var hi64);
        bufNum[0] = (uint)low64;
        bufNum[1] = (uint)(low64 >> 32);
        hi64 += (ulong)bufNum[2] * power;
        bufNum[2] = (uint)hi64;
        bufNum[3] = (uint)(hi64 >> 32);
    }

    /// <summary>本家 IncreaseScale64 (Buf12 版、U2 を更新しない)。64 ビット値 × power。</summary>
    private static void IncreaseScale64(uint[] bufNum, uint power) {
        Mul64x64(((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32), power, out var low64, out var hi64);
        bufNum[0] = (uint)low64;
        bufNum[1] = (uint)(low64 >> 32);
        bufNum[2] = (uint)hi64;
    }

    /// <summary>本家 Add32To96。96 ビット値に 32 ビット値を加算。戻り値 false = 96 ビット溢れ。</summary>
    private static bool Add32To96(uint[] bufNum, uint value) {
        var low64 = ((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32);
        low64 += value;
        bufNum[0] = (uint)low64;
        bufNum[1] = (uint)(low64 >> 32);
        if (low64 < value) {
            if (++bufNum[2] == 0)
                return false;
        }
        return true;
    }

    /// <summary>本家 Div96By32。96 ビット値 ÷ den。商を bufNum に書き戻し、余りを返す。</summary>
    private static uint Div96By32(uint[] bufNum, uint den) {
        var low64 = ((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32);
        if (bufNum[2] != 0) {
            // 本家 bufNum.High64 = U1 | ((ulong)U2 << 32) (Buf12 の offset 4 オーバーレイ)。
            // High64 = div は U1 = 下位 32 ビット / U2 = 上位 32 ビットの代入
            var tmp = bufNum[1] | ((ulong)bufNum[2] << 32);
            var div = tmp / den;
            var rem = (uint)(tmp % den);
            bufNum[1] = (uint)div;
            bufNum[2] = (uint)(div >> 32);

            tmp = ((ulong)rem << 32) | bufNum[0];
            if (tmp == 0)
                return 0;
            var div2 = tmp / den;
            var rem2 = (uint)(tmp % den);
            bufNum[0] = (uint)div2;
            return rem2;
        }

        if (low64 == 0)
            return 0;
        var quotient = low64 / den;
        var remainder = (uint)(low64 % den);
        bufNum[0] = (uint)quotient;
        bufNum[1] = (uint)(quotient >> 32);
        return remainder;
    }

    /// <summary>本家 Div64By32 (非 X86 経路)。商を返し、余りを out で返す
    /// (本家タプル (Quotient, Remainder) と同順)。</summary>
    private static uint Div64By32(ulong dividend, uint den, out uint remainder) {
        var quo = (uint)(dividend / den);
        remainder = (uint)dividend - quo * den;
        return quo;
    }

    /// <summary>本家 Div96By64。96 ビット値 (u2 + low64) ÷ den (64 ビット、den &gt; u2 前提)。
    /// 商の下位 32 ビットを返し、残り (余り) を bufNum に書き戻す。</summary>
    private static uint Div96By64(uint[] bufNum, ulong den) {
        var num2 = bufNum[2];
        if (num2 == 0) {
            var num = ((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32);
            if (num < den)
                // Result is zero.  Entire dividend is remainder.
                return 0;

            var quo64 = num / den;
            bufNum[0] = (uint)quo64;
            bufNum[1] = (uint)(quo64 >> 32);
            return (uint)(num % den);
        }

        uint quo;
        var denHigh32 = (uint)(den >> 32);
        if (num2 >= denHigh32) {
            // Divide would overflow.  Assume a quotient of 2^32, and set up remainder accordingly.
            var num = ((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32);
            num -= den << 32;
            quo = 0;

            // Remainder went negative.  Add divisor back in until it's positive, a max of 2 times.
            do {
                quo--;
                num += den;
            } while (num >= den);

            bufNum[0] = (uint)num;
            bufNum[1] = (uint)(num >> 32);
            return quo;
        }

        // Hardware divide won't overflow
        // 本家 bufNum.High64 = U1 | ((ulong)U2 << 32) (Buf12 の offset 4 オーバーレイ)
        var num64 = bufNum[1] | ((ulong)num2 << 32);
        if (num64 < denHigh32)
            // Result is zero.  Entire dividend is remainder.
            return 0;

        var q = Div64By32(num64, denHigh32, out var rem);
        var n = bufNum[0] | ((ulong)rem << 32); // remainder

        // Compute full remainder, rem = dividend - (quo * divisor).
        Mul64x64(q, (uint)den, out var prod, out var prodHi); // quo * lo divisor
        n -= prod;

        if (n > ~prod) {
            // Remainder went negative.  Add divisor back in until it's positive, a max of 2 times.
            do {
                q--;
                n += den;
            } while (n >= den);
        }

        bufNum[0] = (uint)n;
        bufNum[1] = (uint)(n >> 32);
        return q;
    }

    /// <summary>本家 Div128By64。128 ビット値 (buf[3]上位, buf[0..2]下位) ÷ den (64 ビット、
    /// den &gt; 上位 64 ビット前提)。商 64 ビットを返し、余りを buf 下位 96 ビットに書き戻す。</summary>
    private static ulong Div128By64(uint[] bufNum, ulong den) {
        var hiBits = Div96By64Offset(bufNum, 1, den);
        var loBits = Div96By64Offset(bufNum, 0, den);
        return ((ulong)hiBits << 32) | loBits;
    }

    /// <summary>Div96By64 の「配列オフセット版」: bufNum[offset..offset+3] の 96 ビットを den で割る。
    /// 本家は *(Buf12*)&amp;bufNum->U1 の形で呼ぶ (Div128By64 の非 X86 経路)。</summary>
    private static uint Div96By64Offset(uint[] bufNum, int offset, ulong den) {
        var num2 = bufNum[offset + 2];
        if (num2 == 0) {
            var num = ((ulong)bufNum[offset]) | ((ulong)bufNum[offset + 1] << 32);
            if (num < den)
                return 0;
            var quo64 = num / den;
            bufNum[offset] = (uint)quo64;
            bufNum[offset + 1] = (uint)(quo64 >> 32);
            return (uint)(num % den);
        }

        uint quo;
        var denHigh32 = (uint)(den >> 32);
        if (num2 >= denHigh32) {
            var num = ((ulong)bufNum[offset]) | ((ulong)bufNum[offset + 1] << 32);
            num -= den << 32;
            quo = 0;
            do {
                quo--;
                num += den;
            } while (num >= den);
            bufNum[offset] = (uint)num;
            bufNum[offset + 1] = (uint)(num >> 32);
            return quo;
        }

        // 本家 bufNum.High64 = U1 | ((ulong)U2 << 32) (Buf12 の offset 4 オーバーレイ)
        var num64 = bufNum[offset + 1] | ((ulong)num2 << 32);
        if (num64 < denHigh32)
            return 0;

        var q = Div64By32(num64, denHigh32, out var rem);
        var n = bufNum[offset] | ((ulong)rem << 32);
        Mul64x64(q, (uint)den, out var prod, out _);
        n -= prod;

        if (n > ~prod) {
            do {
                q--;
                n += den;
            } while (n >= den);
        }

        bufNum[offset] = (uint)n;
        bufNum[offset + 1] = (uint)(n >> 32);
        return q;
    }

    /// <summary>本家 Div128By96。128 ビット値 ÷ 96 ビット値 (上位 32 ビット den &gt; dividend 上位 前提)。
    /// 商 32 ビットを返し、128 ビット残りを bufNum に書き戻す。</summary>
    private static uint Div128By96(uint[] bufNum, uint[] bufDen) {
        var dividend = ((ulong)bufNum[2]) | ((ulong)bufNum[3] << 32);
        var den = bufDen[2];
        if (dividend < den)
            // Result is zero.  Entire dividend is remainder.
            return 0;

        var quo = Div64By32(dividend, den, out var remainder);

        // Compute full remainder, rem = dividend - (quo * divisor).
        Mul64x64(bufDen[0] | ((ulong)bufDen[1] << 32), quo, out var prod1, out var prod1Hi);
        var prod2 = (uint)prod1Hi;
        var num = (((ulong)bufNum[0]) | ((ulong)bufNum[1] << 32)) - prod1;
        remainder -= prod2;

        // Propagate carries
        if (num > ~prod1) {
            remainder--;
            if (remainder < ~prod2)
                goto PosRem;
        } else if (remainder <= ~prod2) {
            goto PosRem;
        }

        // Remainder went negative.  Add divisor back in until it's positive, a max of 2 times.
        var denLow64 = ((ulong)bufDen[0]) | ((ulong)bufDen[1] << 32);
        while (true) {
            quo--;
            num += denLow64;
            remainder += den;

            if (num < denLow64) {
                // Detected carry. Check for carry out of top before adding it in.
                if (remainder++ < den)
                    break;
            }
            if (remainder < den)
                break; // detected carry
        }

        PosRem:
        bufNum[0] = (uint)num;
        bufNum[1] = (uint)(num >> 32);
        bufNum[2] = remainder;
        return quo;
    }

    /// <summary>本家 OverflowUnscale。96 ビット溢れした商に上位 1 ビットを補い ÷10 する。
    /// 戻り値は調整後の scale。丸めは half-up (sticky または奇数で切り上げ)。</summary>
    private static int OverflowUnscale(uint[] bufQuo, int scale, bool sticky) {
        if (--scale < 0)
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        // We have overflown, so load the high bit with a one.
        const ulong highbit = 1ul << 32;
        bufQuo[2] = (uint)(highbit / 10);

        uint remainder;
        var tmp = ((highbit % 10) << 32) + bufQuo[1];
        var div = (uint)(tmp / 10);
        bufQuo[1] = div;
        tmp = ((tmp - div * 10) << 32) + bufQuo[0];
        div = (uint)(tmp / 10);
        bufQuo[0] = div;
        remainder = (uint)(tmp - div * 10);

        // The remainder is the last digit that does not fit, so we can use it to work out if we need to round up
        if (remainder > 5 || (remainder == 5 && (sticky || (bufQuo[0] & 1) != 0)))
            Add32To96(bufQuo, 1);
        return scale;
    }

    /// <summary>本家 Unscale。96 ビット値の末尾 0 を scale の許す限り削る (結果の正規化)。</summary>
    private static void Unscale(ref uint low, ref ulong high64, ref int scale) {
        // Since 10 = 2 * 5, there must be a factor of 2 for every power of 10 we can extract.
        // We use this as a quick test on whether to try a given power.
        while ((byte)low == 0 && scale >= 8 && Div96ByConst(ref high64, ref low, 100000000))
            scale -= 8;

        if ((low & 0xF) == 0 && scale >= 4 && Div96ByConst(ref high64, ref low, 10000))
            scale -= 4;

        if ((low & 3) == 0 && scale >= 2 && Div96ByConst(ref high64, ref low, 100))
            scale -= 2;

        if ((low & 1) == 0 && scale >= 1 && Div96ByConst(ref high64, ref low, 10))
            scale--;
    }

    /// <summary>本家 Div96ByConst (64 ビット経路)。96 ビット値 (high64:mid+hi, low:lo) ÷ pow。
    /// 割り切れた場合のみ書き換えて true を返す。</summary>
    private static bool Div96ByConst(ref ulong high64, ref uint low, uint pow) {
        var div64 = high64 / pow;
        var div = (uint)((((high64 - div64 * pow) << 32) + low) / pow);
        if (low == div * pow) {
            high64 = div64;
            low = div;
            return true;
        }
        return false;
    }

    /// <summary>本家 VarDecDiv。d1 /= d2 (結果は d1 へ in-place)。</summary>
    private static void VarDecDiv(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, uint d2flags) {
        uint power;
        int curScale;

        var scale = (int)(sbyte)((flags - d2flags) >> ScaleShift);
        var unscale = false;
        var bufQuo = new uint[3]; // [0..1] = Low64, [2] = U2

        if ((d2High | (uint)(d2Low64 >> 32)) == 0) {
            // Divisor is only 32 bits.  Easy divide.
            var den = (uint)d2Low64;
            if (den == 0)
                throw new DivideByZeroException();

            bufQuo[0] = (uint)low64;
            bufQuo[1] = (uint)(low64 >> 32);
            bufQuo[2] = high;
            var remainder = Div96By32(bufQuo, den);

            while (true) {
                if (remainder == 0) {
                    if (scale < 0) {
                        curScale = Math.Min(9, -scale);
                        goto HaveScale;
                    }
                    break;
                }

                // We need to unscale if and only if we have a non-zero remainder
                unscale = true;

                // We have computed a quotient based on the natural scale
                // ( <dividend scale> - <divisor scale> ).  We have a non-zero
                // remainder, so now we should increase the scale if possible to
                // include more quotient bits.
                if (scale == DecScaleMax || (curScale = SearchScale(((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32), bufQuo[2], scale)) == 0) {
                    // No more scaling to be done, but remainder is non-zero.
                    // Round quotient.
                    var tmp = remainder << 1;
                    if (tmp < remainder || (tmp >= den && (tmp > den || (bufQuo[0] & 1) != 0)))
                        goto RoundUp;
                    break;
                }

                HaveScale:
                power = UInt32Powers10[curScale];
                scale += curScale;

                if (IncreaseScale(bufQuo, power) != 0)
                    goto ThrowOverflow;

                var num = (ulong)remainder * power; // 本家 Math.BigMul(remainder, power) = 64 ビット積
                var div32 = Div64By32(num, den, out var rem2);

                if (!Add32To96(bufQuo, div32)) {
                    scale = OverflowUnscale(bufQuo, scale, rem2 != 0);
                    break;
                }
                remainder = rem2;
            } // while (true)
        } else {
            // Divisor has bits set in the upper 64 bits.
            //
            // Divisor must be fully normalized (shifted so bit 31 of the most significant uint is 1).
            // Locate the MSB so we know how much to normalize by.  The dividend will be shifted
            // by the same amount so the quotient is not changed.
            var tmp = d2High;
            if (tmp == 0)
                tmp = (uint)(d2Low64 >> 32);

            curScale = LeadingZeroCount(tmp);

            // Shift both dividend and divisor left by curScale.
            var bufRem = new uint[4]; // [0..1] = Low64, [2..3] = High64
            var dividendLow64 = low64 << curScale;
            bufRem[0] = (uint)dividendLow64;
            bufRem[1] = (uint)(dividendLow64 >> 32);
            // 本家: bufRem.High64 = (d1.Mid + ((ulong)d1.High << 32)) >> (32 - curScale);
            var dividendHigh64 = ((ulong)(uint)(low64 >> 32) + ((ulong)high << 32)) >> (32 - curScale);
            bufRem[2] = (uint)dividendHigh64;
            bufRem[3] = (uint)(dividendHigh64 >> 32);

            var divisor = d2Low64 << curScale;

            if (d2High == 0) {
                // Have a 64-bit divisor.  The remainder (currently 96 bits spread over 4 uints) will be < divisor.
                bufQuo[2] = 0;
                var q64 = Div128By64(bufRem, divisor);
                bufQuo[0] = (uint)q64;
                bufQuo[1] = (uint)(q64 >> 32);
                while (true) {
                    if ((((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32)) == 0) {
                        if (scale < 0) {
                            curScale = Math.Min(9, -scale);
                            goto HaveScale64;
                        }
                        break;
                    }

                    // We need to unscale if and only if we have a non-zero remainder
                    unscale = true;

                    // Remainder is non-zero.  Scale up quotient and remainder by powers of 10
                    // so we can compute more significant bits.
                    if (scale == DecScaleMax || (curScale = SearchScale(((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32), bufQuo[2], scale)) == 0) {
                        // No more scaling to be done, but remainder is non-zero.
                        // Round quotient.
                        var tmp64 = ((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32);
                        if ((long)tmp64 < 0) {
                            goto RoundUp;
                        }
                        tmp64 <<= 1;
                        if (tmp64 > divisor || (tmp64 == divisor && (bufQuo[0] & 1) != 0))
                            goto RoundUp;
                        break;
                    }

                    HaveScale64:
                    power = UInt32Powers10[curScale];
                    scale += curScale;

                    if (IncreaseScale(bufQuo, power) != 0)
                        goto ThrowOverflow;

                    IncreaseScale64(bufRem, power);
                    var q = Div96By64(bufRem, divisor);
                    if (!Add32To96(bufQuo, q)) {
                        scale = OverflowUnscale(bufQuo, scale, ((ulong)bufRem[0]) != 0 || ((ulong)bufRem[1] << 32) != 0);
                        break;
                    }
                } // while (true)
            } else {
                // Have a 96-bit divisor in bufDivisor.
                var bufDivisor = new uint[3];
                bufDivisor[0] = (uint)divisor;
                bufDivisor[1] = (uint)(divisor >> 32);
                bufDivisor[2] = (uint)(((uint)(d2Low64 >> 32) + ((ulong)d2High << 32)) >> (32 - curScale));

                // The remainder (currently 96 bits spread over 4 uints) will be < divisor.
                var q = Div128By96(bufRem, bufDivisor);
                // 本家は bufQuo.Low64 = Div128By96(...) (uint → ulong のゼロ拡張)。
                // q は 32 ビット商なので上位 32 ビットは必ず 0 (q >> 32 は uint では
                // シフト量がマスクされ no-op になるため使わない)
                bufQuo[0] = q;
                bufQuo[1] = 0;
                bufQuo[2] = 0;

                while (true) {
                    if ((((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32)) == 0 && bufRem[2] == 0) {
                        if (scale < 0) {
                            curScale = Math.Min(9, -scale);
                            goto HaveScale96;
                        }
                        break;
                    }

                    // We need to unscale if and only if we have a non-zero remainder
                    unscale = true;

                    // Remainder is non-zero.  Scale up quotient and remainder by powers of 10
                    // so we can compute more significant bits.
                    if (scale == DecScaleMax || (curScale = SearchScale(((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32), bufQuo[2], scale)) == 0) {
                        // No more scaling to be done, but remainder is non-zero.
                        // Round quotient.
                        if ((int)bufRem[2] < 0)
                            goto RoundUp;

                        var tmp2 = bufRem[1] >> 31;
                        var remLo64 = ((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32);
                        remLo64 <<= 1;
                        bufRem[0] = (uint)remLo64;
                        bufRem[1] = (uint)(remLo64 >> 32);
                        bufRem[2] = (bufRem[2] << 1) + tmp2;

                        if (bufRem[2] > bufDivisor[2] || (bufRem[2] == bufDivisor[2] &&
                            ((((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32)) > (((ulong)bufDivisor[0]) | ((ulong)bufDivisor[1] << 32)) ||
                             (((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32)) == (((ulong)bufDivisor[0]) | ((ulong)bufDivisor[1] << 32)) &&
                             (bufQuo[0] & 1) != 0)))
                            goto RoundUp;
                        break;
                    }

                    HaveScale96:
                    power = UInt32Powers10[curScale];
                    scale += curScale;

                    if (IncreaseScale(bufQuo, power) != 0)
                        goto ThrowOverflow;

                    IncreaseScale16(bufRem, power);
                    var q96 = Div128By96(bufRem, bufDivisor);
                    if (!Add32To96(bufQuo, q96)) {
                        scale = OverflowUnscale(bufQuo, scale, (((ulong)bufRem[0]) | ((ulong)bufRem[1] << 32)) != 0 || bufRem[2] != 0);
                        break;
                    }
                } // while (true)
            }
        }

        Unscale2:
        if (unscale) {
            var low = bufQuo[0];
            var high64 = ((ulong)bufQuo[1]) | ((ulong)bufQuo[2] << 32);
            Unscale(ref low, ref high64, ref scale);
            bufQuo[0] = low;
            bufQuo[1] = (uint)high64;
            bufQuo[2] = (uint)(high64 >> 32);
        }
        low64 = ((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32);
        high = bufQuo[2];
        flags = ((flags ^ d2flags) & SignMask) | ((uint)scale << ScaleShift);
        return;

        RoundUp:
        var qLow64 = ((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32);
        qLow64++;
        bufQuo[0] = (uint)qLow64;
        bufQuo[1] = (uint)(qLow64 >> 32);
        if (qLow64 == 0 && ++bufQuo[2] == 0) {
            scale = OverflowUnscale(bufQuo, scale, true);
        }
        goto Unscale2;

        ThrowOverflow:
        throw new OverflowException("Value was either too large or too small for a Decimal.");
    }

    /// <summary>本家 VarDecMod。d1 %= d2 (結果は d1 へ in-place、d2 は破壊される)。</summary>
    private static void VarDecMod(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, uint d2flags) {
        if ((d2Low64 | d2High) == 0)
            throw new DivideByZeroException();

        if ((low64 | high) == 0)
            return;

        // In the operation x % y the sign of y does not matter. Result will have the sign of x.
        d2flags = (d2flags & ~SignMask) | (flags & SignMask);

        var cmp = VarDecCmpSub(low64, high, flags, d2Low64, d2High, d2flags);
        if (cmp == 0) {
            low64 = 0;
            high = 0;
            if (d2flags > flags)
                flags = d2flags;
            return;
        }
        if ((cmp ^ (int)(flags & SignMask)) < 0)
            return;

        // The divisor is smaller than the dividend and both are non-zero.
        // Calculate the integer remainder using the larger scaling factor.
        var scale = (int)(sbyte)((flags - d2flags) >> ScaleShift);
        if (scale > 0) {
            // Divisor scale can always be increased to dividend scale for remainder calculation.
            do {
                var power = scale >= MaxInt32Scale ? TenToPowerNine : UInt32Powers10[scale];
                Mul64x64(d2Low64, power, out var low, out var hi32);
                d2Low64 = low;
                d2High = (uint)hi32 + d2High * power;
            } while ((scale -= MaxInt32Scale) > 0);
            scale = 0;
        }

        do {
            if (scale < 0) {
                flags = d2flags;
                // Try to scale up dividend to match divisor.
                var bufQuo = new uint[3];
                bufQuo[0] = (uint)low64;
                bufQuo[1] = (uint)(low64 >> 32);
                bufQuo[2] = high;
                do {
                    var iCurScale = SearchScale(((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32), bufQuo[2], DecScaleMax + scale);
                    if (iCurScale == 0)
                        break;
                    var power = iCurScale >= MaxInt32Scale ? TenToPowerNine : UInt32Powers10[iCurScale];
                    scale += iCurScale;
                    IncreaseScale(bufQuo, power);
                    if (power != TenToPowerNine)
                        break;
                } while (scale < 0);
                low64 = ((ulong)bufQuo[0]) | ((ulong)bufQuo[1] << 32);
                high = bufQuo[2];
            }

            if (high == 0) {
                low64 %= d2Low64;
                return;
            } else if ((d2High | (uint)(d2Low64 >> 32)) == 0) {
                var den = (uint)d2Low64;
                var tmp = ((ulong)high << 32) | (uint)(low64 >> 32);
                tmp = ((tmp % den) << 32) | (uint)low64;
                low64 = tmp % den;
                high = 0;
            } else {
                VarDecModFull(ref low64, ref high, ref flags, d2Low64, d2High, d2flags, scale);
                return;
            }
        } while (scale < 0);
    }

    /// <summary>本家 VarDecModFull (64/96 ビット分母の多倍長余り計算)。</summary>
    private static void VarDecModFull(ref ulong low64, ref uint high, ref uint flags,
        ulong d2Low64, uint d2High, uint d2flags, int scale) {
        // Divisor has bits set in the upper 64 bits.
        //
        // Divisor must be fully normalized (shifted so bit 31 of the most significant uint is 1).
        var tmp = d2High;
        if (tmp == 0)
            tmp = (uint)(d2Low64 >> 32);
        var shift = LeadingZeroCount(tmp);

        var b = new uint[7]; // 本家 Buf28 (7 uint)
        var d1Low64 = low64 << shift;
        b[0] = (uint)d1Low64;
        b[1] = (uint)(d1Low64 >> 32);
        var mid64 = ((uint)(low64 >> 32) + ((ulong)high << 32)) >> (32 - shift);
        b[2] = (uint)mid64;
        b[3] = (uint)(mid64 >> 32);

        // The dividend might need to be scaled up to 221 significant bits.
        // Maximum scaling is required when the divisor is 2^64 with scale 28 and is left shifted 31 bits
        // and the dividend is decimal.MaxValue: (2^96 - 1) * 10^28 << 31 = 221 bits.
        var highIdx = 3u;
        while (scale < 0) {
            var power = scale <= -MaxInt32Scale ? TenToPowerNine : UInt32Powers10[-scale];
            var tmp64 = (ulong)b[0] * power; // 本家 Math.BigMul(b[0], power)
            b[0] = (uint)tmp64;
            for (var i = 1; (int)i <= (int)highIdx; i++) {
                tmp64 >>= 32;
                tmp64 += (ulong)b[i] * power;
                b[i] = (uint)tmp64;
            }
            // The high bit of the dividend must not be set.
            if (tmp64 > int.MaxValue) {
                highIdx++;
                b[highIdx] = (uint)(tmp64 >> 32);
            }
            scale += MaxInt32Scale;
        }

        if (d2High == 0) {
            var divisor = d2Low64 << shift;
            switch (highIdx) {
                case 6:
                    Div96By64OffsetWindow(b, 4, divisor);
                    goto case 5;
                case 5:
                    Div96By64OffsetWindow(b, 3, divisor);
                    goto case 4;
                case 4:
                    Div96By64OffsetWindow(b, 2, divisor);
                    break;
            }
            Div96By64OffsetWindow(b, 1, divisor);
            Div96By64OffsetWindow(b, 0, divisor);

            low64 = (((ulong)b[0]) | ((ulong)b[1] << 32)) >> shift;
            high = 0;
        } else {
            var bufDivisor = new uint[3];
            bufDivisor[0] = (uint)(d2Low64 << shift);
            bufDivisor[1] = (uint)(d2Low64 << shift >> 32);
            bufDivisor[2] = (uint)(((uint)(d2Low64 >> 32) + ((ulong)d2High << 32)) >> (32 - shift));

            switch (highIdx) {
                case 6:
                    Div128By96Window(b, 3, bufDivisor);
                    goto case 5;
                case 5:
                    Div128By96Window(b, 2, bufDivisor);
                    goto case 4;
                case 4:
                    Div128By96Window(b, 1, bufDivisor);
                    break;
            }
            Div128By96Window(b, 0, bufDivisor);

            low64 = ((((ulong)b[0]) | ((ulong)b[1] << 32)) >> shift) + ((ulong)b[2] << (32 - shift) << 32);
            high = b[2] >> shift;
        }
    }

    /// <summary>Div96By64 の 96 ビット窓版 (VarDecModFull の case カスケード用)。
    /// buf[offset..offset+3] (u2 = buf[offset+2]) を den で割る。本家 Div96By64 と同一。</summary>
    private static void Div96By64OffsetWindow(uint[] buf, int offset, ulong den) {
        Div96By64Offset(buf, offset, den);
    }

    /// <summary>Div128By96 の 128 ビット窓版 (VarDecModFull の case カスケード用)。
    /// buf[offset..offset+3] の 128 ビットを bufDen で割る (余りが buf に残る)。</summary>
    private static void Div128By96Window(uint[] buf, int offset, uint[] bufDen) {
        Div128By96WindowImpl(buf, offset, bufDen);
    }

    /// <summary>Div128By96 の配列窓版本体 (本家 Div128By96 の offset 版)。</summary>
    private static uint Div128By96WindowImpl(uint[] bufNum, int offset, uint[] bufDen) {
        var dividend = ((ulong)bufNum[offset + 2]) | ((ulong)bufNum[offset + 3] << 32);
        var den = bufDen[2];
        if (dividend < den)
            return 0;

        var quo = Div64By32(dividend, den, out var remainder);

        Mul64x64(bufDen[0] | ((ulong)bufDen[1] << 32), quo, out var prod1, out var prod1Hi);
        var prod2 = (uint)prod1Hi;
        var num = (((ulong)bufNum[offset]) | ((ulong)bufNum[offset + 1] << 32)) - prod1;
        remainder -= prod2;

        if (num > ~prod1) {
            remainder--;
            if (remainder < ~prod2)
                goto PosRem;
        } else if (remainder <= ~prod2) {
            goto PosRem;
        }

        var denLow64 = ((ulong)bufDen[0]) | ((ulong)bufDen[1] << 32);
        while (true) {
            quo--;
            num += denLow64;
            remainder += den;
            if (num < denLow64) {
                if (remainder++ < den)
                    break;
            }
            if (remainder < den)
                break;
        }

        PosRem:
        bufNum[offset] = (uint)num;
        bufNum[offset + 1] = (uint)(num >> 32);
        bufNum[offset + 2] = remainder;
        return quo;
    }

    /// <summary>本家 InternalRound。d の下位 scale 桁を丸め落とす (mode は本家 5 種対応)。</summary>
    private static void InternalRound(ref ulong low64, ref uint high, ref uint flags, uint scale, MidpointRounding mode) {
        // the scale becomes the desired decimal count
        flags -= scale << ScaleShift;

        uint remainder;
        var sticky = 0u;
        uint power;
        var hi = high;
        var mid = (uint)(low64 >> 32);
        var lo = (uint)low64;
        // First divide the value by constant 10^9 up to three times
        while (scale >= MaxInt32Scale) {
            scale -= MaxInt32Scale;

            const uint divisor = TenToPowerNine;
            var n = hi;
            if (n == 0) {
                var tmp64 = ((ulong)mid << 32) | lo;
                var div64 = tmp64 / divisor;
                mid = (uint)(div64 >> 32);
                lo = (uint)div64;
                remainder = (uint)(tmp64 - div64 * divisor);
            } else {
                hi = n / divisor;
                remainder = n % divisor;
                n = mid;
                if ((n | remainder) != 0) {
                    var q = (uint)((((ulong)remainder << 32) | n) / divisor);
                    mid = q;
                    remainder = n - q * divisor;
                }
                n = lo;
                if ((n | remainder) != 0) {
                    var q = (uint)((((ulong)remainder << 32) | n) / divisor);
                    lo = q;
                    remainder = n - q * divisor;
                }
            }
            power = divisor;
            if (scale == 0)
                goto checkRemainder;
            sticky |= remainder;
        }

        {
            power = UInt32Powers10[scale];
            // TODO: https://github.com/dotnet/runtime/issues/5213
            var n = hi;
            if (n == 0) {
                var tmp64 = ((ulong)mid << 32) | lo;
                if (tmp64 == 0) {
                    // 本家は除算ごとに d.Low64/d.uhi を書き換えるため、早期 done でも
                    // 除算済みの値が残る。ポートはローカル→ref を checkRemainder で
                    // まとめて書き戻すため、この早期 done の前に書き戻す必要がある
                    low64 = ((ulong)lo) | ((ulong)mid << 32);
                    high = hi;
                    if (mode <= MidpointRounding.ToZero)
                        goto done;
                    remainder = 0;
                    goto checkRemainder;
                }
                var div64 = tmp64 / power;
                mid = (uint)(div64 >> 32);
                lo = (uint)div64;
                remainder = (uint)(tmp64 - div64 * power);
            } else {
                hi = n / power;
                remainder = n % power;
                n = mid;
                if ((n | remainder) != 0) {
                    var q = (uint)((((ulong)remainder << 32) | n) / power);
                    mid = q;
                    remainder = n - q * power;
                }
                n = lo;
                if ((n | remainder) != 0) {
                    var q = (uint)((((ulong)remainder << 32) | n) / power);
                    lo = q;
                    remainder = n - q * power;
                }
            }
        }

        checkRemainder:
        low64 = ((ulong)lo) | ((ulong)mid << 32);
        high = hi;
        if (mode == MidpointRounding.ToZero)
            goto done;
        if (mode == MidpointRounding.ToEven) {
            // To do IEEE rounding, we add LSB of result to sticky bits so either causes round up if remainder * 2 == last divisor.
            remainder <<= 1;
            if ((sticky | (uint)(lo & 1)) != 0)
                remainder++;
            if (power >= remainder)
                goto done;
        } else if (mode == MidpointRounding.AwayFromZero) {
            // Round away from zero at the mid point.
            remainder <<= 1;
            if (power > remainder)
                goto done;
        } else if (mode == MidpointRounding.ToNegativeInfinity) {
            // Round toward -infinity if we have chopped off a non-zero amount from a negative value.
            if ((remainder | sticky) == 0 || (flags & SignMask) == 0)
                goto done;
        } else {
            // Round toward infinity if we have chopped off a non-zero amount from a positive value.
            if ((remainder | sticky) == 0 || (flags & SignMask) != 0)
                goto done;
        }
        if (++low64 == 0)
            high++;

        done:
        return;
    }

    /// <summary>本家 Round(ref decimal, int decimals, MidpointRounding) の引数検証 + スケール計算。</summary>
    private static void RoundParts(ref ulong low64, ref uint high, ref uint flags, int decimals, MidpointRounding mode) {
        if ((uint)decimals > 28u)
            throw new ArgumentOutOfRangeException(nameof(decimals), "Rounding decimal values to the number of decimal places must be between 0 and 28.");
        if ((uint)(int)mode > 4u)
            throw new ArgumentException("Invalid value for enum MidpointRounding.");
        var scale = (int)((flags & ScaleMask) >> ScaleShift) - decimals;
        if (scale > 0)
            InternalRound(ref low64, ref high, ref flags, (uint)scale, mode);
    }

    /// <summary>本家 Truncate(ref decimal) (scale &gt; 0 なら InternalRound ToZero)。</summary>
    private static void TruncateInPlace(ref ulong low64, ref uint high, ref uint flags) {
        if ((flags & ScaleMask) != 0)
            InternalRound(ref low64, ref high, ref flags, (flags & ScaleMask) >> ScaleShift, MidpointRounding.ToZero);
    }

    /// <summary>本家 VarDecCmpSub。96 ビット比較 (結果: &lt;0 / 0 / &gt;0)。符号・scale を考慮。</summary>
    /// <summary>本家 VarDecCmpSub。96 ビット比較 (結果: &lt;0 / 0 / &gt;0)。符号・scale を考慮。
    /// 本家どおり d2 の符号が結果の向きを決め、scale 不一致時は d1 側を 10^n 倍して揃える
    /// (d1 が 96 ビットを超えた時点で d2 より大きいと確定する)。</summary>
    private static int VarDecCmpSub(ulong low64, uint high, uint flags,
        ulong d2Low64, uint d2High, uint d2flags) {
        // 本家は int 演算で flags を扱う (>> 31 は算術シフトで符号拡張される)
        var sign = ((int)d2flags >> 31) | 1;
        var scale = (int)d2flags - (int)flags;

        if (scale != 0) {
            scale >>= ScaleShift;

            // Scale factors are not equal. Assume that a larger scale factor (more decimal
            // places) is likely to mean that number is smaller. Start by guessing that the
            // right operand has the larger scale factor.
            if (scale < 0) {
                // Guessed scale factor wrong. Swap operands.
                scale = -scale;
                sign = -sign;

                var tmp64 = low64;
                low64 = d2Low64;
                d2Low64 = tmp64;

                var tmpHigh = high;
                high = d2High;
                d2High = tmpHigh;
            }

            // d1 will need to be multiplied by 10^scale so it will have the same scale as d2.
            // Scaling loop, up to 10^9 at a time.
            do {
                var power = scale >= MaxInt32Scale ? TenToPowerNine : UInt32Powers10[scale];
                Mul64x64(low64, power, out var lo, out var tmpHi);
                low64 = lo;
                tmpHi += (ulong)high * power;
                // If the scaled value has more than 96 significant bits then it's greater than d2
                if (tmpHi > uint.MaxValue)
                    return sign;
                high = (uint)tmpHi;
            } while ((scale -= MaxInt32Scale) > 0);
        }

        var cmpHigh = high - d2High;
        if (cmpHigh != 0) {
            // check for overflow
            if (cmpHigh > high)
                sign = -sign;
            return sign;
        }

        var cmpLow64 = low64 - d2Low64;
        if (cmpLow64 == 0)
            sign = 0;
        // check for overflow
        else if (cmpLow64 > low64)
            sign = -sign;
        return sign;
    }

    /// <summary>64 ビット × 64 ビット → 128 ビット積 (本家 Math.BigMul(ulong, ulong, out ulong)
    /// の 32 ビットスライス実装。VM IL で実行可能な ulong/uint 演算のみ)。</summary>
    internal static void Mul64x64(ulong a, ulong b, out ulong low, out ulong high) {
        var aLo = (uint)a;
        var aHi = (uint)(a >> 32);
        var bLo = (uint)b;
        var bHi = (uint)(b >> 32);

        var ll = (ulong)aLo * bLo;
        var lh = (ulong)aLo * bHi;
        var hl = (ulong)aHi * bLo;
        var hh = (ulong)aHi * bHi;

        var mid = (ll >> 32) + (uint)lh + (uint)hl;
        low = (ll & 0xFFFFFFFFu) | (mid << 32);
        high = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
    }

    /// <summary>本家 BitOperations.LeadingZeroCount(uint) のビット走査版。</summary>
    private static int LeadingZeroCount(uint v) {
        var count = 0;
        while (count < 32 && (v & 0x80000000u) == 0) {
            v <<= 1;
            count++;
        }
        return count;
    }
}
