using DotnetVM.Diagnostics;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>M9 の命令トレースと命令境界デバッガのテスト。</summary>
public sealed class ExecutionDiagnosticsTests {
    private const string Source = """
        namespace Vm.M9;
        public static class TraceTarget {
            public static int Calculate(int value) {
                var adjusted = value + 1;
                return adjusted * 2;
            }
        }
        """;

    [Fact]
    public void Tracer_Records_Instruction_Events_In_Execution_Order() {
        var pe = TestAssemblyCompiler.CompileToBytes(Source, "M9Trace");
        using var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(pe));

        vm.Tracer.Start(new ExecutionTraceOptions { MaxEvents = 1_000 });
        Assert.Equal(8, vm.Invoke("Vm.M9.TraceTarget", "Calculate", 3));
        vm.Tracer.Stop();

        var events = vm.Tracer.Events;
        Assert.NotEmpty(events);
        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.All(events, e => {
            Assert.Equal("M9Trace", e.Frame.AssemblyName);
            Assert.Equal("Vm.M9.TraceTarget", e.Frame.TypeFullName);
            Assert.Equal("Calculate", e.Frame.MethodName);
            Assert.False(string.IsNullOrWhiteSpace(e.OpName));
        });
        Assert.Equal(0, vm.Tracer.DroppedEventCount);
        Assert.Contains(vm.Tracer.Frames, f => f.TypeFullName == "Vm.M9.TraceTarget" && f.MethodName == "Calculate");
    }

    [Fact]
    public void Tracer_Enforces_Event_Limit_Without_Changing_Result() {
        var pe = TestAssemblyCompiler.CompileToBytes(Source, "M9TraceLimit");
        using var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(pe));

        vm.Tracer.Start(new ExecutionTraceOptions { MaxEvents = 1 });
        Assert.Equal(8, vm.Invoke("Vm.M9.TraceTarget", "Calculate", 3));
        vm.Tracer.Stop();

        Assert.Single(vm.Tracer.Events);
        Assert.True(vm.Tracer.DroppedEventCount > 0);
    }

    [Fact]
    public async Task Tracer_Orders_Events_When_Guest_Calls_Run_Concurrently() {
        var pe = TestAssemblyCompiler.CompileToBytes(Source, "M9TraceParallel");
        using var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(pe));

        vm.Tracer.Start(new ExecutionTraceOptions { MaxEvents = 10_000 });
        var calls = Enumerable.Range(0, 8)
            .Select(value => Task.Run(() => vm.Invoke("Vm.M9.TraceTarget", "Calculate", value)))
            .ToArray();
        await Task.WhenAll(calls);
        vm.Tracer.Stop();

        var events = vm.Tracer.Events;
        Assert.NotEmpty(events);
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public void Debugger_Stops_At_Breakpoint_And_Supports_StepInto() {
        var pe = TestAssemblyCompiler.CompileToBytes(Source, "M9Debugger");
        using var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(pe));

        var debugger = vm.Debugger;
        debugger.AddBreakpoint("Vm.M9.TraceTarget", "Calculate", 0, "M9Debugger");
        var stops = new List<DebuggerStop>();
        debugger.Stopped += (_, args) => {
            stops.Add(args.Stop);
            if (stops.Count == 1)
                debugger.StepInto();
            else
                debugger.Continue();
        };

        Assert.Equal(8, vm.Invoke("Vm.M9.TraceTarget", "Calculate", 3));

        Assert.True(stops.Count >= 2);
        Assert.Equal(DebuggerStopReason.Breakpoint, stops[0].Reason);
        Assert.Equal(0, stops[0].Instruction.IlOffset);
        Assert.Equal(DebuggerStopReason.Step, stops[1].Reason);
        Assert.True(stops[1].Instruction.Sequence > stops[0].Instruction.Sequence);
        Assert.False(debugger.IsPaused);
    }

    [Fact]
    public async Task Debugger_Dispose_Releases_A_Paused_Execution() {
        var pe = TestAssemblyCompiler.CompileToBytes(Source, "M9DebuggerDispose");
        using var vm = new VirtualMachine();
        vm.LoadAssembly(new MemoryStream(pe));
        vm.Debugger.AddBreakpoint("Vm.M9.TraceTarget", "Calculate", 0, "M9DebuggerDispose");

        var task = Task.Run(() => vm.Invoke("Vm.M9.TraceTarget", "Calculate", 3));
        var stop = vm.Debugger.WaitForBreak(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(0, stop.Instruction.IlOffset);

        vm.Debugger.Dispose();
        Assert.Equal(8, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
