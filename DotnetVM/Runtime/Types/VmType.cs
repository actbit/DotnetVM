using DotnetVM.Metadata;

namespace DotnetVM.Runtime.Types;

/// <summary>VM 内の型の基底クラス。同一の型は常に同一インスタンス (識別は参照等価)。</summary>
public abstract class VmType {
    /// <summary>名前空間名を含む完全名 (ネスト型は包含型名を含まない)。</summary>
    public abstract string FullName { get; }
    public abstract VmType? BaseType { get; }

    /// <summary>名前空間を除いた型名 (FullName の最後の '.' 以降)。</summary>
    public string Name => FullName[(FullName.LastIndexOf('.') + 1)..];

    public IReadOnlyList<VmMethod> Methods { get; internal set; } = [];
    public IReadOnlyList<VmField> Fields { get; internal set; } = [];
    public IReadOnlyList<VmType> Interfaces { get; internal set; } = [];

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

    /// <summary>この型が target に代入可能か (同一、基底チェーン、実装インターフェース)。</summary>
    public bool IsAssignableTo(VmType target) {
        if (ReferenceEquals(this, target))
            return true;
        for (var current = BaseType; current is not null; current = current.BaseType)
            if (ReferenceEquals(current, target))
                return true;
        foreach (var iface in Interfaces)
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
    public required string Name { get; init; }
    public override uint Flags { get; }

    private VmType? _baseType;
    private bool _baseTypeResolved;

    public override string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;

    /// <summary>ネスト型の場合の包含型 (無ければ null)。</summary>
    public VmType? DeclaringType { get; internal set; }

    public VmClassType(uint flags) {
        Flags = flags;
    }

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
    public required string Name { get; init; }
    public required bool IsValue { get; init; }
    public VmType? Parent { get; init; }

    public override string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
    public override VmType? BaseType => Parent;
    public override bool IsValueType => IsValue;
}

/// <summary>1 次元 0 原点配列 (SZArray)。</summary>
public sealed class VmArrayType : VmType {
    public required VmType ElementType { get; init; }
    public override string FullName => ElementType.FullName + "[]";
    public override VmType? BaseType { get; } = null; // System.Array ファサードは M6 で接続
    public override bool IsValueType => false;
}

/// <summary>多次元配列。</summary>
public sealed class VmMultiDimArrayType : VmType {
    public required VmType ElementType { get; init; }
    public required int Rank { get; init; }
    public override string FullName => $"{ElementType.FullName}[{Rank}]";
    public override VmType? BaseType => null;
    public override bool IsValueType => false;
}

/// <summary>ByRef 型 (ref T)。</summary>
public sealed class VmByRefType : VmType {
    public required VmType ElementType { get; init; }
    public override string FullName => ElementType.FullName + "&";
    public override VmType? BaseType => null;
    public override bool IsValueType => false;
}

/// <summary>ジェネリックパラメータ (!n / !!n)。</summary>
public sealed class VmGenericParameterType : VmType {
    public required bool IsMethodParameter { get; init; }
    public required int Number { get; init; }
    public override string FullName => (IsMethodParameter ? "!!" : "!") + Number;
    public override VmType? BaseType => null;
    public override bool IsValueType => false;
}

/// <summary>構築ジェネリック型 (GenericInst)。</summary>
public sealed class VmConstructedType : VmType {
    /// <summary>ジェネリック定義 (VmClassType またはネストした VmConstructedType)。</summary>
    public required VmType Definition { get; init; }
    public required VmType[] TypeArguments { get; init; }

    public override string FullName =>
        $"{Definition.FullName}<{string.Join(", ", TypeArguments.Select(t => t.FullName))}>";
    public override VmType? BaseType => Definition.BaseType; // TODO(M5): 型引数を置換した基底型
    public override bool IsValueType => Definition.IsValueType;
    public override uint Flags => Definition.Flags;
}
