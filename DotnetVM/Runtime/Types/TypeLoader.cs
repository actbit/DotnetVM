using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// アセンブリの TypeDef を VmType に遅延ロードする。
/// 1 つの TypeDef は必ず 1 つの VmClassType インスタンスに対応 (キャッシュで保証)。
/// アセンブリに存在しない型のうち VM が面を提供するもの (System.Object 等) は
/// ファサード型 (VmIntrinsicType) として合成する。
/// 他アセンブリの型参照 (TypeRef の ResolutionScope = AssemblyRef) は
/// <see cref="Context"/> (VmAssemblyContext) 経由で依存アセンブリに解決する。
/// </summary>
public sealed class TypeLoader {
    /// <summary>GenericParam.Flags の変性ビット (ECMA-335 II.22.20)。</summary>
    internal const uint Covariant = 0x0001;
    internal const uint Contravariant = 0x0002;

    private readonly AssemblyImage _image;

    /// <summary>ロード対象のアセンブリ。</summary>
    public AssemblyImage Image => _image;

    /// <summary>所属する多アセンブリ コンテキスト (依存解決に使用。未所属 = null)。</summary>
    public VmAssemblyContext? Context { get; internal set; }

    private readonly Dictionary<int, VmClassType> _typeDefs = [];
    private readonly Dictionary<string, VmIntrinsicType> _intrinsicTypes = [];
    private readonly Dictionary<int, VmMethod> _methods = [];
    private readonly Dictionary<int, VmField> _fields = [];
    private readonly List<VmClassType> _pendingCompletion = [];
    private readonly HashSet<int> _completedTypeDefs = [];
    /// <summary>完全名 → TypeDef rid の索引 (遅延構築。CoreLib 規模の画像で線形走査を避ける)。</summary>
    private Dictionary<string, int>? _typeDefByFullName;
    /// <summary>型統合辞書 (完全名 → 実型)。Context 配下の画像から解決できた実 TypeDef をキャッシュする
    /// (参照アセンブリ⇔CoreLib のユニフィケーション。否定はキャッシュしない — 遅延ロードで新画像が増えうる)。</summary>
    private readonly Dictionary<string, VmType> _unifiedTypes = [];
    /// <summary>仮想/インターフェースディスパッチ表の構築担当 (遅延生成)。</summary>
    private DispatchMapBuilder? _dispatchBuilder;

    /// <summary>この loader が trusted System.Private.CoreLib 実装画像か (LoadHostCoreLib の
    /// 取得した loader 参照に対して VM 構築時に確定する。タスク 2 hardening:
    /// ファイル名照合でなく参照同一性による trusted marker)。</summary>
    public bool IsTrustedCoreLib { get; internal set; }

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
            "NotImplementedException", "RankException",
        })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false, Parent = systemException });
        // Activator.CreateInstance の失敗分類 (CLR の継承鎖どおり MemberAccess ← MissingMember ← MissingMethod)
        var memberAccess = new VmIntrinsicType { Namespace = "System", Name = "MemberAccessException", IsValue = false, Parent = systemException };
        Add(memberAccess);
        var missingMember = new VmIntrinsicType { Namespace = "System", Name = "MissingMemberException", IsValue = false, Parent = memberAccess };
        Add(missingMember);
        Add(new VmIntrinsicType { Namespace = "System", Name = "MissingMethodException", IsValue = false, Parent = missingMember });
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
        // 文字列補間 ($"...") がコンパイルされる DefaultInterpolatedStringHandler (ref struct) の
        // ファサード。本体は intrinsic 面 (AppendLiteral / AppendFormatted / ToStringAndClear) が担う。
        Add(new VmIntrinsicType {
            Namespace = "System.Runtime.CompilerServices",
            Name = "DefaultInterpolatedStringHandler", IsValue = true, Parent = valueType,
        });
        // 頻出 BCL 列挙型のファサード (署名上の TypeRef 解決に必要。値は i4 スロットとして扱う)
        foreach (var name in new[] { "StringSplitOptions", "StringComparison" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = true, Parent = valueType });

        // デリゲート機構のファサード。Delegate/MulticastDelegate は継承判定の根で、
        // Action/Func/Predicate 等はそれらの派生として合成する (newobj デリゲート生成と
        // callvirt Invoke のデリゲート呼出は Interpreter 側でこの継承関係を判定に使う)
        var @delegate = new VmIntrinsicType { Namespace = "System", Name = "Delegate", IsValue = false, Parent = @object };
        Add(@delegate);
        var multicastDelegate = new VmIntrinsicType { Namespace = "System", Name = "MulticastDelegate", IsValue = false, Parent = @delegate };
        Add(multicastDelegate);
        foreach (var arity in Enumerable.Range(0, 17))
            Add(new VmIntrinsicType { Namespace = "System", Name = arity == 0 ? "Action" : $"Action`{arity}", IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());
        foreach (var arity in Enumerable.Range(1, 16))
            Add(new VmIntrinsicType { Namespace = "System", Name = $"Func`{arity}", IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());
        foreach (var (name, ns, arity) in new[] {
            ("Predicate`1", "System", 1),
            ("Comparison`1", "System", 1),
            ("EventHandler", "System", 0),
            ("EventHandler`1", "System", 1),
            ("Converter`2", "System", 2),
            ("ResolveEventHandler", "System.Reflection", 0),
        })
            Add(new VmIntrinsicType { Namespace = ns, Name = name, IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());

        // TypedReference / varargs 周辺の特殊値型 (__makeref / arglist の署名解決に必要)
        Add(new VmIntrinsicType { Namespace = "System", Name = "TypedReference", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgIterator", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "RuntimeArgumentHandle", IsValue = true, Parent = valueType });
        // native int のファサード (ldftn の戻り型 / sizeof 等)。既存登録があれば尊重する
        _intrinsicTypes.TryAdd("System.IntPtr", new VmIntrinsicType { Namespace = "System", Name = "IntPtr", IsValue = true, Parent = valueType });
        _intrinsicTypes.TryAdd("System.UIntPtr", new VmIntrinsicType { Namespace = "System", Name = "UIntPtr", IsValue = true, Parent = valueType });

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

    // ---- SigType → VmType 解決 ----

    /// <summary>署名中の型を VmType に解決し、ジェネリックパラメータを context の実引数で置換する。</summary>
    public VmType ResolveToken(SigType sigType, GenericContext? context) =>
        GenericSubstitutor.Substitute(ResolveToken(sigType), context);

    /// <summary>署名中の型を VmType に解決する。ジェネリックパラメータは置換コンテキストがないため
    /// VmGenericParameterType をそのまま返す (インタプリタは ResolveToken(sigType, context) を使う)。
    /// GenericInst の解決時にもネスト上限を強制する (TypeSpec 連鎖の再帰対策)。</summary>
    public VmType ResolveToken(SigType sigType) => ResolveTokenWithDepth(sigType, 0);

    private VmType ResolveTokenWithDepth(SigType sigType, int genericDepth) => sigType.Kind switch {
        SigKind.TypeToken => ResolveTypeDefOrRefToken(sigType.Token),
        SigKind.GenericInst => ResolveGenericInst(sigType, genericDepth),
        SigKind.SzArray => ArrayWithBase(new VmArrayType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) }),
        SigKind.Array => ArrayWithBase(new VmMultiDimArrayType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth), Rank = sigType.Rank }),
        SigKind.ByRef => new VmByRefType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) },
        SigKind.Pointer => new VmByRefType { ElementType = ResolveTokenWithDepth(sigType.Inner!, genericDepth) }, // ポインタは ByRef と同様に扱う (未対応扱い)
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

    private VmType ResolveGenericInst(SigType sigType, int genericDepth) {
        var max = _image.Limits?.MaxGenericNestingDepth ?? 64;
        if (genericDepth + 1 > max)
            throw new BadImageFormatException(
                $"ジェネリックのネスト深さ {genericDepth + 1:N0} が上限 {max:N0} を超えています。");
        return new VmConstructedType {
            Definition = ResolveTypeDefOrRefToken(sigType.Token),
            TypeArguments = sigType.Args!.Select(a => ResolveTokenWithDepth(a, genericDepth + 1)).ToArray(),
        };
    }

    /// <summary>配列型に System.Array (実型またはファサード) を基底として接続する。</summary>
    private VmType ArrayWithBase(VmType arrayType) {
        var baseType = ResolveWellKnownType("System.Array");
        switch (arrayType) {
            case VmArrayType szArray:
                szArray.SetBaseType(baseType);
                break;
            case VmMultiDimArrayType multiDim:
                multiDim.SetBaseType(baseType);
                break;
        }
        return arrayType;
    }

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

    /// <summary>TypeSpec rid を解決する (ジェネリックパラメータは context の実引数で置換)。
    /// GenericInst のネスト深度は署名デコード時と解決時の双方で強制する。</summary>
    public VmType ResolveTypeSpec(int typeSpecRid, GenericContext? context = null) {
        var blob = _image.GetBlob(_image.Tables.GetRowIndex(TableKind.TypeSpec, typeSpecRid, 0));
        var sigType = SignatureDecoder.DecodeTypeSpecSignature(blob.ToArray(),
            _image.Limits?.MaxSignatureDepth ?? 64,
            _image.Limits?.MaxGenericNestingDepth ?? 64).Type;
        CheckGenericNestingDepth(sigType, 0);
        return GenericSubstitutor.Substitute(ResolveToken(sigType), context);
    }

    /// <summary>解決済み SigType の GenericInst ネストが上限を超えていないか検査する
    /// (デコード時は blob 再帰、解決時は TypeSpec 連鎖の双方があり得るため)。</summary>
    private void CheckGenericNestingDepth(SigType type, int depth) {
        var max = _image.Limits?.MaxGenericNestingDepth ?? 64;
        if (type.Kind == SigKind.GenericInst) {
            depth++;
            if (depth > max)
                throw new BadImageFormatException(
                    $"ジェネリックのネスト深さ {depth:N0} が上限 {max:N0} を超えています。");
            foreach (var arg in type.Args!)
                CheckGenericNestingDepth(arg, depth);
        } else if (type.Inner is not null) {
            CheckGenericNestingDepth(type.Inner, depth);
        } else if (type.Args is not null) {
            foreach (var arg in type.Args)
                CheckGenericNestingDepth(arg, depth);
        }
    }

    /// <summary>TypeRef rid を解決する。解決順:
    /// ① Context 登録済みの実アセンブリ (AssemblyRef スコープの依存解決 / Module スコープの自己参照 /
    ///    TypeRef スコープのネスト型) → ② intrinsic ファサード → ③ ファサード無し時のネスト/自己解決 →
    /// ④ fail-closed (AssemblyDependencyNotFoundException / NotSupportedException)。</summary>
    private VmType ResolveTypeRef(int typeRefRid) {
        var (ns, name, scope) = _image.GetTypeRefName(typeRefRid);
        var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        var (scopeTable, scopeRid) = scope;

        // ① 実アセンブリからの解決 (Context に登録された画像が優先。CoreLib ロード時は
        //    ファサードより実 TypeDef が勝つ)
        var context = Context;
        if (context is not null) {
            var real = scopeTable switch {
                TableKind.Module => (VmType?)FindTypeByFullName(fullName),
                TableKind.AssemblyRef => ResolveViaAssemblyRef(fullName, scopeRid),
                TableKind.TypeRef => ResolveNestedTypeRef(typeRefRid),
                _ => null,
            };
            if (real is not null)
                return real;
        }

        // ② intrinsic ファサード (CoreLib 未ロード時の既定面)
        if (_intrinsicTypes.TryGetValue(fullName, out var intrinsic))
            return intrinsic;

        // ③ Context 未所属時の従来解決 (ネスト型 TypeRef は包含チェーンごと TypeDef と照合)
        if (context is null && scopeTable == TableKind.TypeRef &&
            ResolveNestedTypeRef(typeRefRid) is { } nested)
            return nested;

        // ④ fail-closed
        if (scopeTable == TableKind.AssemblyRef) {
            var refIdentity = _image.GetAssemblyRefIdentity(scopeRid);
            if (context?.TryResolveAssembly(refIdentity, _image) is null)
                throw new AssemblyDependencyNotFoundException(refIdentity.Name,
                    $"参照アセンブリ '{refIdentity}' (型 '{fullName}' の解決に必要) がロード済みでも" +
                    $"同一ディレクトリ ({LoaderDependencyDirectoryHint()}) にも見つかりません。");
        }
        throw new NotSupportedException($"型参照 '{fullName}' を解決できません (スコープ {scopeTable})。");
    }

    /// <summary>依存アセンブリ同一ディレクトリ探索のヒント文言。Stream ロード (SourcePath 無し)
    /// の場合は「同一ディレクトリ探索も行われない (明示 resolver でのロードが必須)」と示す。
    /// host current directory への暗黙フォールバック (SourcePath ?? ".") を廃止した。</summary>
    private string LoaderDependencyDirectoryHint() =>
        _image.SourcePath is { } path
            ? $"同一ディレクトリ ({Path.GetDirectoryName(Path.GetFullPath(path))})"
            : "同一ディレクトリ (Stream ロードのため探索なし。依存は明示 resolver / LoadAssembly(path) でのロードが必要)";

    /// <summary>AssemblyRef スコープの TypeRef を解決する。優先順:
    /// ①自分自身への参照は自己画像 (identity 照合) → ②依存アセンブリを identity で確定し、
    /// その loader 内の TypeDef を解決する (ExportedType forwarder 経由を含む) →
    /// ③trusted assembly (IsTrustedCoreLib) に限定した FullName 統合 (BCL ref→実装の解決)。
    /// global な FullName 探索を先に行わない (同名別 identity への誤結合防止)。</summary>
    private VmType? ResolveViaAssemblyRef(string fullName, int assemblyRefRid) {
        var refIdentity = _image.GetAssemblyRefIdentity(assemblyRefRid);
        // ① 自分自身への参照は自己画像で解決する (単一画像ロード時の自己参照 TypeRef)。
        //    identity (Name + 公開鍵トークン) で照合する
        if (refIdentity.Matches(_image.Identity))
            return FindTypeByFullName(fullName) ?? ResolveForwarderIn(_image, fullName);
        // ② 依存アセンブリを identity で確定し、その loader 内で解決する
        var target = Context!.TryResolveAssembly(refIdentity, _image);
        if (target is not null) {
            if (target.FindTypeByFullName(fullName) is { } direct)
                return direct;
            // TypeForwarder: global 探索ではなく対象画像の ExportedType で転送先を辿る
            if (ResolveForwarderIn(target.Image, fullName) is { } forwarded)
                return forwarded;
        }
        // ③ trusted assembly に限定した FullName 統合 (BCL の参照アセンブリ→実装の解決)。
        // 例: ゲストが System.Runtime (ref) を参照し、実装が trusted System.Private.CoreLib に
        // ある場合。非 trusted なゲスト画像同士では統合しない (fake 混入防止)。
        return TryResolveTrustedUnifiedType(fullName);
    }

    /// <summary>画像の ExportedType テーブルに FullName の転送宣言があれば転送先を解決する
    /// (TypeForwardedTo 相当。global FullName 探索はしない)。</summary>
    private VmType? ResolveForwarderIn(AssemblyImage image, string fullName) {
        var count = image.Tables.GetRowCount(TableKind.ExportedType);
        for (var rid = 1; rid <= count; rid++) {
            var typeName = image.GetString(image.Tables.GetRowIndex(TableKind.ExportedType, rid, 2));
            var typeNs = image.GetString(image.Tables.GetRowIndex(TableKind.ExportedType, rid, 3));
            var candidate = string.IsNullOrEmpty(typeNs) ? typeName : typeNs + "." + typeName;
            if (!string.Equals(candidate, fullName, StringComparison.Ordinal))
                continue;
            var impl = image.Tables.DecodeCoded(TableKind.ExportedType, rid, 4, CodedIndexKind.Implementation);
            if (impl.Table == TableKind.AssemblyRef) {
                var fwdIdentity = image.GetAssemblyRefIdentity(impl.Rid);
                var fwdTarget = Context?.TryResolveAssembly(fwdIdentity, image);
                if (fwdTarget?.FindTypeByFullName(fullName) is { } fwdType)
                    return fwdType;
                // 転送先が未ロードでも trusted 統合で拾える場合はそちらへ (BCL forwarder の救済)
                if (TryResolveTrustedUnifiedType(fullName) is { } trusted)
                    return trusted;
            }
        }
        return null;
    }

    /// <summary>TypeRef rid を解決した結果を返す (intrinsic ファサードまたは実 VmClassType)。
    /// 呼出/オブジェクト生成/フィールド解決が MemberRef の TypeRef 親を実体に解決するのに使う。</summary>
    public VmType ResolveTypeRefType(int typeRefRid) => ResolveTypeRef(typeRefRid);

    /// <summary>AssemblyRef rid の参照先アセンブリの単純名。</summary>
    public string GetAssemblyRefName(int assemblyRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.AssemblyRef, assemblyRefRid, 6));

    /// <summary>ネスト型 TypeRef を TypeDef のネスト構造 (NestedClass) と名前照合で解決する。
    /// 終端スコープが AssemblyRef の場合は identity で対象画像を確定してから (自画像の
    /// 場合は自己解決)、その画像内で包含チェーンを辿る。BCL 参照 (System.Runtime 等) は
    /// trusted 実装画像への統合で救済する (global 探索ではなく identity 起点)。</summary>
    private VmClassType? ResolveNestedTypeRef(int typeRefRid) {
        // TypeRef のスコープチェーン (内側 → 外側) を名前として集める
        var names = new List<string>();
        var current = typeRefRid;
        var (terminalTable, terminalRid) = (TableKind.Module, 0);
        while (true) {
            var (_, nestedName, nestedScope) = _image.GetTypeRefName(current);
            names.Insert(0, nestedName);
            (terminalTable, terminalRid) = nestedScope;
            if (terminalTable != TableKind.TypeRef)
                break;
            current = terminalRid;
        }

        // 終端スコープの画像を確定する
        TypeLoader targetLoader = this;
        if (terminalTable == TableKind.AssemblyRef) {
            var refIdentity = _image.GetAssemblyRefIdentity(terminalRid);
            if (refIdentity.Matches(_image.Identity)) {
                targetLoader = this;
            } else if (Context?.TryResolveAssembly(refIdentity, _image) is { } resolved) {
                targetLoader = resolved;
            } else {
                // BCL 参照アセンブリ (System.Runtime 等) のネスト型は trusted 実装で救済する
                var unified = TryResolveTrustedUnifiedType(names[0]);
                if (unified is VmClassType unifiedClass)
                    return WalkNested(unifiedClass, names, 1);
                return null;
            }
        } else if (terminalTable != TableKind.Module) {
            return null;
        }

        var owner = targetLoader.FindTypeByFullName(names[0]) ?? targetLoader.FindTypeByName(names[0]);
        if (owner is null) {
            // 自画像に無く BCL 統合で拾える場合 (参照アセンブリ経由の BCL ネスト型)
            if (targetLoader == this && TryResolveTrustedUnifiedType(names[0]) is VmClassType unifiedClass)
                return WalkNested(unifiedClass, names, 1);
            return null;
        }
        return WalkNested(owner, names, 1);
    }

    /// <summary>包含型からネスト名列を辿る (対象画像の NestedClass で解決する)。</summary>
    private VmClassType? WalkNested(VmClassType owner, List<string> names, int start) {
        var ownerLoader = owner.Loader ?? this;
        for (var i = start; i < names.Count; i++) {
            VmClassType? child = null;
            var image = ownerLoader.Image;
            var typeDefs = image.Tables.GetRowCount(TableKind.TypeDef);
            for (var rid = 1; rid <= typeDefs; rid++) {
                if (image.GetEnclosingTypeDef(rid) != owner.TypeDefRid)
                    continue;
                var (_, childName) = image.GetTypeDefName(rid);
                if (childName == names[i]) {
                    child = ownerLoader.GetTypeDef(rid);
                    break;
                }
            }
            if (child is null)
                return null;
            owner = child;
            ownerLoader = owner.Loader ?? ownerLoader;
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

    /// <summary>TypeDef のフルネームで型を検索する (ネスト型は "Outer/Inner"、独自表記は "Outer.Inner")。
    /// 最初の呼び出しで完全名索引を 1 回だけ構築する (CoreLib 規模の画像での線形走査を避ける)。</summary>
    public VmClassType? FindTypeByFullName(string fullName) {
        if (_typeDefByFullName is null) {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
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
                index.TryAdd(full, rid);
                index.TryAdd(full.Replace('/', '+'), rid);
            }
            _typeDefByFullName = index;
        }
        return _typeDefByFullName.TryGetValue(fullName, out var ridMatch) ? GetTypeDef(ridMatch) : null;
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

    /// <summary>MemberRef rid から親型名 (TypeRef 親のフルネーム。TypeRef 以外は null) を得る。
    /// ネスト型親 (Interop+BCrypt 等) は包含チェーンを辿って CLR 規約の '+' 連結完全名にする
    /// (単純末尾名ではバインド照合できないため)。</summary>
    public string? GetMemberRefParentTypeName(int memberRefRid) {
        var parent = _image.Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
        if (parent.Table != TableKind.TypeRef)
            return null;
        // 内側→外側へ辿り、名前を集めると同時に名前空間を持つ最外要素を探す
        // (ネスト型自体の Namespace 列は空のため)
        var names = new List<string>();
        string? ns = null;
        var current = parent.Rid;
        while (true) {
            var (innerNs, innerName, scope) = _image.GetTypeRefName(current);
            names.Insert(0, innerName);
            if (!string.IsNullOrEmpty(innerNs))
                ns = innerNs;
            if (scope.Table != TableKind.TypeRef)
                break;
            current = scope.Rid;
        }
        var full = string.Join("+", names);
        return string.IsNullOrEmpty(ns) ? full : ns + "." + full;
    }

    /// <summary>MemberRef rid のメソッド名。</summary>
    public string GetMemberRefName(int memberRefRid) =>
        _image.GetString(_image.Tables.GetRowIndex(TableKind.MemberRef, memberRefRid, 1));

    /// <summary>SigKind 直引きの既知型 (プリミティブ/Object/String 等) を解決する。
    /// trusted 実型 (CoreLib) があれば優先し、無ければ intrinsic ファサード。
    /// 非 trusted なゲスト画像の同名型には統合しない (fake 混入防止)。</summary>
    private VmType RequiredIntrinsic(string fullName) =>
        TryResolveTrustedUnifiedType(fullName) ?? _intrinsicTypes[fullName];

    // ---- 仮想/インターフェースディスパッチ表 (C3) ----

    /// <summary>本画像のディスパッチ表ビルダ (遅延生成)。</summary>
    internal DispatchMapBuilder DispatchBuilder => _dispatchBuilder ??= new DispatchMapBuilder(this);

    /// <summary>型のディスパッチ表 (VTable + InterfaceMap) を取得する (遅延構築・キャッシュ)。
    /// 他アセンブリの型はそのローダのビルダに委譲する (索引は画像ごとに分かれる)。</summary>
    internal DispatchMaps EnsureDispatchMaps(VmClassType type) {
        var owner = type.Loader;
        return owner is null || ReferenceEquals(owner, this) ? DispatchBuilder.EnsureMaps(type) : owner.EnsureDispatchMaps(type);
    }

    /// <summary>スロットキー用にパラメータ型列 (SigType) を VmType 列へ解決する。
    /// 1 つでも解決できない型があれば null (呼出側はパラメータ数照合にフォールバック)。</summary>
    internal VmType[]? TryResolveSlotParams(SigType[] paramTypes) {
        var result = new VmType[paramTypes.Length];
        for (var i = 0; i < paramTypes.Length; i++) {
            try {
                result[i] = ResolveToken(paramTypes[i]);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
                or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
                return null;
            }
        }
        return result;
    }

    /// <summary>完全名を Context 配下の全画像 (ロード順 = CoreLib 優先) の実 TypeDef に解決する。
    /// 見つからなければ null (ファサード等のフォールバックは呼び出し側)。実体化に失敗する型
    /// (未対応の署角度を含む画像固有の型) はその画像をスキップする。
    /// 注意: 非 trusted 画像を含む global 統合は新規コードでは使わないこと。
    /// 新規は TryResolveTrustedUnifiedType (trusted 限定) を使う。</summary>
    public VmType? TryResolveUnifiedType(string fullName) {
        if (_unifiedTypes.TryGetValue(fullName, out var cached))
            return cached;
        if (Context is not { } context)
            return null;
        foreach (var loader in context.Loaders) {
            VmClassType? real;
            try {
                real = loader.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is VmExecutionException or NotSupportedException
                or BadImageFormatException or InvalidOperationException) {
                continue; // この画像では実体化できない型 (未対応面)。ファサードに委ねる
            }
            if (real is not null) {
                _unifiedTypes[fullName] = real;
                return real;
            }
        }
        return null;
    }

    /// <summary>完全名を trusted 画像 (IsTrustedCoreLib) の実 TypeDef に限定して解決する。
    /// BCL の参照アセンブリ→実装の統合はここに限定する (非 trusted なゲスト画像同士は
    /// 統合しない)。プリミティブ等の既知型や ResolveViaAssemblyRef の最終救済に使う。</summary>
    public VmType? TryResolveTrustedUnifiedType(string fullName) {
        if (_unifiedTypes.TryGetValue(fullName, out var cached) && cached is VmClassType cachedCls &&
            cachedCls.Loader?.IsTrustedCoreLib == true)
            return cached;
        if (Context is not { } context)
            return null;
        foreach (var loader in context.Loaders) {
            if (!loader.IsTrustedCoreLib)
                continue;
            VmClassType? real;
            try {
                real = loader.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is VmExecutionException or NotSupportedException
                or BadImageFormatException or InvalidOperationException) {
                continue;
            }
            if (real is not null) {
                _unifiedTypes[fullName] = real;
                return real;
            }
        }
        return null;
    }

    /// <summary>既知型 (プリミティブ/String/Object/Array 等) を実型またはファサードで解決する
    /// (ホスト境界のボックス化や配列基底型の接続に使う。trusted 実型に限定)。</summary>
    public VmType ResolveWellKnownType(string fullName) =>
        TryResolveTrustedUnifiedType(fullName) ?? _intrinsicTypes[fullName];
}
