using DotnetVM.PE;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Metadata;

/// <summary>EH 句の種別。</summary>
public enum ExceptionClauseKind : byte {
    /// <summary>型による catch (ClassTokenOrFilterOffset に型トークン)。</summary>
    Catch = 0,
    /// <summary>フィルタ (ClassTokenOrFilterOffset にフィルタ IL オフセット)。</summary>
    Filter = 1,
    /// <summary>finally。</summary>
    Finally = 2,
    /// <summary>fault。</summary>
    Fault = 3,
}

/// <summary>例外処理句。</summary>
public readonly record struct ExceptionClause(
    int TryOffset, int TryLength, int HandlerOffset, int HandlerLength,
    ExceptionClauseKind Kind, int ClassTokenOrFilterOffset);

/// <summary>メソッド本体 (tiny/fat ヘッダ + IL + EH 句)。RVA = 0 (abstract/pinvoke) は取得不可。</summary>
public sealed class MethodBodyBlock {
    public int MaxStack { get; private init; }
    /// <summary>ローカル変数署名の StandAloneSig トークン (0 = ローカルなし)。</summary>
    public int LocalVarSigToken { get; private init; }
    private ReadOnlyMemory<byte> IlBytes { get; init; }
    public ReadOnlySpan<byte> IlCode => IlBytes.Span;
    public ExceptionClause[]? ExceptionClauses { get; private init; }
    internal SigType[]? DynamicLocalTypes { get; private init; }
    public int Size => IlCode.Length;

    private MethodBodyBlock() { }

    /// <summary>VM DynamicMethod の IL を既存インタプリタに渡すための本体。</summary>
    internal static MethodBodyBlock FromDynamicCode(ReadOnlyMemory<byte> code, SigType[]? localTypes = null) => new() {
        MaxStack = 8,
        LocalVarSigToken = 0,
        IlBytes = code,
        DynamicLocalTypes = localTypes,
    };

    /// <summary>RVA 位置のメソッド本体を解析する。RVA = 0 の場合は null。
    /// maxMethodBodyBytes を渡すと IL コードサイズを読み込み時に強制する
    /// (巨大メソッド本体を確保する前に拒否する)。</summary>
    public static MethodBodyBlock? FromRva(PEImage pe, int rva, int? maxMethodBodyBytes = null) {
        if (rva == 0)
            return null;
        var image = pe.GetSegmentToEnd(rva);
        var span = image.Span;
        if (span.Length == 0)
            throw new BadImageFormatException($"RVA 0x{rva:X} のメソッド本体が読めません。");

        var firstByte = span[0];
        switch (firstByte & 0b11) {
            case 0b10: {
                // tiny ヘッダ: 上位 6 ビット = コードサイズ
                var codeSize = firstByte >> 2;
                if (maxMethodBodyBytes.HasValue && codeSize > maxMethodBodyBytes.Value)
                    throw new BadImageFormatException(
                        $"メソッド本体 (tiny) のサイズ {codeSize:N0} が上限 {maxMethodBodyBytes.Value:N0} を超えています。");
                return new MethodBodyBlock {
                    MaxStack = 8,
                    LocalVarSigToken = 0,
                    IlBytes = image.Slice(1, codeSize),
                };
            }
            case 0b11: {
                // fat ヘッダ: 12 バイト
                if (span.Length < 12)
                    throw new BadImageFormatException("fat メソッドヘッダが範囲外です。");
                var flagsAndSize = firstByte | (span[1] << 8);
                const int CorILMethodMoreSects = 0x08;
                var headerDwords = (flagsAndSize >> 12) & 0xF;
                var headerSize = headerDwords * 4;
                var maxStack = (ushort)(span[2] | (span[3] << 8));
                var codeSize = (int)(uint)(span[4] | (span[5] << 8) | (span[6] << 16) | (span[7] << 24));
                var localVarSigToken = (int)(uint)(span[8] | (span[9] << 8) | (span[10] << 16) | (span[11] << 24));
                if (maxMethodBodyBytes.HasValue && codeSize > maxMethodBodyBytes.Value)
                    throw new BadImageFormatException(
                        $"メソッド本体 (fat) のサイズ {codeSize:N0} が上限 {maxMethodBodyBytes.Value:N0} を超えています。");
                if (headerSize > span.Length || codeSize > span.Length - headerSize)
                    throw new BadImageFormatException("fat メソッド本体が範囲外です。");

                ExceptionClause[]? clauses = null;
                if ((flagsAndSize & CorILMethodMoreSects) != 0) {
                    var sectionOffset = Align(headerSize + codeSize, 4);
                    clauses = ParseExceptionSections(span, sectionOffset);
                }

                return new MethodBodyBlock {
                    MaxStack = maxStack,
                    LocalVarSigToken = localVarSigToken,
                    IlBytes = image.Slice(headerSize, codeSize),
                    ExceptionClauses = clauses,
                };
            }
            default:
                throw new BadImageFormatException($"メソッドヘッダのフォーマットビットが不正です (0x{firstByte:X2})。");
        }
    }

    /// <summary>
    /// EH セクションを解析する (II.25.4.4/25.4.5, corhdr.h CorILMethodSect)。
    /// 種別バイト: 種別ビット 0x01 = EHTable (他に 0x02 = OptILTable 等)、
    /// 形式フラグ 0x40 = FatFormat、0x80 = MoreSects。small EH = 0x01、fat EH = 0x41。
    /// </summary>
    private static ExceptionClause[] ParseExceptionSections(ReadOnlySpan<byte> image, int sectionOffset) {
        if (sectionOffset >= image.Length)
            throw new BadImageFormatException("EH セクションが範囲外です。");

        var kindByte = image[sectionOffset];
        const int CorILMethodSectEHTable = 0x01;
        const int CorILMethodSectFatFormat = 0x40;
        if ((kindByte & CorILMethodSectEHTable) == 0)
            throw new BadImageFormatException($"EH セクション以外が指定されました (種別バイト 0x{kindByte:X2})。");
        var isFat = (kindByte & CorILMethodSectFatFormat) != 0;

        int dataSize, clauseSize, clausesOffset;
        if (!isFat) {
            dataSize = (ushort)(image[sectionOffset + 1] | (image[sectionOffset + 2] << 8));
            clauseSize = 12;
            clausesOffset = sectionOffset + 4;
        } else {
            dataSize = (int)(uint)(image[sectionOffset + 1] | (image[sectionOffset + 2] << 8) | (image[sectionOffset + 3] << 16));
            clauseSize = 24;
            clausesOffset = sectionOffset + 4;
        }
        var clauseCount = (dataSize - 4) / clauseSize;
        if (clausesOffset + clauseCount * clauseSize > image.Length)
            throw new BadImageFormatException("EH 句が範囲外です。");

        var clauses = new ExceptionClause[clauseCount];
        for (var i = 0; i < clauseCount; i++) {
            var p = clausesOffset + i * clauseSize;

            int flags, tryOffset, tryLength, handlerOffset, handlerLength, tokenOrFilter;
            if (!isFat) {
                // small (12バイト): u32 = Flags(下位16) | TryOffset(上位16)、
                // byte4 = TryLength、bytes5-6 = HandlerOffset、byte7 = HandlerLength、u32 = ClassToken/FilterOffset
                var head = (int)U32(image, p, 0);
                flags = head & 0xFFFF;
                tryOffset = head >> 16;
                tryLength = image[p + 4];
                handlerOffset = image[p + 5] | (image[p + 6] << 8);
                handlerLength = image[p + 7];
                tokenOrFilter = (int)U32(image, p, 8);
            } else {
                // fat (24バイト): すべて 4 バイト、Flags が先頭
                flags = (int)U32(image, p, 0);
                tryOffset = (int)U32(image, p, 4);
                tryLength = (int)U32(image, p, 8);
                handlerOffset = (int)U32(image, p, 12);
                handlerLength = (int)U32(image, p, 16);
                tokenOrFilter = (int)U32(image, p, 20);
            }

            // COR_ILEXCEPTION_CLAUSE_FILTER = 0x0001, FINALLY = 0x0002, FAULT = 0x0004
            var (kind, classTokenOrFilter) = (flags & 0x0002) != 0 ? (ExceptionClauseKind.Finally, 0)
                : (flags & 0x0004) != 0 ? (ExceptionClauseKind.Fault, 0)
                : (flags & 0x0001) != 0 ? (ExceptionClauseKind.Filter, tokenOrFilter)
                : (ExceptionClauseKind.Catch, tokenOrFilter);

            clauses[i] = new ExceptionClause(tryOffset, tryLength, handlerOffset, handlerLength, kind, classTokenOrFilter);
        }
        return clauses;
    }

    private static uint U32(ReadOnlySpan<byte> data, int baseOffset, int relative) =>
        (uint)(data[baseOffset + relative] | (data[baseOffset + relative + 1] << 8)
               | (data[baseOffset + relative + 2] << 16) | (data[baseOffset + relative + 3] << 24));

    private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
