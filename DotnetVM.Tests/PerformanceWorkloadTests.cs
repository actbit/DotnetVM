using System.Reflection;
using Microsoft.CodeAnalysis;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

public class PerformanceWorkloadTests {
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> DebugGuest = new(() => Compile(false, "GuestWorkloads.cs", "PerformanceDebugGuest"));
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> ReleaseGuest = new(() => Compile(true, "GuestWorkloads.cs", "PerformanceReleaseGuest"));
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> DebugLoops = new(() => Compile(false, "LoopWorkloads.cs", "PerformanceDebugLoops"));
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> ReleaseLoops = new(() => Compile(true, "LoopWorkloads.cs", "PerformanceReleaseLoops"));

    private static (byte[], Assembly) Compile(bool optimize, string resourceName, string assemblyName) {
        using var resource = typeof(PerformanceWorkloadTests).Assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(resource);
        var bytes = TestAssemblyCompiler.CompileToBytes(reader.ReadToEnd(), assemblyName,
            extraReferences: [MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location)], optimize: optimize);
        return (bytes, Assembly.Load(bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MixedWorkloadsMatchClrUnderGcPressure(bool optimize, bool jit) {
        var guest = (optimize ? ReleaseGuest : DebugGuest).Value;
        var loops = (optimize ? ReleaseLoops : DebugLoops).Value;
        using var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = true, EnableJit = jit, JitPromotionThreshold = 2,
            Memory = new MemoryPolicy { GcTriggerAllocationInterval = 8192 },
        });
        using var stream = new MemoryStream(guest.Bytes);
        vm.LoadAssembly(stream);
        using var loopStream = new MemoryStream(loops.Bytes);
        vm.LoadAssembly(loopStream);
        vm.LoadAssembly(typeof(Enumerable).Assembly.Location);
        const string typeName = "DotnetVM.Benchmarks.GuestWorkloads";
        foreach (var name in new[] { "Arithmetic", "FieldAccess", "GenericFieldAccess", "MethodCalls", "List", "ListGrowth", "Linq",
            "DictionaryInt", "DictionaryGrowth", "DictionaryString", "AsyncCompleted", "ValueTaskCompleted", "AsyncWorkers", "ReflectionInvoke" }) {
            var count = name == "AsyncWorkers" ? 8 : 80;
            var expected = (int)guest.Clr.GetType(typeName)!.GetMethod(name)!.Invoke(null, [count])!;
            // Repeat in one VM to exercise warm caches, state machine reuse and
            // JIT promotion. Bound suspension failures so they cannot hang CI.
            for (var repeat = 0; repeat < 3; repeat++) {
                var actual = await Task.Run(() => (int)vm.Invoke(typeName, name, count)!)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(expected == actual, $"{name}: CLR={expected}, VM={actual}, optimize={optimize}, jit={jit}");
            }
        }
        foreach (var name in new[] { "ArithmeticLoop", "BranchLoop", "ArraySum", "CallLoop", "ObjectLoop" }) {
            const string loopType = "DotnetVM.BenchmarkGuest.Workloads";
            const int count = 80;
            var expected = (int)loops.Clr.GetType(loopType)!.GetMethod(name)!.Invoke(null, [count])!;
            for (var repeat = 0; repeat < 3; repeat++) {
                var actual = (int)vm.Invoke(loopType, name, count)!;
                Assert.True(expected == actual, $"{name}: CLR={expected}, VM={actual}, optimize={optimize}, jit={jit}");
            }
        }
        Assert.True(vm.CollectGarbage().CollectionCount > 1);
    }
}
