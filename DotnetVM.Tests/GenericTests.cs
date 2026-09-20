using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M5 ジェネリックの突合テスト。自作 List&lt;T&gt; 風型 (GenericInst 配列 + foreach + 構造体列)、
/// ジェネリックメソッド (MethodSpec)、ジェネリック基底クラス (基底 TypeSpec の !0 置換)、
/// out 変性付きインターフェースへの castclass を VM 実行と CLR (反射) 実行で比べる。
/// </summary>
public class GenericTests {
    private const string Source = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        namespace Vm {
            // ---- 自作 List<T> 風型 (GenericInst 配列 + foreach + 入れ子構造体 enumerator) ----
            public class MyList<T> : IEnumerable<T> {
                private T[] _items;
                private int _count;

                public MyList() { _items = new T[4]; }

                public int Count => _count;

                public void Add(T item) {
                    if (_count == _items.Length) {
                        var next = new T[_items.Length * 2];
                        for (var i = 0; i < _count; i++) next[i] = _items[i];
                        _items = next;
                    }
                    _items[_count] = item;
                    _count++;
                }

                public T this[int index] => _items[index];

                public IEnumerator<T> GetEnumerator() => new Enumerator(this);
                IEnumerator IEnumerable.GetEnumerator() => null;

                public struct Enumerator : IEnumerator<T> {
                    private readonly MyList<T> _list;
                    private int _index;
                    public Enumerator(MyList<T> list) { _list = list; _index = -1; }
                    public T Current => _list[_index];
                    object IEnumerator.Current => null;
                    public bool MoveNext() { _index++; return _index < _list.Count; }
                    public void Reset() { _index = -1; }
                    public void Dispose() { }
                }
            }

            // ---- ジェネリック列に入れる構造体 (値型実引数) ----
            public struct Point {
                public int X;
                public int Y;
                public Point(int x, int y) { X = x; Y = y; }
            }

            // ---- out 変性付きインターフェース ----
            public interface IProducer<out T> { T Get(); }
            public class StringProducer : IProducer<string> {
                public string Get() => "made";
            }
            public class ObjectProducer : IProducer<object> {
                public object Get() => "oops";
            }

            // ---- ジェネリック基底クラス (基底 TypeSpec が !0 を参照) ----
            public class Base<T> {
                protected T _value;
                public Base(T value) { _value = value; }
                public T GetValue() => _value;
            }
            public class Derived<T> : Base<T> {
                public Derived(T value) : base(value) { }
                public string Describe() => GetValue() + "!";
            }

            public static class Generics {
                // ---- ジェネリックメソッド (MethodSpec) ----
                public static T First<T>(T[] items) => items[0];

                public static T[] Repeat<T>(T value, int count) {
                    var result = new T[count];
                    for (var i = 0; i < count; i++) result[i] = value;
                    return result;
                }

                public static T Pick<T>(MyList<T> list, int index) => list[index];

                // ---- 検証メソッド ----

                public static MyList<int> MakeList() {
                    var list = new MyList<int>();
                    list.Add(10); list.Add(20); list.Add(30); list.Add(40); list.Add(50);
                    return list;
                }

                public static string ListBasics() {
                    var list = new MyList<int>();
                    list.Add(10); list.Add(20); list.Add(30); list.Add(40); list.Add(50); // 4 → 8 に拡張
                    var s = "" + list.Count;
                    for (var i = 0; i < list.Count; i++) s += "," + list[i];
                    return s;
                }

                public static int ForeachSum(MyList<int> list) {
                    var sum = 0;
                    foreach (var item in list) sum += item;
                    return sum;
                }

                public static string StructsInGeneric() {
                    var list = new MyList<Point>();
                    list.Add(new Point(1, 2));
                    list.Add(new Point(3, 4));
                    var sum = 0;
                    for (var i = 0; i < list.Count; i++) sum += list[i].X;
                    var s = "" + sum;
                    s += ":" + list.Count;
                    return s;
                }

                public static string GenericMethods() {
                    var first = Generics.First<int>(new[] { 10, 20, 30 });
                    var repeated = Generics.Repeat<string>("x", 3);
                    var list = new MyList<int>();
                    list.Add(7); list.Add(8);
                    var picked = Generics.Pick<int>(list, 1);
                    var s = "" + first;
                    s += "|";
                    s += repeated.Length;
                    s += ":";
                    s += repeated[0];
                    s += repeated[1];
                    s += repeated[2];
                    s += "|";
                    s += picked;
                    return s;
                }

                public static string Inheritance() {
                    var d = new Derived<int>(5);
                    var s = "" + d.GetValue();
                    s += d.Describe();
                    return s;
                }

                public static string Covariance() {
                    IProducer<string> p = new StringProducer();
                    object boxedRef = p;
                    IProducer<object> o = (IProducer<object>)boxedRef; // castclass (out 変性で成功)
                    return (string)o.Get();
                }

                // 逆方向 (IProducer<object> → IProducer<string>) は object → string に昇格できないので失敗
                public static string CovarianceRejected() {
                    IProducer<object> o = new ObjectProducer();
                    object boxedRef = o;
                    try {
                        var p = (IProducer<string>)boxedRef;
                        return "accepted:" + p.Get();
                    } catch (InvalidCastException) {
                        return "rejected";
                    }
                }

                // 変性なし (不変) の構築型キャストは同一引数のみ成功
                public static string Invariance() {
                    var list = new MyList<string>();
                    object boxedRef = list;
                    var ok = (MyList<string>)boxedRef;
                    try {
                        var bad = (MyList<object>)boxedRef;
                        return "accepted:" + bad.Count;
                    } catch (InvalidCastException) {
                        return "rejected:" + ok.Count;
                    }
                }
            }
        }
        """;

    // Assembly.Load は呼び出しごとに別アセンブリ (型同一性が崩れる) のため 1 回だけロードして共有する
    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source);
        return (Assembly.Load(bytes), bytes);
    }

    private static (Assembly Clr, byte[] Bytes) Compile() => Compiled;

    private static VirtualMachine CreateVm() {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy {
                InstructionQuota = 100_000_000,
            },
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(string typeName, string method, params object?[] args) {
        var (clr, _) = Compile();
        var type = clr.GetType("Vm." + typeName)!;
        return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
    }

    private static void AssertMatchesClr(string typeName, string method, params object?[] args) {
        Assert.Equal(RunClr(typeName, method, args), CreateVm().Invoke("Vm." + typeName, method, args));
    }

    // ---- 自作 List<T> 風型 ----

    [Fact]
    public void MyList_Basics_MatchesClr() {
        AssertMatchesClr("Generics", "ListBasics");
        Assert.Equal("5,10,20,30,40,50", CreateVm().Invoke("Vm.Generics", "ListBasics"));
    }

    [Fact]
    public void MyList_ForeachOverStructEnumerator_MatchesClr() {
        // リストは各側で自前構築 (VM オブジェクトをホスト境界で受け渡しするため)
        var clr = RunClr("Generics", "ForeachSum", RunClr("Generics", "MakeList"));
        var vm = CreateVm();
        var vmList = vm.Invoke("Vm.Generics", "MakeList");
        Assert.Equal(clr, vm.Invoke("Vm.Generics", "ForeachSum", vmList));
        Assert.Equal(150, clr);
    }

    [Fact]
    public void MyList_StructElements_MatchesClr() {
        AssertMatchesClr("Generics", "StructsInGeneric");
        Assert.Equal("4:2", CreateVm().Invoke("Vm.Generics", "StructsInGeneric"));
    }

    // ---- ジェネリックメソッド (MethodSpec) ----

    [Fact]
    public void GenericMethods_MatchesClr() {
        AssertMatchesClr("Generics", "GenericMethods");
        Assert.Equal("10|3:xxx|8", CreateVm().Invoke("Vm.Generics", "GenericMethods"));
    }

    // ---- ジェネリック基底クラス ----

    [Fact]
    public void GenericBaseClass_MatchesClr() {
        AssertMatchesClr("Generics", "Inheritance");
        Assert.Equal("55!", CreateVm().Invoke("Vm.Generics", "Inheritance"));
    }

    // ---- 変性付き castclass ----

    [Fact]
    public void CovariantCast_MatchesClr() {
        AssertMatchesClr("Generics", "Covariance");
        Assert.Equal("made", CreateVm().Invoke("Vm.Generics", "Covariance"));
    }

    [Fact]
    public void CovariantCast_RejectedDirection_MatchesClr() {
        AssertMatchesClr("Generics", "CovarianceRejected");
        Assert.Equal("rejected", CreateVm().Invoke("Vm.Generics", "CovarianceRejected"));
    }

    [Fact]
    public void InvariantCast_MatchesClr() {
        AssertMatchesClr("Generics", "Invariance");
        Assert.Equal("rejected:0", CreateVm().Invoke("Vm.Generics", "Invariance"));
    }
}
