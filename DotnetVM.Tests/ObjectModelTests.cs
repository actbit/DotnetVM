using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M3 オブジェクトモデルの突合テスト。継承/多態/インターフェース/構造体/静的フィールド/
/// box/配列/キャストを VM 実行と CLR (反射) 実行で比べる。
/// </summary>
public class ObjectModelTests {
    private const string Source = """
        using System;
        namespace Vm {
            // 継承 + 仮想メソッドの多態
            public abstract class Shape {
                public string Label;
                protected Shape(string label) { Label = label; }
                public abstract double Area();
                public override string ToString() => Label + ":" + Area();
            }
            public class Circle : Shape {
                public double Radius;
                public Circle(double r) : base("circle") { Radius = r; }
                public override double Area() => 3.0 * Radius * Radius;
            }
            public class Square : Shape {
                public int Side;
                public Square(int s) : base("square") { Side = s; }
                public override double Area() => Side * Side;
            }
            public static class Shapes {
                public static double TotalArea(Shape[] shapes) {
                    var total = 0.0;
                    for (var i = 0; i < shapes.Length; i++) total += shapes[i].Area();
                    return total;
                }
                public static string Describe(Shape s) => s.ToString();
            }

            // インターフェース経由の呼出
            public interface ICounter { int Next(); }
            public class Counter : ICounter {
                private int _n;
                public int Next() => ++_n;
            }
            public static class Counters {
                public static int Drain(ICounter c, int times) {
                    var last = 0;
                    for (var i = 0; i < times; i++) last = c.Next();
                    return last;
                }
                public static int DrainAsObject(object o) => ((ICounter)o).Next();
            }

            // 構造体のコピー独立性
            public struct Point {
                public int X;
                public int Y;
                public Point(int x, int y) { X = x; Y = y; }
                public int Sum() => X + Y;
            }
            public struct Line {
                public Point Start;
                public Point End;
            }
            public static class Structs {
                public static int CopyIndependence() {
                    var p = new Point(1, 2);
                    var q = p;
                    q.X = 100;
                    return p.X * 1000 + q.X;
                }
                public static int NestedCopy() {
                    var line = new Line();
                    line.Start = new Point(1, 1);
                    line.End = new Point(2, 2);
                    var copy = line;
                    copy.Start.X = 50;
                    return line.Start.X * 100 + copy.Start.X;
                }
                public static int ByRefMutation() {
                    var p = new Point(5, 6);
                    Mutate(ref p);
                    return p.X + p.Y;
                }
                private static void Mutate(ref Point p) { p.X += 3; p.Y += 3; }
                public static int CtorAndCall() {
                    var p = new Point(20, 22);
                    return p.Sum();
                }
            }

            // 静的フィールド + .cctor
            public static class Globals {
                public static int Counter;
                public static string Prefix;
                static Globals() {
                    Counter = 41;
                    Prefix = "n=";
                }
                public static int Bump() => ++Counter;
                public static string PrefixStatic() => Prefix;
            }

            // box / unbox
            public static class Boxing {
                public static int RoundTrip(object boxed) => (int)boxed;
                public static object BoxInt(int x) => x;
                public static string Concat(object a, object b) => a + "" + b;
                public static bool SameBox(int x) {
                    object a = x;
                    object b = x;
                    return a == b;
                }
            }

            // 配列
            public static class Arrays {
                public static int[] MakeInts(int n) {
                    var a = new int[n];
                    for (var i = 0; i < n; i++) a[i] = i * i;
                    return a;
                }
                public static int Sum(int[] a) {
                    var s = 0;
                    for (var i = 0; i < a.Length; i++) s += a[i];
                    return s;
                }
                public static int OutOfBounds() {
                    var a = new int[3];
                    return a[5];
                }
                public static string[] MakeStrings() {
                    var a = new string[2];
                    a[0] = "ab";
                    a[1] = "cd";
                    return a;
                }
                // 共変配列への不正書込
                public static void WriteWrongType(object[] arr) { arr[0] = 42; }
                public static int CovarianceWrite() {
                    var strings = new string[1];
                    WriteWrongType(strings);
                    return 0;
                }
                public static int ElemRef(int[] a) {
                    var r = a;
                    r[0] = 99;
                    return a[0];
                }
            }

            // キャスト
            public static class Casts {
                public static string IsinstOk(object o) {
                    var s = o as string;
                    return s == null ? "null" : s.Length.ToString();
                }
                public static int CastFail(object o) => (int)o;
                public static int CastOk(object o) => (int)o;
            }

            // 型初期化の起動順 (初回静的メンバアクセスで .cctor)
            public static class Lazy {
                public static int Value = Init();
                private static int Init() { Console.WriteLine("init"); return 7; }
                public static int Get() => Value;
            }
        }
        """;

    private static (Assembly Clr, byte[] Bytes) Compile() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source);
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(long? allocationLimit = null) {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy {
                InstructionQuota = 100_000_000,
                TotalAllocationByteLimit = allocationLimit ?? long.MaxValue,
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

    // ---- 継承・多態 ----

    [Fact]
    public void VirtualDispatch_MatchesClr() {
        using var vm = CreateVm();
        var circle = vm.CreateInstance("Vm.Circle", 2.0);
        var square = vm.CreateInstance("Vm.Square", 3);
        var shapeType = vm.Loaders[0].FindTypeByFullName("Vm.Shape")!;
        var arrayType = new VmArrayType { ElementType = shapeType };
        var arr = new VmArray(arrayType, [
            StackSlot.OfObject(circle),
            StackSlot.OfObject(square),
        ]);
        // Circle.Area = 3.0*2*2 = 12.0、Square.Area = 9.0 → 合計 21.0 (CLR 同式と一致)
        var total = vm.Invoke("Vm.Shapes", "TotalArea", arr);
        Assert.Equal(21.0, (double)total!);
    }

    [Fact]
    public void VirtualToString_MatchesClr() {
        using var vm = CreateVm();
        var circle = vm.CreateInstance("Vm.Circle", 2.0);
        var text = (string)vm.Invoke("Vm.Shapes", "Describe", circle)!;
        Assert.Equal("circle:12", text);
    }

    // ---- インターフェース ----

    [Fact]
    public void InterfaceDispatch_StateIsPerInstance() {
        using var vm = CreateVm();
        var c1 = vm.CreateInstance("Vm.Counter");
        var c2 = vm.CreateInstance("Vm.Counter");
        Assert.Equal(3, vm.Invoke("Vm.Counters", "Drain", c1, 3));
        Assert.Equal(1, vm.Invoke("Vm.Counters", "DrainAsObject", c2));
        // c1 の状態は保持される
        Assert.Equal(4, vm.Invoke("Vm.Counters", "Drain", c1, 1));
    }

    // ---- 構造体 ----

    [Fact]
    public void StructCopy_Independence_MatchesClr() {
        Assert.Equal(RunClr("Structs", "CopyIndependence"), CreateVm().Invoke("Vm.Structs", "CopyIndependence"));
        Assert.Equal(1100, CreateVm().Invoke("Vm.Structs", "CopyIndependence"));
    }

    [Fact]
    public void StructNestedCopy_MatchesClr() {
        Assert.Equal(RunClr("Structs", "NestedCopy"), CreateVm().Invoke("Vm.Structs", "NestedCopy"));
    }

    [Fact]
    public void StructByRefMutation_MatchesClr() {
        Assert.Equal(RunClr("Structs", "ByRefMutation"), CreateVm().Invoke("Vm.Structs", "ByRefMutation"));
    }

    [Fact]
    public void StructCtorAndInstanceCall_MatchesClr() {
        Assert.Equal(RunClr("Structs", "CtorAndCall"), CreateVm().Invoke("Vm.Structs", "CtorAndCall"));
    }

    // ---- 静的フィールド + .cctor ----

    [Fact]
    public void StaticFields_CctorRunsOnce() {
        using var vm = CreateVm();
        // .cctor が Counter=41 を設定し、最初の Bump で 42 になる
        Assert.Equal(42, vm.Invoke("Vm.Globals", "Bump"));
        Assert.Equal(43, vm.Invoke("Vm.Globals", "Bump"));
        Assert.Equal("n=", vm.Invoke("Vm.Globals", "PrefixStatic"));
    }

    [Fact]
    public void LazyInit_OutputsOnce() {
        using var vm = CreateVm();
        Assert.Equal(7, vm.Invoke("Vm.Lazy", "Get"));
        Assert.Equal(7, vm.Invoke("Vm.Lazy", "Get"));
        var initLines = vm.Console.OutputLog.Count(e => e.Text.Contains("init"));
        Assert.Equal(1, initLines); // .cctor は 1 回だけ
    }

    // ---- box / unbox ----

    [Fact]
    public void BoxUnbox_MatchesClr() {
        using var vm = CreateVm();
        var boxed = vm.Invoke("Vm.Boxing", "BoxInt", 42);
        Assert.IsType<VmBoxedValue>(boxed); // VM ヒープ上のボックス
        Assert.Equal(42, vm.Invoke("Vm.Boxing", "RoundTrip", boxed));
        Assert.Equal("12", vm.Invoke("Vm.Boxing", "Concat", 1, 2));
        Assert.Equal(RunClr("Boxing", "SameBox", 5), vm.Invoke("Vm.Boxing", "SameBox", 5));
    }

    // ---- 配列 ----

    [Fact]
    public void Arrays_MatchesClr() {
        using var vm = CreateVm();
        var ints = Assert.IsType<VmArray>(vm.Invoke("Vm.Arrays", "MakeInts", 5));
        Assert.Equal(5, ints.Length);
        Assert.Equal(0 + 1 + 4 + 9 + 16, vm.Invoke("Vm.Arrays", "Sum", ints));
        Assert.Equal(99, vm.Invoke("Vm.Arrays", "ElemRef", vm.Invoke("Vm.Arrays", "MakeInts", 2)));
        Assert.NotNull(vm.Invoke("Vm.Arrays", "MakeStrings"));
    }

    [Fact]
    public void ArrayOutOfBounds_ThrowsGuestException() {
        using var vm = CreateVm();
        Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Arrays", "OutOfBounds"));
    }

    [Fact]
    public void ArrayCovariantWrite_ThrowsGuestException() {
        using var vm = CreateVm();
        Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Arrays", "CovarianceWrite"));
    }

    // ---- キャスト ----

    [Fact]
    public void CastOperations_MatchClr() {
        using var vm = CreateVm();
        // as 演算子 (isinst)
        Assert.Equal("2", vm.Invoke("Vm.Casts", "IsinstOk", "ab"));
        Assert.Equal("null", vm.Invoke("Vm.Casts", "IsinstOk", 42));
        // 成功するキャスト
        Assert.Equal(42, vm.Invoke("Vm.Casts", "CastOk", vm.Invoke("Vm.Boxing", "BoxInt", 42)));
        // 失敗するキャスト (string → int)
        Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Casts", "CastFail", "ab"));
    }

    // ---- アロケーション quota ----

    [Fact]
    public void AllocationQuota_RejectsOvershoot() {
        using var vm = CreateVm(allocationLimit: 100);
        // int[5] は概算 24+16*5=104 バイト → 100 バイト上限で拒否
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("Vm.Arrays", "MakeInts", 5));
    }

    [Fact]
    public void AllocationQuota_AllowsSmallAllocations() {
        using var vm = CreateVm(allocationLimit: 1_000_000);
        // 0+1+4+9 = 14
        Assert.Equal(14, vm.Invoke("Vm.Arrays", "Sum", vm.Invoke("Vm.Arrays", "MakeInts", 4)));
    }

    // ---- ホスト API (CreateInstance/CallInstance) ----

    [Fact]
    public void HostApi_CreateInstanceAndCallInstance() {
        using var vm = CreateVm();
        var circle = vm.CreateInstance("Vm.Circle", 3.0);
        Assert.Equal(27.0, vm.CallInstance(circle, "Area"));
        Assert.Equal("circle:27", vm.CallInstance(circle, "ToString"));
    }
}
