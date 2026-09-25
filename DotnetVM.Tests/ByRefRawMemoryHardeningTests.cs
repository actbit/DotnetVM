using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>ByRef/raw-memory の越境、overflow、readonly を guest 例外へ閉じ込めるテスト。</summary>
public sealed class ByRefRawMemoryHardeningTests {
    private const string Source = """
        using System;
        namespace Vm;
        public static unsafe class Raw {
            public static int DereferenceLast() {
                byte* p = stackalloc byte[4];
                p[3] = 42;
                return p[3];
            }

            public static void DereferenceOnePast() {
                byte* p = stackalloc byte[4];
                p[4] = 1;
            }

            public static void DereferenceNegative() {
                byte* p = stackalloc byte[4];
                p[-1] = 1;
            }

            public static int OverlapCopy() {
                byte* p = stackalloc byte[8];
                for (var i = 0; i < 8; i++) p[i] = (byte)i;
                for (var i = 5; i >= 0; i--) p[i + 2] = p[i];
                return p[2] * 100 + p[3] * 10 + p[7];
            }

            public static void NegativeAllocation() {
                var n = -1;
                int* p = stackalloc int[n];
                _ = p;
            }

            public static int ArrayElementAddress() {
                var values = new[] { 1, 2, 3 };
                ref var element = ref values[1];
                element = 42;
                return values[1];
            }

            public static int ReadOnlyArrayElementAddress() {
                var values = new[] { 7, 8, 9 };
                ref readonly var element = ref values[1];
                return element;
            }
        }
        """;

    private static readonly byte[] Bytes = TestAssemblyCompiler.CompileToBytes(Source, "ByRefRawMemoryHardening", allowUnsafe: true);

    private static VirtualMachine CreateVm(bool enableJit = false) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000 },
        });
        vm.LoadAssembly(new MemoryStream(Bytes));
        return vm;
    }

    [Fact]
    public void ValidLastByteAccessStillWorks() {
        using var vm = CreateVm();
        Assert.Equal(42, vm.Invoke("Vm.Raw", "DereferenceLast"));
    }

    [Theory]
    [InlineData("DereferenceOnePast", "System.IndexOutOfRangeException")]
    [InlineData("DereferenceNegative", "System.OverflowException")]
    public void NativePointerBoundaryIsGuestFailure(string method, string expectedException) {
        using var vm = CreateVm();
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Raw", method));
        Assert.Equal(expectedException, error.ExceptionTypeName);
    }

    [Fact]
    public void NegativeLocallocCannotReachHostAllocation() {
        using var vm = CreateVm();
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Raw", "NegativeAllocation"));
        Assert.Equal("System.OverflowException", error.ExceptionTypeName);
    }

    [Fact]
    public void ArrayElementByRefMutatesOnlyTheSelectedElement() {
        using var vm = CreateVm();
        Assert.Equal(42, vm.Invoke("Vm.Raw", "ArrayElementAddress"));
    }

    [Fact]
    public void ReadOnlyArrayByRefCanBeRead() {
        using var vm = CreateVm();
        Assert.Equal(8, vm.Invoke("Vm.Raw", "ReadOnlyArrayElementAddress"));
    }

    [Fact]
    public void ManagedByRefMethodPromotesToJitWithoutChangingResult() {
        using var vm = CreateVm(enableJit: true);
        Assert.Equal(42, vm.Invoke("Vm.Raw", "ArrayElementAddress"));
        var method = vm.Loaders[0].FindTypeByFullName("Vm.Raw")!.Methods
            .Single(candidate => candidate.Name == "ArrayElementAddress");
        Assert.True(vm.IsJitCompiled(method));
    }

    [Fact]
    public void InvalidAndReadonlyByRefsFailClosed() {
        var readOnly = new VmByRef([StackSlot.OfInt32(1)], 0, isReadOnly: true);
        Assert.Throws<UnhandledGuestException>(() => readOnly.Write(StackSlot.OfInt32(2)));

        var onePast = new VmByRef([], 0);
        Assert.Throws<UnhandledGuestException>(() => _ = onePast.Read());
    }

    [Fact]
    public void OverlappingNativeCopyUsesMemmoveSemantics() {
        using var vm = CreateVm();
        Assert.Equal(15, vm.Invoke("Vm.Raw", "OverlapCopy"));
    }
}
