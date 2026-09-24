using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// ゲスト Thread/Monitor、ホストからの同時呼出、Task/async-await の回帰テスト。
/// </summary>
public sealed class ConcurrencyTests {
    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Vm {
            public static class ConcurrentCode {
                private static int counter;
                private static int awaiterCallbackValue;

                public static int Increment() => Interlocked.Increment(ref counter);
                public static int GetCounter() => Interlocked.CompareExchange(ref counter, 0, 0);

                private static object gate = new object();
                private static int value;
                private static void Worker() {
                    Monitor.Enter(gate);
                    try {
                        value++;
                    }
                    finally {
                        Monitor.Exit(gate);
                    }
                }

                public static int RunThreads() {
                    gate = new object();
                    value = 0;
                    var first = new Thread(Worker);
                    var second = new Thread(Worker);
                    first.Start();
                    second.Start();
                    first.Join();
                    second.Join();
                    return value;
                }

                public static async Task<int> AddAfterDelay(int value) {
                    await Task.Delay(1);
                    return value + 1;
                }

                public static async ValueTask<int> AddAfterDelayValueTask(int value) {
                    await Task.Delay(1);
                    return value + 2;
                }
                public static async Task<int> AddAfterConfigureAwait(int value) {
                    await Task.Delay(1).ConfigureAwait(false);
                    return value + 1;
                }
                public static async ValueTask<int> AddAfterValueTaskConfigureAwait(int value) {
                    await ValueTask.FromResult(value).ConfigureAwait(false);
                    return value + 1;
                }
                public static async Task<int> FaultAsync() {
                    await Task.Delay(1);
                    throw new InvalidOperationException("task fault");
                }
                public static async ValueTask<int> FaultValueTaskAsync() {
                    await Task.Delay(1);
                    throw new InvalidOperationException("value task fault");
                }
                public static int RunFaultAsync() => FaultAsync().GetAwaiter().GetResult();
                public static int RunFaultValueTaskAsync() => FaultValueTaskAsync().GetAwaiter().GetResult();

                public static int RunAsync() => AddAfterDelay(41).GetAwaiter().GetResult();
                public static int RunCompletedAwait() => Task.FromResult(41).GetAwaiter().GetResult();
                public static int RunValueTaskAsync() => AddAfterDelayValueTask(40).GetAwaiter().GetResult();
                public static int RunValueTaskCompleted() => ValueTask.FromResult(41).GetAwaiter().GetResult();
                private static async Task<int> AwaitDefaultValueTask() {
                    await default(ValueTask);
                    return 41;
                }
                private static async Task<int> AwaitDefaultValueTaskOfT() => await default(ValueTask<int>);
                public static int RunDefaultValueTask() => AwaitDefaultValueTask().GetAwaiter().GetResult();
                public static int RunDefaultValueTaskOfT() => AwaitDefaultValueTaskOfT().GetAwaiter().GetResult();
                public static int RunDefaultValueTaskConfigureAwait() =>
                    default(ValueTask<int>).ConfigureAwait(false).GetAwaiter().GetResult();
                public static Task<int> DefaultValueTaskAsTask() => default(ValueTask<int>).AsTask();
                public static int ProbeDefaultValueTaskAllocations(int count) {
                    var voidValue = default(ValueTask);
                    var value = default(ValueTask<int>);
                    var completed = 0;
                    for (var i = 0; i < count; i++) {
                        if (voidValue.IsCompleted) completed++;
                        voidValue.GetAwaiter().GetResult();
                        if (value.IsCompleted) completed++;
                        completed += value.GetAwaiter().GetResult();
                        completed += value.Result;
                    }
                    return completed;
                }
                public static int RunValueTaskValueCtor() => new ValueTask<int>(41).GetAwaiter().GetResult();
                public static int RunValueTaskTaskCtor() => new ValueTask<int>(Task.FromResult(41)).GetAwaiter().GetResult();
                public static int RunConfigureAwait() => AddAfterConfigureAwait(41).GetAwaiter().GetResult();
                public static int RunValueTaskConfigureAwait() => AddAfterValueTaskConfigureAwait(41).GetAwaiter().GetResult();
                private static void MarkAwaiterCallback() => awaiterCallbackValue = 42;
                public static int DirectAwaiterCallback() {
                    awaiterCallbackValue = 0;
                    Task.Delay(1).GetAwaiter().OnCompleted(MarkAwaiterCallback);
                    Thread.Sleep(250);
                    return awaiterCallbackValue;
                }

                public static class InitializationProbe {
                    public static int Runs;
                    static InitializationProbe() {
                        Runs++;
                        Thread.Sleep(25);
                    }
                    public static int GetRuns() => Runs;
                }

                public static class FailingInitializationProbe {
                    static FailingInitializationProbe() => throw new InvalidOperationException("cctor failed");
                    public static int GetValue() => 7;
                }

                private static void HoldThreadWorker() => Thread.Sleep(250);
                private static void HoldTaskWorker() => Thread.Sleep(250);
                private static void HoldLongTaskWorker() => Thread.Sleep(5000);

                public static int ExceedThreadLimit() {
                    var first = new Thread(HoldThreadWorker);
                    var second = new Thread(HoldThreadWorker);
                    first.Start();
                    try {
                        second.Start();
                        return -1;
                    }
                    finally {
                        first.Join();
                    }
                }

                public static int ExceedTaskWorkerLimit() {
                    var first = Task.Run((Action)HoldTaskWorker);
                    try {
                        _ = Task.Run((Action)HoldTaskWorker);
                        return -1;
                    }
                    finally {
                        first.GetAwaiter().GetResult();
                    }
                }

                public static int RunSequentialTasks() {
                    var first = Task.Run((Action)HoldTaskWorker);
                    first.GetAwaiter().GetResult();
                    var second = Task.Run((Action)HoldTaskWorker);
                    second.GetAwaiter().GetResult();
                    return 2;
                }

                public static int ExceedSharedWorkerLimit() {
                    var first = new Thread(HoldThreadWorker);
                    first.Start();
                    try {
                        _ = Task.Run((Action)HoldTaskWorker);
                        return -1;
                    }
                    finally {
                        first.Join();
                    }
                }

                public static int ExceedTimerLimit() {
                    _ = Task.Delay(10000);
                    _ = Task.Delay(10000);
                    return -1;
                }

                public static Task StartLongTask() => Task.Run((Action)HoldLongTaskWorker);
            }
        }
        """;

    private static readonly byte[] AssemblyBytes =
        TestAssemblyCompiler.CompileToBytes(Source, "ConcurrencyAsm");

    private static VirtualMachine CreateVm(VmHostOptions? options = null) {
        var vm = new VirtualMachine(options ?? new VmHostOptions {
            MaxGuestThreads = 16,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(AssemblyBytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static void AssertGuestWorkersDrained(VirtualMachine vm) {
        Assert.True(SpinWait.SpinUntil(
            () => vm.SharedState.GuestThreads.ActiveCount == 0 &&
                  vm.SharedState.GuestTasks.ActiveWorkerCount == 0,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ExpectationsMatchClrBehavior() {
        var (_, assembly) = TestAssemblyCompiler.Compile(Source, "ConcurrencyClrOracle");
        var type = assembly.GetType("Vm.ConcurrentCode")!;
        static object? Call(Type target, string name, params object?[] args) =>
            target.GetMethod(name)!.Invoke(null, args);

        Assert.Equal(2, Call(type, "RunThreads"));
        Assert.Equal(42, Call(type, "RunAsync"));
        Assert.Equal(41, Call(type, "RunCompletedAwait"));
        Assert.Equal(42, Call(type, "RunValueTaskAsync"));
        Assert.Equal(41, Call(type, "RunValueTaskCompleted"));
        Assert.Equal(41, Call(type, "RunDefaultValueTask"));
        Assert.Equal(0, Call(type, "RunDefaultValueTaskOfT"));
        Assert.Equal(0, Call(type, "RunDefaultValueTaskConfigureAwait"));
        Assert.Equal(41, Call(type, "RunValueTaskValueCtor"));
        Assert.Equal(41, Call(type, "RunValueTaskTaskCtor"));
        Assert.Equal(42, Call(type, "RunConfigureAwait"));
        Assert.Equal(42, Call(type, "RunValueTaskConfigureAwait"));
        Assert.Equal(42, Call(type, "DirectAwaiterCallback"));
    }

    [Fact]
    public async Task HostCalls_CanRunConcurrently() {
        using var vm = CreateVm();
        var calls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => vm.Invoke("Vm.ConcurrentCode", "Increment")))
            .ToArray();

        var results = await Task.WhenAll(calls);

        Assert.Equal(32, results.Distinct().Count());
        Assert.Equal(32, vm.Invoke("Vm.ConcurrentCode", "GetCounter"));
    }

    [Fact]
    public void GuestThreads_UseMonitorForSharedState() {
        using var vm = CreateVm();

        Assert.Equal(2, vm.Invoke("Vm.ConcurrentCode", "RunThreads"));
    }

    [Fact]
    public void AsyncMethod_CanAwaitTaskDelay() {
        using var vm = CreateVm();

        Assert.Equal(42, vm.Invoke("Vm.ConcurrentCode", "RunAsync"));
    }

    [Fact]
    public void CompletedTask_AwaitReturnsResult() {
        using var vm = CreateVm();

        Assert.Equal(41, vm.Invoke("Vm.ConcurrentCode", "RunCompletedAwait"));
    }

    [Fact]
    public void AsyncValueTask_AwaitsTaskDelay() {
        using var vm = CreateVm();

        Assert.Equal(42, vm.Invoke("Vm.ConcurrentCode", "RunValueTaskAsync"));
    }

    [Fact]
    public void CompletedValueTask_AwaitReturnsResult() {
        using var vm = CreateVm();

        Assert.Equal(41, vm.Invoke("Vm.ConcurrentCode", "RunValueTaskCompleted"));
    }

    [Fact]
    public void DefaultValueTasks_MatchClrCompletedSuccessBehavior() {
        var (_, assembly) = TestAssemblyCompiler.Compile(Source, "DefaultValueTaskClrOracle");
        var clrType = assembly.GetType("Vm.ConcurrentCode")!;
        using var vm = CreateVm();

        foreach (var (method, expected) in new[] {
            ("RunDefaultValueTask", 41),
            ("RunDefaultValueTaskOfT", 0),
            ("RunDefaultValueTaskConfigureAwait", 0),
        }) {
            var clr = (int)clrType.GetMethod(method)!.Invoke(null, null)!;
            var actual = vm.Invoke("Vm.ConcurrentCode", method);
            Assert.Equal(expected, clr);
            Assert.Equal(clr, actual);
        }
    }

    [Fact]
    public void DefaultValueTask_InspectionDoesNotConsumeGuestAllocationQuota() {
        using var vm = CreateVm(new VmHostOptions {
            MaxGuestThreads = 16,
            Memory = new MemoryPolicy {
                InstructionQuota = 100_000_000,
                TotalAllocationByteLimit = 1_000_000,
            },
        });
        // 初回呼出しの型初期化による allocation を回帰対象から切り離す。
        Assert.Equal(0, vm.Invoke("Vm.ConcurrentCode", "ProbeDefaultValueTaskAllocations", 0));
        var before = vm.Heap.Snapshot();

        Assert.Equal(20_000, vm.Invoke("Vm.ConcurrentCode", "ProbeDefaultValueTaskAllocations", 10_000));

        var after = vm.Heap.Snapshot();
        Assert.Equal(before.TotalAllocatedBytes, after.TotalAllocatedBytes);
        Assert.Equal(before.LiveBytes, after.LiveBytes);
    }

    [Fact]
    public void DefaultValueTask_AsTaskAllocatesOneHeapTrackedTaskPerVmType() {
        using var vm = CreateVm();
        var before = vm.Heap.Snapshot();

        var first = Assert.IsType<VmTaskObject>(vm.Invoke("Vm.ConcurrentCode", "DefaultValueTaskAsTask"));
        var afterFirst = vm.Heap.Snapshot();
        Assert.True(afterFirst.TotalAllocatedBytes > before.TotalAllocatedBytes);
        Assert.True(first.IsCompleted);
        Assert.Equal(0, first.Snapshot().Result.AsInt32);

        var second = Assert.IsType<VmTaskObject>(vm.Invoke("Vm.ConcurrentCode", "DefaultValueTaskAsTask"));
        var afterSecond = vm.Heap.Snapshot();
        Assert.Same(first, second);
        Assert.Equal(afterFirst.TotalAllocatedBytes, afterSecond.TotalAllocatedBytes);
        Assert.Equal(afterFirst.LiveBytes, afterSecond.LiveBytes);
    }

    [Fact]
    public void ValueTaskConstructors_AwaitResults() {
        using var vm = CreateVm();

        Assert.Equal(41, vm.Invoke("Vm.ConcurrentCode", "RunValueTaskValueCtor"));
        Assert.Equal(41, vm.Invoke("Vm.ConcurrentCode", "RunValueTaskTaskCtor"));
    }

    [Fact]
    public void ConfigureAwait_WorksForTaskAndValueTask() {
        using var vm = CreateVm();

        Assert.Equal(42, vm.Invoke("Vm.ConcurrentCode", "RunConfigureAwait"));
        Assert.Equal(42, vm.Invoke("Vm.ConcurrentCode", "RunValueTaskConfigureAwait"));
        Assert.Equal(0, vm.Invoke("Vm.ConcurrentCode", "RunDefaultValueTaskConfigureAwait"));
    }

    [Fact]
    public void AsyncExceptions_AreObservedThroughTaskAndValueTaskAwaiters() {
        using var vm = CreateVm();

        var taskFailure = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.ConcurrentCode", "RunFaultAsync"));
        var valueTaskFailure = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.ConcurrentCode", "RunFaultValueTaskAsync"));
        Assert.Equal("System.InvalidOperationException", taskFailure.ExceptionTypeName);
        Assert.Equal("System.InvalidOperationException", valueTaskFailure.ExceptionTypeName);
    }

    [Fact]
    public void AwaiterOnCompleted_InvokesGuestContinuation() {
        using var vm = CreateVm();

        Assert.Equal(42, vm.Invoke("Vm.ConcurrentCode", "DirectAwaiterCallback"));
    }

    [Fact]
    public void StaticConstructors_RunOnceAndAreVmLocal() {
        using (var first = CreateVm()) {
            Assert.Equal(1, first.Invoke("Vm.ConcurrentCode+InitializationProbe", "GetRuns"));
            Assert.Equal(1, first.Invoke("Vm.ConcurrentCode+InitializationProbe", "GetRuns"));
        }

        using var second = CreateVm();
        Assert.Equal(1, second.Invoke("Vm.ConcurrentCode+InitializationProbe", "GetRuns"));
    }

    [Fact]
    public async Task StaticConstructors_AreSerializedWithoutHoldingTheTypeLock() {
        using var vm = CreateVm();
        var calls = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => vm.Invoke("Vm.ConcurrentCode+InitializationProbe", "GetRuns")))
            .ToArray();

        var results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.Equal(1, result));
    }

    [Fact]
    public void FailedStaticConstructor_IsCachedPerVm() {
        using var vm = CreateVm();

        var first = Assert.Throws<UnhandledGuestException>(() =>
            vm.Invoke("Vm.ConcurrentCode+FailingInitializationProbe", "GetValue"));
        var second = Assert.Throws<UnhandledGuestException>(() =>
            vm.Invoke("Vm.ConcurrentCode+FailingInitializationProbe", "GetValue"));

        Assert.Equal(first.ExceptionTypeName, second.ExceptionTypeName);
    }

    [Fact]
    public void GuestThreads_RejectTheConfiguredLimit() {
        using var vm = CreateVm(new VmHostOptions {
            MaxGuestThreads = 1,
            MaxGuestWorkers = 4,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });

        Assert.Throws<GuestConcurrencyLimitExceededException>(() =>
            vm.Invoke("Vm.ConcurrentCode", "ExceedThreadLimit"));
        AssertGuestWorkersDrained(vm);
    }

    [Fact]
    public void GuestTasks_RejectTheConfiguredWorkerLimit() {
        using var vm = CreateVm(new VmHostOptions {
            MaxTaskWorkers = 1,
            MaxGuestWorkers = 4,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });

        Assert.Throws<GuestConcurrencyLimitExceededException>(() =>
            vm.Invoke("Vm.ConcurrentCode", "ExceedTaskWorkerLimit"));
        AssertGuestWorkersDrained(vm);
    }

    [Fact]
    public void GuestTaskWorkerBudgetIsReleasedBeforeTaskCompletion() {
        using var vm = CreateVm(new VmHostOptions {
            MaxTaskWorkers = 1,
            MaxGuestWorkers = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });

        Assert.Equal(2, vm.Invoke("Vm.ConcurrentCode", "RunSequentialTasks"));
        AssertGuestWorkersDrained(vm);
    }

    [Fact]
    public void ThreadAndTaskWorkersShareOneVmWideBudget() {
        using var vm = CreateVm(new VmHostOptions {
            MaxGuestThreads = 4,
            MaxTaskWorkers = 4,
            MaxGuestWorkers = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });

        Assert.Throws<GuestConcurrencyLimitExceededException>(() =>
            vm.Invoke("Vm.ConcurrentCode", "ExceedSharedWorkerLimit"));
        AssertGuestWorkersDrained(vm);
    }

    [Fact]
    public void TaskDelay_RejectsTheConfiguredTimerLimitAndDisposesTimers() {
        var vm = CreateVm(new VmHostOptions {
            MaxPendingTaskTimers = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });

        Assert.Throws<GuestConcurrencyLimitExceededException>(() =>
            vm.Invoke("Vm.ConcurrentCode", "ExceedTimerLimit"));
        Assert.Equal(1, vm.SharedState.GuestTasks.PendingTimerCount);

        vm.Dispose();
        Assert.Equal(0, vm.SharedState.GuestTasks.PendingTimerCount);
    }

    [Fact]
    public void Dispose_StopsGuestTaskWorkersAndIsIdempotent() {
        var vm = CreateVm(new VmHostOptions {
            ShutdownTimeoutMilliseconds = 1_000,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        var task = Assert.IsType<VmTaskObject>(vm.Invoke("Vm.ConcurrentCode", "StartLongTask"));
        Assert.True(vm.SharedState.GuestTasks.ActiveWorkerCount >= 1);

        vm.Dispose();
        vm.Dispose();

        Assert.Equal(0, vm.SharedState.GuestTasks.ActiveWorkerCount);
        Assert.True(task.IsCompleted);
        Assert.IsType<ObjectDisposedException>(task.Snapshot().HostException);
    }

    [Fact]
    public void PublicOperations_RejectUseAfterDispose() {
        var vm = CreateVm();
        vm.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            vm.Invoke("Vm.ConcurrentCode", "GetCounter"));
        using var stream = new MemoryStream(AssemblyBytes);
        Assert.Throws<ObjectDisposedException>(() => vm.LoadAssembly(stream));
    }
}
