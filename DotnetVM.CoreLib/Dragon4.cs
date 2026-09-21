namespace DotnetVM.CoreLib;

/// <summary>
/// 浮動小数点 Dragon4 用の多倍長符号なし整数。
///
/// dotnet/runtime (MIT) release/10.0 の System.Number.BigInteger (ref struct + fixed
/// uint バッファ + ポインタ overlay の Pow10BigNumTable) を VM IL 実行可能な形に移植
/// したもの。fixed バッファ → uint[] (little-endian ブロック列)、ポインタ overlay →
/// フラット テーブルからの都度構築、out パラメータ → タプル / 新規インスタンス戻り値
/// に置換している。アルゴリズム本体 (Knuth 風 DivRem、HeuristicDivide、Pow10 の指数
/// 分割) は本家と同一。
///
/// 必要ブロック数は本家と同じ計算: 最長 2 進仮数 1074 bit + 最長桁列 2552 bit +
/// シフト用 32 bit = 3658 bit → 116 ブロック。
/// </summary>
internal sealed class BigUInt {
    private const int BitsPerBlock = 32;
    private const int BitsForLongestBinaryMantissa = 1074;
    private const int BitsForLongestDigitSequence = 2552;
    private const int MaxBits = BitsForLongestBinaryMantissa + BitsForLongestDigitSequence + BitsPerBlock;
    private const int MaxBlockCount = ((MaxBits + (BitsPerBlock - 1)) / BitsPerBlock) + 1;

    private static readonly uint[] Pow10UInt32Table = [
        1, 10, 100, 1000, 10000, 100000, 1000000, 10000000,
        // 末尾 2 つは MultiplyPow10 のみがアクセスする
        100000000, 1000000000,
    ];

    // Pow10BigNumTable の各エントリ (10^8, 10^16, ..., 10^1024) の先頭オフセット。
    // 各エントリは [length, blocks...] のフラット並び。
    private static readonly int[] Pow10BigNumTableIndices = [0, 2, 5, 10, 18, 33, 61, 116];

    private static readonly uint[] Pow10BigNumTable = [
        // 10^8
        1,          // _length
        100000000,  // _blocks

        // 10^16
        2,          // _length
        0x6FC10000, // _blocks
        0x002386F2,

        // 10^32
        4,          // _length
        0x00000000, // _blocks
        0x85ACEF81,
        0x2D6D415B,
        0x000004EE,

        // 10^64
        7,          // _length
        0x00000000, // _blocks
        0x00000000,
        0xBF6A1F01,
        0x6E38ED64,
        0xDAA797ED,
        0xE93FF9F4,
        0x00184F03,

        // 10^128
        14,         // _length
        0x00000000, // _blocks
        0x00000000,
        0x00000000,
        0x00000000,
        0x2E953E01,
        0x03DF9909,
        0x0F1538FD,
        0x2374E42F,
        0xD3CFF5EC,
        0xC404DC08,
        0xBCCDB0DA,
        0xA6337F19,
        0xE91F2603,
        0x0000024E,

        // 10^256
        27,         // _length
        0x00000000, // _blocks
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x982E7C01,
        0xBED3875B,
        0xD8D99F72,
        0x12152F87,
        0x6BDE50C6,
        0xCF4A6E70,
        0xD595D80F,
        0x26B2716E,
        0xADC666B0,
        0x1D153624,
        0x3C42D35A,
        0x63FF540E,
        0xCC5573C0,
        0x65F9EF17,
        0x55BC28F2,
        0x80DCC7F7,
        0xF46EEDDC,
        0x5FDCEFCE,
        0x000553F7,

        // 10^512
        54,         // _length
        0x00000000, // _blocks
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0xFC6CF801,
        0x77F27267,
        0x8F9546DC,
        0x5D96976F,
        0xB83A8A97,
        0xC31E1AD9,
        0x46C40513,
        0x94E65747,
        0xC88976C1,
        0x4475B579,
        0x28F8733B,
        0xAA1DA1BF,
        0x703ED321,
        0x1E25CFEA,
        0xB21A2F22,
        0xBC51FB2E,
        0x96E14F5D,
        0xBFA3EDAC,
        0x329C57AE,
        0xE7FC7153,
        0xC3FC0695,
        0x85A91924,
        0xF95F635E,
        0xB2908EE0,
        0x93ABADE4,
        0x1366732A,
        0x9449775C,
        0x69BE5B0E,
        0x7343AFAC,
        0xB099BC81,
        0x45A71D46,
        0xA2699748,
        0x8CB07303,
        0x8A0B1F13,
        0x8CAB8A97,
        0xC1D238D9,
        0x633415D4,
        0x0000001C,

        // 10^1024
        107,        // _length
        0x00000000, // _blocks
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x00000000,
        0x2919F001,
        0xF55B2B72,
        0x6E7C215B,
        0x1EC29F86,
        0x991C4E87,
        0x15C51A88,
        0x140AC535,
        0x4C7D1E1A,
        0xCC2CD819,
        0x0ED1440E,
        0x896634EE,
        0x7DE16CFB,
        0x1E43F61F,
        0x9FCE837D,
        0x231D2B9C,
        0x233E55C7,
        0x65DC60D7,
        0xF451218B,
        0x1C5CD134,
        0xC9635986,
        0x922BBB9F,
        0xA7E89431,
        0x9F9F2A07,
        0x62BE695A,
        0x8E1042C4,
        0x045B7A74,
        0x1ABE1DE3,
        0x8AD822A5,
        0xBA34C411,
        0xD814B505,
        0xBF3FDEB3,
        0x8FC51A16,
        0xB1B896BC,
        0xF56DEEEC,
        0x31FB6BFD,
        0xB6F4654B,
        0x101A3616,
        0x6B7595FB,
        0xDC1A47FE,
        0x80D98089,
        0x80BDA5A5,
        0x9A202882,
        0x31EB0F66,
        0xFC8F1F90,
        0x976A3310,
        0xE26A7B7E,
        0xDF68368A,
        0x3CE3A0B8,
        0x8E4262CE,
        0x75A351A2,
        0x6CB0B6C9,
        0x44597583,
        0x31B5653F,
        0xC356E38A,
        0x35FAABA6,
        0x0190FBA0,
        0x9FC4ED52,
        0x88BC491B,
        0x1640114A,
        0x005B8041,
        0xF4F3235E,
        0x1E8D4649,
        0x36A8DE06,
        0x73C55349,
        0xA7E6BD2A,
        0xC1A6970C,
        0x47187094,
        0xD2DB49EF,
        0x926C3F5B,
        0xAE6209D4,
        0x2D433949,
        0x34F4A3C6,
        0xD4305D94,
        0xD9D61A05,
        0x00000325,
    ];

    private readonly uint[] _blocks = new uint[MaxBlockCount];
    private int _length;

    /// <summary>内部ブロック列を dest の先頭へコピーする (length を含む)。</summary>
    private void CopyBlocksTo(BigUInt dest) {
        for (int i = 0; i < _length; i++)
            dest._blocks[i] = _blocks[i];
        dest._length = _length;
    }

    public static BigUInt FromUInt32(uint value) {
        var result = new BigUInt();
        result.SetUInt32(value);
        return result;
    }

    public static BigUInt FromUInt64(ulong value) {
        var result = new BigUInt();
        result.SetUInt64(value);
        return result;
    }

    public static BigUInt CopyOf(BigUInt value) {
        var result = new BigUInt();
        result.CopyFrom(value);
        return result;
    }

    public void SetZero() {
        _length = 0;
    }

    public void SetUInt32(uint value) {
        if (value == 0) {
            _length = 0;
        } else {
            _blocks[0] = value;
            _length = 1;
        }
    }

    public void SetUInt64(ulong value) {
        if (value <= uint.MaxValue) {
            SetUInt32((uint)value);
        } else {
            _blocks[0] = (uint)value;
            _blocks[1] = (uint)(value >> 32);
            _length = 2;
        }
    }

    public void CopyFrom(BigUInt value) {
        int rhsLength = value._length;
        for (int i = 0; i < rhsLength; i++)
            _blocks[i] = value._blocks[i];
        _length = rhsLength;
    }

    public bool IsZero() => _length == 0;

    public int GetLength() => _length;

    public uint GetBlock(int index) => _blocks[index];

    public uint ToUInt32() => _length > 0 ? _blocks[0] : 0;

    public ulong ToUInt64() {
        if (_length > 1)
            return ((ulong)_blocks[1] << 32) + _blocks[0];
        if (_length > 0)
            return _blocks[0];
        return 0;
    }

    private void Clear(uint count) {
        for (int i = 0; (uint)i < count; i++)
            _blocks[i] = 0;
    }

    public static uint CountSignificantBits(uint value) => (uint)(32 - FloatOps.LeadingZeroCount(value));

    public static uint CountSignificantBits(ulong value) => (uint)(64 - FloatOps.LeadingZeroCount(value));

    public static uint CountSignificantBits(BigUInt value) {
        if (value.IsZero())
            return 0;
        int lastIndex = value._length - 1;
        return (uint)(lastIndex * BitsPerBlock) + CountSignificantBits(value._blocks[lastIndex]);
    }

    public void Add(uint value) {
        int length = _length;
        if (length == 0) {
            SetUInt32(value);
            return;
        }

        _blocks[0] += value;
        if (_blocks[0] >= value)
            return; // 繰り上がりなし

        for (int index = 1; index < length; index++) {
            _blocks[index]++;
            if (_blocks[index] > 0)
                return; // 繰り上がりなし
        }

        _blocks[length] = 1;
        _length = length + 1;
    }

    public static void Add(BigUInt lhs, BigUInt rhs, BigUInt result) {
        // より長い方のオペランドを特定
        BigUInt large = lhs._length < rhs._length ? rhs : lhs;
        BigUInt small = lhs._length < rhs._length ? lhs : rhs;

        int largeLength = large._length;
        int smallLength = small._length;

        int resultIndex = 0;
        ulong carry = 0;

        for (int i = 0; i < smallLength; i++) {
            ulong sum = carry + large._blocks[i] + small._blocks[i];
            carry = sum >> 32;
            result._blocks[resultIndex++] = (uint)sum;
        }

        for (int i = smallLength; i < largeLength; i++) {
            ulong sum = carry + large._blocks[i];
            carry = sum >> 32;
            result._blocks[resultIndex++] = (uint)sum;
        }

        int resultLength = largeLength;

        if (carry != 0) {
            result._blocks[resultIndex] = 1;
            resultLength++;
        }

        result._length = resultLength;
    }

    public static BigUInt Add(BigUInt lhs, BigUInt rhs) {
        var result = new BigUInt();
        Add(lhs, rhs, result);
        return result;
    }

    public static int Compare(BigUInt lhs, BigUInt rhs) {
        int lengthDelta = lhs._length - rhs._length;
        if (lengthDelta != 0)
            return lengthDelta;

        if (lhs._length == 0)
            return 0;

        for (int index = lhs._length - 1; index >= 0; index--) {
            long delta = (long)lhs._blocks[index] - rhs._blocks[index];
            if (delta != 0)
                return delta > 0 ? 1 : -1;
        }

        return 0;
    }

    /// <summary>単一ブロック rhs での除算 (商と剰余)。</summary>
    public static (BigUInt Quotient, BigUInt Remainder) DivRem(BigUInt lhs, BigUInt rhs) {
        if (lhs.IsZero())
            return (new BigUInt(), new BigUInt());

        int lhsLength = lhs._length;
        int rhsLength = rhs._length;

        var quo = new BigUInt();
        var rem = new BigUInt();

        if (lhsLength == 1 && rhsLength == 1) {
            quo.SetUInt32(lhs._blocks[0] / rhs._blocks[0]);
            rem.SetUInt32(lhs._blocks[0] % rhs._blocks[0]);
            return (quo, rem);
        }

        if (rhsLength == 1) {
            // rhs が 1 ブロックなら計算を大幅に単純化できる
            int quotientLength = lhsLength;
            ulong rhsValue = rhs._blocks[0];
            ulong carry = 0;

            for (int i = quotientLength - 1; i >= 0; i--) {
                ulong value = (carry << 32) | lhs._blocks[i];
                ulong digit = value / rhsValue;
                carry = value % rhsValue;

                if (digit == 0 && i == quotientLength - 1) {
                    quotientLength--;
                } else {
                    quo._blocks[i] = (uint)digit;
                }
            }

            quo._length = quotientLength;
            rem.SetUInt32((uint)carry);
            return (quo, rem);
        }

        if (rhsLength > lhsLength) {
            // 商が 0 のケース
            rem.CopyFrom(lhs);
            return (quo, rem);
        }

        // "grammar-school" アルゴリズムで q = a / b を筆算のように求める
        int quoLength = lhsLength - rhsLength + 1;
        rem.CopyFrom(lhs);
        int remLength = lhsLength;

        uint divHi = rhs._blocks[rhsLength - 1];
        uint divLo = rhs._blocks[rhsLength - 2];

        // 除数の先行ゼロを数え、最上位 bit を立てる
        int shiftLeft = FloatOps.LeadingZeroCount(divHi);
        int shiftRight = 32 - shiftLeft;

        if (shiftLeft > 0) {
            divHi = (divHi << shiftLeft) | (divLo >> shiftRight);
            divLo <<= shiftLeft;

            if (rhsLength > 2)
                divLo |= rhs._blocks[rhsLength - 3] >> shiftRight;
        }

        for (int i = lhsLength; i >= rhsLength; i--) {
            int n = i - rhsLength;
            uint t = i < lhsLength ? rem._blocks[i] : 0;

            ulong valHi = ((ulong)t << 32) | rem._blocks[i - 1];
            uint valLo = i > 1 ? rem._blocks[i - 2] : 0;

            // 除数をシフトしたなら被除数もシフトする
            if (shiftLeft > 0) {
                valHi = (valHi << shiftLeft) | (valLo >> shiftRight);
                valLo <<= shiftLeft;

                if (i > 2)
                    valLo |= rem._blocks[i - 3] >> shiftRight;
            }

            // 商の現在桁の最初の推測 (32 bit に収まる)
            ulong digit = valHi / divHi;
            if (digit > uint.MaxValue)
                digit = uint.MaxValue;

            // 推測が大きすぎるかもしれない
            while (DivideGuessTooBig(digit, valHi, valLo, divHi, divLo)) {
                digit--;
            }

            if (digit > 0) {
                // 現在の商を引く
                uint carry = SubtractDivisor(rem, n, rhs, digit);

                if (carry != t) {
                    // 推測がちょうど 1 大きかった
                    carry = AddDivisor(rem, n, rhs);
                    digit--;
                }
            }

            if (quoLength != 0) {
                if (digit == 0 && n == quoLength - 1) {
                    quoLength--;
                } else {
                    quo._blocks[n] = (uint)digit;
                }
            }

            if (i < remLength)
                remLength--;
        }

        quo._length = quoLength;

        // 剰余が 0 のケースを確認
        for (int i = remLength - 1; i >= 0; i--) {
            if (rem._blocks[i] == 0) {
                remLength--;
            } else {
                // 非 0 ブロックを見つけたら残りはすべて有効
                break;
            }
        }

        rem._length = remLength;
        return (quo, rem);
    }

    /// <summary>推測商で dividend を割る (Dragon4 用のヒューリスティック除算)。
    /// 商の誤差は 2 未満。dividend を破壊的に更新し、推測商を返す。</summary>
    public uint HeuristicDivide(BigUInt divisor) {
        int divisorLength = divisor._length;

        if (_length < divisorLength)
            return 0;

        // 推測商。誤差は 2 未満
        int lastIndex = divisorLength - 1;
        uint quotient = _blocks[lastIndex] / (divisor._blocks[lastIndex] + 1);

        if (quotient != 0) {
            // dividend = dividend - divisor * quotient
            int index = 0;
            ulong borrow = 0;
            ulong carry = 0;

            do {
                ulong product = ((ulong)divisor._blocks[index] * quotient) + carry;
                carry = product >> 32;

                ulong difference = (ulong)_blocks[index] - (uint)product - borrow;
                borrow = (difference >> 32) & 1;

                _blocks[index] = (uint)difference;

                index++;
            } while (index < divisorLength);

            // dividend の先行 0 ブロックを取り除く
            while (divisorLength > 0 && _blocks[divisorLength - 1] == 0) {
                divisorLength--;
            }

            _length = divisorLength;
        }

        // dividend がまだ divisor 以上なら推測を 1 つ増やしてもう 1 つ引く (誤差範囲の保証あり)
        if (Compare(this, divisor) >= 0) {
            quotient++;

            // dividend = dividend - divisor
            int index = 0;
            ulong borrow = 0;

            do {
                ulong difference = (ulong)_blocks[index] - divisor._blocks[index] - borrow;
                borrow = (difference >> 32) & 1;

                _blocks[index] = (uint)difference;

                index++;
            } while (index < divisorLength);

            while (divisorLength > 0 && _blocks[divisorLength - 1] == 0) {
                divisorLength--;
            }

            _length = divisorLength;
        }

        return quotient;
    }

    private static uint SubtractDivisor(BigUInt lhs, int lhsStartIndex, BigUInt rhs, ulong q) {
        int rhsLength = rhs._length;

        // 減算と乗算を 1 つにまとめる
        ulong carry = 0;

        for (int i = 0; i < rhsLength; i++) {
            carry += rhs._blocks[i] * q;
            uint digit = (uint)carry;
            carry >>= 32;

            uint lhsValue = lhs._blocks[lhsStartIndex + i];

            if (lhsValue < digit)
                carry++;

            lhs._blocks[lhsStartIndex + i] = lhsValue - digit;
        }

        return (uint)carry;
    }

    private static uint AddDivisor(BigUInt lhs, int lhsStartIndex, BigUInt rhs) {
        int rhsLength = rhs._length;

        // 最後の減算が引きすぎだった場合に被除数を修復する
        ulong carry = 0;

        for (int i = 0; i < rhsLength; i++) {
            ulong digit = lhs._blocks[lhsStartIndex + i] + carry + rhs._blocks[i];
            lhs._blocks[lhsStartIndex + i] = (uint)digit;
            carry = digit >> 32;
        }

        return (uint)carry;
    }

    private static bool DivideGuessTooBig(ulong q, ulong valHi, uint valLo, uint divHi, uint divLo) {
        // 除数の上位 2 リムに推測商を掛け、被除数の上位 3 リムと比較する
        ulong chkHi = divHi * q;
        ulong chkLo = divLo * q;

        chkHi += chkLo >> 32;
        chkLo &= uint.MaxValue;

        if (chkHi < valHi)
            return false;
        if (chkHi > valHi)
            return true;
        if (chkLo < valLo)
            return false;
        if (chkLo > valLo)
            return true;

        return false;
    }

    public void Multiply(uint value) {
        if (_length <= 1) {
            SetUInt64((ulong)ToUInt32() * value);
            return;
        }

        if (value <= 1) {
            if (value == 0)
                SetZero();
            return;
        }

        int length = _length;
        int index = 0;
        uint carry = 0;

        while (index < length) {
            ulong product = ((ulong)_blocks[index] * value) + carry;
            _blocks[index] = (uint)product;
            carry = (uint)(product >> 32);
            index++;
        }

        if (carry != 0) {
            _blocks[index] = carry;
            length++;
        }

        _length = length;
    }

    public static BigUInt Multiply(BigUInt lhs, uint value) {
        var result = new BigUInt();
        if (lhs._length <= 1) {
            result.SetUInt64((ulong)lhs.ToUInt32() * value);
            return result;
        }

        if (value <= 1) {
            if (value == 0) {
                return result;
            }
            result.CopyFrom(lhs);
            return result;
        }

        int length = lhs._length;
        int index = 0;
        uint carry = 0;

        while (index < length) {
            ulong product = ((ulong)lhs._blocks[index] * value) + carry;
            result._blocks[index] = (uint)product;
            carry = (uint)(product >> 32);
            index++;
        }

        int resultLength = length;
        if (carry != 0) {
            result._blocks[index] = carry;
            resultLength++;
        }

        result._length = resultLength;
        return result;
    }

    /// <summary>result は lhs / rhs と別インスタンスであること。</summary>
    public static void Multiply(BigUInt lhs, uint value, BigUInt result) {
        var computed = Multiply(lhs, value);
        computed.CopyBlocksTo(result);
    }

    /// <summary>result は lhs / rhs と別インスタンスであること。</summary>
    public static void Multiply(BigUInt lhs, BigUInt rhs, BigUInt result) {
        BigUInt large = lhs;
        int largeLength = lhs._length;
        BigUInt small = rhs;
        int smallLength = rhs._length;

        if (largeLength <= 1) {
            Multiply(rhs, lhs.ToUInt32(), result);
            return;
        }
        if (smallLength <= 1) {
            Multiply(lhs, rhs.ToUInt32(), result);
            return;
        }

        if (largeLength < smallLength) {
            large = rhs;
            largeLength = rhs._length;
            small = lhs;
            smallLength = lhs._length;
        }

        int maxResultLength = smallLength + largeLength;

        result._length = maxResultLength;
        result.Clear((uint)maxResultLength);

        int smallIndex = 0;
        int resultStartIndex = 0;

        while (smallIndex < smallLength) {
            if (small._blocks[smallIndex] != 0) {
                int largeIndex = 0;
                int resultIndex = resultStartIndex;
                ulong carry = 0;

                do {
                    ulong product = result._blocks[resultIndex] + ((ulong)small._blocks[smallIndex] * large._blocks[largeIndex]) + carry;
                    carry = product >> 32;
                    result._blocks[resultIndex] = (uint)product;

                    resultIndex++;
                    largeIndex++;
                } while (largeIndex < largeLength);

                result._blocks[resultIndex] = (uint)carry;
            }

            smallIndex++;
            resultStartIndex++;
        }

        if (maxResultLength > 0 && result._blocks[maxResultLength - 1] == 0) {
            result._length--;
        }
    }

    /// <summary>this = this * value (value が多ブロックの場合は一時コピー経由)。</summary>
    public void Multiply(BigUInt value) {
        if (value._length <= 1) {
            Multiply(value.ToUInt32());
        } else {
            BigUInt temp = CopyOf(this);
            Multiply(temp, value, this);
        }
    }

    public void Multiply10() {
        if (IsZero())
            return;

        int index = 0;
        int length = _length;
        ulong carry = 0;

        do {
            ulong block = _blocks[index];
            ulong product = (block << 3) + (block << 1) + carry;
            carry = product >> 32;
            _blocks[index] = (uint)product;
            index++;
        } while (index < length);

        if (carry != 0) {
            _blocks[index] = (uint)carry;
            _length = length + 1;
        }
    }

    public void MultiplyPow10(uint exponent) {
        if (exponent <= 9) {
            Multiply(Pow10UInt32Table[exponent]);
        } else if (!IsZero()) {
            BigUInt poweredValue = Pow10(exponent);
            Multiply(poweredValue);
        }
    }

    /// <summary>2^exponent を持つ新しい BigUInt を返す。</summary>
    public static BigUInt Pow2(uint exponent) {
        var result = new BigUInt();
        uint blocksToShift = (uint)((int)exponent >> 5);
        uint remainingBitsToShift = exponent & 31;
        result._length = (int)blocksToShift + 1;

        if (blocksToShift > 0)
            result.Clear(blocksToShift);
        result._blocks[blocksToShift] = 1U << (int)remainingBitsToShift;
        return result;
    }

    /// <summary>10^exponent を持つ新しい BigUInt を返す。
    /// Pow10BigNumTable (フラット uint[]) から必要なエントリを都度構築する
    /// (本家のポインタ overlay の置換)。</summary>
    public static BigUInt Pow10(uint exponent) {
        // 指数を「uint に収まる小指数部」と「BigNum 冪表の大指数部」に分割
        BigUInt lhs = FromUInt32(Pow10UInt32Table[exponent & 0x7]);
        BigUInt product = new BigUInt();

        exponent >>= 3;
        uint index = 0;

        while (exponent != 0) {
            if ((exponent & 1) != 0) {
                // フラット テーブルのエントリ ([length, blocks...]) から rhs を構築
                int tableIndex = Pow10BigNumTableIndices[index];
                int entryLength = (int)Pow10BigNumTable[tableIndex];
                var rhs = new BigUInt();
                for (int i = 0; i < entryLength; i++)
                    rhs._blocks[i] = Pow10BigNumTable[tableIndex + 1 + i];
                rhs._length = entryLength;

                Multiply(lhs, rhs, product);

                // 次の一時領域へスワップ
                BigUInt temp = product;
                product = lhs;
                lhs = temp;
            }

            index++;
            exponent >>= 1;
        }

        return lhs;
    }

    public void ShiftLeft(uint shift) {
        // 上位から下位へ処理してその場で安全に更新できるようにする
        int length = _length;

        if (length == 0 || shift == 0)
            return;

        uint blocksToShift = (uint)((int)shift >> 5);
        uint remainingBitsToShift = shift & 31;

        int readIndex = length - 1;
        int writeIndex = readIndex + (int)blocksToShift;

        if (remainingBitsToShift == 0) {
            // ブロック境界に整合したシフト
            while (readIndex >= 0) {
                _blocks[writeIndex] = _blocks[readIndex];
                readIndex--;
                writeIndex--;
            }

            _length += (int)blocksToShift;

            // 残りの下位ブロックをゼロにする
            Clear(blocksToShift);
        } else {
            // 部分シフトのために 1 余分なブロックが必要
            writeIndex++;

            _length = writeIndex + 1;

            int lowBitsShift = 32 - (int)remainingBitsToShift;
            uint highBits = 0;
            uint block = _blocks[readIndex];
            uint lowBits = block >> lowBitsShift;
            while (readIndex > 0) {
                _blocks[writeIndex] = highBits | lowBits;
                highBits = block << (int)remainingBitsToShift;

                --readIndex;
                --writeIndex;

                block = _blocks[readIndex];
                lowBits = block >> lowBitsShift;
            }

            // 最終ブロックを出力
            _blocks[writeIndex] = highBits | lowBits;
            _blocks[writeIndex - 1] = block << (int)remainingBitsToShift;

            // 残りの下位ブロックをゼロにする
            Clear(blocksToShift);

            // 終端ブロックに立っている bit がないか確認
            if (_blocks[_length - 1] == 0) {
                _length--;
            }
        }
    }
}

/// <summary>
/// Dragon4 (多倍長を使った 10 進桁生成)。
///
/// dotnet/runtime (MIT) release/10.0 の System.Number.Dragon4 ( ryanjuckett の Dragon4
/// 実装移植) を VM IL 実行可能な形に移植したもの。本家の BigInteger* ポインタ
/// (pScaledMarginHigh が scaledMarginLow / optionalMarginHigh のどちらかを指す switch)
/// は、scaledMarginHigh フィールド + marginHighIsSeparate フラグの 2 状态で再現した
/// (等幅マージン時は常に scaledMarginLow を返し、分離書き込みはスキップする)。
/// </summary>
internal static class Dragon4Engine {
    private const double Log10V2 = 0.30102999566398119521373889472449;

    public static void Dragon4(FloatTraits traits, double value, int cutoffNumber, bool isSignificantDigits, FloatNumberBuffer number) {
        double v = FloatBits.IsNegative(value) ? -value : value;

        var (mantissa, exponent) = FloatBits.ExtractFractionAndBiasedExponent(traits, value);

        uint mantissaHighBitIdx;
        bool hasUnequalMargins;

        if ((mantissa >> traits.DenormalMantissaBits) != 0) {
            mantissaHighBitIdx = (uint)traits.DenormalMantissaBits;
            hasUnequalMargins = mantissa == 1UL << traits.DenormalMantissaBits;
        } else {
            mantissaHighBitIdx = (uint)FloatOps.Log2(mantissa);
            hasUnequalMargins = false;
        }

        (int length, int decimalExponent) = Dragon4Core(mantissa, exponent, mantissaHighBitIdx, hasUnequalMargins, cutoffNumber, isSignificantDigits, number.Digits);

        number.Scale = decimalExponent + 1;
        number.Digits[length] = '\0';
        number.DigitsCount = length;
    }

    /// <summary>2 進浮動小数点値 (mantissa * 2^exponent) を 10 進文字列に変換する。
    /// 出力バッファに書き込んだ桁数 (NUL 終端なし) と最初の桁の指数を返す。</summary>
    private static (int Length, int DecimalExponent) Dragon4Core(ulong mantissa, int exponent, uint mantissaHighBitIdx, bool hasUnequalMargins, int cutoffNumber, bool isSignificantDigits, char[] buffer) {
        int curDigit = 0;

        // value = scaledValue / scale, marginLow = scaledMarginLow / scale の
        // 整数形式で初期状態を計算する。
        BigUInt scale;
        BigUInt scaledValue;
        BigUInt scaledMarginLow;

        // marginHighIsSeparate: hasUnequalMargins 時 true (optionalMarginHigh を保持)。
        // false の場合 marginHigh は scaledMarginLow と同一 (本家 pScaledMarginHigh ==
        // &scaledMarginLow のケース)。本家のポインタ比較と代入はこのフラグで再現する。
        bool marginHighIsSeparate;
        BigUInt scaledMarginHigh;

        if (hasUnequalMargins) {
            marginHighIsSeparate = true;
            if (exponent > 0) {
                // 小数部なし
                // scaledValue = 2 * 2 * mantissa * 2^exponent
                scaledValue = BigUInt.FromUInt64(4 * mantissa);
                scaledValue.ShiftLeft((uint)exponent);

                // scale = 2 * 2 * 1
                scale = BigUInt.FromUInt32(4);

                // scaledMarginLow = 2 * 2^(exponent - 1)
                scaledMarginLow = BigUInt.Pow2((uint)exponent);

                // scaledMarginHigh = 2 * 2 * 2^(exponent + 1)
                scaledMarginHigh = BigUInt.Pow2((uint)(exponent + 1));
            } else {
                // 小数指数あり
                // scaledValue = 2 * 2 * mantissa
                scaledValue = BigUInt.FromUInt64(4 * mantissa);

                // scale = 2 * 2 * 2^(-exponent)
                scale = BigUInt.Pow2((uint)(-exponent + 2));

                // scaledMarginLow = 2 * 2^(-1)
                scaledMarginLow = BigUInt.FromUInt32(1);

                // scaledMarginHigh = 2 * 2 * 2^(-1)
                scaledMarginHigh = BigUInt.FromUInt32(2);
            }
        } else {
            marginHighIsSeparate = false;
            if (exponent > 0) {
                // 小数部なし
                // scaledValue = 2 * mantissa * 2^exponent
                scaledValue = BigUInt.FromUInt64(2 * mantissa);
                scaledValue.ShiftLeft((uint)exponent);

                // scale = 2 * 1
                scale = BigUInt.FromUInt32(2);

                // scaledMarginLow = 2 * 2^(exponent-1)
                scaledMarginLow = BigUInt.Pow2((uint)exponent);
            } else {
                // 小数指数あり
                // scaledValue = 2 * mantissa
                scaledValue = BigUInt.FromUInt64(2 * mantissa);

                // scale = 2 * 2^(-exponent)
                scale = BigUInt.Pow2((uint)(-exponent + 1));

                // scaledMarginLow = 2 * 2^(-1)
                scaledMarginLow = BigUInt.FromUInt32(1);
            }

            // 高低マージンは同一
            scaledMarginHigh = scaledMarginLow;
        }

        // digitExponent の推定 (正しいか 1 小さい)。0.69 を引くことで推定失敗時の
        // 高速分岐に入りやすくしている (本家と同一の定数)。
        int digitExponent = FloatOps.CeilingToInt((mantissaHighBitIdx + exponent) * Log10V2 - 0.69);

        // 10^digitExponent で割る。
        if (digitExponent > 0) {
            // 正の指数は除算なので scale を掛け上げる
            scale.MultiplyPow10((uint)digitExponent);
        } else if (digitExponent < 0) {
            // 負の指数は乗算なので scaledValue / scaledMarginLow / scaledMarginHigh を掛け上げる
            BigUInt pow10 = BigUInt.Pow10((uint)(-digitExponent));

            scaledValue.Multiply(pow10);
            scaledMarginLow.Multiply(pow10);

            if (marginHighIsSeparate) {
                scaledMarginHigh = BigUInt.Multiply(scaledMarginLow, 2);
            }
        }

        bool isEven = (mantissa % 2) == 0;
        bool estimateTooLow;

        if (cutoffNumber == -1) {
            // 最短表現では IEEE のバイアスなし丸めを考慮する (1.23E+22 等のエッジで
            // より短い文字列を返せるように)
            BigUInt scaledValueHigh = BigUInt.Add(scaledValue, scaledMarginHigh);
            int cmpHigh = BigUInt.Compare(scaledValueHigh, scale);
            estimateTooLow = isEven ? cmpHigh >= 0 : cmpHigh > 0;
        } else {
            estimateTooLow = BigUInt.Compare(scaledValue, scale) >= 0;
        }

        // digitExponent の推定が小さすぎたか?
        if (estimateTooLow) {
            // 指数を 1 つ増やし、最初のループ反復用の予備掛け算をしない
            digitExponent++;
        } else {
            // 推定は正しかった。最初のループ反復に備えて出力基数を掛ける
            scaledValue.Multiply10();
            scaledMarginLow.Multiply10();

            if (marginHighIsSeparate) {
                scaledMarginHigh = BigUInt.Multiply(scaledMarginLow, 2);
            }
        }

        // 打ち切り指数 (出力する最後の桁の指数) を求める。既定は出力バッファの最大サイズ。
        int cutoffExponent = digitExponent - buffer.Length;

        if (cutoffNumber != -1) {
            int desiredCutoffExponent;
            if (isSignificantDigits) {
                // 有効桁数を指定された
                desiredCutoffExponent = digitExponent - cutoffNumber;
            } else {
                // 小数桁数を指定された
                desiredCutoffExponent = -cutoffNumber;
            }

            if (desiredCutoffExponent > cutoffExponent) {
                // 転送先バッファを溢れない場合のみ新しい cutoffExponent を採用
                cutoffExponent = desiredCutoffExponent;
            }
        }

        // 出力する最初の桁の指数
        digitExponent--;
        int decimalExponent = digitExponent;

        // HeuristicDivide を呼ぶ準備として、分母の最上位ブロックが 8 以上になるよう
        // スケールアップする (商の推定精度のため。分母は 429496729 以下も保証)。
        uint hiBlock = scale.GetBlock(scale.GetLength() - 1);

        if (hiBlock < 8 || hiBlock > 429496729) {
            // 分母の最上位ブロックを [8, 429496729] に入るようシフト (最上位 bit を
            // 最上位ブロックの index 27 に置く)
            uint hiBlockLog2 = (uint)FloatOps.Log2(hiBlock);
            uint shift = (32 + 27 - hiBlockLog2) % 32;

            scale.ShiftLeft(shift);
            scaledValue.ShiftLeft(shift);
            scaledMarginLow.ShiftLeft(shift);

            if (marginHighIsSeparate) {
                scaledMarginHigh = BigUInt.Multiply(scaledMarginLow, 2);
            }
        }

        // 出力ループが終了した理由を検査し最終桁を正しく丸めるための値
        bool low;         // 値が marginLow の距離まで 0 に近づいたか
        bool high;        // 値が marginHigh の距離まで 1 に近づいたか
        uint outputDigit; // 出力中の桁

        if (cutoffNumber == -1) {
            // unique モード: 値を近傍値と一意に区別できる精度に達するまで出力する。
            // バッファを使い切ったら早期終了。
            while (true) {
                // scale を割って桁を取り出す
                outputDigit = scaledValue.HeuristicDivide(scale);

                // 値の上限側を更新
                BigUInt scaledValueHigh = BigUInt.Add(scaledValue, scaledMarginHigh);

                int cmpLow = BigUInt.Compare(scaledValue, scaledMarginLow);
                int cmpHigh = BigUInt.Compare(scaledValueHigh, scale);

                if (isEven) {
                    low = cmpLow <= 0;
                    high = cmpHigh >= 0;
                } else {
                    low = cmpLow < 0;
                    high = cmpHigh > 0;
                }

                if (low || high || digitExponent == cutoffExponent)
                    break;

                // 出力桁を格納
                buffer[curDigit] = (char)('0' + outputDigit);
                curDigit++;

                // 出力基数を掛ける
                scaledValue.Multiply10();
                scaledMarginLow.Multiply10();

                if (marginHighIsSeparate) {
                    scaledMarginHigh = BigUInt.Multiply(scaledMarginLow, 2);
                }

                digitExponent--;
            }
        } else if (digitExponent >= cutoffExponent) {
            // 長さ指定モード: 精度を使い切るか (残り桁がすべて 0)、目標の打ち切り桁に
            // 達するまで出力する
            low = false;
            high = false;

            while (true) {
                outputDigit = scaledValue.HeuristicDivide(scale);

                if (scaledValue.IsZero() || digitExponent <= cutoffExponent)
                    break;

                buffer[curDigit] = (char)('0' + outputDigit);
                curDigit++;

                scaledValue.Multiply10();
                digitExponent--;
            }
        } else {
            // 最初の有効桁が打ち切り位置より後にあるシナリオ: その最初の有効桁を
            // 丸め桁として扱う。次の桁で丸めが起きるなら decimalExponent を 1 つ増やし
            // 前の桁を 1 にする (二重丸めの回避。詳細は本家コメント)。丸めが起きない
            // ならその桁をそのまま保存する
            outputDigit = scaledValue.HeuristicDivide(scale);

            if (outputDigit > 5 || (outputDigit == 5 && !scaledValue.IsZero())) {
                decimalExponent++;
                outputDigit = 1;
            }

            buffer[curDigit] = (char)('0' + outputDigit);
            curDigit++;

            return (curDigit, decimalExponent);
        }

        // 最終桁を丸める。値が 0 に近すぎた場合は切り下げが既定
        bool roundDown = low;

        if (low == high) {
            // 切り上げ / 切り下げのどちらも合法: 値と 0.5 の比較で最も近い桁に丸める
            //      compare(value, 0.5)
            //      → compare(2 * scale * value, scale)
            scaledValue.ShiftLeft(1); // ×2
            int compare = BigUInt.Compare(scaledValue, scale);
            roundDown = compare < 0;

            // ちょうど真ん中なら偶数桁へ丸める (IEEE の丸め規則)
            if (compare == 0) {
                roundDown = (outputDigit & 1) == 0;
            }
        }

        // 丸めた桁を出力
        if (roundDown) {
            buffer[curDigit] = (char)('0' + outputDigit);
            curDigit++;
        } else if (outputDigit == 9) {
            // 切り上げ: 先頭へ向かって最初の 9 以外の桁を探す
            while (true) {
                if (curDigit == 0) {
                    // 次に高い指数で 1 を出力
                    buffer[curDigit] = '1';
                    curDigit++;
                    decimalExponent++;
                    break;
                }

                curDigit--;

                if (buffer[curDigit] != '9') {
                    // 桁をインクリメント
                    buffer[curDigit]++;
                    curDigit++;
                    break;
                }
            }
        } else {
            // [0, 8] の値は単純な切り上げが可能
            buffer[curDigit] = (char)('0' + outputDigit + 1);
            curDigit++;
        }

        return (curDigit, decimalExponent);
    }
}
