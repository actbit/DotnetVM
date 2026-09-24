using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class JitTests {
    private const string Source = """
        namespace Vm;
        public static class Calc {
            public static int Add(int left, int right) => left + right;
            public static int Select(int value) => value < 0 ? -value : value + 1;
            public static int Sum(int count) {
                var result = 0;
                for (var i = 0; i < count; i++)
                    result += i;
                return result;
            }
            public static int Pick(int value) {
                switch (value) {
                    case 1: return 10;
                    case 2: return 20;
                    default: return -1;
                }
            }
            public static string Text() => "compiled";
            public static int CallsInterpreter(int value) => System.Math.Abs(value);
            public static int Infinite() {
                var i = 0;
                while (true) i++;
            }
        }
        """;

    private static VirtualMachine CreateVm(int threshold = 1, long quota = 1_000_000) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = true,
            JitPromotionThreshold = threshold,
            Memory = new MemoryPolicy { InstructionQuota = quota },
        });
        vm.LoadAssembly(new MemoryStream(TestAssemblyCompiler.CompileToBytes(Source)));
        return vm;
    }

    private static VmMethod Method(VirtualMachine vm, string name) =>
        vm.Loaders[0].FindTypeByFullName("Vm.Calc")!.Methods.Single(method => method.Name == name);

    [Fact]
    public void HotScalarMethodIsPromotedAndKeepsVmSemantics() {
        using var vm = CreateVm();
        var method = Method(vm, nameof(CalcNames.Add));

        Assert.Equal(7, vm.Invoke("Vm.Calc", "Add", 3, 4));
        Assert.True(vm.IsJitCompiled(method));
        Assert.Equal(7, vm.Invoke("Vm.Calc", "Add", 3, 4));
    }

    [Fact]
    public void JitSupportsBranchesLoopsSwitchAndStrings() {
        using var vm = CreateVm();

        Assert.Equal(6, vm.Invoke("Vm.Calc", "Select", -6));
        Assert.Equal(7, vm.Invoke("Vm.Calc", "Select", 6));
        Assert.Equal(45, vm.Invoke("Vm.Calc", "Sum", 10));
        Assert.Equal(20, vm.Invoke("Vm.Calc", "Pick", 2));
        Assert.Equal(-1, vm.Invoke("Vm.Calc", "Pick", 9));
        Assert.Equal("compiled", vm.Invoke("Vm.Calc", "Text"));

        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Sum))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Pick))));
    }

    [Fact]
    public void UnsupportedCallsFallBackToInterpreter() {
        using var vm = CreateVm();

        Assert.Equal(12, vm.Invoke("Vm.Calc", "CallsInterpreter", -12));
        Assert.False(vm.IsJitCompiled(Method(vm, nameof(CalcNames.CallsInterpreter))));
    }

    [Fact]
    public void JitInstructionsStillConsumeQuota() {
        using var vm = CreateVm(quota: 1_000);

        Assert.Throws<InstructionQuotaExceededException>(() => vm.Invoke("Vm.Calc", "Infinite"));
        Assert.True(vm.InstructionCount > 1_000);
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Infinite))));
    }

    [Fact]
    public void ExhaustedQuotaDoesNotTriggerDynamicCompilation() {
        using var vm = CreateVm(quota: 0);
        var method = Method(vm, nameof(CalcNames.Add));

        Assert.Throws<InstructionQuotaExceededException>(() => vm.Invoke("Vm.Calc", "Add", 1, 2));
        Assert.False(vm.IsJitCompiled(method));
    }

    [Fact]
    public void InvalidPromotionThresholdIsRejected() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            EnableJit = true,
            JitPromotionThreshold = 0,
        }));
    }

    // The test source deliberately uses a different type name in nameof calls
    // so the production VM does not need to reference test guest types.
    private static class CalcNames {
        public const string Add = "Add";
        public const string Sum = "Sum";
        public const string Pick = "Pick";
        public const string CallsInterpreter = "CallsInterpreter";
        public const string Infinite = "Infinite";
    }
}
