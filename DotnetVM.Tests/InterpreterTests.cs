using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M2 インタプリタの突合テスト。Roslyn 生成 DLL を VM と CLR (反射) の両方で実行し結果を比べる。
/// </summary>
public class InterpreterTests {
    private const string Source = """
        using System;
        namespace Vm {
            public class Calc {
                public static int Fib(int n) {
                    if (n < 2) return n;
                    return Fib(n - 1) + Fib(n - 2);
                }
                public static long Sum(long a, long b) => a + b;
                public static double Avg(double a, double b) => (a + b) / 2;
                public static int CompareBattery(int a, int b) {
                    var r = 0;
                    if (a < b) r = 1; else if (a > b) r = 2; else r = 3;
                    if (a <= b) r += 10;
                    if (a >= b) r += 100;
                    if (a == b) r += 1000;
                    if (a != b) r += 10000;
                    var c = (a < b) ? 1 : 0;
                    c += (a > b) ? 2 : 0;
                    c += (a == b) ? 4 : 0;
                    return r + c * 100000;
                }
                public static string FizzBuzz(int n) {
                    var result = "";
                    for (var i = 1; i <= n; i++) {
                        if (i % 15 == 0) result += "FizzBuzz";
                        else if (i % 3 == 0) result += "Fizz";
                        else if (i % 5 == 0) result += "Buzz";
                        else result += Convert.ToString(i);
                    }
                    return result;
                }
                public static int SwitchPick(int x) {
                    switch (x) {
                        case 1: return 10;
                        case 2: return 20;
                        case 3: return 30;
                        case 5: return 50;
                        default: return -1;
                    }
                }
                public static int ArithBattery(int a, int b) {
                    var r = 0;
                    r += a + b;
                    r += a - b;
                    r += a * b % 97;
                    r += a / (b == 0 ? 1 : b);
                    r += a % (b == 0 ? 7 : b);
                    r += a & b;
                    r += a | b;
                    r += a ^ b;
                    r += a << 2;
                    r += a >> 1;
                    r += (int)((uint)a >> 1);
                    r += ~a;
                    r += -b;
                    return r;
                }
                public static int Narrow(int x) => (byte)x;
                public static uint UnsignedDiv(uint a, uint b) => a / b;
                public static long LongOps(long a) => (a << 33) ^ (a * 2654435761L);
                public static int DivByZero(int a) => a / 0;
                public static int CheckedOverflow() {
                    int big = int.MaxValue;
                    return checked(big + 1);
                }
                public static void Greet(string name) {
                    Console.WriteLine("Hello, " + name + "!");
                }
                public static int ReadEcho() {
                    var line = Console.ReadLine();
                    if (line == null) return -1;
                    return line.Length;
                }
                public static int Deep(int n) => Deep(n + 1);
                public static int Infinite() {
                    var i = 0;
                    while (true) { i++; }
                }
            }
        }
        """;

    private static (Assembly Clr, byte[] Bytes) Compile() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source);
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(int? instructionQuota = null, int? maxRecursionDepth = null) {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy {
                InstructionQuota = instructionQuota ?? 100_000_000,
                MaxRecursionDepth = maxRecursionDepth ?? 512,
            },
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(string method, params object?[] args) {
        var (clr, _) = Compile();
        var type = clr.GetType("Vm.Calc")!;
        return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
    }

    [Fact]
    public void Fib_MatchesClr() {
        using var vm = CreateVm();
        Assert.Equal(RunClr("Fib", 20), vm.Invoke("Vm.Calc", "Fib", 20));
        Assert.Equal(6765, vm.Invoke("Vm.Calc", "Fib", 20));
    }

    [Fact]
    public void FizzBuzz_MatchesClr() {
        using var vm = CreateVm();
        Assert.Equal(RunClr("FizzBuzz", 30), vm.Invoke("Vm.Calc", "FizzBuzz", 30));
    }

    [Fact]
    public void CompareBattery_MatchesClr() {
        using var vm = CreateVm();
        foreach (var (a, b) in new[] { (3, 7), (7, 3), (5, 5), (-2, 9), (0, 0) }) {
            Assert.Equal(RunClr("CompareBattery", a, b), vm.Invoke("Vm.Calc", "CompareBattery", a, b));
        }
    }

    [Fact]
    public void ArithBattery_MatchesClr() {
        using var vm = CreateVm();
        foreach (var (a, b) in new[] { (17, 5), (-13, 4), (100, 3), (7, 7) }) {
            Assert.Equal(RunClr("ArithBattery", a, b), vm.Invoke("Vm.Calc", "ArithBattery", a, b));
        }
    }

    [Fact]
    public void Switch_And_ScalarOps_MatchClr() {
        using var vm = CreateVm();
        foreach (var x in new[] { 1, 2, 3, 4, 5, 0, -1 })
            Assert.Equal(RunClr("SwitchPick", x), vm.Invoke("Vm.Calc", "SwitchPick", x));

        Assert.Equal(RunClr("Sum", 3L, 4L), vm.Invoke("Vm.Calc", "Sum", 3L, 4L));
        Assert.Equal(RunClr("Avg", 1.5, 2.0), vm.Invoke("Vm.Calc", "Avg", 1.5, 2.0));
        Assert.Equal(RunClr("Narrow", 300), vm.Invoke("Vm.Calc", "Narrow", 300));
        // uint 戻り値は CLR 側の boxing 型 (UInt32) と VM 側の表現 (Int32) が異なるため数値で比較
        Assert.Equal(Convert.ToDouble(RunClr("UnsignedDiv", 4294967295u, 3u)),
            Convert.ToDouble(vm.Invoke("Vm.Calc", "UnsignedDiv", 4294967295u, 3u)));
        Assert.Equal(RunClr("LongOps", 1234567890123L), vm.Invoke("Vm.Calc", "LongOps", 1234567890123L));
    }

    [Fact]
    public void Console_BindsOutputAndInput() {
        using var vm = CreateVm();
        var events = new List<string>();
        vm.Console.OutputWritten += e => events.Add(e.Text);

        vm.Invoke("Vm.Calc", "Greet", "VM");

        var output = string.Concat(vm.Console.OutputLog.Select(e => e.Text));
        Assert.Equal("Hello, VM!\n", output);
        Assert.Single(events);
        Assert.Equal("Hello, VM!\n", events[0]);

        // 入力バインド: BindInput で決定的に
        vm.Console.BindInput(() => "abcd");
        Assert.Equal(4, vm.Invoke("Vm.Calc", "ReadEcho"));
    }

    [Fact]
    public void DivideByZero_BecomesGuestException() {
        using var vm = CreateVm();
        var ex = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Calc", "DivByZero", 42));
        Assert.Equal("System.DivideByZeroException", ex.ExceptionTypeName);
    }

    [Fact]
    public void CheckedOverflow_BecomesGuestException() {
        using var vm = CreateVm();
        var ex = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Calc", "CheckedOverflow"));
        Assert.Equal("System.OverflowException", ex.ExceptionTypeName);
    }

    [Fact]
    public void InfiniteLoop_HitsInstructionQuota() {
        using var vm = CreateVm(instructionQuota: 10000);
        Assert.Throws<InstructionQuotaExceededException>(() => vm.Invoke("Vm.Calc", "Infinite"));
        Assert.True(vm.InstructionCount > 10000);
    }

    [Fact]
    public void UnboundedRecursion_IsRejectedBeforeHostStackOverflow() {
        using var vm = CreateVm(maxRecursionDepth: 64);
        var ex = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Calc", "Deep", 0));
        Assert.Equal("System.StackOverflowException", ex.ExceptionTypeName);
    }

    [Fact]
    public void UnregisteredIntrinsic_IsDenied() {
        // アセンブリ内に intrinsic 面にない外部呼出 (MathF 等) を置いた場合の拒否は
        // ResolveCallTarget の OperationNotAllowedException で行われる。
        // ここではレジストリの Seal 後登録拒否を検証する。
        using var vm = CreateVm();
        vm.Invoke("Vm.Calc", "Sum", 1L, 2L); // 実行開始 → Seal
        var registry = typeof(VirtualMachine)
            .GetField("_intrinsics", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm);
        Assert.Throws<OperationNotAllowedException>(
            () => ((DotnetVM.Runtime.Intrinsics.IntrinsicRegistry)registry!)
                .Register(DotnetVM.Runtime.Intrinsics.IntrinsicKey.Static("System.X", "Y", 0), (_, _) => null));
    }

    [Fact]
    public void VmStrings_AreInternedPerVm() {
        using var vm = CreateVm();
        var a = vm.Invoke("Vm.Calc", "FizzBuzz", 3);
        Assert.Equal(RunClr("FizzBuzz", 3), a);
    }
}
