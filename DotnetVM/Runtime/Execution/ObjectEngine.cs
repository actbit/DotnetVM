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

    /// <summary>intrinsic 静的フィールドのストレージ一覧 (GC ルート源として Interpreter が登録する)。</summary>
    public IEnumerable<StackSlot[]> IntrinsicStaticFields => _intrinsicStaticFields.Values;

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
                if (!_intrinsics.TryGetStaticField(typeName, fieldName, out var value))
                    throw new OperationNotAllowedException(
                        $"intrinsic 型 {typeName} の静的フィールド {fieldName} は未登録です。");
                if (!_intrinsicStaticFields.TryGetValue(token, out var storage)) {
                    storage = [value(_intrinsicContext)];
                    _intrinsicStaticFields[token] = storage;
                }
                return new VmByRef(storage, 0);
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
            throw new NotSupportedException(
                $"newobj の MemberRef 0x{token:X8} ({typeName ?? "?"}::{name}) を解決できません。");
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

        // .ctor は宣言型 (継承チェーン上の基底ジェネリック定義も含む) から探す
        VmMethod? ctor = null;
        for (VmType? t = definition; t is not null && ctor is null; t = t.BaseType) {
            if (t is VmConstructedType ct)
                t = ct.Definition;
            if (t is not VmClassType cls)
                break;
            ctor = cls.Methods.FirstOrDefault(m =>
                m.Name == ctorName && !m.IsStatic && m.Signature.ParamTypes.Length == paramCount);
        }
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
