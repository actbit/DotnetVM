using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>メソッドの事前準備キャッシュ: ローカル変数署名のデコード結果と、IL オフセット基準の
/// EH 句を命令インデックス基準に解決した結果をメソッドごとに 1 回だけ計算して保持する。</summary>
internal sealed class MethodPreparer(TypeLoader loader, int maxPreparedMethods) {
    private readonly Dictionary<VmMethod, PreparedMethod> _prepared = [];
    private readonly Queue<VmMethod> _order = [];
    private readonly object _gate = new();

    internal int Count { get { lock (_gate) return _prepared.Count; } }

    public PreparedMethod Prepare(VmMethod method) {
        lock (_gate)
            return PrepareCore(method);
    }

    private PreparedMethod PrepareCore(VmMethod method) {
        if (_prepared.TryGetValue(method, out var cached))
            return cached;

        var code = method.Body is null ? [] : DecodeIl(method);
        SigType[] localTypes = method.Body?.DynamicLocalTypes ?? [];
        if (method.Body is { } body && body.DynamicLocalTypes is null && body.LocalVarSigToken != 0) {
            var table = (TableKind)(body.LocalVarSigToken >> 24);
            var rid = (int)(body.LocalVarSigToken & 0xFFFFFF);
            if (table != TableKind.StandAloneSig)
                throw new BadImageFormatException($"ローカル変数署名トークン 0x{body.LocalVarSigToken:X8} が不正です。");
            try {
                localTypes = SignatureDecoder.DecodeLocalsSignature(
                    loader.Image.GetBlob(loader.Image.Tables.GetRowIndex(table, rid, 0)).ToArray(),
                    loader.Image.Limits?.MaxSignatureDepth ?? 64,
                    loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
            } catch (BadImageFormatException) {
                throw;
            } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
                throw new BadImageFormatException(
                    $"ローカル変数署名 0x{body.LocalVarSigToken:X8} の構造が不正です。", ex);
            }
        }

        var prepared = new PreparedMethod(localTypes, code) {
            Clauses = ResolveExceptionClauses(method, code),
        };
        if (method.Body is not null)
            IlVerifier.Verify(loader, method, code, localTypes, prepared.Clauses);
        if (maxPreparedMethods > 0) {
            _prepared[method] = prepared;
            _order.Enqueue(method);
            while (_prepared.Count > maxPreparedMethods) {
                var oldest = _order.Dequeue();
                // The dictionary is only populated once per method while the
                // gate is held, so a FIFO eviction is deterministic and bounded.
                _prepared.Remove(oldest);
            }
        }
        return prepared;
    }

    /// <summary>EH 句 (IL オフセット基準) を命令インデックス基準に解決する。</summary>
    private static PreparedClause[]? ResolveExceptionClauses(VmMethod method, DecodedInstruction[] code) {
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

        var offsetToIndex = new Dictionary<int, int>(code.Length * 2);
        for (var i = 0; i < code.Length; i++)
            offsetToIndex[code[i].Offset] = i;

        var clauses = new PreparedClause[raw.Length];
        for (var i = 0; i < raw.Length; i++) {
            var clause = raw[i];
            if (clause.Kind is not (ExceptionClauseKind.Catch or ExceptionClauseKind.Filter or
                ExceptionClauseKind.Finally or ExceptionClauseKind.Fault) ||
                clause.TryLength <= 0 || clause.HandlerLength <= 0 ||
                clause.TryOffset < 0 || clause.HandlerOffset < 0)
                throw new BadImageFormatException($"EH 句 {i} の種別または長さが不正です。");
            if (!offsetToIndex.TryGetValue(clause.TryOffset, out var tryStart) ||
                !offsetToIndex.TryGetValue(clause.HandlerOffset, out var handlerStart))
                throw new BadImageFormatException(
                    $"EH 句 {i} (try IL_{clause.TryOffset:X4}, handler IL_{clause.HandlerOffset:X4}) が命令境界上にありません。");
            var tryEndOffset = CheckedEnd(clause.TryOffset, clause.TryLength, code, i, "try");
            var handlerEndOffset = CheckedEnd(clause.HandlerOffset, clause.HandlerLength, code, i, "handler");
            clauses[i] = new PreparedClause {
                Kind = clause.Kind,
                TryStart = tryStart,
                TryEnd = BoundaryIndex(code, tryEndOffset, i, "try 終端"),
                HandlerStart = handlerStart,
                HandlerEnd = BoundaryIndex(code, handlerEndOffset, i, "handler 終端"),
                FilterStart = clause.Kind == ExceptionClauseKind.Filter
                    ? (offsetToIndex.TryGetValue(clause.ClassTokenOrFilterOffset, out var filterStart)
                        ? filterStart
                        : throw new BadImageFormatException(
                            $"フィルタ先 IL_{clause.ClassTokenOrFilterOffset:X4} が命令境界上にありません。"))
                        : -1,
                ClassToken = clause.Kind == ExceptionClauseKind.Catch
                    ? clause.ClassTokenOrFilterOffset : 0,
            };
            if (clauses[i].Kind == ExceptionClauseKind.Filter &&
                (clauses[i].FilterStart < 0 || clauses[i].FilterStart >= clauses[i].HandlerStart))
                throw new BadImageFormatException($"EH 句 {i} のフィルタ終端が不正です。");
        }
        return clauses;
    }

    private static DecodedInstruction[] DecodeIl(VmMethod method) {
        try {
            return method.DecodeIl();
        } catch (BadImageFormatException) {
            throw;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            throw new BadImageFormatException($"メソッド {method} の IL が不正です。", ex);
        }
    }

    private static int CheckedEnd(int offset, int length, DecodedInstruction[] code, int clauseIndex, string name) {
        long end = (long)offset + length;
        var ilLength = code.Length == 0 ? 0 : code[^1].Offset + code[^1].Size;
        if (end > ilLength)
            throw new BadImageFormatException($"EH 句 {clauseIndex} の {name} 範囲がメソッド本体を超えています。");
        return (int)end;
    }

    private static int BoundaryIndex(DecodedInstruction[] code, int ilOffset, int clauseIndex, string name) {
        if (code.Length > 0 && ilOffset == code[^1].Offset + code[^1].Size)
            return code.Length;
        for (var i = 0; i < code.Length; i++)
            if (code[i].Offset == ilOffset)
                return i;
        throw new BadImageFormatException($"EH 句 {clauseIndex} の {name} IL_{ilOffset:X4} が命令境界上にありません。");
    }
}

/// <summary>メソッドの事前準備結果 (ローカル型 + 解決済み EH 句)。</summary>
internal sealed class PreparedMethod(SigType[] localTypes, DecodedInstruction[] code) {
    public readonly SigType[] LocalTypes = localTypes;
    /// <summary>検証済みの命令列。デコードと検証はメソッドごとに一度だけ行う。</summary>
    public readonly DecodedInstruction[] Code = code;

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
