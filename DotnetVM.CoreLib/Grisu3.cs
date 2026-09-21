namespace DotnetVM.CoreLib;

/// <summary>
/// "Do It Yourself Floating Point"。
///
/// dotnet/runtime (MIT) release/10.0 の System.Number.DiyFp ( google/double-conversion
/// の diy-fp.h 移植) を VM IL 実行可能な形に移植したもの。本家は readonly ref struct
/// だが、ユーザー定義 struct は VM のスロット表現に落ちないため sealed class に置換
/// (フィールド f / e は可変。全メソッドは新しいインスタンスを返す)。
/// 正規化済み DiyFp は仮数の最上位 bit が立つ。Multiply / Subtract は結果を正規化しない。
/// NaN / Infinity は格納できない。
/// </summary>
internal sealed class DiyFp {
    public const int SignificandSize = 64;

    public ulong f;
    public int e;

    public DiyFp(ulong f, int e) {
        this.f = f;
        this.e = e;
    }

    /// <summary>128bit 乗算を 64bit に畳んで再現 (下位 64bit は上位の丸めにのみ使用)。</summary>
    public DiyFp Multiply(DiyFp other) {
        uint a = (uint)(f >> 32);
        uint b = (uint)f;
        uint c = (uint)(other.f >> 32);
        uint d = (uint)other.f;

        ulong ac = (ulong)a * c;
        ulong bc = (ulong)b * c;
        ulong ad = (ulong)a * d;
        ulong bd = (ulong)b * d;

        ulong tmp = (bd >> 32) + (uint)ad + (uint)bc;

        // (1UL << 31) を加えることで最終結果を丸める (halfway は切り上げ)。
        tmp += 1U << 31;

        return new DiyFp(ac + (ad >> 32) + (bc >> 32) + (tmp >> 32), e + other.e + SignificandSize);
    }

    public DiyFp Normalize() {
        int lzcnt = FloatOps.LeadingZeroCount(f);
        return new DiyFp(f << lzcnt, e - lzcnt);
    }

    /// <summary>指数が等しいこと / this の仮数が other より大きいことが前提。結果は非正規化。</summary>
    public DiyFp Subtract(DiyFp other) {
        return new DiyFp(f - other.f, e);
    }
}

/// <summary>
/// Grisu3 (shortest / counted 桁生成)。
///
/// dotnet/runtime (MIT) release/10.0 の System.Number.Grisu3 ( google/double-conversion
/// の fast-dtoa.cc 移植) を VM IL 実行可能な形に移植したもの。
/// out パラメータは VM IL 実績がないためタプル戻り値に置換、Math.DivRem / Math.Ceiling /
/// BitOperations.LeadingZeroCount は FloatOps の手実装へ置換、Span&lt;byte&gt; バッファは
/// char[] へ置換している。アルゴリズム本体 (キャッシュ冪表 / early-exit / round-weed の
/// 判定順) は本家と同一。
/// </summary>
internal static class Grisu3 {
    private const int CachedPowersDecimalExponentDistance = 8;
    private const int CachedPowersMinDecimalExponent = -348;
    private const int CachedPowersPowerMaxDecimalExponent = 340;
    private const int CachedPowersOffset = -CachedPowersMinDecimalExponent;

    // 1 / Log2(10)
    private const double D1Log210 = 0.301029995663981195;

    // w (キャッシュ冪を掛けた結果) の 2 進指数の目標範囲。
    private const int MaximalTargetExponent = -32;
    private const int MinimalTargetExponent = -60;

    private static readonly short[] CachedPowersBinaryExponent = [
        -1220, -1193, -1166, -1140, -1113, -1087, -1060, -1034,
        -1007,  -980,  -954,  -927,  -901,  -874,  -847,  -821,
         -794,  -768,  -741,  -715,  -688,  -661,  -635,  -608,
         -582,  -555,  -529,  -502,  -475,  -449,  -422,  -396,
         -369,  -343,  -316,  -289,  -263,  -236,  -210,  -183,
         -157,  -130,  -103,   -77,   -50,   -24,     3,    30,
           56,    83,   109,   136,   162,   189,   216,   242,
          269,   295,   322,   348,   375,   402,   428,   455,
          481,   508,   534,   561,   588,   614,   641,   667,
          694,   720,   747,   774,   800,   827,   853,   880,
          907,   933,   960,   986,  1013,  1039,  1066,
    ];

    private static readonly short[] CachedPowersDecimalExponent = [
        CachedPowersMinDecimalExponent, -340, -332, -324, -316, -308, -300, -292,
        -284, -276, -268, -260, -252, -244, -236, -228,
        -220, -212, -204, -196, -188, -180, -172, -164,
        -156, -148, -140, -132, -124, -116, -108, -100,
         -92,  -84,  -76,  -68,  -60,  -52,  -44,  -36,
         -28,  -20,  -12,   -4,    4,   12,   20,   28,
          36,   44,   52,   60,   68,   76,   84,   92,
         100,  108,  116,  124,  132,  140,  148,  156,
         164,  172,  180,  188,  196,  204,  212,  220,
         228,  236,  244,  252,  260,  268,  276,  284,
         292,  300,  308,  316,  324,  332,  CachedPowersPowerMaxDecimalExponent,
    ];

    private static readonly ulong[] CachedPowersSignificand = [
        0xFA8FD5A0081C0288, 0xBAAEE17FA23EBF76, 0x8B16FB203055AC76, 0xCF42894A5DCE35EA,
        0x9A6BB0AA55653B2D, 0xE61ACF033D1A45DF, 0xAB70FE17C79AC6CA, 0xFF77B1FCBEBCDC4F,
        0xBE5691EF416BD60C, 0x8DD01FAD907FFC3C, 0xD3515C2831559A83, 0x9D71AC8FADA6C9B5,
        0xEA9C227723EE8BCB, 0xAECC49914078536D, 0x823C12795DB6CE57, 0xC21094364DFB5637,
        0x9096EA6F3848984F, 0xD77485CB25823AC7, 0xA086CFCD97BF97F4, 0xEF340A98172AACE5,
        0xB23867FB2A35B28E, 0x84C8D4DFD2C63F3B, 0xC5DD44271AD3CDBA, 0x936B9FCEBB25C996,
        0xDBAC6C247D62A584, 0xA3AB66580D5FDAF6, 0xF3E2F893DEC3F126, 0xB5B5ADA8AAFF80B8,
        0x87625F056C7C4A8B, 0xC9BCFF6034C13053, 0x964E858C91BA2655, 0xDFF9772470297EBD,
        0xA6DFBD9FB8E5B88F, 0xF8A95FCF88747D94, 0xB94470938FA89BCF, 0x8A08F0F8BF0F156B,
        0xCDB02555653131B6, 0x993FE2C6D07B7FAC, 0xE45C10C42A2B3B06, 0xAA242499697392D3,
        0xFD87B5F28300CA0E, 0xBCE5086492111AEB, 0x8CBCCC096F5088CC, 0xD1B71758E219652C,
        0x9C40000000000000, 0xE8D4A51000000000, 0xAD78EBC5AC620000, 0x813F3978F8940984,
        0xC097CE7BC90715B3, 0x8F7E32CE7BEA5C70, 0xD5D238A4ABE98068, 0x9F4F2726179A2245,
        0xED63A231D4C4FB27, 0xB0DE65388CC8ADA8, 0x83C7088E1AAB65DB, 0xC45D1DF942711D9A,
        0x924D692CA61BE758, 0xDA01EE641A708DEA, 0xA26DA3999AEF774A, 0xF209787BB47D6B85,
        0xB454E4A179DD1877, 0x865B86925B9BC5C2, 0xC83553C5C8965D3D, 0x952AB45CFA97A0B3,
        0xDE469FBD99A05FE3, 0xA59BC234DB398C25, 0xF6C69A72A3989F5C, 0xB7DCBF5354E9BECE,
        0x88FCF317F22241E2, 0xCC20CE9BD35C78A5, 0x98165AF37B2153DF, 0xE2A0B5DC971F303A,
        0xA8D9D1535CE3B396, 0xFB9B7CD9A4A7443C, 0xBB764C4CA7A44410, 0x8BAB8EEFB6409C1A,
        0xD01FEF10A657842C, 0x9B10A4E5E9913129, 0xE7109BFBA19C0C9D, 0xAC2820D9623BF429,
        0x80444B5E7AA7CF85, 0xBF21E44003ACDD2D, 0x8E679C2F5E44FF8F, 0xD433179D9C8CB841,
        0x9E19DB92B4E31BA9, 0xEB96BF6EBADF77D9, 0xAF87023B9BF0EE6B,
    ];

    private static readonly uint[] SmallPowersOfTen = [
        1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000,
    ];

    public static bool TryRun(FloatTraits traits, double value, int requestedDigits, FloatNumberBuffer number) {
        double v = FloatBits.IsNegative(value) ? -value : value;

        bool result;
        int length;
        int decimalExponent;

        if (requestedDigits == -1) {
            var (w, boundaryMinus, boundaryPlus) = CreateAndGetBoundaries(traits, v);
            w = w.Normalize();
            (result, length, decimalExponent) = TryRunShortest(boundaryMinus, w, boundaryPlus, number.Digits);
        } else {
            DiyFp w = Create(traits, v).Normalize();
            (result, length, decimalExponent) = TryRunCounted(w, requestedDigits, number.Digits);
        }

        if (result) {
            number.Scale = length + decimalExponent;
            number.Digits[length] = '\0';
            number.DigitsCount = length;
        }

        return result;
    }

    private static DiyFp Create(FloatTraits traits, double value) {
        var (f, e) = FloatBits.ExtractFractionAndBiasedExponent(traits, value);
        return new DiyFp(f, e);
    }

    /// <summary>v の両境界を計算する (大きい方の境界 mPlus は正規化済み。
    /// 小さい方の境界は mPlus と同じ指数を持つ)。</summary>
    private static (DiyFp W, DiyFp MMinus, DiyFp MPlus) CreateAndGetBoundaries(FloatTraits traits, double value) {
        DiyFp result = Create(traits, value);
        var (mMinus, mPlus) = GetBoundaries(result, traits.DenormalMantissaBits);
        return (result, mMinus, mPlus);
    }

    private static (DiyFp MMinus, DiyFp MPlus) GetBoundaries(DiyFp value, int implicitBitIndex) {
        DiyFp mPlus = new DiyFp((value.f << 1) + 1, value.e - 1).Normalize();

        // f == 2^p - 1 の形 (仮数が implicit bit のみ) のとき境界はより近い。
        // 非正規化は最小の正規化数と同じ指数を持つ。
        DiyFp mMinus;
        if (value.f == 1UL << implicitBitIndex) {
            mMinus = new DiyFp((value.f << 2) - 1, value.e - 2);
        } else {
            mMinus = new DiyFp((value.f << 1) - 1, value.e - 1);
        }

        mMinus = new DiyFp(mMinus.f << (mMinus.e - mPlus.e), mPlus.e);
        return (mMinus, mPlus);
    }

    /// <summary>counted 版: requestedDigits 桁だけ生成する (shortest ではない。
    /// 十分な桁を要求すれば 0.1 が 0.9999999... と出ることもある)。</summary>
    private static (bool Result, int Length, int DecimalExponent) TryRunCounted(DiyFp w, int requestedDigits, char[] buffer) {
        int tenMkMinimalBinaryExponent = MinimalTargetExponent - (w.e + DiyFp.SignificandSize);
        int tenMkMaximalBinaryExponent = MaximalTargetExponent - (w.e + DiyFp.SignificandSize);

        var (tenMk, mk) = GetCachedPowerForBinaryExponentRange(tenMkMinimalBinaryExponent, tenMkMaximalBinaryExponent);

        DiyFp scaledW = w.Multiply(tenMk);

        var (result, length, kappa) = TryDigitGenCounted(scaledW, requestedDigits, buffer);
        int decimalExponent = -mk + kappa;
        return (result, length, decimalExponent);
    }

    /// <summary>shortest 版: v の最短表現を生成する (0.09999999999999999 ではなく 0.1。
    /// 長い表現の方が近い場合でも短い方が選ばれる。最後の桁は実際の v に最も近い)。</summary>
    private static (bool Result, int Length, int DecimalExponent) TryRunShortest(DiyFp boundaryMinus, DiyFp w, DiyFp boundaryPlus, char[] buffer) {
        int tenMkMinimalBinaryExponent = MinimalTargetExponent - (w.e + DiyFp.SignificandSize);
        int tenMkMaximalBinaryExponent = MaximalTargetExponent - (w.e + DiyFp.SignificandSize);

        var (tenMk, mk) = GetCachedPowerForBinaryExponentRange(tenMkMinimalBinaryExponent, tenMkMaximalBinaryExponent);

        DiyFp scaledW = w.Multiply(tenMk);
        DiyFp scaledBoundaryMinus = boundaryMinus.Multiply(tenMk);
        DiyFp scaledBoundaryPlus = boundaryPlus.Multiply(tenMk);

        var (result, length, kappa) = TryDigitGenShortest(scaledBoundaryMinus, scaledW, scaledBoundaryPlus, buffer);
        int decimalExponent = -mk + kappa;
        return (result, length, decimalExponent);
    }

    /// <summary>number 以下の最大の 10 の冪を返す (power &lt;= number &lt; power * 10)。
    /// numberBits == 0 なら 0^(-1)。numberBits は 32 以下。戻り値はタプル。</summary>
    private static (uint Power, int ExponentPlusOne) BiggestPowerTen(uint number, int numberBits) {
        // 1233/4096 ≒ 1/log2(10)
        int exponentGuess = ((numberBits + 1) * 1233) >> 12;
        uint power = SmallPowersOfTen[exponentGuess];

        if (number < power) {
            exponentGuess--;
            power = SmallPowersOfTen[exponentGuess];
        }

        return (power, exponentGuess + 1);
    }

    private static (bool Result, int Length, int Kappa) TryDigitGenCounted(DiyFp w, int requestedDigits, char[] buffer) {
        // w は 1 unit 未満の誤差を持つ前提。scale するたびに誤差も scale する。
        ulong wError = 1;

        // 入力数を整数部と小数部に切る (10 進区切りは書かず kappa で調整)。
        var one = new DiyFp(1UL << -w.e, w.e);

        // one による除算はシフト、剰余は AND。
        uint integrals = (uint)(w.f >> -one.e);
        ulong fractionals = w.f & (one.f - 1);

        // requestedDigits を満たせないと分かる場合は早期リターン。
        if (fractionals == 0 && (requestedDigits >= 11 || integrals < SmallPowersOfTen[requestedDigits - 1])) {
            return (false, 0, 0);
        }

        var (divisor, kappa) = BiggestPowerTen(integrals, DiyFp.SignificandSize - (-one.e));
        int length = 0;

        while (kappa > 0) {
            uint digit = integrals / divisor;
            integrals %= divisor;
            buffer[length] = (char)('0' + digit);

            length++;
            requestedDigits--;
            kappa--;

            if (requestedDigits == 0)
                break;

            divisor /= 10;
        }

        if (requestedDigits == 0) {
            ulong rest = ((ulong)integrals << -one.e) + fractionals;
            ulong tenKappa = (ulong)divisor << -one.e;
            return (TryRoundWeedCounted(buffer, length, rest, tenKappa, wError, ref kappa), length, kappa);
        }

        // 整数部の生成が終わり小数点の位置にいる。残りは 10 倍しつつ one で割って桁を出す。
        while (requestedDigits > 0 && fractionals > wError) {
            fractionals *= 10;
            wError *= 10;

            // one による整数除算。
            uint digit = (uint)(fractionals >> -one.e);
            buffer[length] = (char)('0' + digit);

            length++;
            requestedDigits--;
            kappa--;

            // one による剰余。
            fractionals &= one.f - 1;
        }

        if (requestedDigits != 0) {
            buffer[0] = '\0';
            return (false, 0, 0);
        }

        return (TryRoundWeedCounted(buffer, length, fractionals, one.f, wError, ref kappa), length, kappa);
    }

    private static (bool Result, int Length, int Kappa) TryDigitGenShortest(DiyFp low, DiyFp w, DiyFp high, char[] buffer) {
        // low / w / high は 1 ulp 未満の誤差を持つ。low から 1 ulp 引き、high に 1 ulp
        // 足せば、生成したい区間の外側になることが確実な数になる。
        ulong unit = 1;

        var tooLow = new DiyFp(low.f - unit, low.e);
        var tooHigh = new DiyFp(high.f + unit, high.e);

        DiyFp unsafeInterval = tooHigh.Subtract(tooLow);

        // tooHigh で桁生成し可能な限り早く止める (早く止めた場合は実効的に切り下げ)。
        var one = new DiyFp(1UL << -w.e, w.e);

        uint integrals = (uint)(tooHigh.f >> -one.e);
        ulong fractionals = tooHigh.f & (one.f - 1);

        var (divisor, kappa) = BiggestPowerTen(integrals, DiyFp.SignificandSize - (-one.e));
        int length = 0;

        while (kappa > 0) {
            uint digit = integrals / divisor;
            integrals %= divisor;
            buffer[length] = (char)('0' + digit);

            length++;
            kappa--;

            // Invariant: tooHigh = buffer * 10^kappa + DiyFp(rest, one.e)
            ulong rest = ((ulong)integrals << -one.e) + fractionals;

            if (rest < unsafeInterval.f) {
                // 残りの桁を出さない切り下げでも unsafe 区間に収まる。
                bool result = TryRoundWeedShortest(buffer, length, tooHigh.Subtract(w).f, unsafeInterval.f, rest, (ulong)divisor << -one.e, unit);
                return (result, length, kappa);
            }

            divisor /= 10;
        }

        // 整数部の生成が終わり小数点の位置にいる。10 倍しつつ one で割って桁を出す。
        while (true) {
            fractionals *= 10;
            unit *= 10;

            unsafeInterval = new DiyFp(unsafeInterval.f * 10, unsafeInterval.e);

            // one による整数除算。
            uint digit = (uint)(fractionals >> -one.e);
            buffer[length] = (char)('0' + digit);

            length++;
            kappa--;

            // one による剰余。
            fractionals &= one.f - 1;

            if (fractionals < unsafeInterval.f) {
                bool result = TryRoundWeedShortest(buffer, length, tooHigh.Subtract(w).f * unit, unsafeInterval.f, fractionals, one.f, unit);
                return (result, length, kappa);
            }
        }
    }

    /// <summary>2 進指数が [minExponent; maxExponent] に収まるキャッシュ冪を返す。</summary>
    private static (DiyFp Power, int DecimalExponent) GetCachedPowerForBinaryExponentRange(int minExponent, int maxExponent) {
        int k = FloatOps.CeilingToInt((minExponent + DiyFp.SignificandSize - 1) * D1Log210);
        int index = ((CachedPowersOffset + k - 1) / CachedPowersDecimalExponentDistance) + 1;

        int decimalExponent = CachedPowersDecimalExponent[index];
        return (new DiyFp(CachedPowersSignificand[index], CachedPowersBinaryExponent[index]), decimalExponent);
    }

    /// <summary>結果が v に近いならバッファを切り上げる。計算精度が足りず切上げ方向を
    /// 一意に決められない場合は false (rest &lt; tenKappa が前提)。</summary>
    private static bool TryRoundWeedCounted(char[] buffer, int length, ulong rest, ulong tenKappa, ulong unit, ref int kappa) {
        // オーバーフローを避けるための特定順序で検査。
        if (unit >= tenKappa || tenKappa - unit <= unit) {
            return false;
        }

        // 2 * (rest + unit) <= 10^kappa なら安全に切り下げられる。
        if (tenKappa - rest > rest && tenKappa - 2 * rest >= 2 * unit) {
            return true;
        }

        // 2 * (rest - unit) >= 10^kappa なら安全に切り上げられる。
        if (rest > unit && (tenKappa <= rest - unit || tenKappa - (rest - unit) <= rest - unit)) {
            // '9' でなくなるまで最終桁を再帰的にインクリメント。
            buffer[length - 1]++;

            for (int i = length - 1; i > 0; i--) {
                if (buffer[i] != '0' + 10)
                    break;

                buffer[i] = '0';
                buffer[i - 1]++;
            }

            // 先頭が '0' + 10 になったら全部 '9' だったバッファ。"99" → "10" + kappa++
            if (buffer[0] == '0' + 10) {
                buffer[0] = '1';
                kappa++;
            }

            return true;
        }

        return false;
    }

    /// <summary>生成した数を w に近づけるよう最終桁を調整し、不正確になり得る解を
    /// ふるい落とす。rest &lt;= unsafeInterval が前提。</summary>
    private static bool TryRoundWeedShortest(char[] buffer, int length, ulong distanceTooHighW, ulong unsafeInterval, ulong rest, ulong tenKappa, ulong unit) {
        ulong smallDistance = distanceTooHighW - unit;
        ulong bigDistance = distanceTooHighW + unit;

        // 検査はオーバー / アンダーフローを避けるためこの順で行う。
        while (rest < smallDistance && unsafeInterval - rest >= tenKappa
            && (rest + tenKappa < smallDistance || smallDistance - rest >= rest + tenKappa - smallDistance)) {
            buffer[length - 1]--;
            rest += tenKappa;
        }

        // w- に近づけるのにバッファ変更が必要なら、2 つの候補のどちらが近いか決められないので false。
        if (rest < bigDistance && unsafeInterval - rest >= tenKappa
            && (rest + tenKappa < bigDistance || bigDistance - rest > rest + tenKappa - bigDistance)) {
            return false;
        }

        // Weeding 検査 (安全区間は [tooHigh - unsafeInterval + 4 ulp; tooHigh - 2 ulp])。
        return 2 * unit <= rest && rest <= unsafeInterval - 4 * unit;
    }
}
