using System.Runtime.CompilerServices;

namespace DotnetVM.CoreLib;

/// <summary>
/// 浮動小数点 (Double/Single) 書式エンジン用の型特性表。
///
/// dotnet/runtime (MIT) release/10.0 の System.IBinaryFloatParseAndFormatInfo&lt;TNumber&gt;
/// (System.Private.CoreLib) の定数群を、ジェネリック機構 (制約付き callvirt が VM IL で
/// 実行できない) を使わずに参照型 + static readonly インスタンスで再現したもの。
/// 値は DoubleTraits / SingleTraits の各実型定数と同一
/// (Double: NumberBufferLength=769 / Single: 113 等)。
/// </summary>
internal sealed class FloatTraits {
    public readonly bool IsSingle;
    public readonly int NumberBufferLength;
    public readonly ulong InfinityBits;
    public readonly int ExponentBits;
    public readonly int ExponentBias;
    public readonly int DenormalMantissaBits;
    public readonly int NormalMantissaBits;
    public readonly int MinBinaryExponent;
    public readonly int MaxBinaryExponent;
    public readonly int MinDecimalExponent;
    public readonly int MaxDecimalExponent;
    public readonly int OverflowDecimalExponent;
    public readonly int MinFastFloat;
    public readonly int MaxFastFloat;
    public readonly int RoundToEvenMinExponent;
    public readonly int RoundToEvenMaxExponent;
    public readonly int MaxExponentFastPath;
    public readonly ulong MaxMantissaFastPath;
    public readonly int MaxRoundTripDigits;
    public readonly int MaxPrecisionCustomFormat;
    public readonly ulong DenormalMantissaMask;
    public readonly ulong NormalMantissaMask;
    public readonly int InfinityExponent;

    private FloatTraits(bool isSingle, int numberBufferLength, ulong infinityBits, int exponentBits,
        int exponentBias, int denormalMantissaBits, int minBinaryExponent, int maxBinaryExponent,
        int minDecimalExponent, int maxDecimalExponent, int overflowDecimalExponent,
        int minFastFloat, int maxFastFloat, int roundToEvenMinExponent, int roundToEvenMaxExponent,
        int maxExponentFastPath, ulong maxMantissaFastPath, int maxRoundTripDigits,
        int maxPrecisionCustomFormat) {
        IsSingle = isSingle;
        NumberBufferLength = numberBufferLength;
        InfinityBits = infinityBits;
        ExponentBits = exponentBits;
        ExponentBias = exponentBias;
        DenormalMantissaBits = denormalMantissaBits;
        NormalMantissaBits = denormalMantissaBits + 1;
        MinBinaryExponent = minBinaryExponent;
        MaxBinaryExponent = maxBinaryExponent;
        MinDecimalExponent = minDecimalExponent;
        MaxDecimalExponent = maxDecimalExponent;
        OverflowDecimalExponent = overflowDecimalExponent;
        MinFastFloat = minFastFloat;
        MaxFastFloat = maxFastFloat;
        RoundToEvenMinExponent = roundToEvenMinExponent;
        RoundToEvenMaxExponent = roundToEvenMaxExponent;
        MaxExponentFastPath = maxExponentFastPath;
        MaxMantissaFastPath = maxMantissaFastPath;
        MaxRoundTripDigits = maxRoundTripDigits;
        MaxPrecisionCustomFormat = maxPrecisionCustomFormat;
        DenormalMantissaMask = (1UL << denormalMantissaBits) - 1;
        NormalMantissaMask = (1UL << (denormalMantissaBits + 1)) - 1;
        InfinityExponent = (1 << exponentBits) - 1;
    }

    public static readonly FloatTraits Double = new(
        isSingle: false,
        numberBufferLength: 769,
        infinityBits: 0x7FF0000000000000,
        exponentBits: 11,
        exponentBias: 0x3FF,
        denormalMantissaBits: 52,
        minBinaryExponent: -1022,
        maxBinaryExponent: 1023,
        minDecimalExponent: -324,
        maxDecimalExponent: 309,
        overflowDecimalExponent: (0x3FF + 104) / 3,
        minFastFloat: -342,
        maxFastFloat: 308,
        roundToEvenMinExponent: -4,
        roundToEvenMaxExponent: 23,
        maxExponentFastPath: 22,
        maxMantissaFastPath: 2UL << 52,
        maxRoundTripDigits: 17,
        maxPrecisionCustomFormat: 15);

    public static readonly FloatTraits Single = new(
        isSingle: true,
        numberBufferLength: 113,
        infinityBits: 0x7F800000,
        exponentBits: 8,
        exponentBias: 0x7F,
        denormalMantissaBits: 23,
        minBinaryExponent: -126,
        maxBinaryExponent: 127,
        minDecimalExponent: -45,
        maxDecimalExponent: 39,
        overflowDecimalExponent: (0x7F + 46) / 3,
        minFastFloat: -65,
        maxFastFloat: 38,
        roundToEvenMinExponent: -17,
        roundToEvenMaxExponent: 10,
        maxExponentFastPath: 10,
        maxMantissaFastPath: 2UL << 23,
        maxRoundTripDigits: 9,
        maxPrecisionCustomFormat: 7);
}

/// <summary>
/// 浮動小数点書式エンジンの数値バッファ。
///
/// dotnet/runtime (MIT) の System.Number.NumberBuffer (byte* / byte[] digits + '\0' 終端)
/// を、VM IL 実行可能な形に置換した表現 (char[] Digits + DigitsCount + Scale +
/// IsNegative + HasNonZeroTail)。Kind は常時 FloatingPoint (この置換面は整数を扱わない)。
/// </summary>
internal sealed class FloatNumberBuffer {
    public char[] Digits;
    public int DigitsCount;
    public int Scale;
    public bool IsNegative;
    public bool HasNonZeroTail;

    public FloatNumberBuffer(FloatTraits traits) {
        Digits = new char[traits.NumberBufferLength];
        DigitsCount = 0;
        Scale = 0;
        IsNegative = false;
        HasNonZeroTail = false;
    }
}

/// <summary>bit 変換ヘルパ。VM 側は Unsafe.BitCast バインド (BitCastImpl) が
/// TFrom 型名で解決する (Math.Abs(double) の実 CoreLib IL と同じ経路)。</summary>
internal static class FloatBits {
    public static ulong Of(double value) => Unsafe.BitCast<double, ulong>(value);
    public static uint OfSingle(float value) => Unsafe.BitCast<float, uint>(value);
    public static double ToDouble(ulong bits) => Unsafe.BitCast<ulong, double>(bits);
    public static float ToSingle(uint bits) => Unsafe.BitCast<uint, float>(bits);

    /// <summary>符号ビット (Single は double への拡大格納で符号が保存されるため
    /// double 空間の bit 31 で両型共通に判定できる)。</summary>
    public static bool IsNegative(double value) => (Of(value) >> 63) != 0;

    /// <summary> widened Single もそのまま double bit で判定できる (拡大格納は値を変えない)。</summary>
    public static bool IsFinite(FloatTraits traits, double value) {
        ulong bits = traits.IsSingle ? OfSingle((float)value) : Of(value);
        return ((bits >> traits.DenormalMantissaBits) & (ulong)traits.InfinityExponent) != (ulong)traits.InfinityExponent;
    }

    public static bool IsNaN(FloatTraits traits, double value) {
        ulong bits = traits.IsSingle ? OfSingle((float)value) : Of(value);
        return (((bits >> traits.DenormalMantissaBits) & (ulong)traits.InfinityExponent) == (ulong)traits.InfinityExponent)
            && ((bits & traits.DenormalMantissaMask) != 0);
    }

    public static bool IsInfinity(FloatTraits traits, double value) {
        ulong bits = traits.IsSingle ? OfSingle((float)value) : Of(value);
        return (bits & (traits.InfinityBits | traits.DenormalMantissaMask)) == traits.InfinityBits;
    }

    /// <summary>仮数部とバイアス済み 2 進指数を取り出す。
    /// 本家 ExtractFractionAndBiasedExponent (Number.Formatting.cs) の移植。
    /// 正規化数: fraction |= implicit bit, exponent -= bias + mantissaBits。
    /// 非正規化数: exponent = MinBinaryExponent - DenormalMantissaBits。</summary>
    public static (ulong Fraction, int BiasedExponent) ExtractFractionAndBiasedExponent(FloatTraits traits, double value) {
        ulong bits = traits.IsSingle ? OfSingle((float)value) : Of(value);
        ulong fraction = bits & traits.DenormalMantissaMask;
        int exponent = (int)((bits >> traits.DenormalMantissaBits) & (ulong)traits.InfinityExponent);
        if (exponent != 0) {
            fraction |= 1UL << traits.DenormalMantissaBits;
            exponent -= traits.ExponentBias + traits.DenormalMantissaBits;
        } else {
            exponent = traits.MinBinaryExponent - traits.DenormalMantissaBits;
        }
        return (fraction, exponent);
    }
}

/// <summary>
/// VM IL 上で利用可能な手実装ユーティリティ (本家が BitOperations / Math 内部呼出に
/// 依存する箇所の置換: lzcnt / Log2 / BigMul / Ceiling は実 CLR も JIT intrinsic 面)。
/// </summary>
internal static class FloatOps {
    public static int LeadingZeroCount(ulong value) {
        if (value == 0)
            return 64;
        int count = 0;
        while ((value & 0x8000000000000000UL) == 0) {
            count++;
            value <<= 1;
        }
        return count;
    }

    public static int LeadingZeroCount(uint value) {
        if (value == 0)
            return 32;
        int count = 0;
        while ((value & 0x80000000U) == 0) {
            count++;
            value <<= 1;
        }
        return count;
    }

    public static int Log2(ulong value) => 63 - LeadingZeroCount(value | 1);

    /// <summary>64bit × 64bit → 128bit 乗算 (Math.BigMul(ulong,ulong) は実 CLR も
    /// JIT intrinsic。32bit 半分 4 乗算で再現)。</summary>
    public static (ulong High, ulong Low) BigMul(ulong a, ulong b) {
        uint aLow = (uint)a;
        uint aHigh = (uint)(a >> 32);
        uint bLow = (uint)b;
        uint bHigh = (uint)(b >> 32);

        ulong ll = (ulong)aLow * bLow;
        ulong lh = (ulong)aLow * bHigh;
        ulong hl = (ulong)aHigh * bLow;
        ulong hh = (ulong)aHigh * bHigh;

        ulong mid = (ll >> 32) + (uint)lh + (uint)hl;
        ulong low = (mid << 32) | (uint)ll;
        ulong high = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
        return (high, low);
    }

    /// <summary>double の切り上げを手実装 (truncation + 正側補正。負側は
    /// truncation == ceil なので補正不要)。無限大 / NaN は呼出前の前提で除外。</summary>
    public static int CeilingToInt(double value) {
        long truncated = (long)value;
        if (truncated < value)
            truncated++;
        return (int)truncated;
    }

    public static int Min(int a, int b) => a < b ? a : b;
    public static int Abs(int value) => value < 0 ? -value : value;
}
