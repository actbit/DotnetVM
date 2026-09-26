using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// JIT を有効にした VM とインタプリタ専用 VM が、同じ guest IL に対して
/// 同じ意味論・例外を返すことを決定的な入力列で確認する。
/// </summary>
public sealed class InterpreterJitDifferentialTests {
    private const string Source = """
        namespace Vm;
        public static class Differential {
            public static int Arithmetic(int a, int b, int shift) {
                var divisor = b == 0 ? 1 : b;
                var amount = shift & 31;
                var value = unchecked(a + b);
                value = unchecked(value ^ (a * 31));
                value = unchecked(value + a / divisor + a % divisor);
                return unchecked((value << amount) ^ (value >> amount));
            }

            public static int Branches(int value) {
                if (value < 0) return -value;
                return value switch {
                    0 => 17,
                    1 => 31,
                    2 => 47,
                    _ => value + 100,
                };
            }

            public static int ArrayKernel(int seed, int count) {
                var values = new int[count];
                for (var i = 0; i < values.Length; i++)
                    values[i] = unchecked(seed + i * 17);
                var result = 0;
                for (var i = values.Length - 1; i >= 0; i--)
                    result = unchecked(result + values[i] ^ (i + 3));
                return result;
            }

            public static float FloatResult(float value) => (float)(value * 1.5f + 0.25f);

            public static double NativeUnsignedToDouble(ulong value) => (double)(nuint)value;

            public static int CheckedAdd(int left, int right) => checked(left + right);
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "InterpreterJitDifferential");
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(bool enableJit) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = 2_000_000 },
        });
        vm.LoadAssembly(new MemoryStream(Compiled.Bytes));
        return vm;
    }

    private static VmMethod Method(VirtualMachine vm, string name) =>
        vm.Loaders[0].FindTypeByFullName("Vm.Differential")!.Methods.Single(method => method.Name == name);

    private static object? RunClr(string method, params object?[] args) =>
        Compiled.Clr.GetType("Vm.Differential")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);

    [Fact]
    public void RandomScalarInputsMatchInterpreterAndJit() {
        using var interpreter = CreateVm(enableJit: false);
        using var jit = CreateVm(enableJit: true);
        var random = new Random(20260925);

        for (var i = 0; i < 256; i++) {
            var a = random.Next(int.MinValue, int.MaxValue);
            var b = random.Next(int.MinValue, int.MaxValue);
            var shift = random.Next(int.MinValue, int.MaxValue);
            var expected = RunClr("Arithmetic", a, b, shift);
            Assert.Equal(expected, interpreter.Invoke("Vm.Differential", "Arithmetic", a, b, shift));
            Assert.Equal(expected, jit.Invoke("Vm.Differential", "Arithmetic", a, b, shift));
        }

        Assert.True(jit.IsJitCompiled(Method(jit, "Arithmetic")));
    }

    [Fact]
    public void BranchAndArrayBoundariesMatchInterpreterAndJit() {
        using var interpreter = CreateVm(enableJit: false);
        using var jit = CreateVm(enableJit: true);

        foreach (var value in new[] { int.MinValue, -1, 0, 1, 2, 3, 99, int.MaxValue }) {
            var expected = RunClr("Branches", value);
            Assert.Equal(expected, interpreter.Invoke("Vm.Differential", "Branches", value));
            Assert.Equal(expected, jit.Invoke("Vm.Differential", "Branches", value));
        }

        foreach (var count in new[] { 0, 1, 2, 7, 16 }) {
            var expected = RunClr("ArrayKernel", -1234567, count);
            Assert.Equal(expected, interpreter.Invoke("Vm.Differential", "ArrayKernel", -1234567, count));
            Assert.Equal(expected, jit.Invoke("Vm.Differential", "ArrayKernel", -1234567, count));
        }

        Assert.True(jit.IsJitCompiled(Method(jit, "Branches")));
        Assert.True(jit.IsJitCompiled(Method(jit, "ArrayKernel")));
    }

    [Fact]
    public void FloatingAndNativeUnsignedBoundariesMatch() {
        using var interpreter = CreateVm(enableJit: false);
        using var jit = CreateVm(enableJit: true);

        foreach (var value in new[] { 0f, -0f, float.Epsilon, -1.5f, float.MaxValue }) {
            var expected = RunClr("FloatResult", value);
            Assert.Equal(expected, interpreter.Invoke("Vm.Differential", "FloatResult", value));
            Assert.Equal(expected, jit.Invoke("Vm.Differential", "FloatResult", value));
            Assert.IsType<float>(jit.Invoke("Vm.Differential", "FloatResult", value));
        }

        foreach (var value in new[] { 0UL, 1UL, uint.MaxValue, 0x1_0000_0000UL, ulong.MaxValue }) {
            var expected = RunClr("NativeUnsignedToDouble", value);
            Assert.Equal(expected, interpreter.Invoke("Vm.Differential", "NativeUnsignedToDouble", value));
            Assert.Equal(expected, jit.Invoke("Vm.Differential", "NativeUnsignedToDouble", value));
        }

        Assert.True(jit.IsJitCompiled(Method(jit, "FloatResult")));
        Assert.True(jit.IsJitCompiled(Method(jit, "NativeUnsignedToDouble")));
    }

    [Fact]
    public void OverflowExceptionMatchesAcrossExecutionEngines() {
        using var interpreter = CreateVm(enableJit: false);
        using var jit = CreateVm(enableJit: true);

        foreach (var vm in new[] { interpreter, jit }) {
            var error = Assert.Throws<UnhandledGuestException>(() =>
                vm.Invoke("Vm.Differential", "CheckedAdd", int.MaxValue, 1));
            Assert.Equal("System.OverflowException", error.ExceptionTypeName);
        }
        Assert.True(jit.IsJitCompiled(Method(jit, "CheckedAdd")));
    }
}
