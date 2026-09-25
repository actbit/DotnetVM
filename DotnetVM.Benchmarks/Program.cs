using System.Diagnostics;
using DotnetVM.BenchmarkGuest;
using DotnetVM.Host;

namespace DotnetVM.Benchmarks;

internal static class Program {
    private const string GuestType = "DotnetVM.BenchmarkGuest.Workloads";
    private const string GuestMethod = "ArithmeticLoop";
    private const int Iterations = 100_000;
    private const int WarmupInvocations = 3;
    private const int InvocationsPerSample = 5;
    private const int Samples = 9;
    private static readonly int ExpectedResult = Workloads.ArithmeticLoop(Iterations);

    private static int Main() {
        var guestPath = Path.Combine(AppContext.BaseDirectory, "DotnetVM.BenchmarkGuest.dll");
        if (!File.Exists(guestPath)) {
            Console.Error.WriteLine($"ゲストアセンブリが見つかりません: {guestPath}");
            return 1;
        }

        var interpreted = Prepare(enableJit: false, guestPath);
        var jitted = Prepare(enableJit: true, guestPath);
        try {
            Warmup(interpreted);
            Warmup(jitted);

            var (interpretedSamples, jittedSamples) = MeasureBoth(interpreted, jitted);
            var interpretedMedian = Median(interpretedSamples);
            var jittedMedian = Median(jittedSamples);
            var speedup = interpretedMedian / jittedMedian;

            Console.WriteLine("DotnetVM JIT benchmark");
            Console.WriteLine($"Runtime: {Environment.Version}");
            Console.WriteLine($"OS: {Environment.OSVersion}");
            Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine($"Workload: {GuestType}.{GuestMethod}({Iterations:N0})");
            Console.WriteLine($"Samples: {Samples} x {InvocationsPerSample} invocations (warmup: {WarmupInvocations})");
            Console.WriteLine();
            Console.WriteLine("Mode                 Median (ms)   Min (ms)   Max (ms)");
            Print("JIT disabled", interpretedSamples);
            Print("JIT enabled ", jittedSamples);
            Console.WriteLine($"Speedup (disabled / enabled): {speedup:F2}x");
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

    private static void Warmup(VirtualMachine vm) {
        for (var i = 0; i < WarmupInvocations; i++)
            _ = InvokeAndCheck(vm);
    }

    private static (double[] Interpreted, double[] Jitted) MeasureBoth(
        VirtualMachine interpreted, VirtualMachine jitted) {
        var interpretedSamples = new double[Samples];
        var jittedSamples = new double[Samples];
        for (var sample = 0; sample < Samples; sample++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Alternate the order to avoid systematically favoring one mode
            // when the host machine changes frequency or receives background work.
            if (sample % 2 == 0) {
                interpretedSamples[sample] = MeasureSample(interpreted);
                jittedSamples[sample] = MeasureSample(jitted);
            } else {
                jittedSamples[sample] = MeasureSample(jitted);
                interpretedSamples[sample] = MeasureSample(interpreted);
            }
        }
        return (interpretedSamples, jittedSamples);
    }

    private static double MeasureSample(VirtualMachine vm) {
        var stopwatch = Stopwatch.StartNew();
        for (var invocation = 0; invocation < InvocationsPerSample; invocation++)
            _ = InvokeAndCheck(vm);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static int InvokeAndCheck(VirtualMachine vm) {
        var result = vm.Invoke(GuestType, GuestMethod, Iterations);
        if (result is not int value)
            throw new InvalidOperationException($"予期しないベンチマーク結果です: {result}");
        if (value != ExpectedResult)
            throw new InvalidOperationException($"ベンチマーク結果が CLR と一致しません: {value} (期待値 {ExpectedResult})");
        return value;
    }

    private static double Median(double[] values) {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void Print(string label, double[] samples) {
        Console.WriteLine($"{label,-20} {Median(samples),10:F3} {samples.Min(),10:F3} {samples.Max(),10:F3}");
    }
}
