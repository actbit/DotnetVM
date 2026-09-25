using System.Buffers.Binary;

namespace DotnetVM.Binary;

/// <summary>
/// ECMA-335 のメタデータ/IL を読むための軽量なバイト列リーダ。
/// uleb128 / sleb128 / 圧縮整数 (II.23.2) を提供する。
/// </summary>
public ref struct SpanReader {
    private ReadOnlySpan<byte> _buffer;
    private int _offset;

    public SpanReader(ReadOnlySpan<byte> buffer) {
        _buffer = buffer;
        _offset = 0;
    }

    public int Offset => _offset;
    public int Remaining => _buffer.Length - _offset;
    public bool EndOfBuffer => Remaining == 0;

    public void Seek(int offset) {
        if (offset < 0 || offset > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _offset = offset;
    }

    public void Advance(int count) {
        if (count < 0 || count > _buffer.Length - _offset)
            throw new ArgumentOutOfRangeException(nameof(count));
        _offset += count;
    }

    public byte ReadByte() {
        CheckAvailable(1);
        return _buffer[_offset++];
    }

    public ReadOnlySpan<byte> ReadBytes(int count) {
        CheckAvailable(count);
        var result = _buffer.Slice(_offset, count);
        _offset += count;
        return result;
    }

    public ushort ReadUInt16() {
        CheckAvailable(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_offset));
        _offset += 2;
        return value;
    }

    public short ReadInt16() => (short)ReadUInt16();

    public uint ReadUInt32() {
        CheckAvailable(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_offset));
        _offset += 4;
        return value;
    }

    public int ReadInt32() => (int)ReadUInt32();

    public ulong ReadUInt64() {
        CheckAvailable(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_offset));
        _offset += 8;
        return value;
    }

    public long ReadInt64() => (long)ReadUInt64();

    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    /// <summary>uleb128 符号なし可変長整数。</summary>
    public uint ReadULEB128() {
        uint result = 0;
        int shift = 0;
        while (true) {
            var b = ReadByte();
            if (shift == 28 && (b & 0x80) != 0 || shift == 28 && (b & 0x7F) > 0x0F)
                throw new FormatException("uleb128 が uint32 の範囲を超えています。");
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 28)
                throw new FormatException("uleb128 が長すぎます (5バイト超)。");
        }
    }

    /// <summary>sleb128 符号付き可変長整数。</summary>
    public int ReadSLEB128() {
        int result = 0;
        int shift = 0;
        byte b;
        do {
            b = ReadByte();
            result |= (b & 0x7F) << shift;
            shift += 7;
            if (shift > 35)
                throw new FormatException("sleb128 が長すぎます。");
        } while ((b & 0x80) != 0);

        // 符号拡張 (7の倍数ビット幅)
        if (shift < 32 && (b & 0x40) != 0)
            result |= -1 << shift;
        return result;
    }

    /// <summary>ECMA-335 II.23.2 の圧縮符号なし整数。1/2/4バイト可変。</summary>
    public uint ReadCompressedUInt32() {
        var first = ReadByte();
        if ((first & 0b1000_0000) == 0)
            return first;
        if ((first & 0b1100_0000) == 0b1000_0000) {
            var second = ReadByte();
            return ((uint)(first & 0b0011_1111) << 8) | second;
        }
        if ((first & 0b1110_0000) == 0b1100_0000) {
            var b1 = ReadByte();
            var b2 = ReadByte();
            var b3 = ReadByte();
            return ((uint)(first & 0b0001_1111) << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
        }
        throw new FormatException($"圧縮整数の不正な先頭バイト: 0x{first:X2} (0xE0..0xFF は予約)。");
    }

    /// <summary>ECMA-335 II.23.2 の圧縮符号付き整数 (zigzag 符号化)。</summary>
    public int ReadCompressedInt32() {
        var encoded = ReadCompressedUInt32();
        return (int)(encoded >> 1) ^ -(int)(encoded & 1);
    }

    private void CheckAvailable(int count) {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count > _buffer.Length - _offset)
            throw new EndOfStreamException($"バッファ末尾を超えて読もうとしました (offset={_offset}, requested={count}, length={_buffer.Length})。");
    }
}
