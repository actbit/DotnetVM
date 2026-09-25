using System.Buffers.Binary;
using System.Text;
using DotnetVM.Binary;

namespace DotnetVM.Metadata;

/// <summary>#Strings ヒープ (null 終端 UTF-8 文字列の集まり)。</summary>
public sealed class StringHeap {
    private readonly ReadOnlyMemory<byte> _data;

    internal StringHeap(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>オフセット位置の文字列を取得。</summary>
    public string GetString(int offset) {
        var span = _data.Span;
        if (offset == 0)
            return string.Empty;
        if (offset < 0 || offset >= span.Length)
            throw new BadImageFormatException($"#Strings オフセット 0x{offset:X} が範囲外です。");
        var end = offset;
        while (end < span.Length && span[end] != 0)
            end++;
        return Encoding.UTF8.GetString(span.Slice(offset, end - offset));
    }
}

/// <summary>#US ヒープ (リテラル文字列。圧縮長 + UTF-16 + 末尾1バイトフラグ)。</summary>
public sealed class UserStringHeap {
    private readonly ReadOnlyMemory<byte> _data;

    internal UserStringHeap(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>オフセット位置のリテラル文字列を取得。</summary>
    public string GetString(int offset) {
        var span = _data.Span;
        if (offset < 1 || offset >= span.Length)
            throw new BadImageFormatException($"#US オフセット 0x{offset:X} が範囲外です。");
        var reader = new SpanReader(span[offset..]);
        var rawByteCount = reader.ReadCompressedUInt32();
        if (rawByteCount > int.MaxValue)
            throw new BadImageFormatException($"#US エントリの長さ {rawByteCount:N0} が大きすぎます。");
        var byteCount = (int)rawByteCount;
        if (byteCount < 1 || byteCount > reader.Remaining)
            throw new BadImageFormatException("#US エントリの長さがヒープ末尾を超えています。");
        var payload = reader.ReadBytes(byteCount);
        // 末尾 1 バイトはフラグ (0/1 = ASCII 安全, それ以外 = 特殊文字を含む) なので除く
        if ((payload.Length - 1) % 2 != 0)
            throw new BadImageFormatException("#US エントリの UTF-16 ペイロード長が奇数です。");
        return Encoding.Unicode.GetString(payload[..^1]);
    }
}

/// <summary>#Blob ヒープ (圧縮長 + バイナリ)。署名の取得に使う。</summary>
public sealed class BlobHeap {
    private readonly ReadOnlyMemory<byte> _data;

    internal BlobHeap(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>オフセット位置のブロブを取得。</summary>
    public ReadOnlySpan<byte> GetBlob(int offset) {
        var span = _data.Span;
        if (offset == 0)
            return default;
        if (offset < 0 || offset >= span.Length)
            throw new BadImageFormatException($"#Blob オフセット 0x{offset:X} が範囲外です。");
        var reader = new SpanReader(span[offset..]);
        var rawLength = reader.ReadCompressedUInt32();
        if (rawLength > int.MaxValue)
            throw new BadImageFormatException($"#Blob エントリの長さ {rawLength:N0} が大きすぎます。");
        var length = (int)rawLength;
        if (length > reader.Remaining)
            throw new BadImageFormatException("#Blob エントリの長さがヒープ末尾を超えています。");
        return reader.ReadBytes(length);
    }
}

/// <summary>#GUID ヒープ (16 バイト区切りの GUID)。</summary>
public sealed class GuidHeap {
    private readonly ReadOnlyMemory<byte> _data;

    internal GuidHeap(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>1始まりのインデックスで GUID を取得 (index &lt;= 0 は空)。</summary>
    public Guid GetGuid(int index) {
        if (index <= 0)
            return default;
        var offset = (long)(index - 1) * 16;
        if (offset < 0 || offset > _data.Length - 16)
            throw new BadImageFormatException($"#GUID インデックス {index} が範囲外です。");
        return new Guid(_data.Span.Slice((int)offset, 16).ToArray());
    }
}
