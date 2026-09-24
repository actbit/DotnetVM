using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // ---- TypeDef ロード ----

    /// <summary>TypeDef rid をロードする (ロード済みならキャッシュを返す)。</summary>
    public VmClassType GetTypeDef(int typeDefRid) {
        lock (MetadataGate)
            return GetTypeDefCore(typeDefRid);
    }

    private VmClassType GetTypeDefCore(int typeDefRid) {
        if (_typeDefs.TryGetValue(typeDefRid, out var cached))
            return cached;

        var (ns, name) = _image.GetTypeDefName(typeDefRid);
        var flags = _image.Tables.GetCell(TableKind.TypeDef, typeDefRid, 0);
        var type = new VmClassType(flags) {
            Image = _image,
            TypeDefRid = typeDefRid,
            Namespace = ns,
            Name = name,
            Loader = this,
        };
        _typeDefs[typeDefRid] = type;

        LoadMembers(type);
        LoadGenericParams(type);
        ResolveNesting(type);
        _pendingCompletion.Add(type); // 基底型/インターフェースの解決はメンバ後に行う
        CompletePendingTypes();       // 依存型の連鎖ロードも含めて即時に完了させる
        return type;
    }

    /// <summary>GenericParam テーブルから本型の変性フラグを取り込む (ジェネリック定義のみ)。</summary>
    private void LoadGenericParams(VmClassType type) {
        var count = _image.Tables.GetRowCount(TableKind.GenericParam);
        var flags = new List<uint>();
        for (var rid = 1; rid <= count; rid++) {
            var owner = _image.Tables.DecodeCoded(TableKind.GenericParam, rid, 2, CodedIndexKind.TypeOrMethodDef);
            if (owner.Table != TableKind.TypeDef || owner.Rid != type.TypeDefRid)
                continue;
            // 行は Owner 順にソートされている規約だが、Number 順に並べ替えてから採用する
            var number = (int)_image.Tables.GetCell(TableKind.GenericParam, rid, 0);
            var paramFlags = _image.Tables.GetCell(TableKind.GenericParam, rid, 1);
            if (number < flags.Count)
                flags.Insert(number, paramFlags);
            else {
                while (flags.Count < number)
                    flags.Add(0);
                flags.Add(paramFlags);
            }
        }
        type.SetGenericParamFlags([.. flags]);
    }

    private void LoadMembers(VmClassType type) {
        var (fieldStart, fieldEnd, methodStart, methodEnd) = MemberRanges(type.TypeDefRid);

        var fields = new List<VmField>(fieldEnd - fieldStart);
        for (var rid = fieldStart; rid < fieldEnd; rid++) {
            var fieldFlags = _image.Tables.GetCell(TableKind.Field, rid, 0);
            var name = _image.GetString(_image.Tables.GetRowIndex(TableKind.Field, rid, 1));
            // CoreLib 等の実画像には VM が解釈できない署名要素 (関数ポインタ等) を含むメンバが
            // ある。メンバ単位でスキップし (当該面は呼出時に fail-closed)、型全体のロードは継続する
            FieldSignature signature;
            try {
                signature = SignatureDecoder.DecodeFieldSignature(
                    _image.GetBlob(_image.Tables.GetRowIndex(TableKind.Field, rid, 2)).ToArray(),
                    _image.Limits?.MaxSignatureDepth ?? 64,
                    _image.Limits?.MaxGenericNestingDepth ?? 64);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException) {
                continue;
            }
            var field = new VmField {
                DeclaringType = type,
                FieldRid = rid,
                Name = name,
                Signature = signature,
                Flags = fieldFlags,
            };
            _fields[rid] = field;
            fields.Add(field);
        }

        var methods = new List<VmMethod>(methodEnd - methodStart);
        for (var rid = methodStart; rid < methodEnd; rid++) {
            var rva = (int)_image.Tables.GetCell(TableKind.MethodDef, rid, 0);
            var implFlags = _image.Tables.GetCell(TableKind.MethodDef, rid, 1);
            var methodFlags = _image.Tables.GetCell(TableKind.MethodDef, rid, 2);
            var name = _image.GetMethodName(rid);
            // 同上: 解釈不能な署名のメソッドはスキップする (呼出時に未解決として fail-closed)
            MethodSignature signature;
            try {
                signature = SignatureDecoder.DecodeMethodSignature(
                    _image.GetMethodSignature(rid).ToArray(),
                    _image.Limits?.MaxSignatureDepth ?? 64,
                    _image.Limits?.MaxGenericNestingDepth ?? 64);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException) {
                continue;
            }
            var method = new VmMethod {
                DeclaringType = type,
                MethodDefRid = rid,
                Name = name,
                Signature = signature,
                Flags = methodFlags,
                ImplFlags = implFlags,
                Body = rva == 0 ? null : _image.GetMethodBody(rid),
            };
            _methods[rid] = method;
            method.Loader = this; // 実行時の token 解決 (ldstr/ldtoken/呼出) を自画像に固定する
            methods.Add(method);
        }

        type.Fields = fields;
        type.Methods = methods;
    }

    /// <summary>TypeDef rid の (FieldList 先頭, FieldList 終端, MethodList 先頭, MethodList 終端)。</summary>
    private (int, int, int, int) MemberRanges(int typeDefRid) {
        var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
        var fieldCount = _image.Tables.GetRowCount(TableKind.Field);
        var methodCount = _image.Tables.GetRowCount(TableKind.MethodDef);
        var fieldStart = _image.Tables.GetRowIndex(TableKind.TypeDef, typeDefRid, 4);
        var methodStart = _image.Tables.GetRowIndex(TableKind.TypeDef, typeDefRid, 5);
        var fieldEnd = typeDefRid < typeDefs
            ? _image.Tables.GetRowIndex(TableKind.TypeDef, typeDefRid + 1, 4)
            : fieldCount + 1;
        var methodEnd = typeDefRid < typeDefs
            ? _image.Tables.GetRowIndex(TableKind.TypeDef, typeDefRid + 1, 5)
            : methodCount + 1;
        return (fieldStart, fieldEnd, methodStart, methodEnd);
    }

    private void ResolveNesting(VmClassType type) {
        var enclosing = _image.GetEnclosingTypeDef(type.TypeDefRid);
        if (enclosing != 0)
            type.DeclaringType = GetTypeDef(enclosing);
    }

    /// <summary>保留中の型の基底型/インターフェース/フィールド型を解決する。全型解決後に 1 回呼ぶ。</summary>
    public void CompletePendingTypes() {
        lock (MetadataGate)
            CompletePendingTypesCore();
    }

    private void CompletePendingTypesCore() {
        // スタックで処理 (CompleteType 内の GetTypeDef が再入して依存型を完了させるため、
        // 二重完了にならないよう完了済みセットで守る)
        while (_pendingCompletion.Count > 0) {
            var type = _pendingCompletion[^1];
            _pendingCompletion.RemoveAt(_pendingCompletion.Count - 1);
            if (_completedTypeDefs.Add(type.TypeDefRid))
                CompleteType(type);
        }
    }

    private void CompleteType(VmClassType type) {
        // 基底型 (Extends)
        var extendsTag = _image.Tables.DecodeCoded(TableKind.TypeDef, type.TypeDefRid, 3, CodedIndexKind.TypeDefOrRef);
        if (extendsTag.Rid != 0) {
            var baseToken = Token.From(extendsTag.Table, extendsTag.Rid).Value;
            type.SetBaseType(ResolveToken(new SigType(SigKind.TypeToken, Token: baseToken)));
        } else {
            // <module> 型等 (Extends = null)。System.Object 自身は基底を持たない
            // (実型に統合した場合の自己参照循環を断つ)
            if (type.FullName != "System.Object") {
                type.SetBaseType(TryResolveTrustedUnifiedType("System.Object")
                    ?? FindIntrinsicType("System.Object"));
            } else {
                type.SetBaseType(null);
            }
        }

        // インターフェース (InterfaceImpl)
        var interfaces = new List<VmType>();
        var ifaceCount = _image.Tables.GetRowCount(TableKind.InterfaceImpl);
        for (var rid = 1; rid <= ifaceCount; rid++) {
            if (_image.Tables.GetRowIndex(TableKind.InterfaceImpl, rid, 0) != type.TypeDefRid)
                continue;
            var token = _image.Tables.DecodeCoded(TableKind.InterfaceImpl, rid, 1, CodedIndexKind.TypeDefOrRef);
            interfaces.Add(ResolveToken(new SigType(SigKind.TypeToken, Token: Token.From(token.Table, token.Rid).Value)));
        }
        type.SetInterfaces([.. interfaces]);

        // フィールド型
        foreach (var field in type.Fields)
            field.FieldType = ResolveToken(field.Signature.FieldType);
    }
}
