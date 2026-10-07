using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;

namespace DotnetVM.Runtime.Objects;

/// <summary>
/// VM 内の文字列オブジェクト。ゲストが見る System.String の実体。
/// CoreLib String の表現 (4 バイト LE の長さヘッダ + UTF-16LE char データ) を
/// 単一の byte[] で再現する。これが唯一の真実源であり、CoreLib の String IL が
/// 直接読み書きする _stringLength / _firstChar フィールドや GetRawStringData 経由の
/// 生ポインタアクセスもすべてこのバッファ上で成立する。
/// ホストの string をそのまま参照として渡さず VM 独自オブジェクトで包む
/// (アロケーション計上・オブジェクトモデル統一のため)。
/// </summary>
public sealed class VmString {
    /// <summary>長さヘッダのバイト長 (int32 LE)。</summary>
    internal const int HeaderByteCount = 4;

    /// <summary>char データ開始のバイトオフセット (= CoreLib の _firstChar の位置)。</summary>
    internal const int CharDataByteOffset = HeaderByteCount;

    internal readonly byte[] _bytes;
    private VmLocallocMemory? _pointerMemory;
    /// <summary>ホスト文字列のキャッシュ (Value の呼び出しごとのデコードを避ける、タスク 2)。
    /// 文字列は不変というゲスト規約の下で安全 (CoreLib IL による length / first-char 書込後の
    /// 再キャッシュは WriteStringLength / WriteFirstChar の呼び出し側で削除される)。</summary>
    internal string? _cachedValue;
    internal int _cachedForLength = -1;
    private int _cachedInt32State;
    private int _cachedInt32Value;

    /// <summary>ホスト文字列から作る (ldstr / intrinsic 境界からの正規化用)。</summary>
    public VmString(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _bytes = new byte[BufferByteCount(value.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(_bytes, value.Length);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(_bytes.AsSpan(CharDataByteOffset));
        else
            for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(CharDataByteOffset + i * 2), value[i]);
        _cachedForLength = value.Length;
        _cachedValue = value;
    }

    /// <summary>CoreLib 表現のバッファと文字数から作る (FastAllocateString 相当)。</summary>
    internal VmString(byte[] bytes, int length) {
        _bytes = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
    }

    // CoreLib reads the final UTF-16 character and null terminator together as
    // a uint when hashing. Preserve the terminator and align its backing block.
    internal static long StorageByteCount(int length) =>
        (HeaderByteCount + 2L * (length + 1L) + 3) & ~3L;

    internal static int BufferByteCount(int length) => checked((int)StorageByteCount(length));

    internal bool TryParseInt32(out int value) {
        if (_cachedInt32State != 0) {
            value = _cachedInt32Value;
            return _cachedInt32State > 0;
        }
        value = 0;
        var length = Length;
        if (length == 0) {
            _cachedInt32State = -1;
            return false;
        }
        var index = 0;
        var negative = false;
        var first = (char)BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(CharDataByteOffset, 2));
        if (first is '-' or '+') {
            negative = first == '-';
            index = 1;
            if (index == length) {
                _cachedInt32State = -1;
                return false;
            }
        }
        var limit = negative ? 2147483648L : 2147483647L;
        long result = 0;
        for (; index < length; index++) {
            var character = (char)BinaryPrimitives.ReadUInt16LittleEndian(
                _bytes.AsSpan(CharDataByteOffset + index * 2, 2));
            if (character is < '0' or > '9') {
                _cachedInt32State = -1;
                return false;
            }
            result = result * 10 + character - '0';
            if (result > limit) {
                _cachedInt32State = -1;
                return false;
            }
        }
        value = negative ? (int)-result : (int)result;
        _cachedInt32Value = value;
        _cachedInt32State = 1;
        return true;
    }

    /// <summary>char 数 (CoreLib IL が ldfld する _stringLength と同一の値)。</summary>
    public int Length => BinaryPrimitives.ReadInt32LittleEndian(_bytes);

    /// <summary>ホスト文字列としての内容 (同一性比較 / ホスト境界の入出力用)。
    /// バイト実体の (length, bytes) スナップショットでホスト変換をキャッシュする
    /// (CoreLib String IL の ldfld 経由の読み出しが繰り返しホスト文字列を要求するため、
    /// 毎呼び出しの Unicode デコードを避ける)。バッファ書込後は誤キャッシュを避けるため
    /// WriteStringLength / WriteFirstChar で取り消す。</summary>
    public string Value {
        get {
            var len = Length;
            if (_cachedValue is { } cached && _cachedForLength == len)
                return cached;
            var decoded = DecodeUtf16(_bytes.AsSpan(CharDataByteOffset, len * 2));
            _cachedForLength = len;
            _cachedValue = decoded;
            return decoded;
        }
    }

    internal static string DecodeUtf16(ReadOnlySpan<byte> bytes) {
        if (BitConverter.IsLittleEndian) return new string(MemoryMarshal.Cast<byte, char>(bytes));
        var chars = new char[bytes.Length / 2];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        return new string(chars);
    }

    /// <summary>生バッファ (Buffer.Memmove / Unsafe.* / GetRawStringData の参照先)。</summary>
    internal byte[] Bytes => _bytes;

    /// <summary>生バッファを指す unmanaged ポインタの実体 (ldflda の結果に使う)。
    /// VmLocallocMemory 経由で包むことで GC グラフ上の到達可能性と境界検査を共有する。</summary>
    internal VmLocallocMemory PointerMemory => _pointerMemory ??= new VmLocallocMemory { Bytes = _bytes };

    /// <summary>ldfld 用の合成フィールドスロット ([0] = _stringLength, [1] = _firstChar)。
    /// バイト実体が真実源のため、読み出しのたびに SyncFieldSlotsFromBytes で同期する。</summary>
    internal StackSlot[] FieldSlots { get; } = new StackSlot[2];

    /// <summary>バイト実体 → 合成スロットの同期 (ldfld / ldflda の入口で呼ぶ)。</summary>
    internal void SyncFieldSlotsFromBytes() {
        FieldSlots[0] = StackSlot.OfInt32(Length);
        FieldSlots[1] = StackSlot.OfInt32(
            _bytes.Length > CharDataByteOffset ? _bytes[CharDataByteOffset] : 0);
    }

    /// <summary>_stringLength への書込 (stfld 相当)。ヘッダ 4 バイトに書く。</summary>
    internal void WriteStringLength(int value) {
        BinaryPrimitives.WriteInt32LittleEndian(_bytes, value);
        _cachedValue = null; // バッファ書込後は誤キャッシュを無効化
        _cachedInt32State = 0;
    }

    /// <summary>_firstChar への書込 (stfld 相当)。先頭 char の 2 バイトに書く。</summary>
    internal void WriteFirstChar(char value) {
        if (_bytes.Length >= CharDataByteOffset + 2) {
            _bytes[CharDataByteOffset] = (byte)value;
            _bytes[CharDataByteOffset + 1] = (byte)((ushort)value >> 8);
        }
        _cachedValue = null;
        _cachedInt32State = 0;
    }

    public override string ToString() => Value;
}

/// <summary>
/// VM ごとの文字列インタン プール。ldstr 等のリテラルはここを通して同一 VmString を共有する。
/// </summary>
public sealed class VmStringPool {
    private readonly Dictionary<string, VmString> _pool = [];
    private readonly VmHeap? _heap;
    private readonly object _gate = new();

    public VmStringPool(VmHeap? heap = null) {
        _heap = heap;
    }

    /// <summary>文字列を取得 (未登録なら新規作成して登録。新規作成時はアロケーションを計上する)。</summary>
    public VmString Get(string value) {
        lock (_gate) {
            if (_pool.TryGetValue(value, out var existing))
                return existing;
            // インタニング済み文字列はヒープ管理外だが、新規作成は計上する
            // (intrinsic/ホスト境界経由の文字列生成も IL の ldstr と等価に制約される)
            _heap?.ChargeString(value.Length);
            var created = new VmString(value);
            _pool[value] = created;
            return created;
        }
    }

    /// <summary>既に同一内容がプール済みならそれを返す (演算結果用)。
    /// CLR 規約では ldstr (リテラル) のみがインタニングを保証し、演算結果文字列は
    /// 別インスタンスになりうる (string.Intern と同義論)。タスク 2 hardening:
    /// 「runtime 生成 string と interned string を分離する」のため、ここでプールに
    /// 戻さず新しい独立オブジェクトを返す (interned に寄せたい経路は Get を使う)。</summary>
    public VmString GetOrNew(string value) {
        _heap?.ChargeString(value.Length);
        return new VmString(value);
    }

    internal VmString FormatInt32(int value) {
        Span<char> chars = stackalloc char[11];
        var negative = value < 0;
        var magnitude = negative ? -(long)value : value;
        var end = chars.Length;
        do {
            chars[--end] = (char)('0' + magnitude % 10);
            magnitude /= 10;
        } while (magnitude != 0);
        if (negative)
            chars[--end] = '-';
        var result = Allocate(chars.Length - end);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(chars[end..]).CopyTo(result._bytes.AsSpan(VmString.CharDataByteOffset));
        else
            for (var i = end; i < chars.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(result._bytes.AsSpan(
                    VmString.CharDataByteOffset + (i - end) * 2, 2), chars[i]);
        result._cachedForLength = chars.Length - end;
        result._cachedValue = null;
        return result;
    }

    /// <summary>リテラル (ldstr / 例外文言) 用のインタニング入口。既存の「同一内容は
    /// 同一 VmString を共有する」意味論を維持する。</summary>
    public VmString Intern(string value) => Get(value);

    /// <summary>FastAllocateString 相当: charCount 文字分のゼロ初期化バッファを確保する。
    /// リテラルと違い構築途中の可変バッファなのでプールには入らない。アロケーションは
    /// IL の newobj 相当に計上する (実 CLR の FastAllocateString と同じ確保点)。</summary>
    public VmString Allocate(int charCount) {
        _heap?.ChargeString(charCount);
        return new VmString(new byte[VmString.BufferByteCount(charCount)], charCount);
    }

    public int Count { get { lock (_gate) return _pool.Count; } }
}
