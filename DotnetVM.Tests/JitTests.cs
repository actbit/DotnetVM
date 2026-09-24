using System.Reflection;
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
            public static int SignedCompare(int left, int right) {
                var result = 0;
                if (left < right) result += 1;
                if (left >= right) result += 10;
                if (left == right) result += 100;
                if (left != right) result += 1000;
                return result;
            }
            public static int UnsignedCompare(uint left, uint right) {
                var result = 0;
                if (left < right) result += 1;
                if (left > right) result += 2;
                return result;
            }
            public static double FloatOps(double value) => (value * 1.5 + 0.5) / 2.0;
            public static int Classify(double value) {
                if (value != value) return 1;
                if (value == 0.0) return 2;
                if (value < 0.0) return 3;
                return 4;
            }
            public static int CheckedAdd(int left, int right) => checked(left + right);
            public static int ConvertChecked(long value) => checked((int)value);
            public static int Shift(int value, int amount) =>
                (value << amount) ^ (value >> amount) ^ (int)((uint)value >> amount);
            public static int DivRem(int value, int divisor) => value / divisor + value % divisor;
            public static int NullState(string value) => value is null ? 1 : 0;
            public static int Reassign(int value, int replacement) {
                value = replacement;
                return value;
            }
            public static int CallsInterpreter(int value) => System.Math.Abs(value);
            public static int Infinite() {
                var i = 0;
                while (true) i++;
            }
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "JitTestsAssembly");
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(int threshold = 1, long quota = 1_000_000,
        bool enableJit = true, MemoryPolicy? memory = null) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = threshold,
            Memory = memory ?? new MemoryPolicy { InstructionQuota = quota },
        });
        vm.LoadAssembly(new MemoryStream(Compiled.Bytes));
        return vm;
    }

    private static VmMethod Method(VirtualMachine vm, string name) =>
        vm.Loaders[0].FindTypeByFullName("Vm.Calc")!.Methods.Single(method => method.Name == name);

    private static object? RunClr(string method, params object?[] args) =>
        Compiled.Clr.GetType("Vm.Calc")!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);

    private static void AssertScalarMatches(string method, params object?[] args) {
        using var interpreter = CreateVm(enableJit: false);
        using var jit = CreateVm();
        var clr = RunClr(method, args);
        var interpreted = interpreter.Invoke("Vm.Calc", method, args);
        var compiled = jit.Invoke("Vm.Calc", method, args);
        if (clr is double expectedDouble && double.IsNaN(expectedDouble)) {
            Assert.True(double.IsNaN(Assert.IsType<double>(interpreted)));
            Assert.True(double.IsNaN(Assert.IsType<double>(compiled)));
        } else {
            Assert.Equal(clr, interpreted);
            Assert.Equal(clr, compiled);
        }
        Assert.True(jit.IsJitCompiled(Method(jit, method)));
    }

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
    public void JitMatchesInterpreterAndClrAcrossScalarBoundaries() {
        foreach (var (left, right) in new[] {
            (int.MinValue, 0), (0, int.MinValue), (-1, -1), (42, 42), (int.MaxValue, -1),
        })
            AssertScalarMatches(nameof(CalcNames.SignedCompare), left, right);

        foreach (var (left, right) in new[] {
            (0u, uint.MaxValue), (uint.MaxValue, 0u), (uint.MaxValue, uint.MaxValue),
        })
            AssertScalarMatches(nameof(CalcNames.UnsignedCompare), left, right);

        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 1.25 }) {
            AssertScalarMatches(nameof(CalcNames.FloatOps), value);
            AssertScalarMatches(nameof(CalcNames.Classify), value);
        }
        foreach (var (value, amount) in new[] { (int.MinValue, 0), (int.MinValue, 1), (42, 31), (-7, 32) })
            AssertScalarMatches(nameof(CalcNames.Shift), value, amount);
        foreach (var (value, divisor) in new[] { (int.MinValue, 2), (-17, 5), (17, -5), (42, 1) })
            AssertScalarMatches(nameof(CalcNames.DivRem), value, divisor);
        AssertScalarMatches(nameof(CalcNames.ConvertChecked), 42L);
        AssertScalarMatches(nameof(CalcNames.NullState), (object?)null);
        AssertScalarMatches(nameof(CalcNames.NullState), "value");
        AssertScalarMatches(nameof(CalcNames.Reassign), 1, -7);
        AssertScalarMatches(nameof(CalcNames.CheckedAdd), 40, 2);
    }

    [Fact]
    public void CheckedArithmeticAndConversionsKeepGuestOverflowSemantics() {
        using var vm = CreateVm();
        Assert.Equal(3, vm.Invoke("Vm.Calc", nameof(CalcNames.CheckedAdd), 1, 2));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.CheckedAdd))));
        var addError = Assert.Throws<UnhandledGuestException>(() =>
            vm.Invoke("Vm.Calc", nameof(CalcNames.CheckedAdd), int.MaxValue, 1));
        Assert.Equal("System.OverflowException", addError.ExceptionTypeName);

        Assert.Equal(42, vm.Invoke("Vm.Calc", nameof(CalcNames.ConvertChecked), 42L));
        var convertError = Assert.Throws<UnhandledGuestException>(() =>
            vm.Invoke("Vm.Calc", nameof(CalcNames.ConvertChecked), (long)int.MaxValue + 1));
        Assert.Equal("System.OverflowException", convertError.ExceptionTypeName);
    }

    [Fact]
    public void UnsupportedCallsFallBackToInterpreter() {
        using var vm = CreateVm();

        Assert.Equal(12, vm.Invoke("Vm.Calc", "CallsInterpreter", -12));
        Assert.False(vm.IsJitCompiled(Method(vm, nameof(CalcNames.CallsInterpreter))));
    }

    [Fact]
    public void CompilationResourceLimitsFallBackToInterpreter() {
        foreach (var memory in new[] {
            new MemoryPolicy { InstructionQuota = 100_000, JitCompilationBudget = 0 },
            new MemoryPolicy { InstructionQuota = 100_000, HostWorkBudget = 0 },
            new MemoryPolicy { InstructionQuota = 100_000, HostTempAllocationByteLimit = 0 },
            new MemoryPolicy { InstructionQuota = 100_000, MaxJitCompiledMethods = 0 },
            new MemoryPolicy { InstructionQuota = 100_000, MaxJitExpressionNodes = 1 },
        }) {
            using var vm = CreateVm(memory: memory);
            Assert.Equal(7, vm.Invoke("Vm.Calc", "Add", 3, 4));
            Assert.False(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Add))));
        }
    }

    [Fact]
    public void JitCompiledMethodAndCacheEntryLimitsAreVmWide() {
        using (var vm = CreateVm(memory: new MemoryPolicy {
            InstructionQuota = 100_000, MaxJitCompiledMethods = 2,
        })) {
            _ = vm.Invoke("Vm.Calc", nameof(CalcNames.Add), 1, 2);
            _ = vm.Invoke("Vm.Calc", nameof(CalcNames.Select), 1);
            _ = vm.Invoke("Vm.Calc", nameof(CalcNames.Sum), 3);
            Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Add))));
            Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Select))));
            Assert.False(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Sum))));
        }

        using var limited = CreateVm(memory: new MemoryPolicy {
            InstructionQuota = 100_000, MaxJitCacheEntries = 1,
        });
        _ = limited.Invoke("Vm.Calc", "Add", 1, 2);
        _ = limited.Invoke("Vm.Calc", "Select", 1);
        Assert.True(limited.IsJitCompiled(Method(limited, nameof(CalcNames.Add))));
        Assert.False(limited.IsJitCompiled(Method(limited, nameof(CalcNames.Select))));
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
        public const string Select = "Select";
        public const string Sum = "Sum";
        public const string Pick = "Pick";
        public const string SignedCompare = "SignedCompare";
        public const string UnsignedCompare = "UnsignedCompare";
        public const string FloatOps = "FloatOps";
        public const string Classify = "Classify";
        public const string CheckedAdd = "CheckedAdd";
        public const string ConvertChecked = "ConvertChecked";
        public const string Shift = "Shift";
        public const string DivRem = "DivRem";
        public const string NullState = "NullState";
        public const string Reassign = "Reassign";
        public const string CallsInterpreter = "CallsInterpreter";
        public const string Infinite = "Infinite";
    }
}
