using System.Diagnostics;
using System.Reflection;
using DotnetVM.BenchmarkGuest;
using DotnetVM.Host;

namespace DotnetVM.Benchmarks;

internal static class Program {
    private const string GuestType = "DotnetVM.BenchmarkGuest.Workloads";
    private const int WarmupInvocations = 3;
    private const int InvocationsPerSample = 5;
    private const int Samples = 9;
    private static readonly WorkloadDefinition[] Definitions = [
        new("Arithmetic", "ArithmeticLoop", 100_000),
        new("Branches", "BranchLoop", 100_000),
        new("Array access", "ArraySum", 10_000),
        new("Method calls", "CallLoop", 100_000),
        new("Object allocation", "ObjectLoop", 10_000),
    ];

    private static int Main() {
        var guestPath = Path.Combine(AppContext.BaseDirectory, "DotnetVM.BenchmarkGuest.dll");
        if (!File.Exists(guestPath)) {
            Console.Error.WriteLine($"ゲストアセンブリが見つかりません: {guestPath}");
            return 1;
        }

        var workloads = Definitions.Select(Bind).ToArray();
        var interpreted = Prepare(enableJit: false, guestPath);
        var jitted = Prepare(enableJit: true, guestPath);
        try {
            foreach (var workload in workloads) {
                Warmup(interpreted, workload);
                Warmup(jitted, workload);
                Warmup(workload);
            }

            Console.WriteLine("DotnetVM execution benchmark");
            Console.WriteLine($"Runtime: {Environment.Version}");
            Console.WriteLine($"OS: {Environment.OSVersion}");
            Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine("CoreCLR: same guest assembly called through a typed delegate");
            Console.WriteLine($"Samples: {Samples} x {InvocationsPerSample} invocations per pattern (warmup: {WarmupInvocations})");
            Console.WriteLine();
            Console.WriteLine("Pattern             CoreCLR ms   VM interp ms   VM JIT ms");
            Console.WriteLine("                    (median)     (median)      (median)");
            var summaries = new TimingSummary[workloads.Length];
            for (var i = 0; i < workloads.Length; i++)
                summaries[i] = Measure(interpreted, jitted, workloads[i]);
            for (var i = 0; i < workloads.Length; i++)
                Print(workloads[i], summaries[i]);
            Console.WriteLine();
            Console.WriteLine("Ratios: VM/CoreCLR and VM interpreter/JIT (median)");
            for (var i = 0; i < workloads.Length; i++) {
                var workload = workloads[i];
                var result = summaries[i];
                Console.WriteLine($"{workload.Name,-18} interp/CoreCLR {result.InterpreterToCoreClr,7:F1}x  " +
                    $"JIT/CoreCLR {result.JitToCoreClr,7:F1}x  interp/JIT {result.InterpreterToJit,6:F2}x");
            }
            return 0;
        } finally {
            interpreted.Dispose();
            jitted.Dispose();
        }
    }

    private static VirtualMachine Prepare(bool enableJit, string guestPath) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            // Compile the workload on its first invocation when JIT is enabled.
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy {
                InstructionQuota = 1_000_000_000,
            },
        });
        vm.LoadAssembly(guestPath);
        return vm;
    }

    private static Workload Bind(WorkloadDefinition definition) {
        var method = typeof(Workloads).GetMethod(definition.Method,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"ワークロードが見つかりません: {definition.Method}");
        var coreClr = (WorkloadDelegate)method.CreateDelegate(typeof(WorkloadDelegate));
        return new Workload(definition.Name, definition.Method, definition.Input, coreClr(definition.Input), coreClr);
    }

    private static void Warmup(VirtualMachine vm, Workload workload) {
        for (var i = 0; i < WarmupInvocations; i++)
            _ = InvokeAndCheck(vm, workload);
    }

    private static void Warmup(Workload workload) {
        for (var i = 0; i < WarmupInvocations; i++)
            _ = InvokeAndCheck(workload);
    }

    private static TimingSummary Measure(VirtualMachine interpreted, VirtualMachine jitted,
        Workload workload) {
        var samples = MeasureSamples(interpreted, jitted, workload);
        var coreClrMedian = Median(samples.CoreClr);
        var interpreterMedian = Median(samples.Interpreter);
        var jitMedian = Median(samples.Jit);
        return new TimingSummary(
            coreClrMedian, interpreterMedian, jitMedian,
            interpreterMedian / coreClrMedian,
            jitMedian / coreClrMedian,
            interpreterMedian / jitMedian);
    }

    private static TimingSamples MeasureSamples(VirtualMachine interpreted, VirtualMachine jitted,
        Workload workload) {
        var coreClr = new double[Samples];
        var interpreter = new double[Samples];
        var jit = new double[Samples];
        for (var sample = 0; sample < Samples; sample++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Rotate the order to avoid systematically favoring one mode when
            // the host changes frequency or receives background work.
            switch (sample % 3) {
                case 0:
                    coreClr[sample] = MeasureSample(() => InvokeAndCheck(workload));
                    interpreter[sample] = MeasureSample(() => InvokeAndCheck(interpreted, workload));
                    jit[sample] = MeasureSample(() => InvokeAndCheck(jitted, workload));
                    break;
                case 1:
                    jit[sample] = MeasureSample(() => InvokeAndCheck(jitted, workload));
                    coreClr[sample] = MeasureSample(() => InvokeAndCheck(workload));
                    interpreter[sample] = MeasureSample(() => InvokeAndCheck(interpreted, workload));
                    break;
                default:
                    interpreter[sample] = MeasureSample(() => InvokeAndCheck(interpreted, workload));
                    jit[sample] = MeasureSample(() => InvokeAndCheck(jitted, workload));
                    coreClr[sample] = MeasureSample(() => InvokeAndCheck(workload));
                    break;
            }
        }
        return new TimingSamples(coreClr, interpreter, jit);
    }

    private static double MeasureSample(Func<int> invoke) {
        var stopwatch = Stopwatch.StartNew();
        for (var invocation = 0; invocation < InvocationsPerSample; invocation++)
            _ = invoke();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static int InvokeAndCheck(VirtualMachine vm, Workload workload) {
        var result = vm.Invoke(GuestType, workload.Method, workload.Input);
        if (result is not int value)
            throw new InvalidOperationException($"予期しないベンチマーク結果です: {result}");
        if (value != workload.Expected)
            throw new InvalidOperationException($"{workload.Name} の VM 結果が CLR と一致しません: {value} (期待値 {workload.Expected})");
        return value;
    }

    private static int InvokeAndCheck(Workload workload) {
        var value = workload.CoreClr(workload.Input);
        if (value != workload.Expected)
            throw new InvalidOperationException($"{workload.Name} の CoreCLR 結果が不一致です: {value} (期待値 {workload.Expected})");
        return value;
    }

    private static double Median(double[] values) {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void Print(Workload workload, TimingSummary result) {
        Console.WriteLine($"{workload.Name,-18} {result.CoreClrMedian,11:F3} {result.InterpreterMedian,14:F3} {result.JitMedian,12:F3}");
    }

    private delegate int WorkloadDelegate(int input);
    private sealed record WorkloadDefinition(string Name, string Method, int Input);
    private sealed record Workload(string Name, string Method, int Input, int Expected, WorkloadDelegate CoreClr);
    private sealed record TimingSamples(double[] CoreClr, double[] Interpreter, double[] Jit);
    private sealed record TimingSummary(
        double CoreClrMedian, double InterpreterMedian, double JitMedian,
        double InterpreterToCoreClr, double JitToCoreClr, double InterpreterToJit);
}
