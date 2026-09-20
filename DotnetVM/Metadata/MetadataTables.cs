using System.Buffers.Binary;
using System.Numerics;
using DotnetVM.Binary;

namespace DotnetVM.Metadata;

/// <summary>
/// #~ ストリームの解析とテーブル行アクセス。
/// 列幅は HeapSizes ビットと各テーブルの行数から ECMA-335 II.24.2.6 に従って動的計算する
/// (固定レイアウトのハードコードはしない)。
/// </summary>
public sealed class MetadataTables {
    private sealed class TableLayout {
        public int RowCount;
        public int RowSize;
        public int FirstRowOffset;
        public int[] ColumnWidths = [];
        public int[] ColumnOffsets = [];
    }

    private readonly ReadOnlyMemory<byte> _data;
    private readonly bool _wideStringIndexes;    // #Strings が 4 バイト (bit 0)
    private readonly bool _wideGuidIndexes;      // #GUID が 4 バイト (bit 1)
    private readonly bool _wideBlobIndexes;      // #Blob が 4 バイト (bit 2)
    private readonly TableLayout[] _layouts = new TableLayout[64];

    internal MetadataTables(ReadOnlyMemory<byte> data) {
        _data = data;
        var reader = new SpanReader(data.Span);

        reader.Advance(4);                                 // Reserved
        reader.Advance(2);                                 // MajorVersion / MinorVersion
        var heapSizes = reader.ReadByte();
        reader.Advance(1);                                 // Reserved
        _wideStringIndexes = (heapSizes & 0x01) != 0;
        _wideGuidIndexes = (heapSizes & 0x02) != 0;
        _wideBlobIndexes = (heapSizes & 0x04) != 0;

        var validMask = reader.ReadUInt64();
        reader.ReadUInt64();                               // Sorted (未使用)

        // 行数テーブル: Valid ビットが立っているテーブル順
        var rowCounts = new int[64];
        for (var t = 0; t < 64; t++) {
            if ((validMask & (1UL << t)) != 0)
                rowCounts[t] = (int)reader.ReadUInt32();
        }

        // 各テーブルの列幅を計算
        for (var t = 0; t < 64; t++) {
            if (rowCounts[t] == 0)
                continue;
            var table = (TableKind)t;
            var columns = TableSchema.GetColumns(table);
            var layout = new TableLayout {
                RowCount = rowCounts[t],
                ColumnWidths = new int[columns.Count],
            };
            for (var c = 0; c < columns.Count; c++)
                layout.ColumnWidths[c] = GetColumnWidth(columns[c], rowCounts);
            layout.RowSize = layout.ColumnWidths.Sum();
            layout.ColumnOffsets = PrefixSums(layout.ColumnWidths);
            _layouts[t] = layout;
        }

        // 先頭行オフセットを確定 (テーブルは Valid ビット順に連続配置)
        var cursor = reader.Offset;
        for (var t = 0; t < 64; t++) {
            if (_layouts[t] is { RowCount: > 0 } layout) {
                layout.FirstRowOffset = cursor;
                cursor += layout.RowCount * layout.RowSize;
            }
        }
        if (cursor > _data.Length)
            throw new BadImageFormatException($"メタデータテーブルが #~ ストリームを超えています (計算 {cursor}, 実際 {_data.Length})。");
    }

    private int GetColumnWidth(ColumnDef column, int[] rowCounts) {
        switch (column.Kind) {
            case ColumnKind.U1: return 1;
            case ColumnKind.U2: return 2;
            case ColumnKind.U4: return 4;
            case ColumnKind.String:
                return _wideStringIndexes ? 4 : 2;
            case ColumnKind.Guid:
                return _wideGuidIndexes ? 4 : 2;
            case ColumnKind.Blob:
                return _wideBlobIndexes ? 4 : 2;
            case ColumnKind.Table:
                return rowCounts[(int)column.Target] > 0xFFFF ? 4 : 2;
            case ColumnKind.Coded: {
                var targets = TableSchema.GetCodedTargets(column.Coded);
                var tagBits = BitOperations.Log2((uint)BitOperations.RoundUpToPowerOf2((uint)targets.Length));
                var maxRows = 0;
                foreach (var target in targets)
                    if (target != (TableKind)0xFF)
                        maxRows = Math.Max(maxRows, rowCounts[(int)target]);
                // 2 バイトに収まる条件: 最大行数が 2^(16 - tagBits) 未満
                return maxRows < (1 << (16 - tagBits)) ? 2 : 4;
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private static int[] PrefixSums(int[] widths) {
        var offsets = new int[widths.Length];
        var sum = 0;
        for (var i = 0; i < widths.Length; i++) {
            offsets[i] = sum;
            sum += widths[i];
        }
        return offsets;
    }

    /// <summary>テーブルの行数 (テーブルが存在しない場合は 0)。</summary>
    public int GetRowCount(TableKind table) => _layouts[(int)table]?.RowCount ?? 0;

    /// <summary>行の生のセル値 (インデックス/定数)。rid は 1 始まり、column は 0 始まり。</summary>
    public uint GetCell(TableKind table, int rid, int column) {
        var layout = _layouts[(int)table] ?? throw new ArgumentOutOfRangeException($"テーブル {table} は存在しません。");
        if (rid < 1 || rid > layout.RowCount)
            throw new ArgumentOutOfRangeException($"テーブル {table} の rid {rid} は範囲外です (行数 {layout.RowCount})。");
        var offset = layout.FirstRowOffset + (rid - 1) * layout.RowSize + layout.ColumnOffsets[column];
        var span = _data.Span;
        return layout.ColumnWidths[column] switch {
            1 => span[offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>typed テーブル参照セルの値 (1 始まりの rid)。</summary>
    public int GetRowIndex(TableKind table, int rid, int column) => (int)GetCell(table, rid, column);

    /// <summary>
    /// coded index セルを復号する。tag は GetCodedTargets のターゲット配列へのインデックス。
    /// </summary>
    public (TableKind Table, int Rid) DecodeCoded(TableKind table, int rid, int column, CodedIndexKind coded) {
        var value = GetCell(table, rid, column);
        var targets = TableSchema.GetCodedTargets(coded);
        var tagBits = BitOperations.Log2((uint)BitOperations.RoundUpToPowerOf2((uint)targets.Length));
        var tag = (int)(value & ((1u << tagBits) - 1));
        if (tag >= targets.Length)
            throw new BadImageFormatException($"coded index のタグ {tag} が不正です ({coded})。");
        var target = targets[tag];
        if (target == (TableKind)0xFF)
            throw new BadImageFormatException($"coded index が未使用タグを参照しています ({coded}, tag={tag})。");
        return (target, (int)(value >> tagBits));
    }
}
