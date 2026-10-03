using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

public class SpanLeafCallTests {
    private const string Source = """
        public static class SpanCalls {
            public static int Mix(int left, int right) => unchecked(left * 31 + right);
            public static int Divide(int left, int right) => left / right;
            public static int CallDivide(int left, int right) => 7 + Divide(left, right);
            public static int Nested(int left, int right) => 17 + Mix(Mix(left, right), Mix(right, left));
            public static int Catch(int divisor) {
                try { return CallDivide(84, divisor); }
                catch (System.DivideByZeroException) { return -1; }
            }
            public static int Loop(int count) {
                var result = 0;
                for (var i = 0; i < count; i++) result = Mix(result, i);
                return result;
            }
        }
        """;
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> Guest = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "SpanLeafCalls", optimize: true);
        return (bytes, Assembly.Load(bytes));
    });

    private static VirtualMachine CreateVm(bool jit, long quota = 100_000) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = jit, JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = quota, GcTriggerAllocationInterval = 32 },
        });
        vm.LoadAssembly(new MemoryStream(Guest.Value.Bytes));
        return vm;
    }

    [Fact]
    public void NestedArgumentsAndExceptionRecoveryMatchClr() {
        using var vm = CreateVm(true);
        vm.Invoke("SpanCalls", "Mix", 1, 2);
        vm.Invoke("SpanCalls", "Divide", 8, 2);
        foreach (var (name, args) in new (string, object?[])[] {
            ("Nested", [3, 5]), ("Catch", [0]), ("Catch", [2]), ("Nested", [int.MaxValue, -17]),
        }) {
            var expected = Guest.Value.Clr.GetType("SpanCalls")!.GetMethod(name)!.Invoke(null, args);
            for (var repeat = 0; repeat < 3; repeat++)
                Assert.Equal(expected, vm.Invoke("SpanCalls", name, args));
        }
        Assert.True(vm.IsJitCompiled(vm.Loaders[0].FindTypeByFullName("SpanCalls")!.Methods.Single(m => m.Name == "Mix")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedCallsConsumeEveryInstruction(bool jit) {
        using var vm = CreateVm(jit, quota: 200);
        vm.Invoke("SpanCalls", "Mix", 1, 2);
        Assert.Throws<InstructionQuotaExceededException>(() => vm.Invoke("SpanCalls", "Loop", 100));
        Assert.Equal(201, vm.InstructionCount);
    }
}
