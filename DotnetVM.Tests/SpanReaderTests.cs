using DotnetVM.Binary;
using Xunit;

namespace DotnetVM.Tests;

public class SpanReaderTests {
    [Fact]
    public void ReadLittleEndian_Primitives() {
        var bytes = new byte[] { 0x78, 0x56, 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00 };
        var reader = new SpanReader(bytes);

        Assert.Equal(0x5678u, reader.ReadUInt16());
        Assert.Equal(0xCDEF1234u, reader.ReadUInt32());
        Assert.Equal(0xEEFF0123456789ABul, reader.ReadUInt64());
    }

    [Theory]
    // ECMA-335 II.23.2 圧縮符号なし整数の既知ベクトル
    [InlineData(new byte[] { 0x03 }, 3u)]
    [InlineData(new byte[] { 0x7F }, 0x7Fu)]
    [InlineData(new byte[] { 0x80, 0x80 }, 0x80u)]
    [InlineData(new byte[] { 0xAE, 0x57 }, 0x2E57u)]
    [InlineData(new byte[] { 0xBF, 0xFF }, 0x3FFFu)]
    [InlineData(new byte[] { 0xC0, 0x00, 0x40, 0x00 }, 0x4000u)]
    [InlineData(new byte[] { 0xDF, 0xFF, 0xFF, 0xFF }, 0x1FFFFFFFu)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0xFFFFFFFFu)] // C0..DF先頭のはずだが 0xFF は予約 → エラーのはず
    public void ReadCompressedUInt32_KnownVectors(byte[] bytes, uint expected) {
        // 0xFF 先頭は予約領域なので FormatException を期待する特別扱い
        if (bytes[0] == 0xFF) {
            Assert.Throws<FormatException>(() => { var r = new SpanReader(bytes); r.ReadCompressedUInt32(); });
            return;
        }
        var reader = new SpanReader(bytes);
        Assert.Equal(expected, reader.ReadCompressedUInt32());
    }

    [Fact]
    public void ReadCompressedInt32_ZigzagVectors() {
        // zigzag: 0 -> 0, -1 -> 1, 1 -> 2, -2 -> 3, 2 -> 4 ...
        Assert.Equal(0, new SpanReader(new byte[] { 0x00 }).ReadCompressedInt32());
        Assert.Equal(-1, new SpanReader(new byte[] { 0x01 }).ReadCompressedInt32());
        Assert.Equal(1, new SpanReader(new byte[] { 0x02 }).ReadCompressedInt32());
        Assert.Equal(-64, new SpanReader(new byte[] { 0x7F }).ReadCompressedInt32());
        Assert.Equal(63, new SpanReader(new byte[] { 0x7E }).ReadCompressedInt32());
        // -8192: enc = 16383 = 0x3FFF → 2byte 形式 0xBF, 0xFF
        Assert.Equal(-8192, new SpanReader(new byte[] { 0xBF, 0xFF }).ReadCompressedInt32());
    }

    [Fact]
    public void ReadULEB128_MultiByte() {
        // 624485 = 0x98765 → e5 8e 26
        var reader = new SpanReader(new byte[] { 0xE5, 0x8E, 0x26 });
        Assert.Equal(624485u, reader.ReadULEB128());
    }

    [Fact]
    public void ReadSLEB128_Negative() {
        // -123456 → 0xC0 0xBB 0x78
        var reader = new SpanReader(new byte[] { 0xC0, 0xBB, 0x78 });
        Assert.Equal(-123456, reader.ReadSLEB128());
    }

    [Fact]
    public void Overrun_Throws() {
        // SpanReader は ref struct なのでラムダで掴めず、try/catch で検証する
        var reader = new SpanReader(new byte[] { 0x01 });
        try {
            reader.ReadUInt32();
            Assert.Fail("EndOfStreamException が期待されます。");
        } catch (EndOfStreamException) {
            // 期待通り
        }
    }

    [Fact]
    public void OffsetAndSeek_Work() {
        var reader = new SpanReader(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(0, reader.Offset);
        reader.ReadByte();
        Assert.Equal(1, reader.Offset);
        Assert.Equal(3, reader.Remaining);
        reader.Seek(0);
        Assert.Equal(0x04030201u, reader.ReadUInt32());
        Assert.True(reader.EndOfBuffer);
    }
}
