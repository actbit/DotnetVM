using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C3 署名精度ディスパッチのテスト: VTable / InterfaceMap (メソッド識別 = 名前 + パラメータ署名)
/// による仮想・インターフェース呼出が CLR と同一結果になることを CoreLib あり/なしの両 VM で確認する。
/// オーバーロード混在の仮想呼び、明示的インターフェース実装 (EII)、インターフェース継承、
/// 構築ジェネリック基底の override、ジェネリック仮想メソッドを突合する。
/// </summary>
public class CoreLibDispatchTests {
    private const string Source = """
        namespace Vm.C3 {
            public interface IGreeter {
                string Greet(string name);
                int Value { get; }
            }

            public interface ICounting : IGreeter {
                int Count();
            }

            public interface IStore<T> {
                T Get(int index);
                void Put(int index, T value);
            }

            public class Overloads {
                public virtual string Describe(int x) => "int";
                public virtual string Describe(long x) => "long";
                public virtual string Describe(string x) => "string";
            }

            public class PreciseOverloads : Overloads {
                public override string Describe(int x) => "int:derived";
                public override string Describe(long x) => "long:derived";
                public override string Describe(string x) => "string:derived";
            }

            public class ExplicitGreeter : IGreeter {
                string IGreeter.Greet(string name) => "explicit:" + name;
                public int Value => 7;
            }

            public class ExplicitCounter : ICounting {
                string IGreeter.Greet(string name) => "greeter:" + name;
                int ICounting.Count() => 42;
                public int Value => 9;
            }

            public class GenericExplicitStore : IStore<string> {
                private readonly string[] items = new string[4];
                string IStore<string>.Get(int index) {
                    var prefix = "s" + index.ToString();
                    var withEquals = prefix + "=";
                    return withEquals + items[index];
                }
                void IStore<string>.Put(int index, string value) => items[index] = value;
            }

            public class ImplicitBase : ICounting {
                public virtual string Greet(string name) => "base:" + name;
                public int Value => 1;
                public virtual int Count() => 10;
            }

            public class ImplicitDerived : ImplicitBase {
                public override string Greet(string name) => "derived:" + name;
                public override int Count() => 20;
            }

            public class Repo<T> {
                protected readonly T[] items = new T[4];
                public virtual T Get(int index) => items[index];
                public virtual void Put(int index, T value) => items[index] = value;
            }

            public class IntRepo : Repo<int> {
                public override int Get(int index) => base.Get(index) + 100;
            }

            public class GenericOverrideBase<T> {
                public virtual T Echo(T value) => value;
            }

            public class StringEcho : GenericOverrideBase<string> {
                public override string Echo(string value) => "e:" + value;
            }

            public static class Entry {
                // 同一名 + 同一引数個数のオーバーロード混在クラスの仮想呼び (署名での解決が必須)
                public static string CallOverloads() {
                    Overloads o = new PreciseOverloads();
                    var r = o.Describe(1);
                    r = r + ",";
                    r = r + o.Describe(2L);
                    r = r + ",";
                    r = r + o.Describe("x");
                    return r;
                }

                // 明示的インターフェース実装 (EII) の interface callvirt
                public static string CallInterfaceExplicit() {
                    IGreeter g = new ExplicitGreeter();
                    var r = g.Greet("a");
                    r = r + ",";
                    r = r + g.Value.ToString();
                    return r;
                }

                // インターフェース継承 (ICounting : IGreeter) の各スロットの明示的実装
                public static string CallInterfaceInherited() {
                    ICounting c = new ExplicitCounter();
                    var r = c.Count().ToString();
                    r = r + ",";
                    r = r + c.Greet("b");
                    r = r + ",";
                    r = r + c.Value.ToString();
                    return r;
                }

                // 構築ジェネリック インターフェース (TypeSpec 親) の明示的実装
                public static string CallGenericInterface() {
                    IStore<string> s = new GenericExplicitStore();
                    s.Put(0, "v0");
                    s.Put(1, "v1");
                    var r = s.Get(0);
                    r = r + ",";
                    r = r + s.Get(1);
                    return r;
                }

                // 暗黙実装 + 派生 override: インターフェース呼出が最派生に着地すること
                public static string CallVirtualThroughInterface() {
                    ICounting c = new ImplicitDerived();
                    var r = c.Greet("g");
                    r = r + ",";
                    r = r + c.Count().ToString();
                    r = r + ",";
                    r = r + ((IGreeter)c).Greet("h");
                    return r;
                }

                // 構築ジェネリック基底 (Repo<int>) の仮想呼び: !0 → int への置換解決が必須
                public static string CallConstructedBase() {
                    Repo<int> r2 = new IntRepo();
                    r2.Put(0, 5);
                    return r2.Get(0).ToString();
                }

                // ジェネリック クラスのジェネリック仮想メソッドの override
                public static string CallGenericVirtual() {
                    GenericOverrideBase<string> b = new StringEcho();
                    return b.Echo("x");
                }
            }
        }
        """;

    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new CompiledTestAssembly(Source, "C3Asm"));

    private static object? RunClr(string method) =>
        Compiled.Value.InvokeClr("Vm.C3.Entry", method);

    private static DotnetVM.Host.VirtualMachine CreateVm(bool loadCoreLib) =>
        Compiled.Value.CreateVm(loadCoreLib);

    /// <summary>CoreLib あり/なしの両 VM と CLR の 3 方突合 (ディスパッチ経路が変わっても結果が不変なこと)。</summary>
    private static void AssertSameEverywhere(string method) {
        var expected = RunClr(method);
        using (var withCoreLib = CreateVm(loadCoreLib: true))
            Assert.Equal(expected, withCoreLib.Invoke("Vm.C3.Entry", method));
        using (var withoutCoreLib = CreateVm(loadCoreLib: false))
            Assert.Equal(expected, withoutCoreLib.Invoke("Vm.C3.Entry", method));
    }

    [Fact]
    public void Overloaded_Virtual_Dispatch_Matches_Clr() =>
        AssertSameEverywhere("CallOverloads");

    [Fact]
    public void Explicit_Interface_Implementation_Matches_Clr() =>
        AssertSameEverywhere("CallInterfaceExplicit");

    [Fact]
    public void Inherited_Interface_Implementation_Matches_Clr() =>
        AssertSameEverywhere("CallInterfaceInherited");

    [Fact]
    public void Constructed_Generic_Interface_Implementation_Matches_Clr() =>
        AssertSameEverywhere("CallGenericInterface");

    [Fact]
    public void Interface_Call_Lands_On_Most_Derived_Override() =>
        AssertSameEverywhere("CallVirtualThroughInterface");

    [Fact]
    public void Constructed_Generic_Base_Virtual_Matches_Clr() =>
        AssertSameEverywhere("CallConstructedBase");

    [Fact]
    public void Generic_Virtual_Method_Override_Matches_Clr() =>
        AssertSameEverywhere("CallGenericVirtual");
}
