using System.Text;
using DotnetVM.IL;
using DotnetVM.Metadata;

namespace DotnetVM.Diagnostics;

/// <summary>
/// メソッド本体をテキスト化する逆アセンブラ (M1 最小版)。
/// トークンは可能な範囲で解決して "Ns.Type::Member" 形式で表示する。
/// </summary>
public static class IlDisassembler {
    /// <summary>MethodDef を逆アセンブルしたテキストを返す。</summary>
    public static string DisassembleMethod(AssemblyImage image, int methodDefRid) {
        using var writer = new StringWriter();
        WriteMethod(image, methodDefRid, writer);
        return writer.ToString();
    }

    /// <summary>MethodDef をライターへ書き出す。</summary>
    public static void WriteMethod(AssemblyImage image, int methodDefRid, TextWriter writer) {
        var body = image.GetMethodBody(methodDefRid)
            ?? throw new ArgumentException($"MethodDef rid={methodDefRid} にはメソッド本体がありません (abstract/pinvoke)。");
        writer.WriteLine($"// MethodDef rid={methodDefRid}  maxstack={body.MaxStack}  locals={FormatLocals(image, body.LocalVarSigToken)}");
        foreach (var instruction in IlDecoder.Decode(body.IlCode)) {
            var ehMarks = GetExceptionClauseMarks(body, instruction.Offset, instruction.Size);
            writer.WriteLine($"IL_{instruction.Offset:X4}:  {FormatInstruction(image, instruction)}{ehMarks}");
        }
        WriteExceptionClauses(image, body, writer);
    }

    /// <summary>1 命令をテキスト化する。</summary>
    public static string FormatInstruction(AssemblyImage image, in DecodedInstruction instruction) {
        var opName = IlOpcodeTable.Get(instruction.Op)?.Name ?? $"0x{(ushort)instruction.Op:X4}";
        return instruction.OperandKind switch {
            IlOperandKind.None => opName,
            IlOperandKind.ShortVar or IlOperandKind.Var => $"{opName} {instruction.IntOperand}",
            IlOperandKind.ShortI => $"{opName} {instruction.IntOperand}",
            IlOperandKind.I4 => $"{opName} ({instruction.IntOperand})",
            IlOperandKind.I8 => $"{opName} ({instruction.LongOperand})",
            IlOperandKind.R4 or IlOperandKind.R8 => $"{opName} ({FormatDouble(instruction.DoubleOperand)})",
            IlOperandKind.ShortBrTarget or IlOperandKind.BrTarget => $"{opName} IL_{instruction.IntOperand:X4}",
            IlOperandKind.Switch => $"{opName} {FormatSwitch(instruction)}",
            IlOperandKind.Method => $"{opName} {FormatMethodToken(image, instruction.IntOperand)}",
            IlOperandKind.Field => $"{opName} {FormatFieldToken(image, instruction.IntOperand)}",
            IlOperandKind.Type => $"{opName} {FormatTypeToken(image, (uint)instruction.IntOperand)}",
            // ldstr のオペランドは完全なトークン (0x70 = UserString テーブル)。#US オフセットは下位 24 ビット
            IlOperandKind.String => $"{opName} \"{Escape(image.GetUserString(instruction.IntOperand & 0xFFFFFF))}\"",
            IlOperandKind.Signature => $"{opName} token(0x{instruction.IntOperand:X8})",
            IlOperandKind.Token => $"{opName} {FormatAnyToken(image, (uint)instruction.IntOperand)}",
            _ => opName,
        };
    }

    private static string FormatSwitch(in DecodedInstruction instruction) {
        var targets = instruction.SwitchTargets!;
        var sb = new StringBuilder("(");
        for (var i = 0; i < targets.Length; i++) {
            if (i > 0)
                sb.Append(", ");
            sb.Append($"IL_{targets[i]:X4}");
        }
        return sb.Append(')').ToString();
    }

    private static string FormatDouble(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // ---- EH 表示 ----

    private static string GetExceptionClauseMarks(MethodBodyBlock body, int offset, int size) {
        if (body.ExceptionClauses is not { } clauses)
            return "";
        var marks = new StringBuilder();
        for (var i = 0; i < clauses.Length; i++) {
            var clause = clauses[i];
            if (clause.TryOffset == offset)
                marks.Append($"  // EH[{i}].try");
            if (clause.HandlerOffset == offset)
                marks.Append(clause.Kind switch {
                    ExceptionClauseKind.Filter => $"  // EH[{i}].filter/handler",
                    _ => $"  // EH[{i}].handler",
                });
            if (clause.Kind == ExceptionClauseKind.Filter
                && clause.ClassTokenOrFilterOffset == offset)
                marks.Append($"  // EH[{i}].filter");
        }
        return marks.ToString();
    }

    private static void WriteExceptionClauses(AssemblyImage image, MethodBodyBlock body, TextWriter writer) {
        if (body.ExceptionClauses is not { } clauses)
            return;
        for (var i = 0; i < clauses.Length; i++) {
            var clause = clauses[i];
            var target = clause.Kind == ExceptionClauseKind.Catch
                ? FormatTypeToken(image, (uint)clause.ClassTokenOrFilterOffset)
                : clause.Kind == ExceptionClauseKind.Filter
                    ? $"IL_{clause.ClassTokenOrFilterOffset:X4}"
                    : "";
            writer.WriteLine($"  // EH[{i}]: {clause.Kind} try=[IL_{clause.TryOffset:X4}..{clause.TryOffset + clause.TryLength:X4}) " +
                             $"handler=[IL_{clause.HandlerOffset:X4}..{clause.HandlerOffset + clause.HandlerLength:X4}) {target}");
        }
    }

    private static string FormatLocals(AssemblyImage image, int localVarSigToken) =>
        localVarSigToken == 0 ? "none" : $"token(0x{localVarSigToken:X8})";

    // ---- トークン解決 (解決できない場合は token(0x...) のまま) ----

    public static string FormatMethodToken(AssemblyImage image, int token) {
        var table = (TableKind)(token >> 24);
        var rid = token & 0xFFFFFF;
        try {
            return table switch {
                TableKind.MethodDef => $"{FormatTypeDefName(image, GetOwnerTypeDefOfMethod(image, rid))}::{image.GetMethodName(rid)}",
                TableKind.MemberRef => FormatMemberRef(image, rid),
                TableKind.MethodSpec => $"generic token(0x{token:X8})",
                _ => $"token(0x{token:X8})",
            };
        } catch (Exception) {
            return $"token(0x{token:X8})";
        }
    }

    public static string FormatFieldToken(AssemblyImage image, int token) {
        var table = (TableKind)(token >> 24);
        var rid = token & 0xFFFFFF;
        try {
            return table switch {
                TableKind.Field => $"{FormatTypeDefName(image, GetOwnerTypeDefOfField(image, rid))}::{GetFieldName(image, rid)}",
                TableKind.MemberRef => FormatMemberRef(image, rid),
                _ => $"token(0x{token:X8})",
            };
        } catch (Exception) {
            return $"token(0x{token:X8})";
        }
    }

    public static string FormatTypeToken(AssemblyImage image, uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        try {
            return table switch {
                TableKind.TypeDef => FormatTypeDefName(image, rid),
                TableKind.TypeRef => FormatTypeRefName(image, rid),
                TableKind.TypeSpec => "type-spec",
                _ => $"token(0x{token:X8})",
            };
        } catch (Exception) {
            return $"token(0x{token:X8})";
        }
    }

    public static string FormatAnyToken(AssemblyImage image, uint token) {
        var table = (TableKind)(token >> 24);
        return table switch {
            TableKind.TypeDef or TableKind.TypeRef or TableKind.TypeSpec => FormatTypeToken(image, token),
            TableKind.MethodDef or TableKind.MemberRef => FormatMethodToken(image, (int)token),
            TableKind.Field => FormatFieldToken(image, (int)token),
            _ => $"token(0x{token:X8})",
        };
    }

    private static string FormatMemberRef(AssemblyImage image, int rid) {
        var (parent, name) = image.GetMemberRef(rid);
        var parentName = parent.Table switch {
            TableKind.TypeDef => FormatTypeDefName(image, parent.Rid),
            TableKind.TypeRef => FormatTypeRefName(image, parent.Rid),
            TableKind.TypeSpec => "type-spec",
            _ => $"token(0x{(int)parent.Table:X2})",
        };
        return $"{parentName}::{name}";
    }

    private static string FormatTypeDefName(AssemblyImage image, int typeDefRid) {
        if (typeDefRid == 0)
            return "<unknown-type>";
        var (ns, name) = image.GetTypeDefName(typeDefRid);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FormatTypeRefName(AssemblyImage image, int typeRefRid) {
        var (ns, name, _) = image.GetTypeRefName(typeRefRid);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetFieldName(AssemblyImage image, int fieldRid) =>
        image.GetString(image.Tables.GetRowIndex(TableKind.Field, fieldRid, 1));

    /// <summary>MethodDef rid を所有する TypeDef rid (MethodList の範囲走査)。</summary>
    public static int GetOwnerTypeDefOfMethod(AssemblyImage image, int methodDefRid) {
        var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
        var methodCount = image.Tables.GetRowCount(TableKind.MethodDef);
        for (var rid = 1; rid <= typeDefs; rid++) {
            var start = image.Tables.GetRowIndex(TableKind.TypeDef, rid, 5);
            var end = rid < typeDefs
                ? image.Tables.GetRowIndex(TableKind.TypeDef, rid + 1, 5)
                : methodCount + 1;
            if (methodDefRid >= start && methodDefRid < end)
                return rid;
        }
        return 0;
    }

    /// <summary>Field rid を所有する TypeDef rid (FieldList の範囲走査)。</summary>
    public static int GetOwnerTypeDefOfField(AssemblyImage image, int fieldRid) {
        var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
        var fieldCount = image.Tables.GetRowCount(TableKind.Field);
        for (var rid = 1; rid <= typeDefs; rid++) {
            var start = image.Tables.GetRowIndex(TableKind.TypeDef, rid, 4);
            var end = rid < typeDefs
                ? image.Tables.GetRowIndex(TableKind.TypeDef, rid + 1, 4)
                : fieldCount + 1;
            if (fieldRid >= start && fieldRid < end)
                return rid;
        }
        return 0;
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
