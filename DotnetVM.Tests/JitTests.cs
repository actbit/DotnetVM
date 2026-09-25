using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
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
            public static int SumArray(int count) {
                var values = new int[count];
                for (var i = 0; i < count; i++) values[i] = i * 2;
                var result = 0;
                for (var i = 0; i < values.Length; i++) result += values[i];
                return result;
            }
            public static int NewObjectAndField(int value) => new Holder(value).Value + 1;
            public static object BoxValue(int value) => value;
            public static int UnboxValue(int value) => (int)(object)value;
            public static int IsString(object value) => value is string ? 1 : 0;
            public static int CallVirt(string value) => value.Length;
            public static int StaticField() => StaticValue;
            public static int RefLocal(int value) {
                var local = value;
                Increment(ref local);
                return local;
            }
            public static int RefValueArgument(int value) {
                Increment(ref value);
                return value;
            }
            public static int RefArray(int value) {
                var values = new int[1];
                values[0] = value;
                Increment(ref values[0]);
                return values[0];
            }
            public static int RefField(int value) {
                var holder = new Holder(value);
                Increment(ref holder.Value);
                return holder.Value;
            }
            public static int RefStatic() {
                Increment(ref StaticValue);
                return StaticValue;
            }
            public static int ReadonlyArray(int value) {
                var values = new int[1];
                values[0] = value;
                ref readonly var item = ref values[0];
                return item;
            }
            public static int StructLdobj(int value) {
                var pair = new Pair { Left = value, Right = 2 };
                return ReadPair(ref pair).Left;
            }
            public static int StructInitobj() {
                ClearPair(out var pair);
                return pair.Left + pair.Right;
            }
            public static int StructStobj(int value) {
                var pair = new Pair { Left = 0, Right = 0 };
                var replacement = new Pair { Left = value, Right = 3 };
                WritePair(ref pair, replacement);
                return pair.Left + pair.Right;
            }
            public static string TokenTypeName() => typeof(Calc).Name;
            public static string TokenMethodName() =>
                System.Reflection.MethodBase.GetCurrentMethod()!.Name;
            public static void ThrowFromJit() => throw new System.InvalidOperationException("jit");
            public static int SizeOfInt() => sizeof(int);
            public static int Infinite() {
                var i = 0;
                while (true) i++;
            }
            private static int StaticValue;
            private static int Increment(ref int value) {
                value += 1;
                return value;
            }
            private static Pair ReadPair(ref Pair value) => value;
            private static void ClearPair(out Pair value) => value = default;
            private static void WritePair(ref Pair target, Pair value) => target = value;
        }
        public sealed class Holder {
            public int Value;
            public Holder(int value) => Value = value;
        }
        public struct Pair {
            public int Left;
            public int Right;
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
    public void CallsAndObjectOperationsPromote() {
        using var vm = CreateVm();

        Assert.Equal(12, vm.Invoke("Vm.Calc", "CallsInterpreter", -12));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.CallsInterpreter))));
        Assert.Equal(20, vm.Invoke("Vm.Calc", "SumArray", 5));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.SumArray))));
        Assert.Equal(8, vm.Invoke("Vm.Calc", "NewObjectAndField", 7));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.NewObjectAndField))));
        var boxed = Assert.IsType<VmBoxedValue>(vm.Invoke("Vm.Calc", "BoxValue", 42));
        Assert.Equal(42, boxed.Fields[0].AsInt32);
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.BoxValue))));
        Assert.Equal(42, vm.Invoke("Vm.Calc", "UnboxValue", 42));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.UnboxValue))));
        Assert.Equal(1, vm.Invoke("Vm.Calc", "IsString", "value"));
        Assert.Equal(0, vm.Invoke("Vm.Calc", "IsString", 42));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.IsString))));
        Assert.Equal(5, vm.Invoke("Vm.Calc", "CallVirt", "value"));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.CallVirt))));
        Assert.Equal(0, vm.Invoke("Vm.Calc", "StaticField"));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.StaticField))));
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Calc", "ThrowFromJit"));
        Assert.Equal("System.InvalidOperationException", error.ExceptionTypeName);
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.ThrowFromJit))));
        Assert.Equal(4, vm.Invoke("Vm.Calc", "SizeOfInt"));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.SizeOfInt))));
    }

    [Fact]
    public void JitSupportsManagedByRefsAndLdtoken() {
        using var vm = CreateVm();

        Assert.Equal(8, vm.Invoke("Vm.Calc", "RefLocal", 7));
        Assert.Equal(8, vm.Invoke("Vm.Calc", "RefValueArgument", 7));
        Assert.Equal(8, vm.Invoke("Vm.Calc", "RefArray", 7));
        Assert.Equal(8, vm.Invoke("Vm.Calc", "RefField", 7));
        Assert.Equal(1, vm.Invoke("Vm.Calc", "RefStatic"));
        Assert.Equal(7, vm.Invoke("Vm.Calc", "ReadonlyArray", 7));
        Assert.Equal(7, vm.Invoke("Vm.Calc", "StructLdobj", 7));
        Assert.Equal(0, vm.Invoke("Vm.Calc", "StructInitobj"));
        Assert.Equal(10, vm.Invoke("Vm.Calc", "StructStobj", 7));
        Assert.Equal("Calc", vm.Invoke("Vm.Calc", "TokenTypeName"));
        Assert.Equal("TokenMethodName", vm.Invoke("Vm.Calc", "TokenMethodName"));

        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.RefLocal))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.RefValueArgument))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.RefArray))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.RefField))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.RefStatic))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.ReadonlyArray))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.StructLdobj))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.StructInitobj))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.StructStobj))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.ReadPair))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.ClearPair))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.WritePair))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.TokenTypeName))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.TokenMethodName))));
        Assert.True(vm.IsJitCompiled(Method(vm, nameof(CalcNames.Increment))));
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
        public const string SumArray = "SumArray";
        public const string NewObjectAndField = "NewObjectAndField";
        public const string BoxValue = "BoxValue";
        public const string UnboxValue = "UnboxValue";
        public const string IsString = "IsString";
        public const string CallVirt = "CallVirt";
        public const string StaticField = "StaticField";
        public const string RefLocal = "RefLocal";
        public const string RefValueArgument = "RefValueArgument";
        public const string RefArray = "RefArray";
        public const string RefField = "RefField";
        public const string RefStatic = "RefStatic";
        public const string ReadonlyArray = "ReadonlyArray";
        public const string StructLdobj = "StructLdobj";
        public const string StructInitobj = "StructInitobj";
        public const string StructStobj = "StructStobj";
        public const string TokenTypeName = "TokenTypeName";
        public const string TokenMethodName = "TokenMethodName";
        public const string Increment = "Increment";
        public const string ReadPair = "ReadPair";
        public const string ClearPair = "ClearPair";
        public const string WritePair = "WritePair";
        public const string ThrowFromJit = "ThrowFromJit";
        public const string SizeOfInt = "SizeOfInt";
        public const string Infinite = "Infinite";
    }
}
