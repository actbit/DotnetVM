using DotnetVM.Metadata;

namespace DotnetVM.Runtime.Types;

/// <summary>VM 内の型の基底クラス。同一の型は常に同一インスタンス (識別は参照等価)。</summary>
public abstract class VmType {
    /// <summary>名前空間名を含む完全名 (ネスト型は包含型名を含まない)。</summary>
    public abstract string FullName { get; }
    public abstract VmType? BaseType { get; }

    /// <summary>名前空間を除いた型名 (FullName の最後の '.' / '+' 以降)。
    /// ネスト型の FullName は CLR 規約どおり '+' 区切り (例: Vm.Compat+Box) なので両方を見る。</summary>
    public virtual string Name {
        get {
            var fullName = FullName;
            var cut = System.Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('+'));
            return fullName[(cut + 1)..];
        }
    }

    public IReadOnlyList<VmMethod> Methods { get; internal set; } = [];
    public IReadOnlyList<VmField> Fields { get; internal set; } = [];

    /// <summary>実装するインターフェース (構築型は型引数を置換したものを返す)。</summary>
    public virtual IReadOnlyList<VmType> Interfaces { get; internal set; } = [];

    /// <summary>ジェネリックパラメータの変性/制約フラグ (ECMA-335 GenericParam.Flags)。
    /// 0x0001 = Covariant (out)、0x0002 = Contravariant (in)。非ジェネリック型は空。</summary>
    public virtual uint[] GenericParamFlags => [];

    /// <summary>ジェネリックパラメータの個数 (非ジェネリック型は 0)。</summary>
    public virtual int GenericParamCount => GenericParamFlags.Length;

    public bool IsEnum => BaseType?.FullName == "System.Enum";
    public bool IsInterface => (Flags & 0x20) != 0;

    /// <summary>TypeDef Flags (ファサード型は 0)。</summary>
    public virtual uint Flags => 0;

    /// <summary>基底チェーンに ValueType / Enum が現れるかで値型を判定する (派生側で上書き可)。</summary>
    public virtual bool IsValueType {
        get {
            for (var current = BaseType; current is not null; current = current.BaseType)
                if (current.FullName is "System.ValueType" or "System.Enum" || current.IsValueType)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// この型が target に代入可能か (同一、基底チェーン、実装インターフェース)。
    /// 構築型同士は型引数の一致 + 変性 (共変/反変) を考慮する (M5)。
    /// 末尾の完全名一致は「参照アセンブリ⇔実装アセンブリの型統合」のための緩和:
    /// CoreLib 実型化 (C2) 前後で同一 BCL 名のファサード/実型が混在しても同一視する。
    /// </summary>
    public bool IsAssignableTo(VmType target) {
        if (ReferenceEquals(this, target))
            return true;
        if (FullName == target.FullName)
            return true;
        if (this is VmConstructedType constructed && target is VmConstructedType targetConstructed)
            return constructed.IsConstructedAssignableTo(targetConstructed);
        // 基底チェーンも先頭と同じ緩和で照合する (VM 内部例外のファサード階層 ⇔
        // CoreLib 実型階層の橋渡し: facade DivideByZeroException → facade Exception と
        // 実型 System.Exception を完全名で一致させる)
        for (var current = BaseType; current is not null; current = current.BaseType)
            if (ReferenceEquals(current, target) || current.FullName == target.FullName)
                return true;
        // インターフェース実装は基底クラスで宣言されたものも派生型に引き継がれる (CLR 規約)。
        // 基底チェーン全体の実装インターフェースを照合する (enum → System.Enum の IConvertible 等)
        for (var current = (VmType?)this; current is not null; current = current.BaseType)
            foreach (var iface in current.Interfaces)
                if (iface.IsAssignableTo(target))
                    return true;
        return false;
    }

    public override string ToString() => FullName;
}

/// <summary>アセンブリの TypeDef から生成するクラス/値型/列挙型/インターフェース。</summary>
public sealed class VmClassType : VmType {
    public required AssemblyImage Image { get; init; }
    public required int TypeDefRid { get; init; }
    public required string Namespace { get; init; }
    public required new string Name { get; init; }
    public override uint Flags { get; }

    private VmType? _baseType;
    private bool _baseTypeResolved;

    public override string FullName =>
        // CLR 規約: ネスト型の FullName は包含型を '+' で連結する (Type.FullName 互換)。
        // DeclaringType は TypeLoader の遅延解決なので、未解決間はメタデータ名のみ。
        DeclaringType is VmClassType declaring
            ? declaring.FullName + "+" + Name
            : string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;

    /// <summary>ネスト型の場合の包含型 (無ければ null)。</summary>
    public VmType? DeclaringType { get; internal set; }

    /// <summary>この型をロードした TypeLoader (ディスパッチ表構築や token 解決の担当ローダ)。</summary>
    public TypeLoader? Loader { get; internal set; }

    public VmClassType(uint flags) {
        Flags = flags;
    }

    private uint[] _genericParamFlags = [];

    /// <summary>GenericParam テーブル (Owner = 本型) の Flags。TypeLoader が補完する。</summary>
    public override uint[] GenericParamFlags => _genericParamFlags;

    internal void SetGenericParamFlags(uint[] flags) => _genericParamFlags = flags;

    /// <summary>基底型。TypeLoader が Extends を遅延解決する。</summary>
    public override VmType? BaseType {
        get {
            if (!_baseTypeResolved)
                throw new InvalidOperationException($"型 {FullName} の基底型が未解決です (TypeLoader の Finalize を待ってください)。");
            return _baseType;
        }
    }


    // TypeLoader からのみ呼ぶ構完メソッド
    internal void SetBaseType(VmType? baseType) {
        _baseType = baseType;
        _baseTypeResolved = true;
    }

    internal void SetInterfaces(VmType[] interfaces) => Interfaces = interfaces;

    /// <summary>メソッドを名前で検索 (静的/インスタンス区別なし、最初の一致)。</summary>
    public VmMethod? FindMethod(string name) {
        foreach (var method in Methods)
            if (method.Name == name)
                return method;
        return null;
    }

    /// <summary>フィールドを名前で検索。</summary>
    public VmField? FindField(string name) {
        foreach (var field in Fields)
            if (field.Name == name)
                return field;
        return null;
    }
}

/// <summary>アセンブリに実装が無い型 (System.Object 等) を表すファサード。実体は intrinsic が担う。</summary>
public sealed class VmIntrinsicType : VmType {
    public required string Namespace { get; init; }
    public required new string Name { get; init; }
    public required bool IsValue { get; init; }
    public VmType? Parent { get; init; }

    private uint[] _genericParamFlags = [];

    /// <summary>BCL 既知の変性 (IEnumerable`1 = 共変、IComparable`1 = 反変 等)。</summary>
    public override uint[] GenericParamFlags => _genericParamFlags;

    internal void SetGenericParamFlags(uint[] flags) => _genericParamFlags = flags;

    public override string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
    public override VmType? BaseType => Parent;
    public override bool IsValueType => IsValue;
}

/// <summary>1 次元 0 原点配列 (SZArray)。</summary>
public sealed class VmArrayType : VmType {
    public required VmType ElementType { get; init; }
    public override string FullName => ElementType.FullName + "[]";

    private VmType? _baseType;

    /// <summary>System.Array (実型またはファサード)。TypeLoader が解決時に接続する。</summary>
    public override VmType? BaseType => _baseType;

    internal void SetBaseType(VmType baseType) => _baseType = baseType;

    public override bool IsValueType => false;
    public override uint[] GenericParamFlags => [];
}

/// <summary>多次元配列。</summary>
public sealed class VmMultiDimArrayType : VmType {
    public required VmType ElementType { get; init; }
    public required int Rank { get; init; }
    public override string FullName => $"{ElementType.FullName}[{Rank}]";

    private VmType? _baseType;

    /// <summary>System.Array (実型またはファサード)。TypeLoader が解決時に接続する。</summary>
    public override VmType? BaseType => _baseType;

    internal void SetBaseType(VmType baseType) => _baseType = baseType;
    public override bool IsValueType => false;
    public override uint[] GenericParamFlags => [];
}

/// <summary>ByRef 型 (ref T)。</summary>
public sealed class VmByRefType : VmType {
    public required VmType ElementType { get; init; }
    public override string FullName => ElementType.FullName + "&";
    public override VmType? BaseType => null;
    public override bool IsValueType => false;
    public override uint[] GenericParamFlags => [];
}

/// <summary>ジェネリックパラメータ (!n / !!n)。</summary>
public sealed class VmGenericParameterType : VmType {
    public required bool IsMethodParameter { get; init; }
    public required int Number { get; init; }
    public override string FullName => (IsMethodParameter ? "!!" : "!") + Number;
    public override VmType? BaseType => null;
    public override bool IsValueType => false;
    public override uint[] GenericParamFlags => [];
}

/// <summary>構築ジェネリック型 (GenericInst)。</summary>
public sealed class VmConstructedType : VmType {
    /// <summary>ジェネリック定義 (VmClassType またはネストした VmConstructedType)。</summary>
    public required VmType Definition { get; init; }
    public required VmType[] TypeArguments { get; init; }

    private VmType? _baseType;

    public override string FullName =>
        $"{Definition.FullName}<{string.Join(", ", TypeArguments.Select(t => t.FullName))}>";

    /// <summary>Type.Name は構築型の型引数を含まず、ジェネリック定義名を返す。</summary>
    public override string Name => Definition.Name;

    /// <summary>定義の基底型に型引数を適用したもの (例: Sub`1&lt;int&gt; → Base`1&lt;!0&gt; → Base`1&lt;int&gt;)。</summary>
    public override VmType? BaseType {
        get {
            if (_baseType is null && !_baseResolved) {
                _baseType = Definition.BaseType is { } raw ? SubstituteOwn(raw) : null;
                _baseResolved = true;
            }
            return _baseType;
        }
    }

    private bool _baseResolved;

    /// <summary>定義の実装インターフェースに型引数を適用したもの。</summary>
    public override IReadOnlyList<VmType> Interfaces => Definition.Interfaces
        .Select(i => SubstituteOwn(i))
        .ToArray();

    public override bool IsValueType => Definition.IsValueType;
    public override uint Flags => Definition.Flags;
    public override uint[] GenericParamFlags => Definition.GenericParamFlags;

    private VmType SubstituteOwn(VmType type) =>
        GenericSubstitutor.Substitute(type, new GenericContext { ClassArgs = TypeArguments });

    /// <summary>構築型同士の代入可能性 (同一定義なら型引数の変性込み比較、異なるなら置換済み基底/インターフェースを辿る)。</summary>
    public bool IsConstructedAssignableTo(VmConstructedType target) {
        if (ReferenceEquals(Definition, target.Definition))
            return TypeArgumentsMatch(TypeArguments, target.TypeArguments, Definition.GenericParamFlags);

        var baseType = BaseType; // 型引数置換済み (構築型なら再帰的に変性判定される)
        if (baseType?.IsAssignableTo(target) == true)
            return true;
        foreach (var iface in Interfaces)
            if (iface.IsAssignableTo(target))
                return true;
        return false;
    }

    /// <summary>変性を考慮した型引数比較 (flags は定義側の GenericParam.Flags)。</summary>
    internal static bool TypeArgumentsMatch(VmType[] actual, VmType[] target, uint[] flags) {
        if (actual.Length != target.Length)
            return false;
        for (var i = 0; i < actual.Length; i++) {
            if (ReferenceEquals(actual[i], target[i]))
                continue;
            // 参照が異なっても同一型名なら同一視 (ファサード型の再合成に備えた緩和)
            if (actual[i].FullName == target[i].FullName)
                continue;
            var variance = i < flags.Length ? flags[i] : 0;
            if ((variance & CovariantFlag) != 0) {
                if (!actual[i].IsAssignableTo(target[i]))
                    return false;
            } else if ((variance & ContravariantFlag) != 0) {
                if (!target[i].IsAssignableTo(actual[i]))
                    return false;
            } else {
                return false; // 不変 (invariant)
            }
        }
        return true;
    }

    internal const uint CovariantFlag = 0x0001;
    internal const uint ContravariantFlag = 0x0002;
}

/// <summary>
/// VM が「単一スロット」表現で保持するプリミティブ値型の集合。
/// 型同一性は CoreLib 実型化 (C2) 後もこの判定で保たれる: 実型 (CoreLib TypeDef) と
/// intrinsic ファサードのどちらで解決されても、ストレージ表現はスロットのまま変わらない。
/// </summary>
public static class VmPrimitiveTypes {
    /// <summary>
    /// VM の native-int 表現はホスト OS に依存させず 64-bit に固定する。
    /// これにより Ubuntu/Windows/macOS 間で conv.i、IntPtr 配列、ポインタ演算の
    /// 意味論と heap layout が変化しない。32-bit guest ABI は現時点では提供しない。
    /// </summary>
    public const int NativeIntSizeBytes = 8;
    public const int NativeIntBits = NativeIntSizeBytes * 8;

    /// <summary>単一スロット表現のプリミティブ完全名 (i4 統合面 + i8 + fp + native int)。</summary>
    private static readonly HashSet<string> SlotPrimitives = new(StringComparer.Ordinal) {
        "System.Boolean", "System.Char", "System.SByte", "System.Byte",
        "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64", "System.Single", "System.Double",
        "System.IntPtr", "System.UIntPtr",
    };

    /// <summary>型が単一スロット表現のプリミティブか (実型/ファサード両方で true)。</summary>
    public static bool IsSlotPrimitive(string fullName) => SlotPrimitives.Contains(fullName);
}
