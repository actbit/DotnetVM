using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class GuestTaskRuntimeHardeningTests {
    [Fact]
    public void WhenAnyRejectedInputsDoNotLeakRootsOrLiveHeapBytes() {
        using var quotaShared = new VmSharedState(maxGuestThreads: 64, maxTaskWorkers: 64,
            maxGuestWorkers: 64, maxPendingTaskTimers: 1_024, shutdownTimeoutMilliseconds: 5_000,
            culture: null, clockProvider: null, randomFill: null, timeZone: null,
            maxTaskCombinatorInputs: 1);
        var quotaHeap = new VmHeap(new MemoryPolicy());
        quotaHeap.AddRootSlotSource(quotaShared.GuestTasks.EnumerateRoots);
        var taskType = new VmIntrinsicType {
            Namespace = "System.Threading.Tasks",
            Name = "Task",
            IsValue = false,
        };
        var completed = quotaShared.GuestTasks.Create(taskType, completed: true);
        var baselineLiveBytes = quotaHeap.Collect().LiveBytes;

        for (var i = 0; i < 2_000; i++) {
            var composite = quotaHeap.Allocate(quotaShared.GuestTasks.Create(taskType));
            Assert.Throws<GuestConcurrencyLimitExceededException>(() =>
                quotaShared.GuestTasks.WhenAny(composite, [completed, completed]));
        }

        Assert.Empty(quotaShared.GuestTasks.EnumerateRoots());
        Assert.Equal(baselineLiveBytes, quotaHeap.Collect().LiveBytes);

        using var nullShared = new VmSharedState(maxGuestThreads: 64, maxTaskWorkers: 64,
            maxGuestWorkers: 64, maxPendingTaskTimers: 1_024, shutdownTimeoutMilliseconds: 5_000,
            culture: null, clockProvider: null, randomFill: null, timeZone: null,
            maxTaskCombinatorInputs: 2);
        var nullHeap = new VmHeap(new MemoryPolicy());
        nullHeap.AddRootSlotSource(nullShared.GuestTasks.EnumerateRoots);
        var nullTask = nullShared.GuestTasks.Create(taskType, completed: true);
        var nullBaselineLiveBytes = nullHeap.Collect().LiveBytes;
        for (var i = 0; i < 2_000; i++) {
            var composite = nullHeap.Allocate(nullShared.GuestTasks.Create(taskType));
            Assert.Throws<UnhandledGuestException>(() =>
                nullShared.GuestTasks.WhenAny(composite, [nullTask, null!]));
        }

        Assert.Empty(nullShared.GuestTasks.EnumerateRoots());
        Assert.Equal(nullBaselineLiveBytes, nullHeap.Collect().LiveBytes);
    }

    [Fact]
    public void CompletedDelayReleasesItsQuotaSlot() {
        using var shared = new VmSharedState(maxPendingTaskTimers: 1);
        var type = new VmIntrinsicType {
            Namespace = "System.Threading.Tasks",
            Name = "Task",
            IsValue = false,
        };
        var task = shared.GuestTasks.Create(type);

        shared.GuestTasks.Delay(task, 1);
        Assert.Equal(1, shared.GuestTasks.PendingTimerCount);
        Assert.True(task.Wait(1_000));
        Assert.True(SpinWait.SpinUntil(
            () => shared.GuestTasks.PendingTimerCount == 0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void PendingCancellationTimerStateIsRootedUntilTimerIsReleased() {
        using var shared = new VmSharedState(maxPendingTaskTimers: 1);
        var heap = new VmHeap(new MemoryPolicy());
        heap.AddRootSlotSource(shared.GuestTasks.EnumerateRoots);
        var type = new VmIntrinsicType {
            Namespace = "System.Threading",
            Name = "CancellationTokenSource",
            IsValue = false,
        };
        var state = heap.Allocate(new VmCancellationState(type));

        shared.GuestTasks.ScheduleCancellation(state, 10_000);
        Assert.Contains(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, state)));

        heap.Collect();
        Assert.Contains(state, heap.TrackedObjects);

        state.CancelAfter(Timeout.Infinite);
        Assert.Equal(0, shared.GuestTasks.PendingTimerCount);
        Assert.DoesNotContain(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, state)));

        heap.Collect();
        Assert.DoesNotContain(state, heap.TrackedObjects);
    }

    [Fact]
    public void FiredCancellationTimerReleasesItsRootAndQuotaSlot() {
        using var shared = new VmSharedState(maxPendingTaskTimers: 1);
        var heap = new VmHeap(new MemoryPolicy());
        heap.AddRootSlotSource(shared.GuestTasks.EnumerateRoots);
        var type = new VmIntrinsicType {
            Namespace = "System.Threading",
            Name = "CancellationTokenSource",
            IsValue = false,
        };
        var state = heap.Allocate(new VmCancellationState(type));

        shared.GuestTasks.ScheduleCancellation(state, 1);
        Assert.True(SpinWait.SpinUntil(
            () => shared.GuestTasks.PendingTimerCount == 0, TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, state)));
        heap.Collect();
        Assert.DoesNotContain(state, heap.TrackedObjects);
    }

    [Fact]
    public void ZeroDelayCancellationTimerIsPublishedBeforeItCanFire() {
        using var shared = new VmSharedState(maxPendingTaskTimers: 1);
        var type = new VmIntrinsicType {
            Namespace = "System.Threading",
            Name = "CancellationTokenSource",
            IsValue = false,
        };
        var state = new VmCancellationState(type);

        shared.GuestTasks.ScheduleCancellation(state, 0);
        Assert.True(SpinWait.SpinUntil(
            () => state.IsCancellationRequested && shared.GuestTasks.PendingTimerCount == 0,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DisposedCancellationSourceReleasesItsTimerRootAndQuotaSlot() {
        using var shared = new VmSharedState(maxPendingTaskTimers: 1);
        var heap = new VmHeap(new MemoryPolicy());
        heap.AddRootSlotSource(shared.GuestTasks.EnumerateRoots);
        var type = new VmIntrinsicType {
            Namespace = "System.Threading",
            Name = "CancellationTokenSource",
            IsValue = false,
        };
        var state = heap.Allocate(new VmCancellationState(type));

        shared.GuestTasks.ScheduleCancellation(state, 10_000);
        Assert.Equal(1, shared.GuestTasks.PendingTimerCount);
        state.Dispose();
        Assert.Equal(0, shared.GuestTasks.PendingTimerCount);
        Assert.DoesNotContain(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, state)));
    }
}
