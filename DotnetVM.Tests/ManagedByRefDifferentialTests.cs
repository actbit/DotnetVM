using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// managed ByRef 命令について CLR / Interpreter / JIT の結果と guest exception を比較する。
/// 特に array opcode の符号拡張と値型コピーは、同じ StackSlot 表現でも命令幅を失うと
/// 実行エンジン間で差が出るため、個別 opcode を固定して確認する。
/// </summary>
public sealed class ManagedByRefDifferentialTests {
    private const string Source = """
        using System;
        namespace Vm;

        public enum Small : byte { One = 1 }

        public struct Pair {
            public int Left;
            public int Right;
        }

        public static class Probes {
            public static int SignedByte() {
                sbyte[] values = [-1];
                return values[0];
            }

            public static int UnsignedByte() {
                byte[] values = [255];
                return values[0];
            }

            public static int SignedShort() {
                short[] values = [-2];
                return values[0];
            }

            public static int UnsignedShort() {
                ushort[] values = [65534];
                return values[0];
            }

            public static uint UnsignedInt() {
                uint[] values = [uint.MaxValue];
                return values[0];
            }

            public static long NativeIntArray() {
                nint[] values = [unchecked((nint)long.MaxValue)];
                return (long)values[0];
            }

            public static long StoreNativeInt() {
                nint[] values = new nint[1];
                values[0] = (nint)(-7L);
                return (long)values[0];
            }

            public static int NestedFieldAlias(int value) {
                var holder = new Holder { Pair = new Pair { Left = value, Right = 3 } };
                Increment(ref holder.Pair.Left);
                return holder.Pair.Left + holder.Pair.Right;
            }

            public static int StructCopy() {
                var source = new Pair { Left = 4, Right = 5 };
                var copy = source;
                source.Left = 99;
                return copy.Left * 10 + copy.Right;
            }

            public static int UnboxExact() {
                object value = 42;
                return (int)value;
            }

            public static int UnboxIntFromEnum() {
                object value = Small.One;
                return (int)value;
            }

            public static int UnboxEnumFromInt() {
                object value = 1;
                return (int)(Small)value;
            }

            public static int NullArray() {
                int[]? values = null;
                return values![0];
            }

            public static int OutOfBounds() {
                var values = new int[1];
                return values[1];
            }

            private static void Increment(ref int value) => value++;

            private sealed class Holder {
                public Pair Pair;
            }
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "ManagedByRefDifferential");
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(bool enableJit) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000 },
        });
        vm.LoadAssembly(new MemoryStream(Compiled.Bytes));
        return vm;
    }

    private static VmMethod Method(VirtualMachine vm, string name) =>
        vm.Loaders[0].FindTypeByFullName("Vm.Probes")!.Methods.Single(method => method.Name == name);

    private static object? RunClr(string method, params object?[] args) =>
        Compiled.Clr.GetType("Vm.Probes")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);

    [Theory]
    [InlineData(ProbeNames.SignedByte)]
    [InlineData(ProbeNames.UnsignedByte)]
    [InlineData(ProbeNames.SignedShort)]
    [InlineData(ProbeNames.UnsignedShort)]
    [InlineData(ProbeNames.UnsignedInt)]
    public void ArrayIntegerExtensionsMatchClrInterpreterAndJit(string method) {
        var expected = RunClr(method);
        foreach (var enableJit in new[] { false, true }) {
            using var vm = CreateVm(enableJit);
            Assert.Equal(expected, vm.Invoke("Vm.Probes", method));
            if (enableJit)
                Assert.True(vm.IsJitCompiled(Method(vm, method)));
        }
    }

    [Theory]
    [InlineData(ProbeNames.NativeIntArray)]
    [InlineData(ProbeNames.StoreNativeInt)]
    public void NativeIntArrayOperationsUseTheFixed64BitVmSemantics(string method) {
        var expected = RunClr(method);
        foreach (var enableJit in new[] { false, true }) {
            using var vm = CreateVm(enableJit);
            Assert.Equal(expected, vm.Invoke("Vm.Probes", method));
            if (enableJit)
                Assert.True(vm.IsJitCompiled(Method(vm, method)));
        }

        Assert.Equal(VmPrimitiveTypes.NativeIntSizeBytes, 8);
        Assert.Equal(VmPrimitiveTypes.NativeIntBits, 64);
    }

    [Theory]
    [InlineData(ProbeNames.NestedFieldAlias)]
    [InlineData(ProbeNames.StructCopy)]
    public void ManagedByRefAliasingAndValueCopyMatchClrInterpreterAndJit(string method) {
        var args = method == ProbeNames.NestedFieldAlias ? new object?[] { 7 } : [];
        var expected = RunClr(method, args);
        foreach (var enableJit in new[] { false, true }) {
            using var vm = CreateVm(enableJit);
            Assert.Equal(expected, vm.Invoke("Vm.Probes", method, args));
            if (enableJit)
                Assert.True(vm.IsJitCompiled(Method(vm, method)));
        }
    }

    [Fact]
    public void ExactUnboxMatchesClrAndBothExecutionEngines() {
        foreach (var enableJit in new[] { false, true }) {
            using var vm = CreateVm(enableJit);
            Assert.Equal(RunClr(ProbeNames.UnboxExact), vm.Invoke("Vm.Probes", ProbeNames.UnboxExact));
            Assert.True(enableJit == false || vm.IsJitCompiled(Method(vm, ProbeNames.UnboxExact)));

            foreach (var method in new[] { ProbeNames.UnboxIntFromEnum, ProbeNames.UnboxEnumFromInt }) {
                var clrError = Assert.Throws<TargetInvocationException>(() => RunClr(method));
                Assert.IsType<InvalidCastException>(clrError.InnerException);
                var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Probes", method));
                Assert.Equal("System.InvalidCastException", error.ExceptionTypeName);
                if (enableJit)
                    Assert.True(vm.IsJitCompiled(Method(vm, method)));
            }
        }
    }

    [Theory]
    [InlineData(ProbeNames.NullArray, "System.NullReferenceException")]
    [InlineData(ProbeNames.OutOfBounds, "System.IndexOutOfRangeException")]
    public void ArrayFailuresRemainGuestExceptionsInInterpreterAndJit(string method, string exceptionType) {
        var clrError = Assert.Throws<TargetInvocationException>(() => RunClr(method));
        Assert.Equal(exceptionType, clrError.InnerException?.GetType().FullName);
        foreach (var enableJit in new[] { false, true }) {
            using var vm = CreateVm(enableJit);
            var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Probes", method));
            Assert.Equal(exceptionType, error.ExceptionTypeName);
            if (enableJit)
                Assert.True(vm.IsJitCompiled(Method(vm, method)));
        }
    }

    private static class ProbeNames {
        public const string SignedByte = "SignedByte";
        public const string UnsignedByte = "UnsignedByte";
        public const string SignedShort = "SignedShort";
        public const string UnsignedShort = "UnsignedShort";
        public const string UnsignedInt = "UnsignedInt";
        public const string NativeIntArray = "NativeIntArray";
        public const string StoreNativeInt = "StoreNativeInt";
        public const string NestedFieldAlias = "NestedFieldAlias";
        public const string StructCopy = "StructCopy";
        public const string UnboxExact = "UnboxExact";
        public const string UnboxIntFromEnum = "UnboxIntFromEnum";
        public const string UnboxEnumFromInt = "UnboxEnumFromInt";
        public const string NullArray = "NullArray";
        public const string OutOfBounds = "OutOfBounds";
    }
}
