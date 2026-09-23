using DotnetVM.Host;
using DotnetVM.Policy;
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

                public static int RunAsync() => AddAfterDelay(41).GetAwaiter().GetResult();
                public static int RunCompletedAwait() => Task.FromResult(41).GetAwaiter().GetResult();
            }
        }
        """;

    private static readonly byte[] AssemblyBytes =
        TestAssemblyCompiler.CompileToBytes(Source, "ConcurrencyAsm");

    private static VirtualMachine CreateVm() {
        var vm = new VirtualMachine(new VmHostOptions {
            MaxGuestThreads = 16,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(AssemblyBytes);
        vm.LoadAssembly(stream);
        return vm;
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
}
