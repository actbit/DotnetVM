using System.Collections.Concurrent;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// VM 全体の security boundary を横断する回帰テスト。
/// 入力検証、quota 会計、bridge の fail-closed、GC handle、共有 registry、
/// unmanaged pointer の境界を、実装詳細ではなく拒否動作と状態不変条件で確認する。
/// </summary>
public sealed class SecurityBoundaryTests {
    [Fact]
    public void MemoryPolicy_RejectsInvalidValuesBeforeVmConstruction() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { MaxRecursionDepth = 0 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { HostWorkBudget = -1 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { MaxMetadataStreamBytes = -1 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { MaxPreparedMethodBytes = -1 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { LoadedAssemblyHostByteLimit = -1 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(new VmHostOptions {
            MaxBreakpoints = 0,
        }));
    }

    [Fact]
    public void AssemblyImage_RejectsEmptyAndTruncatedInputs() {
        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(Array.Empty<byte>()));
        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(new byte[64]));
        Assert.Throws<BadImageFormatException>(() => AssemblyImage.Parse(new byte[] { (byte)'M', (byte)'Z', 0, 0 }));
    }

    [Fact]
    public void VmHeap_RejectsNegativeReservationWithoutChangingAccounting() {
        var heap = new VmHeap(new MemoryPolicy());
        var before = heap.Snapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Reserve(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.ReserveArray(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.ReserveLocalloc(-1));

        Assert.Equal(before, heap.Snapshot());
    }

    [Fact]
    public void VmHeap_HostBufferQuotaFailureIsTransactional() {
        var heap = new VmHeap(new MemoryPolicy {
            HostTempAllocationByteLimit = 48,
            TotalAllocationByteLimit = 24,
        });

        // charCount=1 は 26 bytes。TotalAllocation の拒否時に host quota だけを
        // 先に消費すると、後続の最小バッファまで誤って拒否される。
        Assert.Throws<MemoryQuotaExceededException>(() => heap.ChargeHostBuffer(1));
        heap.ChargeHostBuffer(0);

        Assert.Equal(24, heap.Snapshot().TotalAllocatedBytes);
    }

    [Fact]
    public void VmHeap_RawHostBytesUseByteAccurateAccounting() {
        var heap = new VmHeap(new MemoryPolicy { LoadedAssemblyHostByteLimit = 32 });

        heap.ChargeHostBytes(8);

        Assert.Equal(32, heap.LoadedAssemblyBytes);
        Assert.Throws<MemoryQuotaExceededException>(() => heap.ChargeHostBytes(1));
    }

    [Fact]
    public void StreamAssemblyLoadChargesRetainedImageBytes() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "StreamAccountingInput");
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { LoadedAssemblyHostByteLimit = 24 + bytes.LongLength },
        });

        vm.LoadAssembly(new MemoryStream(bytes));

        Assert.Equal(24 + bytes.LongLength, vm.Heap.LoadedAssemblyBytes);
    }

    [Fact]
    public void DisposeReleasesLoadedImageBudgetAndInvalidatesGcHandles() {
        var bytes = TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "DisposeAccountingInput");
        var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(bytes));
        var handle = vm.Handles.Register(new VmFieldRvaData { Data = ReadOnlyMemory<byte>.Empty });
        Assert.True(vm.Heap.LoadedAssemblyBytes > 0);

        vm.Dispose();

        Assert.Equal(0, vm.Heap.LoadedAssemblyBytes);
        Assert.Equal(0, vm.Handles.Count);
        Assert.Null(vm.Handles.GetTarget(handle));
    }

    [Fact]
    public void VmHeap_RejectsNegativeHostWorkWithoutChangingAccounting() {
        var heap = new VmHeap(new MemoryPolicy { HostWorkBudget = 10 });

        Assert.Throws<ArgumentOutOfRangeException>(() => heap.ChargeHostWork(-1));
        heap.ChargeHostWork(10);
        Assert.Throws<MemoryQuotaExceededException>(() => heap.ChargeHostWork(1));
    }

    [Fact]
    public void VmHeap_ConcurrentAllocationsRemainWithinAccountingLimit() {
        const int count = 256;
        var heap = new VmHeap(new MemoryPolicy {
            TotalAllocationByteLimit = 24L * count,
            LiveObjectByteLimit = 24L * count,
        });

        Parallel.For(0, count, _ => heap.Allocate(new VmLocallocMemory { Bytes = [] }));

        var snapshot = heap.Snapshot();
        Assert.Equal(24L * count, snapshot.TotalAllocatedBytes);
        Assert.Equal(24L * count, snapshot.LiveBytes);
        Assert.Equal(count, heap.TrackedObjects.Count);
    }

    [Fact]
    public void GcHandleTable_IsSafeUnderConcurrentRegisterReadAndFree() {
        var table = new GcHandleTable();
        var target = new VmIntrinsicInstance(new VmIntrinsicType {
            Namespace = "Tests", Name = "HandleTarget", IsValue = false,
        });
        var handles = new ConcurrentBag<GcHandle>();

        Parallel.For(0, 256, _ => handles.Add(table.Register(target)));
        Assert.Equal(256, table.Count);

        Parallel.ForEach(handles, handle => Assert.Same(target, table.GetTarget(handle)));
        Parallel.ForEach(handles, table.Free);

        Assert.Equal(0, table.Count);
        Assert.Null(table.GetTarget(default));
    }

    [Fact]
    public void IntrinsicRegistry_IsSafeUnderConcurrentRegistrationAndLookup() {
        var registry = new IntrinsicRegistry();
        Parallel.For(0, 128, i => registry.Register(
            new IntrinsicKey("Tests.Intrinsic", $"M{i}", 0, false), static (_, _) => null));

        Parallel.For(0, 128, i => {
            Assert.True(registry.TryGet(new IntrinsicKey("Tests.Intrinsic", $"M{i}", 0, false), out _));
        });

        Assert.Equal(128, registry.Count);
        Assert.Equal(128, registry.Keys.Count());
        registry.Seal();
        Assert.Throws<OperationNotAllowedException>(() => registry.Register(
            new IntrinsicKey("Tests.Intrinsic", "AfterSeal", 0, false), static (_, _) => null));
    }

    [Fact]
    public void NetworkGateway_NullResponseFailsClosedWithoutCharging() {
        var bridge = new FakeNetworkBridge { Response = null };
        var gateway = new NetworkGateway(new NetworkPolicy {
            MaxBytesPerRequest = 10,
            TotalTransferByteLimit = 20,
        }, bridge);

        Assert.Throws<NetworkQuotaExceededException>(() => gateway.Transfer(
            "https://example.test/", ReadOnlyMemory<byte>.Empty));

        Assert.Equal(0, gateway.TotalBytesTransferred);
        Assert.Equal(1, bridge.Requests);
    }

    [Fact]
    public void NetworkGateway_RejectsNegativePolicyBeforeEnablingBridge() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkGateway(
            new NetworkPolicy { MaxBytesPerRequest = -1 }, new FakeNetworkBridge()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkGateway(
            new NetworkPolicy { TotalTransferByteLimit = -1 }, new FakeNetworkBridge()));
    }

    [Fact]
    public void NetworkGateway_PassesRemainingResponseBudgetToBridge() {
        var bridge = new FakeNetworkBridge { Response = [1, 2] };
        var gateway = new NetworkGateway(new NetworkPolicy {
            MaxBytesPerRequest = 10,
            TotalTransferByteLimit = 100,
        }, bridge);

        gateway.Transfer("https://example.test/", new byte[3]);

        var request = Assert.Single(bridge.Captured);
        Assert.Equal(7, request.MaxResponseBytes);
        Assert.Equal(5, gateway.TotalBytesTransferred);
    }

    [Fact]
    public void NetworkGateway_ConcurrentTransfersCannotBypassAggregateQuota() {
        var gateway = new NetworkGateway(new NetworkPolicy {
            MaxBytesPerRequest = 1,
            TotalTransferByteLimit = 40,
        }, new FakeNetworkBridge { Response = [1] });
        var rejected = 0;

        Parallel.For(0, 100, _ => {
            try {
                gateway.Transfer("https://example.test/", ReadOnlyMemory<byte>.Empty);
            } catch (NetworkQuotaExceededException) {
                Interlocked.Increment(ref rejected);
            }
        });

        Assert.Equal(40, gateway.TotalBytesTransferred);
        Assert.Equal(60, rejected);
    }

    [Fact]
    public void StorageGateway_NullResponseFailsClosedWithoutCharging() {
        var bridge = new FakeStorageBridge { Response = null };
        var gateway = new StorageGateway(new StoragePolicy {
            MaxBytesPerOperation = 10,
            TotalByteLimit = 20,
        }, bridge);

        Assert.Throws<StorageQuotaExceededException>(() => gateway.Read("/safe/file", 10));

        Assert.Equal(0, gateway.TotalBytes);
        Assert.Equal(1, bridge.ReadCount);
    }

    [Fact]
    public void StorageGateway_RejectsNegativePolicyAndBlankPath() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageGateway(
            new StoragePolicy { MaxBytesPerOperation = -1 }, new FakeStorageBridge()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageGateway(
            new StoragePolicy { TotalByteLimit = -1 }, new FakeStorageBridge()));

        var bridge = new FakeStorageBridge();
        var gateway = new StorageGateway(new StoragePolicy(), bridge);
        Assert.Throws<StorageQuotaExceededException>(() => gateway.Read("  ", 1));
        Assert.Equal(0, bridge.ReadCount);
    }

    [Fact]
    public void StorageGateway_ConcurrentReadsCannotBypassAggregateQuota() {
        var gateway = new StorageGateway(new StoragePolicy {
            MaxBytesPerOperation = 1,
            TotalByteLimit = 40,
        }, new FakeStorageBridge { Response = [1] });
        var rejected = 0;

        Parallel.For(0, 100, _ => {
            try {
                gateway.Read("/safe/file", 1);
            } catch (StorageQuotaExceededException) {
                Interlocked.Increment(ref rejected);
            }
        });

        Assert.Equal(40, gateway.TotalBytes);
        Assert.Equal(60, rejected);
    }

    [Fact]
    public void VmConsole_ConcurrentWritesKeepEveryOutputAndBindingSnapshot() {
        var console = new Devices.VmConsole();

        Parallel.For(0, 256, i => console.Write(false, i.ToString()));

        Assert.Equal(256, console.OutputLog.Count);
        Assert.Equal(256, console.OutputLog.Select(static entry => entry.Text).Distinct().Count());
    }

    [Fact]
    public void AssemblyLoadContext_ConcurrentUnloadRunsCleanupOnce() {
        var cleanupCount = 0;
        var loadContext = new VmAssemblyLoadContext {
            Context = new VmAssemblyContext(static _ => throw new NotSupportedException()),
            Name = "collectible",
            IsCollectible = true,
            IsDefault = false,
            UnloadAction = () => Interlocked.Increment(ref cleanupCount),
        };

        Parallel.For(0, 64, _ => loadContext.Unload());

        Assert.True(loadContext.IsUnloaded);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void NativePointer_RejectsOutOfBoundsReadAndWrite() {
        var pointer = new VmNativePointer {
            Memory = new VmLocallocMemory { Bytes = new byte[4] },
            ByteOffset = 3,
        };

        Assert.Throws<InvalidOperationException>(() => pointer.ReadInt16());
        Assert.Throws<InvalidOperationException>(() => pointer.WriteInt32(1));
    }

    [Fact]
    public void NativePointer_RejectsOrderingAcrossDifferentMemoryBlocks() {
        var left = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = new byte[8] } };
        var right = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = new byte[8] } };

        Assert.Throws<InvalidOperationException>(() => SlotOps.Compare(
            ILOp.Cgt, StackSlot.OfObject(left), StackSlot.OfObject(right)));
    }

    [Fact]
    public void GcHandleTable_DoubleFreeIsIdempotent() {
        var table = new GcHandleTable();
        var target = new VmIntrinsicInstance(new VmIntrinsicType {
            Namespace = "Tests", Name = "DoubleFreeTarget", IsValue = false,
        });
        var handle = table.Register(target);

        table.Free(handle);
        table.Free(handle);

        Assert.Null(table.GetTarget(handle));
        Assert.Equal(0, table.Count);
    }

    private sealed class FakeNetworkBridge : INetworkBridge {
        public byte[]? Response { get; init; } = [];
        public int Requests { get; private set; }
        public List<NetworkRequest> Captured { get; } = [];

        public byte[] Request(NetworkRequest request) {
            Requests++;
            Captured.Add(request);
            return Response!;
        }
    }

    private sealed class FakeStorageBridge : IStorageBridge {
        public byte[]? Response { get; init; } = [];
        public int ReadCount { get; private set; }
        public bool Exists(string path) => true;
        public byte[] Read(string path, long maxBytes) {
            ReadCount++;
            return Response!;
        }
        public void Write(string path, ReadOnlyMemory<byte> contents) { }
        public void Delete(string path) { }
    }
}
