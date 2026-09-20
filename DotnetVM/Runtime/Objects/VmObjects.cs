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

    public VmClassInstance(VmClassType classType, StackSlot[] fields, VmType[]? typeArguments = null) {
        _classType = classType;
        Fields = fields;
        TypeArguments = typeArguments ?? [];
    }

    public override VmType Type => _classType;
    public VmClassType ClassType => _classType;

    /// <summary>ジェネリック型の実引数 (非ジェネリック型は空)。実行時型の構築型を再構成するのに使う。</summary>
    public VmType[] TypeArguments { get; }

    /// <summary>実行時型 (ジェネリック型なら構築型、それ以外は ClassType)。</summary>
    public VmType RuntimeType =>
        TypeArguments.Length > 0
            ? new VmConstructedType { Definition = _classType, TypeArguments = TypeArguments }
            : _classType;
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
/// intrinsic ファサード型のインスタンス (例: System.Net.WebClient)。例外ファサード以外で
/// .ctor intrinsic が登録された型の実体。状態は State スロット配列に保持し、
/// instance メソッドの intrinsic は this (= args[0]) 経由で読み書きする。
/// </summary>
public sealed class VmIntrinsicInstance : VmObject {
    public VmIntrinsicInstance(VmIntrinsicType instanceType, int stateSlots = 4) {
        ArgumentNullException.ThrowIfNull(instanceType);
        InstanceType = instanceType;
        State = new StackSlot[stateSlots];
    }

    public VmIntrinsicType InstanceType { get; }
    public readonly StackSlot[] State;

    public override VmType Type => InstanceType;
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

    /// <summary>ジェネリック構造体の実引数 (非ジェネリック型は空)。Clone 時に引き継ぐ。</summary>
    public VmType[] TypeArguments { get; }

    public VmStructValue(VmType structType, StackSlot[] fields, VmType[]? typeArguments = null) {
        StructType = structType;
        Fields = fields;
        TypeArguments = typeArguments ?? [];
    }

    /// <summary>値型コピー意味論: フィールドを深コピーする (ネストした構造体は再帰コピー、参照は共有)。</summary>
    public VmStructValue Clone() {
        var copy = new StackSlot[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
            copy[i] = Fields[i].ObjectValue is VmStructValue nested ? StackSlot.OfValueType(nested.Clone()) : Fields[i];
        return new VmStructValue(StructType, copy, TypeArguments);
    }

    /// <summary>実行時型 (ジェネリック構造体なら構築型、それ以外は StructType)。</summary>
    public VmType RuntimeType =>
        TypeArguments.Length > 0
            ? new VmConstructedType { Definition = StructType, TypeArguments = TypeArguments }
            : StructType;
}

/// <summary>
/// ldtoken Field の結果 (FieldRVA 初期データのハンドル)。System.RuntimeFieldHandle の VM 内表現で、
/// RuntimeHelpers::InitializeArray 専用。ハンドル自体はゲストから観測可能な状態を持たない。
/// </summary>
public sealed class VmFieldRvaData : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeFieldHandle", IsValue = true };

    public required ReadOnlyMemory<byte> Data { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>
/// ldtoken Type の結果ハンドル (System.RuntimeTypeHandle の VM 内表現)。
/// System.Type::GetTypeFromHandle intrinsic が System.Type ファサードの実体へ変換する。
/// </summary>
public sealed class VmTypeHandle : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeTypeHandle", IsValue = true };

    public required VmType Target { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>
/// ldtoken Method の結果ハンドル (System.RuntimeMethodHandle の VM 内表現)。
/// MethodBase::GetMethodFromHandle / GetCurrentMethod intrinsic が変換する。
/// </summary>
public sealed class VmMethodHandle : VmObject {
    public static readonly VmIntrinsicType HandleType =
        new() { Namespace = "System", Name = "RuntimeMethodHandle", IsValue = true };

    public required VmMethod Target { get; init; }

    public override VmType Type => HandleType;
}

/// <summary>
/// typeof(X) / Object.GetType() の結果 (System.Type ファサードの実体)。
/// CLR では内部型 System.RuntimeType のインスタンス。ゲストからは get_Name / get_FullName /
/// ToString / op_Equality intrinsic 面のみ観測できる。Target は VM 型系 (GC 管理外)。
/// </summary>
public sealed class VmRuntimeObject : VmObject {
    public static readonly VmIntrinsicType RuntimeTypeFacade =
        new() { Namespace = "System", Name = "RuntimeType", IsValue = false };

    public required VmType Target { get; init; }

    public override VmType Type => RuntimeTypeFacade;
}

/// <summary>MethodBase.GetCurrentMethod() 等の結果 (System.Reflection.MethodBase ファサードの実体)。</summary>
public sealed class VmRuntimeMethod : VmObject {
    public static readonly VmIntrinsicType MethodBaseFacade =
        new() { Namespace = "System.Reflection", Name = "RuntimeMethodInfo", IsValue = false };

    public required VmMethod Target { get; init; }

    public override VmType Type => MethodBaseFacade;
}

/// <summary>
/// intrinsic が VM スロット上に内部状態 (ホスト側バッファ等) を保持するための搬送体。
/// DefaultInterpolatedStringHandler 等の「構造体ファサードのローカルスロットに実体を置く」
/// intrinsic が使う。ゲストに渡されることはなく、Payload はホストメモリ (GC/クォータ計上外)。
/// </summary>
public sealed class VmIntrinsicCarrier : VmObject {
    public static readonly VmIntrinsicType CarrierType =
        new() { Namespace = "DotnetVM", Name = "IntrinsicCarrier", IsValue = false };

    public required object? Payload { get; set; }

    public override VmType Type => CarrierType;
}

/// <summary>
/// オブジェクトモデルの共通処理: インスタンス/静的フィールドのレイアウト (基底型フィールドが先頭)、
/// 型ごとの既定値生成。
/// </summary>
/// <remarks>
/// 互換性上の重要点: このクラスの状態は <strong>VM インスタンスごと</strong>に保持される
/// (かつて static だったため、複数 VM が静的フィールドを共有する分離バグだった)。
/// Interpreter が 1 つ所有する。
/// </remarks>
public sealed class ObjectModel {
    private readonly ConditionalWeakTable<VmType, Dictionary<VmField, int>> Layouts = new();
    // 静的ストレージは「正準型キー (構築型なら FullName)」で保持する。CLR と同じく
    // 構築ジェネリック型 (C<int> と C<string> 等) は静的フィールドを共有しない。
    private readonly Dictionary<string, StackSlot[]> StaticStorage = [];
    private readonly List<StackSlot[]> StaticStorageList = [];

    /// <summary>インスタンスフィールドのスロット配置 (基底型のフィールドが先頭、同一型内は宣言順)。
    /// 基底が構築ジェネリック型 (例: Sub`1 : Base`1&lt;!0&gt;) の場合は定義型に解いて収集する。</summary>
    public Dictionary<VmField, int> GetLayout(VmClassType type) {
        if (Layouts.TryGetValue(type, out var cached))
            return cached;
        var layout = new Dictionary<VmField, int>();
        var index = 0;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed) {
                t = constructed.Definition;
                continue;
            }
            if (t is not VmClassType cls)
                break;
            foreach (var field in cls.Fields) {
                if (!field.IsStatic && !field.IsLiteral)
                    layout[field] = index++;
            }
            t = cls.BaseType;
        }
        Layouts.Add(type, layout);
        return layout;
    }

    /// <summary>インスタンスフィールド既定値のストレージを生成する。</summary>
    public StackSlot[] CreateInstanceStorage(VmClassType type, TypeLoader loader) =>
        CreateInstanceStorage(type, loader, null);

    /// <summary>インスタンスフィールド既定値のストレージを生成する (ジェネリック型は型引数でフィールド型を解決)。</summary>
    public StackSlot[] CreateInstanceStorage(VmClassType type, TypeLoader loader, GenericContext? context) {
        var layout = GetLayout(type);
        var fields = new StackSlot[layout.Values.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        return fields;
    }

    /// <summary>静的フィールドのストレージ。storageKey は非構築型なら FullName、構築型なら
    /// 構築 FullName (CLR の「実引数ごとに別静的ストレージ」規約のため)。</summary>
    public StackSlot[] GetOrCreateStaticStorage(string storageKey, VmClassType type, TypeLoader loader,
        GenericContext? context = null) {
        if (StaticStorage.TryGetValue(storageKey, out var existing))
            return existing;
        var storage = new StackSlot[type.Fields.Count(f => f.IsStatic && !f.IsLiteral)];
        var index = 0;
        foreach (var field in type.Fields)
            if (field.IsStatic && !field.IsLiteral)
                storage[index++] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        StaticStorage[storageKey] = storage;
        StaticStorageList.Add(storage);
        return storage;
    }

    /// <summary>生成済みの全静的ストレージを列挙する (GC ルート源)。</summary>
    internal IEnumerable<StackSlot[]> EnumerateStaticStorage() => StaticStorageList;

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
    public StackSlot DefaultForType(VmType? type, TypeLoader loader) => DefaultForType(type, loader, null);

    /// <summary>型に対するゼロ既定値 (ジェネリックパラメータは context の実引数で置換してから判定)。</summary>
    public StackSlot DefaultForType(VmType? type, TypeLoader loader, GenericContext? context) {
        if (type is null)
            return StackSlot.Null;
        var substituted = GenericSubstitutor.Substitute(type, context);
        if (substituted.IsValueType && substituted is not VmIntrinsicType) {
            var structType = substituted switch {
                VmClassType cls => cls,
                VmConstructedType constructed => (VmClassType)constructed.Definition,
                _ => throw new InvalidOperationException($"値型 {substituted.FullName} の実体を生成できません。"),
            };
            var args = substituted is VmConstructedType ct ? ct.TypeArguments : null;
            // フィールド型の !n は構造体自身の実引数で解決する
            var fieldContext = args is null ? null : new GenericContext { ClassArgs = args };
            return StackSlot.OfValueType(DefaultStruct(structType, loader, fieldContext, args));
        }
        if (substituted is VmIntrinsicType intrinsic) {
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
    public VmStructValue DefaultStruct(VmClassType structType, TypeLoader loader) =>
        DefaultStruct(structType, loader, null, null);

    /// <summary>構造体の既定値。context はフィールド型のジェネリックパラメータ解決に使う
    /// (構造体自身の実引数は typeArguments として VmStructValue に記録する)。</summary>
    public VmStructValue DefaultStruct(VmClassType structType, TypeLoader loader,
        GenericContext? context, VmType[]? typeArguments) {
        var layout = GetLayout(structType);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var (field, index) in layout)
            fields[index] = DefaultForType(GenericSubstitutor.Substitute(field.FieldType!, context), loader);
        return new VmStructValue(structType, fields, typeArguments);
    }

    /// <summary>アロケーションサイズの概算 (バイト)。クォータ計上用。</summary>
    public static long EstimateSize(VmObject obj) => obj switch {
        VmArray array => 24 + 16L * array.Elements.Length,
        VmClassInstance instance => 24 + 16L * instance.Fields.Length,
        VmBoxedValue boxed => 24 + 16L * boxed.Fields.Length,
        _ => 24,
    };
}
