using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>オブジェクトモデル面の実行サービス: newobj による実体化 (ファサード/デリゲート/
/// 構造体/クラス/例外)、フィールド・静的ストレージの位置解決、型初期化子 (.cctor) の起動。
/// .cctor・.ctor の起動は <see cref="IGuestInvoker"/>、intrinsic .ctor の呼出は
/// <see cref="IExecutionGate"/> 経由で行い、IL 実行と同一の制約 (クォータ/セーフポイント/
/// ヒープ計上) を適用する。</summary>
internal sealed class ObjectEngine(
    InterpreterServices services,
    IExecutionGate gate,
    IGuestInvoker invoker) {
    private readonly TypeLoader _loader = services.Loader;
    private readonly IntrinsicRegistry _intrinsics = services.Intrinsics;
    private readonly VmHeap _heap = services.Heap;
    private readonly IntrinsicContext _intrinsicContext = services.IntrinsicContext;
    private readonly ObjectModel _objects = services.Objects;

    private readonly HashSet<VmType> _initializedTypes = [];
    // 構築ジェネリック型の .cctor 起動済み集合 (CLR と同じく実引数ごとに 1 回)
    private readonly HashSet<string> _initializedConstructedTypes = [];
    /// <summary>intrinsic 型の静的フィールドのストレージ (トークンごとに 1 スロット。例: String.Empty)。GC ルート源。</summary>
    private readonly Dictionary<int, StackSlot[]> _intrinsicStaticFields = [];
    /// <summary>静的 FieldRVA データフィールドのアドレス (トークンごとに 1 つ。例: Char.Latin1CharInfo の
    /// &lt;PrivateImplementationDetails&gt; 初期化データ)。GC グラフ源 (Interpreter が到達可能性に使う)。</summary>
    private readonly Dictionary<int, VmNativePointer> _rvaFieldAddresses = [];

    /// <summary>intrinsic 静的フィールドのストレージ一覧 (GC ルート源として Interpreter が登録する)。</summary>
    public IEnumerable<StackSlot[]> IntrinsicStaticFields => _intrinsicStaticFields.Values;

    /// <summary>intrinsic 静的フィールド / FieldRVA データアドレスの実体一覧 (GC グラフ源)。</summary>
    public IEnumerable<VmNativePointer> RvaFieldAddresses => _rvaFieldAddresses.Values;

    /// <summary>静的 FieldRVA データフィールド (&lt;PrivateImplementationDetails&gt; の静的配列初期化データ)
    /// へのアドレス解決 (ldsflda 用)。CoreLib は静的テーブル (例: Char.Latin1CharInfo の byte[256]) を
    /// 「ldsflda + newobj ReadOnlySpan(void*, int)」で参照するため、画像の初期データをバイト実体
    /// (画像からコピーして確保。確保はヒープ会計外のメタデータ派生読み取り専用データ) として公開する。
    /// FieldRVA を持たないフィールドトークンは null (通常の静的ストレージ経路へ)。</summary>
    public StackSlot? TryGetStaticFieldRvaAddress(int token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.Field)
            return null;
        var rva = _loader.Image.GetFieldRva(rid);
        if (rva == 0)
            return null;
        if (!_rvaFieldAddresses.TryGetValue(token, out var pointer)) {
            pointer = new VmNativePointer {
                Memory = new VmLocallocMemory { Bytes = _loader.Image.GetRvaDataToEnd(rva).ToArray() },
                ByteOffset = 0,
            };
            _rvaFieldAddresses[token] = pointer;
        }
        return StackSlot.OfObject(pointer);
    }

    // ---- 型トークン解決 ----

    public VmType ResolveTypeToken(int token, GenericContext? context = null) =>
        _loader.ResolveToken(new SigType(SigKind.TypeToken, Token: (uint)token), context);

    /// <summary>MemberRef の TypeSpec 親を構築型として解決する。VAR/MVAR を含む場合は context で置換する。</summary>
    public VmConstructedType ResolveConstructedParent(int typeSpecRid, GenericContext? context) =>
        _loader.ResolveTypeSpec(typeSpecRid, context) as VmConstructedType
            ?? throw new BadImageFormatException($"TypeSpec 0x02{typeSpecRid:X6} は構築ジェネリック型ではありません。");

    /// <summary>intrinsic を派生ファサード型から基底連鎖まで辿って解決する (継承面のフォールバック)。</summary>
    public bool TryGetIntrinsicThroughHierarchy(string typeName, string name, int arity, bool hasThis, out IntrinsicImpl impl) {
        for (VmType? t = _loader.FindIntrinsicType(typeName); t is not null; t = t.BaseType) {
            if (_intrinsics.TryGet(new IntrinsicKey(t.FullName, name, arity, hasThis), out impl!))
                return true;
        }
        impl = null!;
        return false;
    }

    // ---- フィールドアクセス ----

    /// <summary>CoreLib String の実体フィールド名 → バッファ内バイトオフセット。</summary>
    private static bool TryGetStringFieldOffset(string fieldName, out int byteOffset) {
        // CoreLib は _stringLength/_firstChar、可変長文字列構文の別画像では m_ 接頭辞の可能性も許容
        switch (fieldName) {
            case "_stringLength" or "m_stringLength":
                byteOffset = VmString.HeaderByteCount - sizeof(int); // ヘッダ先頭 = 0
                return true;
            case "_firstChar" or "m_firstChar":
                byteOffset = VmString.CharDataByteOffset;
                return true;
            default:
                byteOffset = 0;
                return false;
        }
    }

    /// <summary>ldflda 用のアドレス解決。VnString (可変 char バッファ) はバイト実体への
    /// unmanaged ポインタを返し (CoreLib IL が Unsafe.Add / Buffer.Memmove に渡す形)、
    /// それ以外は ByRef を返す。</summary>
    public StackSlot FieldAddress(in StackSlot objSlot, VmField field) {
        if (objSlot.Kind == StackKind.Object && objSlot.ObjectValue is VmString str &&
            TryGetStringFieldOffset(field.Name, out var offset))
            return StackSlot.OfObject(new VmNativePointer {
                Memory = str.PointerMemory,
                ByteOffset = offset,
            });
        return StackSlot.OfByRef(FieldLocation(objSlot, field));
    }

    /// <summary>stfld の文字列実体への書込。VnString レシーバでなければ false (通常経路へ)。</summary>
    public bool TryStoreStringField(in StackSlot objSlot, VmField field, in StackSlot value) {
        if (objSlot.Kind != StackKind.Object || objSlot.ObjectValue is not VmString str ||
            !TryGetStringFieldOffset(field.Name, out _))
            return false;
        if (field.Name is "_stringLength" or "m_stringLength")
            str.WriteStringLength(value.AsInt32);
        else
            str.WriteFirstChar((char)value.AsInt32);
        return true;
    }

    /// <summary>フィールドトークン (Field / MemberRef) を解決する。TypeSpec 親 (構築型のフィールド) も解決する。</summary>
    public VmField ResolveFieldToken(int token, GenericContext? context = null) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        switch (table) {
            case TableKind.Field:
                return _loader.GetFieldByToken((uint)token)
                    ?? throw new BadImageFormatException($"Field トークン 0x{token:X8} を解決できません。");
            case TableKind.MemberRef: {
                var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                var fieldName = _loader.GetMemberRefFieldName(rid);
                if (parent.Table == TableKind.TypeDef) {
                    var owner = _loader.GetTypeDef(parent.Rid);
                    return owner.Fields.FirstOrDefault(f => f.Name == fieldName)
                        ?? throw new BadImageFormatException(
                            $"MemberRef 0x{token:X8} の解決先フィールド {owner.FullName}::{fieldName} が見つかりません。");
                }
                if (parent.Table == TableKind.TypeSpec) {
                    // 構築型のフィールド参照 (例: ldfld !0 class List`1<int32>::_items)
                    // 親 TypeSpec が呼出元のパラメータ (!0) を含む場合は context で置換する
                    var constructed = ResolveConstructedParent(parent.Rid, context);
                    var definition = (VmClassType)constructed.Definition;
                    return definition.Fields.FirstOrDefault(f => f.Name == fieldName)
                        ?? throw new BadImageFormatException(
                            $"MemberRef 0x{token:X8} の解決先フィールド {definition.FullName}::{fieldName} が見つかりません。");
                }
                if (parent.Table == TableKind.TypeRef) {
                    // 依存アセンブリの型 / ネスト型のフィールド参照 (intrinsic ファサードの
                    // 静的フィールドは StaticFieldLocation 側で処理する)
                    if (_loader.ResolveTypeRefType(parent.Rid) is VmClassType ownerClass) {
                        for (VmType? t = ownerClass; t is not null;) {
                            if (t is VmConstructedType ct)
                                t = ct.Definition;
                            if (t is not VmClassType cls)
                                break;
                            var field = cls.Fields.FirstOrDefault(f => f.Name == fieldName);
                            if (field is not null)
                                return field;
                            t = cls.BaseType;
                        }
                        throw new BadImageFormatException(
                            $"MemberRef 0x{token:X8} の解決先フィールド {ownerClass.FullName}::{fieldName} が見つかりません。");
                    }
                    throw new NotSupportedException(
                        $"intrinsic ファサード型のフィールド参照 (MemberRef 0x{token:X8}) はインスタンス面として未対応です。");
                }
                throw new NotSupportedException($"フィールド MemberRef 親テーブル {parent.Table} は未対応です。");
            }
            default:
                throw new BadImageFormatException($"フィールドトークン 0x{token:X8} のテーブル 0x{(int)table:X2} が不正です。");
        }
    }

    /// <summary>レシーバ (インスタンス/ByRef/構造体値) からフィールドスロットへの書き込み可能参照を得る。</summary>
    public VmByRef FieldLocation(in StackSlot objSlot, VmField field) {
        switch (objSlot.Kind) {
            case StackKind.Object when objSlot.ObjectValue is null:
                throw new UnhandledGuestException("System.NullReferenceException", null);
            case StackKind.Object when objSlot.ObjectValue is VmString str &&
                TryGetStringFieldOffset(field.Name, out var stringFieldOffset):
                // CoreLib String の実体フィールド。バイト実体が真実源のため、読み出しのたびに
                // 合成スロットへ同期してから返す (直近のポインタ書込が反映される)
                str.SyncFieldSlotsFromBytes();
                return new VmByRef(str.FieldSlots, stringFieldOffset == 0 ? 0 : 1);
            case StackKind.Object when objSlot.ObjectValue is VmClassInstance instance:
                return new VmByRef(instance.Fields, GetInstanceFieldIndex(instance.ClassType, field));
            case StackKind.Object when objSlot.ObjectValue is VmBoxedValue boxed: {
                // ボックス化ジェネリック構造体 (構築型) は定義型に解いてレイアウトを取る
                VmClassType? bt = boxed.Type switch {
                    VmClassType cls => cls,
                    VmConstructedType constructed => constructed.Definition as VmClassType,
                    _ => null,
                };
                return bt is not null
                    ? new VmByRef(boxed.Fields, GetInstanceFieldIndex(bt, field))
                    : new VmByRef(boxed.Fields, 0);
            }
            case StackKind.ByRef when objSlot.ObjectValue is VmByRef outer: {
                // 構造体ローカル/引数へのフィールド書込 (ldloca → ldfld/stfld)
                var target = outer.Slot;
                if (target.Kind == StackKind.ValueType && target.ObjectValue is VmStructValue sv &&
                    sv.StructType is VmClassType st)
                    return new VmByRef(sv.Fields, GetInstanceFieldIndex(st, field));
                if (target.ObjectValue is VmClassInstance nested)
                    return new VmByRef(nested.Fields, GetInstanceFieldIndex(nested.ClassType, field));
                break;
            }
            case StackKind.ValueType when objSlot.ObjectValue is VmStructValue direct &&
                direct.StructType is VmClassType dt:
                return new VmByRef(direct.Fields, GetInstanceFieldIndex(dt, field));
        }
        throw new InvalidOperationException($"フィールド {field.DeclaringType.FullName}::{field.Name} のレシーバが不正です: {SlotOps.Describe(objSlot)}");
    }

    private int GetInstanceFieldIndex(VmClassType type, VmField field) {
        var layout = _objects.GetLayout(type);
        if (layout.TryGetValue(field, out var index))
            return index;
        // 継承チェーン上の基底型で宣言されたフィールド (GetLayout は既に基底を含むが、
        // MemberRef 経由で別 VmField インスタンスになる場合は名前でフォールバック)
        foreach (var (candidate, idx) in layout) {
            if (candidate.Name == field.Name && candidate.DeclaringType.FullName == field.DeclaringType.FullName)
                return idx;
        }
        throw new BadImageFormatException($"フィールド {field.DeclaringType.FullName}::{field.Name} が {type.FullName} のレイアウトにありません。");
    }

    /// <summary>静的フィールドの位置を解決する (.cctor 起動を含む)。intrinsic 型 (TypeRef 親) の静的フィールドも解決する。</summary>
    public VmByRef StaticFieldLocation(int token, GenericContext? context = null) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeRef) {
                var typeName = _loader.GetMemberRefParentTypeName(rid)!;
                var fieldName = _loader.GetMemberRefFieldName(rid);
                if (_intrinsics.TryGetStaticField(typeName, fieldName, out var value)) {
                    if (!_intrinsicStaticFields.TryGetValue(token, out var storage)) {
                        storage = [value(_intrinsicContext)];
                        _intrinsicStaticFields[token] = storage;
                    }
                    return new VmByRef(storage, 0);
                }
                // intrinsic 静的フィールド未登録の TypeRef 親は実 TypeDef に解決できる場合、
                // 共通の静的ストレージ経路 (直下の ResolveFieldToken フロー) へ流す
                if (_loader.ResolveTypeRefType(parent.Rid) is not VmClassType)
                    throw new OperationNotAllowedException(
                        $"intrinsic 型 {typeName} の静的フィールド {fieldName} は未登録です。");
            }
            if (parent.Table == TableKind.TypeSpec) {
                // 構築型の静的フィールド。CLR と同じく値型実引数ごとに別ストレージを持ち、
                // .cctor も実引数ごとに 1 回走る (参照型実引数でもストレージは共有しない)
                var constructed = ResolveConstructedParent(parent.Rid, context);
                var definition = (VmClassType)constructed.Definition;
                EnsureConstructedInitialized(constructed);
                var storage = _objects.GetOrCreateStaticStorage(constructed.FullName, definition, _loader,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
                return new VmByRef(storage, ObjectModel.StaticFieldIndex(definition,
                    ResolveFieldToken(token, context)));
            }
        }
        var field = ResolveFieldToken(token);
        var owner = (VmClassType)field.DeclaringType;
        // 本家 CoreLib の IL 内からの ldsfld / stsfld (Field token 直接)。実 CLR では
        // ランタイムが値を設定する静的フィールド (String.Empty 等) は IL に初期化子が
        // なく static storage の初期値は null になるため、intrinsic 静的フィールド登録
        // (RegisterStaticField) があればそちらを優先する。MemberRef 経由のアクセスと
        // 同一ストレージ (token 単位キャッシュ) を使うため両経路の参照は一致する
        if (_intrinsics.TryGetStaticField(owner.FullName, field.Name, out var intrinsicValue)) {
            if (!_intrinsicStaticFields.TryGetValue(token, out var storage)) {
                storage = [intrinsicValue(_intrinsicContext)];
                _intrinsicStaticFields[token] = storage;
            }
            return new VmByRef(storage, 0);
        }
        EnsureInitialized(owner);
        var staticStorage = _objects.GetOrCreateStaticStorage(owner.FullName, owner, _loader);
        return new VmByRef(staticStorage, ObjectModel.StaticFieldIndex(owner, field));
    }

    // ---- 型初期化 (.cctor) ----

    /// <summary>型初期化子 (.cctor) の起動規約: 静的フィールド初回アクセス/newobj 前に 1 回だけ実行。</summary>
    public void EnsureInitialized(VmClassType type) {
        if (!_initializedTypes.Add(type))
            return;
        var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor?.Body is not null)
            invoker.Invoke(cctor, [], null);
    }

    /// <summary>構築ジェネリック型の .cctor 起動 (CLR と同じく型実引数ごとに 1 回)。</summary>
    public void EnsureConstructedInitialized(VmConstructedType type) {
        if (!_initializedConstructedTypes.Add(type.FullName))
            return;
        var definition = (VmClassType)type.Definition;
        var cctor = definition.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor?.Body is not null)
            invoker.Invoke(cctor, [], new GenericContext { ClassArgs = type.TypeArguments });
    }

    // ---- オブジェクト生成 (newobj) ----

    /// <summary>デリゲート生成 (newobj instance void D::.ctor(object, native int))。
    /// 関数ポインタは ldftn/ldvirtftn の VmMethodPointer、既存デリゲートの複製 (マルチキャスト含む) も可。</summary>
    private VmDelegate NewDelegate(VmType delegateType, StackSlot targetSlot, StackSlot pointerSlot) {
        var invocations = pointerSlot.ObjectValue switch {
            VmMethodPointer pointer => new[] { new DelegateInvocation(targetSlot, pointer.Target) },
            VmDelegate source => source.CopyInvocations(),
            _ => throw new UnhandledGuestException("System.ArgumentException",
                "デリゲート生成の第 2 引数が関数ポインタ (ldftn/ldvirtftn の結果) ではありません。"),
        };
        var @delegate = _heap.Allocate(new VmDelegate { DeclaredType = delegateType });
        foreach (var invocation in invocations)
            @delegate.AddInvocation(invocation);
        return @delegate;
    }

    /// <summary>.ctor を基底連鎖 (ジェネリック定義へ解いて) から探す。署名精度 (スロットキー一致) を
    /// 優先し、キー解決不可の候補は従来どおり名前+引数個数の最初の一致にフォールバックする
    /// (ReadOnlySpan の (in T&amp;) と (T[]) 等の同引数個数オーバーロード誤解決の解消)。</summary>
    private VmMethod? FindCtorThroughChain(VmClassType type, string name, int paramCount, SigType[]? paramTypes) {
        var queryKey = paramTypes is not null && _loader.TryResolveSlotParams(paramTypes) is { } parameters
            ? VmSlotKeys.Of(name, parameters) : null;
        VmMethod? byParamCount = null;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType ct)
                t = ct.Definition;
            if (t is not VmClassType cls)
                break;
            foreach (var method in cls.Methods) {
                if (method.Name != name || method.IsStatic)
                    continue;
                if (queryKey is not null && method.SlotKey == queryKey)
                    return method; // 署名一致 (オーバーロード誤解決の解消)
                if (byParamCount is null && method.Signature.ParamTypes.Length == paramCount)
                    byParamCount = method;
            }
            t = cls.BaseType;
        }
        return byParamCount;
    }

    /// <summary>string::.ctor の構築面 (char[] / char[],int / char)。CLR と同じ確保点
    /// (FastAllocateString 相当の VmStringPool.Allocate) で確保し、char 列をバッファへ
    /// 書き込む。引数検査は CLR と同じ例外分類 (null 配列は ArgumentNullException、
    /// 範囲外は ArgumentOutOfRangeException)。</summary>
    private StackSlot NewStringFromCtor(int paramCount, InterpreterFrame caller) {
        var args = new StackSlot[paramCount];
        for (var i = paramCount; i >= 1; i--)
            args[i - 1] = caller.Stack.Pop();
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        var strings = _intrinsicContext.Strings;
        switch (paramCount) {
            case 1: {
                // string(char[] value)
                var array = RequireCharArray(args[0]);
                var result = strings.Allocate(array.Length);
                CopyChars(result, 0, array, 0, array.Length);
                return StackSlot.OfObject(result);
            }
            case 2: {
                // string(char c, int count)
                var c = (char)args[0].Int64Value;
                var count = (int)args[1].Int64Value;
                if (count < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var result = strings.Allocate(count);
                for (var i = 0; i < count; i++)
                    WriteChar(result, i, c);
                return StackSlot.OfObject(result);
            }
            case 3: {
                // string(char[] value, int startIndex, int length)
                var array = RequireCharArray(args[0]);
                var startIndex = (int)args[1].Int64Value;
                var length = (int)args[2].Int64Value;
                if ((uint)startIndex > (uint)array.Length || length < 0 || (uint)length > (uint)(array.Length - startIndex))
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var result = strings.Allocate(length);
                CopyChars(result, 0, array, startIndex, length);
                return StackSlot.OfObject(result);
            }
            default:
                throw new NotSupportedException($"string::.ctor (引数 {paramCount} 個) は対応していません。");
        }
    }

    private static VmArray RequireCharArray(StackSlot slot) =>
        slot.ObjectValue as VmArray
        ?? throw new UnhandledGuestException("System.ArgumentNullException", null);

    private static void CopyChars(VmString target, int targetIndex, VmArray source, int sourceIndex, int count) {
        for (var i = 0; i < count; i++)
            WriteChar(target, targetIndex + i, (char)source.Elements[sourceIndex + i].Int64Value);
    }

    private static void WriteChar(VmString target, int charIndex, char value) {
        var offset = VmString.CharDataByteOffset + charIndex * 2;
        target.Bytes[offset] = (byte)value;
        target.Bytes[offset + 1] = (byte)((ushort)value >> 8);
    }

    public StackSlot? NewObject(int token, InterpreterFrame caller) {
        VmMethod ctor;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MethodDef) {
            ctor = _loader.GetMethodByToken((uint)token)
                ?? throw new BadImageFormatException($"newobj トークン 0x{token:X8} を解決できません。");
        } else if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeSpec)
                return NewConstructedObject(token, rid, parent.Rid, caller);
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(rid).ToArray());
            var facadeParamCount = signature.ParamTypes.Length;
            var name = _loader.GetMemberRefName(rid);
            var typeName = _loader.GetMemberRefParentTypeName(rid);
            if (typeName is not null) {
                // string の構築面 (new string(char[]) / new string(char, int) 等):
                // FastAllocateString + char 列コピーと同じ確保点で VmString を生成する。
                // 置換面 (DotnetVM.CoreLib の NumberFormatting IL) が使うほか、ゲストの
                // 直接の new string(...) もここに着地する
                if (typeName == "System.String" && name == ".ctor")
                    return NewStringFromCtor(facadeParamCount, caller);
                var facadeType = _loader.FindIntrinsicType(typeName);
                if (facadeType is not null) {
                    // デリゲートファサード (Action/Func/Predicate 等) の newobj (object, native int)
                    if (TypeChecks.IsDelegateType(facadeType)) {
                        var pointerSlot = caller.Stack.Pop();
                        var targetSlot = caller.Stack.Pop();
                        return StackSlot.OfObject(NewDelegate(facadeType, targetSlot, pointerSlot));
                    }
                    var ctorArgs = new StackSlot[facadeParamCount + 1];
                    for (var i = facadeParamCount; i >= 1; i--)
                        ctorArgs[i] = caller.Stack.Pop();
                    // .ctor は基底ファサード連鎖からも解決する (Exception::.ctor を派生型で使う等)
                    var hasCtorIntrinsic = TryGetIntrinsicThroughHierarchy(
                        typeName, name, facadeParamCount + 1, hasThis: true, out var intrinsicCtor);
                    if (TypeChecks.IsExceptionFacade(facadeType)) {
                        // 例外ファサード型: VmExceptionObject として実体化 (throw 機構が依存)
                        var exception = _heap.Allocate(new VmExceptionObject(facadeType, null));
                        ctorArgs[0] = StackSlot.OfObject(exception);
                        if (hasCtorIntrinsic) {
                            gate.ConsumeInstruction();
                            gate.CheckSafepoint();
                            intrinsicCtor(_intrinsicContext, ctorArgs);
                        } else if (facadeParamCount != 0) {
                            throw new OperationNotAllowedException(
                                $"intrinsic {typeName}::{name} (引数 {facadeParamCount} 個) は未登録です。");
                        }
                        return StackSlot.OfObject(exception);
                    }
                    if (hasCtorIntrinsic && name == ".ctor") {
                        // 例外ファサード以外で .ctor intrinsic が登録された型 (例: System.Net.WebClient):
                        // VmIntrinsicInstance として実体化し、状態は intrinsic が State に保持する
                        var facadeInstance = _heap.Allocate(new VmIntrinsicInstance(facadeType));
                        ctorArgs[0] = StackSlot.OfObject(facadeInstance);
                        gate.ConsumeInstruction();
                        gate.CheckSafepoint();
                        intrinsicCtor(_intrinsicContext, ctorArgs);
                        return StackSlot.OfObject(facadeInstance);
                    }
                    // それ以外の intrinsic 型の実体化は BCL 不実装の面として拒否し続ける
                    throw new NotSupportedException(
                        $"intrinsic 型 {typeName} のインスタンス生成は未対応です (例外ファサード型または .ctor intrinsic 登録済み型のみ)。");
                }
            }
            // intrinsic ファサードでない TypeRef 親 (依存アセンブリの型 / ネスト型) は
            // 実 TypeDef の .ctor として解決し、MethodDef と共通の生成経路へ流す
            if (_loader.ResolveTypeRefType(parent.Rid) is VmClassType realClass) {
                ctor = FindCtorThroughChain(realClass, name, facadeParamCount, signature.ParamTypes)
                    ?? throw new BadImageFormatException(
                        $"newobj の MemberRef 0x{token:X8} の解決先 .ctor {realClass.FullName}::{name} (引数 {facadeParamCount} 個) が見つかりません。");
            } else {
                throw new NotSupportedException(
                    $"newobj の MemberRef 0x{token:X8} ({typeName ?? "?"}::{name}) を解決できません。");
            }
        } else {
            throw new BadImageFormatException($"newobj トークン 0x{token:X8} のテーブルが不正です。");
        }

        var owner = (VmClassType)ctor.DeclaringType;
        // ゲストのカスタム delegate 宣言の newobj (object target, native int method)
        if (TypeChecks.IsDelegateType(owner)) {
            var pointerSlot = caller.Stack.Pop();
            var targetSlot = caller.Stack.Pop();
            return StackSlot.OfObject(NewDelegate(owner, targetSlot, pointerSlot));
        }
        EnsureInitialized(owner);
        var paramCount = ctor.Signature.ParamTypes.Length;
        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (owner.IsValueType) {
            // 構造体の newobj: this (既定値) を作り、.ctor があればミューテートして this を返す
            var structValue = _objects.DefaultStruct(owner, _loader);
            args[0] = StackSlot.OfValueType(structValue);
            if (ctor.Body is not null)
                invoker.Invoke(ctor, args, null);
            return StackSlot.OfValueType(structValue);
        }

        var instance = _heap.Allocate(new VmClassInstance(owner, _objects.CreateInstanceStorage(owner, _loader)));
        args[0] = StackSlot.OfObject(instance);
        if (ctor.Body is not null)
            invoker.Invoke(ctor, args, null);
        return StackSlot.OfObject(instance);
    }

    /// <summary>
    /// 構築ジェネリック型 (TypeSpec 親の MemberRef) の newobj。
    /// 例: newobj instance void class List`1&lt;int32&gt;::.ctor() — 実引数を VmClassInstance/VmStructValue に
    /// 記録し、.ctor はその型引数の GenericContext で実行する (フィールドの !0 等が正しく解決される)。
    /// </summary>
    private StackSlot NewConstructedObject(int token, int memberRefRid, int typeSpecRid, InterpreterFrame caller) {
        var constructed = ResolveConstructedParent(typeSpecRid, caller.Context);
        var signature = SignatureDecoder.DecodeMethodSignature(
            _loader.Image.GetMemberRefSignature(memberRefRid).ToArray());
        var paramCount = signature.ParamTypes.Length;
        var ctorName = _loader.GetMemberRefName(memberRefRid);
        var context = new GenericContext { ClassArgs = constructed.TypeArguments };

        // 構築ジェネリック デリゲート (Func<int> 等) の newobj (object, native int)。
        // 定義は BCL ファサード (VmIntrinsicType) のこともあるため ClassType キャストより先に判定する
        if (TypeChecks.IsDelegateType(constructed.Definition)) {
            var pointerSlot = caller.Stack.Pop();
            var targetSlot = caller.Stack.Pop();
            return StackSlot.OfObject(NewDelegate(constructed, targetSlot, pointerSlot));
        }

        var definition = (VmClassType)constructed.Definition;

        // .ctor は宣言型 (継承チェーン上の基底ジェネリック定義も含む) から署名精度で探す
        // (MemberRef の !0 と定義側のスロットキーはどちらも VmGenericParameterType に正規化され照合可能)
        var ctor = FindCtorThroughChain(definition, ctorName, paramCount, signature.ParamTypes);
        EnsureInitialized(definition);
        if (ctor is null)
            throw new BadImageFormatException(
                $"構築型 {constructed.FullName} に引数 {paramCount} 個の .ctor ({ctorName}) が見つかりません。");

        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (definition.IsValueType) {
            var structValue = _objects.DefaultStruct(definition, _loader, context, constructed.TypeArguments);
            args[0] = StackSlot.OfValueType(structValue);
            if (ctor?.Body is not null)
                invoker.Invoke(ctor, args, context);
            return StackSlot.OfValueType(structValue);
        }

        var instance = _heap.Allocate(new VmClassInstance(definition,
            _objects.CreateInstanceStorage(definition, _loader, context), constructed.TypeArguments));
        args[0] = StackSlot.OfObject(instance);
        if (ctor?.Body is not null)
            invoker.Invoke(ctor, args, context);
        return StackSlot.OfObject(instance);
    }
}
