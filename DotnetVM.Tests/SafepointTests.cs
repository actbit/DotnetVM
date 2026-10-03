using DotnetVM.Host;
using DotnetVM.Runtime.Objects;
using Xunit;

namespace DotnetVM.Tests;

public sealed class SafepointTests {
    private static readonly Lazy<byte[]> Guest = new(() => TestAssemblyCompiler.CompileToBytes("""
        public static class SafepointGuest {
            public static int Sum(int count) {
                var sum = 0;
                for (var i = 0; i < count; i++) sum += i;
                return sum;
            }

            public static int Allocate(int count) {
                var keep = new Node(42);
                var sum = 0;
                for (var i = 0; i < count; i++) {
                    var garbage = new Node(i);
                    sum += garbage.Value >= 0 ? 1 : 0;
                }
                return keep.Value + sum;
            }

            public sealed class Node(int value) {
                public int Value = value;
            }
        }
        """, "SafepointGuest"));

    private static VirtualMachine CreateVm(bool enableJit, long gcInterval = 1 << 20) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy {
                InstructionQuota = 100_000_000,
                GcTriggerAllocationInterval = gcInterval,
            },
        });
        vm.LoadAssembly(new MemoryStream(Guest.Value));
        return vm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocationFreeLoopDoesNotAllocateAtEverySafepoint(bool enableJit) {
        using var vm = CreateVm(enableJit);
        Assert.Equal(28, vm.Invoke("SafepointGuest", "Sum", 8));
        if (enableJit)
            Assert.True(vm.IsJitCompiled(vm.Loaders[0].FindTypeByFullName("SafepointGuest")!
                .Methods.Single(method => method.Name == "Sum")));
        var instructionsBefore = vm.InstructionCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = vm.Invoke("SafepointGuest", "Sum", 20_000);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(199_990_000, result);
        Assert.True(vm.InstructionCount - instructionsBefore > 100_000);
        // Allow invocation/frame bookkeeping, but reject per-instruction leases.
        Assert.True(allocated < 64 * 1024, $"Allocated {allocated:N0} host bytes.");
        Assert.Equal(0, vm.Heap.Snapshot().CollectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCollectionRunsBeforeAllocationFreeGuestCode(bool enableJit) {
        using var vm = CreateVm(enableJit, gcInterval: 1);
        var garbage = vm.Heap.Allocate(new VmFieldRvaData { Data = new byte[8] });

        Assert.Equal(28, vm.Invoke("SafepointGuest", "Sum", 8));

        Assert.True(vm.Heap.Snapshot().CollectionCount > 0);
        Assert.DoesNotContain(garbage, vm.Heap.TrackedObjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAllocatingGuestsKeepLocalRoots(bool enableJit) {
        using var vm = CreateVm(enableJit, gcInterval: 40);
        // Prepare the method before starting simultaneous calls.
        Assert.Equal(42, vm.Invoke("SafepointGuest", "Allocate", 0));
        if (enableJit)
            Assert.True(vm.IsJitCompiled(vm.Loaders[0].FindTypeByFullName("SafepointGuest")!
                .Methods.Single(method => method.Name == "Allocate")));

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => vm.Invoke("SafepointGuest", "Allocate", 1_000))));

        Assert.All(results, result => Assert.Equal(1_042, result));
        Assert.True(vm.Heap.Snapshot().CollectionCount > 0);
    }
}
