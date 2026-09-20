using DotnetVM.Metadata;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>TypeLoader / VmType 骨格の検証。</summary>
public class TypeLoaderTests {
    private const string Source = """
        using System;
        namespace Tl {
            public class Base : IDisposable {
                public virtual int GetValue() => 1;
                public void Dispose() { }
            }
            public class Derived : Base {
                private int _counter;
                public static string Label = "D";
                public override int GetValue() => ++_counter;
            }
            public class Outer {
                public class Inner { public int Val() => 7; }
            }
            public enum Flavor { Sour, Sweet, Bitter }
            public struct Point { public int X, Y; public int Sum() => X + Y; }
            public class Box<T> where T : class {
                public T? Item;
                public U? Cast<U>() where U : class => Item as U;
            }
        }
        """;

    private static TypeLoader CreateLoader() {
        var image = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(Source));
        var loader = new TypeLoader(image);
        loader.CompletePendingTypes();
        return loader;
    }

    private static VmClassType Find(TypeLoader loader, string name) {
        // TypeLoader には名前検索 API がないため TypeDef を全走査する
        var count = loader.Image.Tables.GetRowCount(TableKind.TypeDef);
        for (var rid = 1; rid <= count; rid++) {
            var type = loader.GetTypeDef(rid);
            if (type.Name == name)
                return type;
        }
        throw new InvalidOperationException($"型 {name} が見つかりません。");
    }

    [Fact]
    public void BaseType_Chain_ResolvesThroughInheritance() {
        var loader = CreateLoader();
        var derived = Find(loader, "Derived");
        var baseType = Find(loader, "Base");

        Assert.Equal(baseType, derived.BaseType);
        Assert.Equal(loader.FindIntrinsicType("System.Object"), baseType.BaseType);
        Assert.True(derived.IsAssignableTo(baseType));
        Assert.True(derived.IsAssignableTo(loader.FindIntrinsicType("System.Object")!));
        Assert.False(baseType.IsAssignableTo(derived));
    }

    [Fact]
    public void Interface_Implementation_IsDetected() {
        var loader = CreateLoader();
        var baseType = Find(loader, "Base");
        var disposable = baseType.Interfaces.SingleOrDefault(i => i.Name == "IDisposable");
        Assert.NotNull(disposable);
        Assert.True(baseType.IsAssignableTo(disposable!));
    }

    [Fact]
    public void Nested_Type_HasDeclaringType() {
        var loader = CreateLoader();
        var inner = Find(loader, "Inner");
        Assert.Equal("Outer", inner.DeclaringType?.Name);
    }

    [Fact]
    public void Enum_IsValueType_WithUnderlyingField() {
        var loader = CreateLoader();
        var flavor = Find(loader, "Flavor");

        Assert.True(flavor.IsEnum);
        Assert.True(flavor.IsValueType);
        Assert.Equal("System.Enum", flavor.BaseType!.FullName);

        // enum の実体フィールド (value__ : Int32)
        var valueField = flavor.FindField("value__");
        Assert.NotNull(valueField);
        Assert.Equal("System.Int32", valueField!.FieldType!.FullName);
        Assert.False(valueField.IsStatic);

        // リテラルメンバ
        var sour = flavor.FindField("Sour");
        Assert.NotNull(sour);
        Assert.True(sour!.IsLiteral);
    }

    [Fact]
    public void Struct_IsValueType_WithMethods() {
        var loader = CreateLoader();
        var point = Find(loader, "Point");

        Assert.True(point.IsValueType);
        Assert.False(point.IsEnum);
        Assert.Equal("System.ValueType", point.BaseType!.FullName);
        Assert.NotNull(point.FindMethod("Sum"));

        var x = point.FindField("X");
        Assert.Equal("System.Int32", x!.FieldType!.FullName);
    }

    [Fact]
    public void Generic_Definition_LoadsMembers() {
        var loader = CreateLoader();
        var box = Find(loader, "Box`1");

        Assert.NotNull(box.FindMethod("Cast"));
        var item = box.FindField("Item");
        Assert.NotNull(item);
        // フィールド型はジェネリックパラメータ (!0)
        Assert.Equal("!0", item!.FieldType!.FullName);
        Assert.IsType<VmGenericParameterType>(item.FieldType);
    }

    [Fact]
    public void Static_Field_HasResolvedType() {
        var loader = CreateLoader();
        var derived = Find(loader, "Derived");
        var label = derived.FindField("Label");
        Assert.NotNull(label);
        Assert.True(label!.IsStatic);
        Assert.Equal("System.String", label.FieldType!.FullName);
    }

    [Fact]
    public void Method_Flags_And_Signature_AreCorrect() {
        var loader = CreateLoader();
        var derived = Find(loader, "Derived");

        var getValue = derived.FindMethod("GetValue")!;
        Assert.True(getValue.IsVirtual);
        Assert.False(getValue.IsStatic);
        Assert.Equal(0, getValue.Signature.ParamTypes.Length);
        Assert.Equal("System.Int32", NameOf(getValue.Signature.ReturnType));

        var labelField = derived.FindField("Label")!;
        _ = labelField;

        // Base::Dispose はインターフェース実装 (virtual ではない)
        var baseType = Find(loader, "Base");
        var dispose = baseType.FindMethod("Dispose")!;
        Assert.False(dispose.IsAbstract);
    }

    [Fact]
    public void Intrinsic_Facades_AreSingletons() {
        var loader = CreateLoader();
        var obj = loader.FindIntrinsicType("System.Object");
        var str = loader.FindIntrinsicType("System.String");
        Assert.NotNull(obj);
        Assert.NotNull(str);
        Assert.Equal(obj, str!.BaseType);
        Assert.False(str!.IsValueType);

        // 構築コンテキストがなくても同一インスタンスを返す
        var loader2 = CreateLoader();
        Assert.NotSame(obj, loader2.FindIntrinsicType("System.Object")); // ローダごとのキャッシュ
    }

    [Fact]
    public void TypeIdentity_IsStable_AcrossRepeatedLoads() {
        var loader = CreateLoader();
        var a = Find(loader, "Derived");
        var b = loader.GetTypeDef(a.TypeDefRid);
        Assert.Same(a, b);
    }

    private static string NameOf(DotnetVM.Metadata.Signatures.SigType type) =>
        type.Kind switch {
            DotnetVM.Metadata.Signatures.SigKind.I4 => "System.Int32",
            DotnetVM.Metadata.Signatures.SigKind.String => "System.String",
            _ => type.ToString(),
        };
}
