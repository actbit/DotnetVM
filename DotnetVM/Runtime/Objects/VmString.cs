using System.Buffers.Binary;
using System.Text;
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

    private readonly byte[] _bytes;
    private VmLocallocMemory? _pointerMemory;

    /// <summary>ホスト文字列から作る (ldstr / intrinsic 境界からの正規化用)。</summary>
    public VmString(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _bytes = new byte[HeaderByteCount + value.Length * 2];
        BinaryPrimitives.WriteInt32LittleEndian(_bytes, value.Length);
        Encoding.Unicode.GetBytes(value, 0, value.Length, _bytes, CharDataByteOffset);
    }

    /// <summary>CoreLib 表現のバッファから作る (FastAllocateString 相当。ヘッダはキャパシティから算出)。</summary>
    internal VmString(byte[] bytes) {
        _bytes = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (bytes.Length - HeaderByteCount) / 2);
    }

    /// <summary>char 数 (CoreLib IL が ldfld する _stringLength と同一の値)。</summary>
    public int Length => BinaryPrimitives.ReadInt32LittleEndian(_bytes);

    /// <summary>ホスト文字列としての内容 (同一性比較 / ホスト境界の入出力用)。</summary>
    public string Value => Encoding.Unicode.GetString(_bytes, CharDataByteOffset, Length * 2);

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
    internal void WriteStringLength(int value) => BinaryPrimitives.WriteInt32LittleEndian(_bytes, value);

    /// <summary>_firstChar への書込 (stfld 相当)。先頭 char の 2 バイトに書く。</summary>
    internal void WriteFirstChar(char value) {
        if (_bytes.Length >= CharDataByteOffset + 2) {
            _bytes[CharDataByteOffset] = (byte)value;
            _bytes[CharDataByteOffset + 1] = (byte)((ushort)value >> 8);
        }
    }

    public override string ToString() => Value;
}

/// <summary>
/// VM ごとの文字列インタン プール。ldstr 等のリテラルはここを通して同一 VmString を共有する。
/// </summary>
public sealed class VmStringPool {
    private readonly Dictionary<string, VmString> _pool = [];
    private readonly VmHeap? _heap;

    public VmStringPool(VmHeap? heap = null) {
        _heap = heap;
    }

    /// <summary>文字列を取得 (未登録なら新規作成して登録。新規作成時はアロケーションを計上する)。</summary>
    public VmString Get(string value) {
        if (_pool.TryGetValue(value, out var existing))
            return existing;
        // インタニング済み文字列はヒープ管理外だが、新規作成は計上する
        // (intrinsic/ホスト境界経由の文字列生成も IL の ldstr と等価に制約される)
        _heap?.ChargeString(value.Length);
        var created = new VmString(value);
        _pool[value] = created;
        return created;
    }

    /// <summary>既に同一内容がプール済みならそれを返し、無ければ新規オブジェクトを作る (演算結果用)。</summary>
    public VmString GetOrNew(string value) => Get(value);

    /// <summary>FastAllocateString 相当: charCount 文字分のゼロ初期化バッファを確保する。
    /// リテラルと違い構築途中の可変バッファなのでプールには入らない。アロケーションは
    /// IL の newobj 相当に計上する (実 CLR の FastAllocateString と同じ確保点)。</summary>
    public VmString Allocate(int charCount) {
        _heap?.ChargeString(charCount);
        return new VmString(new byte[VmString.HeaderByteCount + charCount * 2]);
    }

    public int Count => _pool.Count;
}
