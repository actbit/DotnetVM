using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class ObjectEngine {
    // ---- フィールドアクセス ----

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
    public VmField ResolveFieldToken(int token, GenericContext? context = null,
        IReadOnlyDictionary<uint, object>? dynamicTokens = null) {
        if (dynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmField dynamicField)
            return dynamicField;
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

    /// <summary>レシーバ (インスタンス/ByRef/構造体値) からフィールドスロットへの書き込み可能参照を得る。
    /// 構築ジェネリック型は定義に解いてレイアウトを取る (VmClassInstance/ボックス/構造体の全経路)。</summary>
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
                var bt = DefinitionOf(boxed.Type);
                return bt is not null
                    ? new VmByRef(boxed.Fields, GetInstanceFieldIndex(bt, field))
                    : new VmByRef(boxed.Fields, 0);
            }
            case StackKind.ByRef when objSlot.ObjectValue is VmByRef outer: {
                // 構造体ローカル/引数へのフィールド書込 (ldloca → ldfld/stfld)
                var target = outer.Read();
                if (target.Kind == StackKind.ValueType && target.ObjectValue is VmStructValue sv &&
                    DefinitionOf(sv.StructType) is VmClassType st)
                    return new VmByRef(sv.Fields, GetInstanceFieldIndex(st, field), outer.IsReadOnly);
                if (target.ObjectValue is VmClassInstance nested)
                    return new VmByRef(nested.Fields, GetInstanceFieldIndex(nested.ClassType, field), outer.IsReadOnly);
                break;
            }
            case StackKind.ValueType when objSlot.ObjectValue is VmStructValue direct &&
                DefinitionOf(direct.StructType) is VmClassType dt:
                return new VmByRef(direct.Fields, GetInstanceFieldIndex(dt, field));
        }
        throw new InvalidOperationException($"フィールド {field.DeclaringType.FullName}::{field.Name} のレシーバが不正です: {SlotOps.Describe(objSlot)}");
    }

    /// <summary>値型の定義型 (構築型は定義に解く。ファサード等の非クラス型は null)。</summary>
    private static VmClassType? DefinitionOf(VmType type) => type switch {
        VmClassType cls => cls,
        VmConstructedType constructed => constructed.Definition as VmClassType,
        _ => null,
    };

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
    public VmByRef StaticFieldLocation(int token, GenericContext? context = null,
        IReadOnlyDictionary<uint, object>? dynamicTokens = null) {
        if (dynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmField dynamicField) {
            if (!dynamicField.IsStatic)
                throw new UnhandledGuestException("System.FieldAccessException",
                    $"{dynamicField.DeclaringType.FullName}::{dynamicField.Name} は静的フィールドではありません。");
            return StaticFieldLocationForField(token, dynamicField, context);
        }
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
                // 構築型の静的フィールド。CLR と同じく実引数ごとに別ストレージを持ち、
                // .cctor も実引数ごとに 1 回走る。キーは定義参照 + 型引数参照列 (FullName 文字列不使用)。
                var constructed = ResolveConstructedParent(parent.Rid, context);
                var definition = (VmClassType)constructed.Definition;
                EnsureConstructedInitialized(constructed);
                var storage = _objects.GetOrCreateStaticStorage(constructed.FullName, definition, _loader,
                    new GenericContext { ClassArgs = constructed.TypeArguments }, _unifiedStaticStorage,
                    constructed.TypeArguments);
                return new VmByRef(storage, ObjectModel.StaticFieldIndex(definition,
                    ResolveFieldToken(token, context)));
            }
        }
        var field = ResolveFieldToken(token, context, dynamicTokens);
        return StaticFieldLocationForField(token, field, context);
    }

    private VmByRef StaticFieldLocationForField(int token, VmField field, GenericContext? context) {
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
        // ジェネリック定義の静的フィールドを Field トークン直接で触る場合
        // (.cctor / get_Default 等の自型内アクセス)、呼出元文脈の型引数が個数一致すれば
        // 構築型として扱う (定義共有ストレージ + null 文脈 .cctor では T ごとに別物に
        // ならないため。型外からの Field 直接参照は稀で、個数不一致時は従来動作に残す)。
        // 定義 .cctor より先に判定し、null 文脈での誤初期化を避ける
        if (owner.GenericParamCount > 0 && context?.ClassArgs is { } classArgs &&
            classArgs.Length == owner.GenericParamCount) {
            var constructed = new VmConstructedType { Definition = owner, TypeArguments = classArgs };
            EnsureConstructedInitialized(constructed);
            var constructedStorage = _objects.GetOrCreateStaticStorage(constructed.FullName, owner, _loader,
                new GenericContext { ClassArgs = classArgs }, _unifiedStaticStorage, classArgs);
            return new VmByRef(constructedStorage, ObjectModel.StaticFieldIndex(owner, field));
        }
        EnsureInitialized(owner);
        var staticStorage = _objects.GetOrCreateStaticStorage(owner.FullName, owner, _loader, null, _unifiedStaticStorage, null);
        return new VmByRef(staticStorage, ObjectModel.StaticFieldIndex(owner, field));
    }
}
