using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>メソッドの事前準備キャッシュ: ローカル変数署名のデコード結果と、IL オフセット基準の
/// EH 句を命令インデックス基準に解決した結果をメソッドごとに 1 回だけ計算して保持する。</summary>
internal sealed class MethodPreparer(TypeLoader loader) {
    private readonly Dictionary<VmMethod, PreparedMethod> _prepared = [];

    public PreparedMethod Prepare(VmMethod method) {
        if (_prepared.TryGetValue(method, out var cached))
            return cached;

        SigType[] localTypes = [];
        if (method.Body is { } body && body.LocalVarSigToken != 0) {
            var table = (TableKind)(body.LocalVarSigToken >> 24);
            var rid = (int)(body.LocalVarSigToken & 0xFFFFFF);
            if (table != TableKind.StandAloneSig)
                throw new BadImageFormatException($"ローカル変数署名トークン 0x{body.LocalVarSigToken:X8} が不正です。");
            localTypes = SignatureDecoder.DecodeLocalsSignature(
                loader.Image.GetBlob(loader.Image.Tables.GetRowIndex(table, rid, 0)).ToArray(),
                loader.Image.Limits?.MaxSignatureDepth ?? 64,
                loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
        }

        var prepared = new PreparedMethod(localTypes) {
            Clauses = ResolveExceptionClauses(method),
        };
        _prepared[method] = prepared;
        return prepared;
    }

    /// <summary>EH 句 (IL オフセット基準) を命令インデックス基準に解決する。</summary>
    private static PreparedClause[]? ResolveExceptionClauses(VmMethod method) {
        var raw = method.Body?.ExceptionClauses;
        if (raw is null || raw.Length == 0)
            return null;

        // 内側の句から外側の順で走査できるよう入れ子順にソート (深い = TryOffset が大きく範囲が狭い)
        if (raw.Length > 1) {
            var sorted = (ExceptionClause[])raw.Clone();
            Array.Sort(sorted, (a, b) => {
                var byStart = b.TryOffset.CompareTo(a.TryOffset);
                return byStart != 0 ? byStart : a.TryLength.CompareTo(b.TryLength);
            });
            raw = sorted;
        }

        var code = method.DecodeIl();
        var offsetToIndex = new Dictionary<int, int>(code.Length * 2);
        for (var i = 0; i < code.Length; i++)
            offsetToIndex[code[i].Offset] = i;

        var clauses = new PreparedClause[raw.Length];
        for (var i = 0; i < raw.Length; i++) {
            var clause = raw[i];
            if (!offsetToIndex.TryGetValue(clause.TryOffset, out var tryStart) ||
                !offsetToIndex.TryGetValue(clause.HandlerOffset, out var handlerStart))
                throw new BadImageFormatException(
                    $"EH 句 {i} (try IL_{clause.TryOffset:X4}, handler IL_{clause.HandlerOffset:X4}) が命令境界上にありません。");
            clauses[i] = new PreparedClause {
                Kind = clause.Kind,
                TryStart = tryStart,
                TryEnd = IndexAfter(code, clause.TryOffset + clause.TryLength, tryStart),
                HandlerStart = handlerStart,
                HandlerEnd = IndexAfter(code, clause.HandlerOffset + clause.HandlerLength, handlerStart),
                FilterStart = clause.Kind == ExceptionClauseKind.Filter
                    ? (offsetToIndex.TryGetValue(clause.ClassTokenOrFilterOffset, out var filterStart)
                        ? filterStart
                        : throw new BadImageFormatException(
                            $"フィルタ先 IL_{clause.ClassTokenOrFilterOffset:X4} が命令境界上にありません。"))
                    : -1,
                ClassToken = clause.ClassTokenOrFilterOffset,
            };
        }
        return clauses;
    }

    /// <summary>指定 IL オフセット以上で最初の命令のインデックス (末尾到達なら code.Length)。</summary>
    private static int IndexAfter(DecodedInstruction[] code, int ilOffset, int fallback) {
        for (var i = fallback; i < code.Length; i++)
            if (code[i].Offset >= ilOffset)
                return i;
        return code.Length;
    }
}

/// <summary>メソッドの事前準備結果 (ローカル型 + 解決済み EH 句)。</summary>
internal sealed class PreparedMethod(SigType[] localTypes) {
    public readonly SigType[] LocalTypes = localTypes;

    /// <summary>解決済み EH 句 (命令インデックス基準)。EH の無いメソッドは null。</summary>
    public PreparedClause[]? Clauses;
}

/// <summary>解決済み EH 句 (PreparedMethod.Clauses の要素)。</summary>
internal sealed class PreparedClause {
    public ExceptionClauseKind Kind;
    public int TryStart;
    public int TryEnd; // 排他
    public int HandlerStart;
    public int HandlerEnd; // 排他 (現状未使用だが範囲検査用に保持)
    /// <summary>フィルタ本体の開始インデックス (Catch/Finally/Fault は -1)。</summary>
    public int FilterStart;
    /// <summary>Catch 句の型トークン (TypeDefOrRef)。</summary>
    public int ClassToken;
}
