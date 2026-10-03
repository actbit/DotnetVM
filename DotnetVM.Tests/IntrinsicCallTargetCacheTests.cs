using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class IntrinsicCallTargetCacheTests {
    private static readonly Lazy<byte[]> Guest = new(() => TestAssemblyCompiler.CompileToBytes("""
        public static class AllocationGuest {
            public static int Allocate(int count) {
                var sum = 0;
                for (var i = 0; i < count; i++) sum += new Node(i).Value;
                return sum;
            }
            public sealed class Node {
                public int Value;
                public Node(int value) { Value = value; }
            }
        }
        """, "AllocationGuest"));

    private static VirtualMachine CreateVm(bool enableJit) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        vm.LoadAssembly(new MemoryStream(Guest.Value));
        return vm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorsReuseSuccessfulIntrinsicResolutionUntilAssemblySetChanges(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var probes = 0;
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException(),
            pathExists: _ => { probes++; return false; });
        var loader = vm.Loaders[0];
        // An explicit source path enables the normal same-directory resolution
        // path; the injected probe keeps this test independent of disk contents.
        loader.Image.SourcePath = Path.Combine(Path.GetTempPath(), "AllocationGuest.dll");
        context.Register(loader);

        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.True(probes > 0);
        if (enableJit)
            Assert.True(vm.IsJitCompiled(loader.FindTypeByFullName("AllocationGuest")!
                .Methods.Single(method => method.Name == "Allocate")));
        var initialProbes = probes;
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.Equal(initialProbes, probes);

        var other = new TypeLoader(DotnetVM.Metadata.AssemblyImage.Parse(
            TestAssemblyCompiler.CompileToBytes("public class Other {}", "Other")));
        context.Register(other);
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.True(probes > initialProbes);
        var afterRegister = probes;
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.Equal(afterRegister, probes);

        context.Unregister(other);
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.True(probes > afterRegister);

        context.Retire();
        Assert.Throws<UnhandledGuestException>(() => vm.Invoke("AllocationGuest", "Allocate", 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildContextDoesNotCacheBindingsAcrossParentChanges(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var probes = 0;
        var parent = new VmAssemblyContext(_ => throw new InvalidOperationException());
        var child = new VmAssemblyContext(_ => throw new InvalidOperationException(), parent,
            pathExists: _ => { probes++; return false; });
        var loader = vm.Loaders[0];
        loader.Image.SourcePath = Path.Combine(Path.GetTempPath(), "AllocationGuest.dll");
        child.Register(loader);

        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        var initialProbes = probes;
        Assert.True(initialProbes > 0);
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.True(probes > initialProbes);
        // A retired parent must still be consulted, even after successful calls.
        parent.Retire();
        var error = Record.Exception(() => vm.Invoke("AllocationGuest", "Allocate", 10));
        Assert.True(error is ObjectDisposedException or UnhandledGuestException);
        Assert.Contains("アンロード済み", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocationLoopAvoidsRepeatedSignatureAndBindingAllocations(bool enableJit) {
        using var vm = CreateVm(enableJit);
        Assert.Equal(45, vm.Invoke("AllocationGuest", "Allocate", 10));
        var instructionsBefore = vm.InstructionCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = vm.Invoke("AllocationGuest", "Allocate", 10_000);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(49_995_000, result);
        Assert.True(vm.InstructionCount - instructionsBefore > 100_000);
        // Includes VM objects, constructor frames and GC bookkeeping; reject
        // repeated resolution allocations without a timing assertion.
        Assert.True(allocated < 40_000_000, $"Allocated {allocated:N0} host bytes.");
    }
}
