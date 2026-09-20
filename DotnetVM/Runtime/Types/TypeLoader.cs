using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// アセンブリの TypeDef を VmType に遅延ロードする。
/// 1 つの TypeDef は必ず 1 つの VmClassType インスタンスに対応 (キャッシュで保証)。
/// アセンブリに存在しない型のうち VM が面を提供するもの (System.Object 等) は
/// ファサード型 (VmIntrinsicType) として合成する。
/// </summary>
public sealed class TypeLoader {
    /// <summary>GenericParam.Flags の変性ビット (ECMA-335 II.22.20)。</summary>
    internal const uint Covariant = 0x0001;
    internal const uint Contravariant = 0x0002;

    private readonly AssemblyImage _image;

    /// <summary>ロード対象のアセンブリ。</summary>
    public AssemblyImage Image => _image;
    private readonly Dictionary<int, VmClassType> _typeDefs = [];
    private readonly Dictionary<string, VmIntrinsicType> _intrinsicTypes = [];
    private readonly Dictionary<int, VmMethod> _methods = [];
    private readonly Dictionary<int, VmField> _fields = [];
    private readonly List<VmClassType> _pendingCompletion = [];
    private readonly HashSet<int> _completedTypeDefs = [];

    public TypeLoader(AssemblyImage image) {
        _image = image;
        InitializeIntrinsicTypes();
    }

    // ---- ファサード型 ----

    private void InitializeIntrinsicTypes() {
        var @object = new VmIntrinsicType { Namespace = "System", Name = "Object", IsValue = false };
        var valueType = new VmIntrinsicType { Namespace = "System", Name = "ValueType", IsValue = false, Parent = @object };
        var @enum = new VmIntrinsicType { Namespace = "System", Name = "Enum", IsValue = false, Parent = valueType };

        void Add(VmIntrinsicType type, uint[]? genericParamFlags = null) {
            if (genericParamFlags is not null)
                type.SetGenericParamFlags(genericParamFlags);
            _intrinsicTypes[type.FullName] = type;
        }
        Add(@object);
        Add(valueType);
        Add(@enum);
        Add(new VmIntrinsicType { Namespace = "System", Name = "Void", IsValue = true });
        Add(new VmIntrinsicType { Namespace = "System", Name = "String", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Exception", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Console", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Math", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Convert", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Array", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Type", IsValue = false, Parent = @object });
        // 例外階層のファサード (ECMA-335 / CLR の SystemException 配下)。VM 内部例外もここから実体化する
        var systemException = new VmIntrinsicType { Namespace = "System", Name = "SystemException", IsValue = false, Parent = _intrinsicTypes["System.Exception"] };
        Add(systemException);
        Add(new VmIntrinsicType { Namespace = "System", Name = "InvalidOperationException", IsValue = false, Parent = systemException });
        var argumentException = new VmIntrinsicType { Namespace = "System", Name = "ArgumentException", IsValue = false, Parent = systemException };
        Add(argumentException);
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgumentOutOfRangeException", IsValue = false, Parent = argumentException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgumentNullException", IsValue = false, Parent = argumentException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ApplicationException", IsValue = false, Parent = _intrinsicTypes["System.Exception"] });
        foreach (var name in new[] {
            "NullReferenceException", "IndexOutOfRangeException", "DivideByZeroException",
            "OverflowException", "InvalidCastException", "ArrayTypeMismatchException",
            "FormatException", "StackOverflowException", "OutOfMemoryException",
            "NotSupportedException", "OperationCanceledException", "TimeoutException",
            "NotImplementedException",
        })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false, Parent = systemException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ObjectDisposedException", IsValue = false, Parent = _intrinsicTypes["System.InvalidOperationException"] });

        // プリミティブはすべて ValueType の派生
        foreach (var (name, isValue) in new[] {
            ("Boolean", true), ("Char", true), ("SByte", true), ("Byte", true),
            ("Int16", true), ("UInt16", true), ("Int32", true), ("UInt32", true),
            ("Int64", true), ("UInt64", true), ("Single", true), ("Double", true),
        })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = isValue, Parent = valueType });

        // ゲストが実装/参照する頻出外部インターフェースのファサード
        foreach (var name in new[] { "IDisposable", "IComparable", "ICloneable", "IFormatProvider" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false });
        foreach (var name in new[] { "IEnumerable", "IEnumerator", "ICollection", "IList" })
            Add(new VmIntrinsicType { Namespace = "System.Collections", Name = name, IsValue = false });
        // ジェネリックインターフェースは BCL 既知の変性を登録する (castclass/isinst の変性判定に使う)
        foreach (var name in new[] { "IEquatable`1", "IComparable`1" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false }, [Contravariant]);
        foreach (var name in new[] { "IComparer`1", "IEqualityComparer`1" })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false }, [Contravariant]);
        foreach (var name in new[] {
            "IEnumerable`1", "IEnumerator`1", "IReadOnlyList`1", "IReadOnlyCollection`1",
            "IReadOnlySet`1", "IAsyncEnumerable`1", "IAsyncEnumerator`1",
        })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false }, [Covariant]);
        foreach (var name in new[] { "ICollection`1", "IList`1", "ISet`1", "IDictionary`2" })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false });

        // 注意: ブリッジ経由の I/O 面 (System.IO.File / System.Net.WebClient) はここでは合成しない。
        // 界面の再現有無はブリッジ設定で制御する (VirtualMachine.LoadAssembly が条件付きで登録)。
        // ブリッジ未設定ならゲストにその面自体が存在しない = fail-closed
    }

    /// <summary>ブリッジ設定がある場合のみ呼ばれる I/O ファサード型の登録 (VirtualMachine から)。</summary>
    internal void AddIoFacade(string name) {
        VmIntrinsicType type = name switch {
            "File" => new VmIntrinsicType { Namespace = "System.IO", Name = "File", IsValue = false },
            "WebClient" => new VmIntrinsicType { Namespace = "System.Net", Name = "WebClient", IsValue = false },
            _ => throw new ArgumentException($"未知の I/O ファサード型: {name}"),
        };
        if (!_intrinsicTypes.TryAdd(type.FullName, type))
            throw new InvalidOperationException($"intrinsic 型 {type.FullName} は既に登録されています。");
    }

    /// <summary>ファサード型を名前で取得 (無ければ null)。</summary>
    public VmIntrinsicType? FindIntrinsicType(string fullName) =>
        _intrinsicTypes.GetValueOrDefault(fullName);

    /// <summary>intrinsic ファサード型を登録する (VM 起動時のみ。実行中の呼び出しは不可)。</summary>
    public void RegisterIntrinsicType(VmIntrinsicType type) {
        if (!_intrinsicTypes.TryAdd(type.FullName, type))
            throw new InvalidOperationException($"intrinsic 型 {type.FullName} は既に登録されています。");
    }

    // ---- TypeDef ロード ----

    /// <summary>TypeDef rid をロードする (ロード済みならキャッシュを返す)。</summary>
    public VmClassType GetTypeDef(int typeDefRid) {
        if (_typeDefs.TryGetValue(typeDefRid, out var cached))
            return cached;

        var (ns, name) = _image.GetTypeDefName(typeDefRid);
        var flags = _image.Tables.GetCell(TableKind.TypeDef, typeDefRid, 0);
        var type = new VmClassType(flags) {
            Image = _image,
            TypeDefRid = typeDefRid,
            Namespace = ns,
            Name = name,
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
            var signature = SignatureDecoder.DecodeFieldSignature(
                _image.GetBlob(_image.Tables.GetRowIndex(TableKind.Field, rid, 2)).ToArray());
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
            var signature = SignatureDecoder.DecodeMethodSignature(
                _image.GetMethodSignature(rid).ToArray());
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
            type.SetBaseType(FindIntrinsicType("System.Object")); // <module> 型等 (Extends = null)
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

    // ---- SigType → VmType 解決 ----

    /// <summary>署名中の型を VmType に解決し、ジェネリックパラメータを context の実引数で置換する。</summary>
    public VmType ResolveToken(SigType sigType, GenericContext? context) =>
        GenericSubstitutor.Substitute(ResolveToken(sigType), context);

    /// <summary>署名中の型を VmType に解決する。ジェネリックパラメータは置換コンテキストがないため
    /// VmGenericParameterType をそのまま返す (インタプリタは ResolveToken(sigType, context) を使う)。</summary>
    public VmType ResolveToken(SigType sigType) => sigType.Kind switch {
        SigKind.TypeToken => ResolveTypeDefOrRefToken(sigType.Token),
        SigKind.GenericInst => new VmConstructedType {
            Definition = ResolveTypeDefOrRefToken(sigType.Token),
            TypeArguments = sigType.Args!.Select(ResolveToken).ToArray(),
        },
        SigKind.SzArray => new VmArrayType { ElementType = ResolveToken(sigType.Inner!) },
        SigKind.Array => new VmMultiDimArrayType { ElementType = ResolveToken(sigType.Inner!), Rank = sigType.Rank },
        SigKind.ByRef => new VmByRefType { ElementType = ResolveToken(sigType.Inner!) },
        SigKind.Pointer => new VmByRefType { ElementType = ResolveToken(sigType.Inner!) }, // ポインタは ByRef と同様に扱う (未対応扱い)
        SigKind.GenericVar => new VmGenericParameterType { IsMethodParameter = false, Number = sigType.VarNumber },
        SigKind.GenericMethodVar => new VmGenericParameterType { IsMethodParameter = true, Number = sigType.VarNumber },
        SigKind.Boolean => RequiredIntrinsic("System.Boolean"),
        SigKind.Char => RequiredIntrinsic("System.Char"),
        SigKind.I1 => RequiredIntrinsic("System.SByte"),
        SigKind.U1 => RequiredIntrinsic("System.Byte"),
        SigKind.I2 => RequiredIntrinsic("System.Int16"),
        SigKind.U2 => RequiredIntrinsic("System.UInt16"),
        SigKind.I4 => RequiredIntrinsic("System.Int32"),
        SigKind.U4 => RequiredIntrinsic("System.UInt32"),
        SigKind.I8 => RequiredIntrinsic("System.Int64"),
        SigKind.U8 => RequiredIntrinsic("System.UInt64"),
        SigKind.R4 => RequiredIntrinsic("System.Single"),
        SigKind.R8 => RequiredIntrinsic("System.Double"),
        SigKind.I => RequiredIntrinsic("System.IntPtr"),
        SigKind.U => RequiredIntrinsic("System.UIntPtr"),
        SigKind.String => RequiredIntrinsic("System.String"),
        SigKind.Object => RequiredIntrinsic("System.Object"),
        SigKind.TypedByRef => RequiredIntrinsic("System.TypedReference"),
        SigKind.Void => RequiredIntrinsic("System.Void"),
        _ => throw new NotSupportedException($"未対応の署名型です: {sigType}"),
    };

    private VmType ResolveTypeDefOrRefToken(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        return table switch {
            TableKind.TypeDef => GetTypeDef(rid),
            TableKind.TypeRef => ResolveTypeRef(rid),
            TableKind.TypeSpec => ResolveTypeSpec(rid),
            _ => throw new BadImageFormatException($"型トークンのテーブル 0x{table:X} が不正です。"),
        };
    }

    /// <summary>TypeSpec rid を解決する (ジェネリックパラメータは context の実引数で置換)。</summary>
    public VmType ResolveTypeSpec(int typeSpecRid, GenericContext? context = null) {
        var blob = _image.GetBlob(_image.Tables.GetRowIndex(TableKind.TypeSpec, typeSpecRid, 0));
        var sigType = SignatureDecoder.DecodeTypeSpecSignature(blob.ToArray()).Type;
        return GenericSubstitutor.Substitute(ResolveToken(sigType), context);
    }

    private VmType ResolveTypeRef(int typeRefRid) {
        var (ns, name, scope) = _image.GetTypeRefName(typeRefRid);
        var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        if (_intrinsicTypes.TryGetValue(fullName, out var intrinsic))
            return intrinsic;

        // ネスト型 TypeRef (ResolutionScope = TypeRef) は包含チェーンごとゲスト TypeDef と照合する
        var (scopeTable, scopeRid) = scope;
        if (scopeTable == TableKind.TypeRef && ResolveNestedTypeRef(typeRefRid) is { } nested)
            return nested;

        throw new NotSupportedException(
            $"アセンブリ外の型参照 '{fullName}' は未対応です (アセンブリ参照の解決は今後のフェーズで実装)。");
    }

    /// <summary>ネスト型 TypeRef を TypeDef のネスト構造 (NestedClass) と名前照合で解決する。</summary>
    private VmClassType? ResolveNestedTypeRef(int typeRefRid) {
        // TypeRef のスコープチェーン (内側 → 外側) を名前として集める
        var names = new List<string>();
        var current = typeRefRid;
        while (true) {
            var (_, nestedName, nestedScope) = _image.GetTypeRefName(current);
            names.Insert(0, nestedName);
            var (scopeTable, scopeRid) = nestedScope;
            if (scopeTable != TableKind.TypeRef)
                break;
            current = scopeRid;
        }

        var owner = FindTypeByName(names[0]);
        if (owner is null)
            return null;
        for (var i = 1; i < names.Count; i++) {
            VmClassType? child = null;
            var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
            for (var rid = 1; rid <= typeDefs; rid++) {
                if (_image.GetEnclosingTypeDef(rid) != owner.TypeDefRid)
                    continue;
                var (_, childName) = _image.GetTypeDefName(rid);
                if (childName == names[i]) {
                    child = GetTypeDef(rid);
                    break;
                }
            }
            if (child is null)
                return null;
            owner = child;
        }
        return owner;
    }

    // ---- 検索 API ----

    /// <summary>MethodDef トークン (0x06xxxxxx) を VmMethod に解決する。所有 TypeDef を MethodList 範囲走査で特定する。</summary>
    public VmMethod? GetMethodByToken(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.MethodDef)
            return null;
        if (_methods.TryGetValue(rid, out var cached))
            return cached;

        // 所有型が未ロードでも解決できるよう TypeDef 全体から MethodList 範囲で探す
        var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var (fieldStart, fieldEnd, methodStart, methodEnd) = MemberRanges(typeRid);
            if (rid < methodStart || rid >= methodEnd)
                continue;
            return GetTypeDef(typeRid).Methods.Single(m => m.MethodDefRid == rid);
        }
        return null;
    }

    /// <summary>TypeDef のフルネームで型を検索する (ネスト型は "Outer/Inner"、独自表記は "Outer.Inner")。</summary>
    public VmClassType? FindTypeByFullName(string fullName) {
        var count = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var rid = 1; rid <= count; rid++) {
            var (ns, name) = _image.GetTypeDefName(rid);
            var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            // ネスト型は包含チェーンを辿って FullName を構成する
            var enclosing = _image.GetEnclosingTypeDef(rid);
            while (enclosing != 0) {
                var (ens, ename) = _image.GetTypeDefName(enclosing);
                full = (string.IsNullOrEmpty(ens) ? ename : ens + "." + ename) + "/" + full;
                enclosing = _image.GetEnclosingTypeDef(enclosing);
            }
            if (full == fullName || full.Replace('/', '+') == fullName)
                return GetTypeDef(rid);
        }
        return null;
    }

    /// <summary>名前 (末尾要素) で TypeDef を検索する。</summary>
    public VmClassType? FindTypeByName(string name) {
        var count = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var rid = 1; rid <= count; rid++) {
            var (_, typeName) = _image.GetTypeDefName(rid);
            if (typeName == name)
                return GetTypeDef(rid);
        }
        return null;
    }

    /// <summary>Field トークン (0x04xxxxxx) を VmField に解決する。</summary>
    public VmField? GetFieldByToken(uint token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.Field)
            return null;
        if (_fields.TryGetValue(rid, out var cached))
            return cached;
        var typeDefs = _image.Tables.GetRowCount(TableKind.TypeDef);
        for (var typeRid = 1; typeRid <= typeDefs; typeRid++) {
            var (fieldStart, fieldEnd, _, _) = MemberRanges(typeRid);
            if (rid < fieldStart || rid >= fieldEnd)
                continue;
            return GetTypeDef(typeRid).Fields.Single(f => f.FieldRid == rid);
        }
        return null;
    }

    /// <summary>MemberRef rid のフィールド名 (ldsfld/stsfld の MemberRef 形式用)。</summary>
    public string GetMemberRefFieldName(int memberRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));

    /// <summary>MemberRef rid から親型名 (TypeRef 親のフルネーム。TypeRef 以外は null) を得る。</summary>
    public string? GetMemberRefParentTypeName(int memberRefRid) {
        var parent = _image.Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
        if (parent.Table != TableKind.TypeRef)
            return null;
        var (ns, name, _) = _image.GetTypeRefName(parent.Rid);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>MemberRef rid のメソッド名。</summary>
    public string GetMemberRefName(int memberRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));

    private VmIntrinsicType RequiredIntrinsic(string fullName) =>
        _intrinsicTypes[fullName];
}
