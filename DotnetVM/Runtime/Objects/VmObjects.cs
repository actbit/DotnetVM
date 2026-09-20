using System.Runtime.CompilerServices;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Objects;

/// <summary>VM ヒープ上のオブジェクトの基底。世代別 GC 拡張用の Generation を初段から保持する。</summary>
public abstract class VmObject {
    /// <summary>GC 世代 (0 = 新世代)。世代別戦略 (M6 以降) で利用。</summary>
    public byte Generation { get; internal set; }

    public abstract VmType Type { get; }
}

/// <summary>クラスのインスタンス。フィールドは宣言順 (基底型フィールドが先頭) のスロット配列。</summary>
public sealed class VmClassInstance : VmObject {
    private readonly VmClassType _classType;
    public readonly StackSlot[] Fields;

    public VmClassInstance(VmClassType classType, StackSlot[] fields) {
        _classType = classType;
        Fields = fields;
    }

    public override VmType Type => _classType;
    public VmClassType ClassType => _classType;
}

/// <summary>ボックス化された値 (ヒープオブジェクト)。Fields[0] に値を保持 (構造体は展開済みフィールド列)。</summary>
public sealed class VmBoxedValue : VmObject {
    private readonly VmType _valueType;
    public readonly StackSlot[] Fields;

    public VmBoxedValue(VmType valueType, StackSlot[] fields) {
        _valueType = valueType;
        Fields = fields;
    }

    public override VmType Type => _valueType;
}

/// <summary>
/// 例外ファサード型のインスタンス (VM 内部例外の合成 / `new System.NullReferenceException()` 等)。
/// ゲストクラスが Exception 派生の例外は VmClassInstance として生成され、メッセージは
/// IntrinsicContext の例外メッセージ表で保持する。こちらはファサード型そのものの実体。
/// </summary>
public sealed class VmExceptionObject : VmObject {
    private readonly VmType _exceptionType;

    public VmExceptionObject(VmType exceptionType, VmString? message) {
        _exceptionType = exceptionType;
        Message = message;
    }

    public override VmType Type => _exceptionType;
    public VmType ExceptionType => _exceptionType;
    public VmString? Message { get; internal set; }
}

/// <summary>配列。要素はゼロ初期化済みスロット配列。</summary>
public sealed class VmArray : VmObject {
    private readonly VmArrayType _arrayType;
    public readonly StackSlot[] Elements;

    public VmArray(VmArrayType arrayType, StackSlot[] elements) {
        _arrayType = arrayType;
        Elements = elements;
    }

    public override VmType Type => _arrayType;
    public VmArrayType ArrayType => _arrayType;
    public int Length => Elements.Length;
}

/// <summary>
/// 値型の値 (スタック/ローカル/フィールド上の実体。ヒープ識別性なし、代入時にコピー)。
/// </summary>
public sealed class VmStructValue {
    public VmType StructType { get; }
    public readonly StackSlot[] Fields;

    public VmStructValue(VmType structType, StackSlot[] fields) {
        StructType = structType;
        Fields = fields;
    }

    /// <summary>値型コピー意味論: フィールドを深コピーする (ネストした構造体は再帰コピー、参照は共有)。</summary>
    public VmStructValue Clone() {
        var copy = new StackSlot[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
            copy[i] = Fields[i].ObjectValue is VmStructValue nested ? StackSlot.OfValueType(nested.Clone()) : Fields[i];
        return new VmStructValue(StructType, copy);
    }
}

/// <summary>
/// オブジェクトモデルの共通処理: インスタンス/静的フィールドのレイアウト (基底型フィールドが先頭)、
/// 型ごとの既定値生成。
/// </summary>
public static class ObjectModel {
    private static readonly ConditionalWeakTable<VmType, Dictionary<VmField, int>> Layouts = new();
    private static readonly ConditionalWeakTable<VmType, StackSlot[]> StaticStorage = new();

    /// <summary>インスタンスフィールドのスロット配置 (基底型のフィールドが先頭、同一型内は宣言順)。</summary>
    public static Dictionary<VmField, int> GetLayout(VmClassType type) {
        if (Layouts.TryGetValue(type, out var cached))
            return cached;
        var layout = new Dictionary<VmField, int>();
        var index = 0;
        for (var t = (VmType?)type; t is VmClassType cls; t = cls.BaseType) {
            foreach (var field in cls.Fields) {
                if (!field.IsStatic && !field.IsLiteral)
                    layout[field] = index++;
            }
        }
        Layouts.Add(type, layout);
        return layout;
    }

    /// <summary>インスタンスフィールド既定値のストレージを生成する。</summary>
    public static StackSlot[] CreateInstanceStorage(VmClassType type, TypeLoader loader) {
        var layout = GetLayout(type);
        var fields = new StackSlot[layout.Values.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(field.FieldType!, loader);
        return fields;
    }

    /// <summary>静的フィールドのストレージ (型ごとに 1 つ)。</summary>
    public static StackSlot[] GetOrCreateStaticStorage(VmClassType type, TypeLoader loader) {
        if (StaticStorage.TryGetValue(type, out var existing))
            return existing;
        var storage = new StackSlot[type.Fields.Count(f => f.IsStatic && !f.IsLiteral)];
        var index = 0;
        foreach (var field in type.Fields)
            if (field.IsStatic && !field.IsLiteral)
                storage[index++] = DefaultForType(field.FieldType!, loader);
        StaticStorage.Add(type, storage);
        return storage;
    }

    public static int StaticFieldIndex(VmClassType type, VmField field) {
        var index = 0;
        foreach (var f in type.Fields) {
            if (!f.IsStatic || f.IsLiteral)
                continue;
            if (f == field)
                return index;
            index++;
        }
        throw new InvalidOperationException($"静的フィールド {field} が見つかりません。");
    }

    /// <summary>型に対するゼロ既定値。</summary>
    public static StackSlot DefaultForType(VmType? type, TypeLoader loader) {
        if (type is null)
            return StackSlot.Null;
        if (type.IsValueType && type is not VmIntrinsicType)
            return StackSlot.OfValueType(DefaultStruct((VmClassType)type, loader));
        if (type is VmIntrinsicType intrinsic) {
            return intrinsic.FullName switch {
                "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte"
                    or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => StackSlot.OfInt32(0),
                "System.Int64" or "System.UInt64" => StackSlot.OfInt64(0),
                "System.Single" or "System.Double" => StackSlot.OfFloat(0),
                "System.IntPtr" or "System.UIntPtr" => StackSlot.OfNativeInt(0),
                _ => StackSlot.Null, // String/Object 等の参照型
            };
        }
        return StackSlot.Null;
    }

    /// <summary>構造体の既定値 (全フィールドを再帰的にゼロ初期化)。</summary>
    public static VmStructValue DefaultStruct(VmClassType structType, TypeLoader loader) {
        var layout = GetLayout(structType);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(field.FieldType!, loader);
        return new VmStructValue(structType, fields);
    }

    /// <summary>アロケーションサイズの概算 (バイト)。クォータ計上用。</summary>
    public static long EstimateSize(VmObject obj) => obj switch {
        VmArray array => 24 + 16L * array.Elements.Length,
        VmClassInstance instance => 24 + 16L * instance.Fields.Length,
        VmBoxedValue boxed => 24 + 16L * boxed.Fields.Length,
        _ => 24,
    };
}
