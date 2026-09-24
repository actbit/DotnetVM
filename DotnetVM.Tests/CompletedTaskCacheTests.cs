using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class CompletedTaskCacheTests {
    [Fact]
    public void FailedSentinelAllocationIsRemovedAndCanRetryAfterCollection() {
        using var shared = new VmSharedState();
        var heap = new VmHeap(new MemoryPolicy { LiveObjectByteLimit = 48 });
        var taskType = new VmIntrinsicType {
            Namespace = "System.Threading.Tasks",
            Name = "Task",
            IsValue = false,
        };

        VmObject? retained = new VmTaskObject(taskType);
        heap.Allocate((VmTaskObject)retained);
        heap.AddRootObjectSource(() => retained is null ? [] : [retained]);

        Assert.Throws<MemoryQuotaExceededException>(() =>
            shared.GuestTasks.CompletedSentinel(taskType, default, heap));

        // The failed Lazy entry must not keep publishing an unregistered task root.
        Assert.Empty(shared.GuestTasks.EnumerateRoots());

        retained = null;
        heap.Collect();

        var task = shared.GuestTasks.CompletedSentinel(taskType, default, heap);
        Assert.True(task.IsCompleted);
        Assert.Contains(heap.TrackedObjects, candidate => ReferenceEquals(candidate, task));
        Assert.Contains(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, task)));
    }
}
