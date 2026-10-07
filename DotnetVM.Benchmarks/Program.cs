using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DotnetVM.Benchmarks;
using DotnetVM.Host;

var samples = 7;
var warmups = 3;
var enableJit = false;
var instructionCharging = true;
var instructionChargeBatchSize = 256;
var legacy = false;
string? output = null;
string? filter = null;
string? revision = null;
int? size = null;
var trace = false;
for (var i = 0; i < args.Length; i++) {
    switch (args[i]) {
        case "--jit": enableJit = true; break;
        case "--no-instruction-charging": instructionCharging = false; break;
        case "--charge-batch-size": instructionChargeBatchSize = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--legacy": legacy = true; break;
        case "--samples": samples = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--warmups": warmups = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--output": output = args[++i]; break;
        case "--filter": filter = args[++i]; break;
        case "--revision": revision = args[++i]; break;
        case "--size": size = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--trace": trace = true; break;
        default: throw new ArgumentException($"Unknown option: {args[i]}");
    }
}
if (samples < 1 || warmups < 1) throw new ArgumentException("Samples and warmups must be positive.");
if (size is < 0) throw new ArgumentException("Size must be nonnegative.");
if (legacy) {
    Environment.ExitCode = LegacyBenchmarks.Run(output);
    return;
}

var mixedGuest = typeof(GuestWorkloads);
var loopGuest = typeof(DotnetVM.BenchmarkGuest.Workloads);
var workloads = new (string Name, Type GuestType, int Count)[] {
    (nameof(GuestWorkloads.SpanCopies), mixedGuest, 1000),
    (nameof(GuestWorkloads.IntegerFormatting), mixedGuest, 1000),
    (nameof(GuestWorkloads.IntegerParsing), mixedGuest, 1000),
    (nameof(GuestWorkloads.StringCopies), mixedGuest, 1000),
    (nameof(GuestWorkloads.ReflectionInvoke), mixedGuest, 100),
    (nameof(GuestWorkloads.ReflectionAttributes), mixedGuest, 50),
    (nameof(GuestWorkloads.JsonRoundTrip), mixedGuest, 10),
    (nameof(GuestWorkloads.Arithmetic), mixedGuest, 5000),
    (nameof(DotnetVM.BenchmarkGuest.Workloads.ArithmeticLoop), loopGuest, 100000),
    (nameof(DotnetVM.BenchmarkGuest.Workloads.BranchLoop), loopGuest, 100000),
    (nameof(DotnetVM.BenchmarkGuest.Workloads.ArraySum), loopGuest, 10000),
    (nameof(GuestWorkloads.FieldAccess), mixedGuest, 5000),
    (nameof(GuestWorkloads.GenericFieldAccess), mixedGuest, 5000),
    (nameof(GuestWorkloads.MethodCalls), mixedGuest, 1000),
    (nameof(DotnetVM.BenchmarkGuest.Workloads.CallLoop), loopGuest, 100000),
    (nameof(DotnetVM.BenchmarkGuest.Workloads.ObjectLoop), loopGuest, 10000),
    (nameof(GuestWorkloads.List), mixedGuest, 500),
    (nameof(GuestWorkloads.ListGrowth), mixedGuest, 500),
    (nameof(GuestWorkloads.Linq), mixedGuest, 500),
    (nameof(GuestWorkloads.DictionaryInt), mixedGuest, 200),
    (nameof(GuestWorkloads.DictionaryGrowth), mixedGuest, 200),
    (nameof(GuestWorkloads.DictionaryString), mixedGuest, 200),
    (nameof(GuestWorkloads.AsyncCompleted), mixedGuest, 200),
    (nameof(GuestWorkloads.ValueTaskCompleted), mixedGuest, 200),
    (nameof(GuestWorkloads.AsyncWorkers), mixedGuest, 8),
};
var results = new List<object>();
Console.WriteLine($"{Environment.Version}; JIT={enableJit}; instruction-charging={instructionCharging}; charge-batch-size={instructionChargeBatchSize}; warmups={warmups}; samples={samples}");
Console.WriteLine("Workload                 CoreCLR ms      VM ms   VM / CLR    Alloc bytes    Instructions");
foreach (var workload in workloads) {
    if (filter is not null && !workload.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var count = size ?? workload.Count;
    var method = workload.GuestType.GetMethod(workload.Name, BindingFlags.Public | BindingFlags.Static)!;
    var coreClrRun = method.CreateDelegate<Func<int, int>>();
    var expected = coreClrRun(count);
    using var vm = new VirtualMachine(new VmHostOptions {
        LoadHostCoreLib = true,
        EnableJit = enableJit,
        JitPromotionThreshold = 2,
            Memory = new MemoryPolicy {
                InstructionQuota = long.MaxValue,
                InstructionChargingEnabled = instructionCharging,
                InstructionChargeBatchSize = instructionChargeBatchSize,
                HostWorkBudget = long.MaxValue,
            },
    });
    vm.LoadAssembly(workload.GuestType.Assembly.Location);
    vm.LoadAssembly(typeof(Enumerable).Assembly.Location);
    void Run() {
        var actual = (int)vm.Invoke(workload.GuestType.FullName!, workload.Name, count)!;
        if (actual != expected) throw new InvalidOperationException($"{workload.Name}: CLR={expected}, VM={actual}");
    }
    try {
        var coreClr = MeasureCoreClr(coreClrRun, count, expected, warmups, samples);
        if (trace) vm.Tracer.Start();
        for (var i = 0; i < warmups; i++) Run();
        vm.Tracer.Stop();
        var elapsed = new double[samples];
        var allocated = new long[samples];
        var instructions = new long[samples];
        for (var i = 0; i < samples; i++) {
            var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
            var instructionBefore = vm.InstructionCount;
            var start = Stopwatch.GetTimestamp();
            Run();
            elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            instructions[i] = vm.InstructionCount - instructionBefore;
            allocated[i] = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        Array.Sort(instructions);
        var median = samples / 2;
        var ratio = elapsed[median] / coreClr.MedianMilliseconds;
        Console.WriteLine($"{workload.Name,-24} {coreClr.MedianMilliseconds,10:F6} {elapsed[median],10:F3} {ratio,10:F1} {allocated[median],14:N0} {instructions[median],15:N0}");
        results.Add(new { workload.Name, DeclaringType = workload.GuestType.FullName, Count = count, MedianMilliseconds = elapsed[median],
            MedianAllocatedBytes = allocated[median], Instructions = instructions[median], Expected = expected,
            ElapsedMilliseconds = elapsed, CoreClr = coreClr, VmToCoreClrRatio = ratio });
    } catch (Exception error) {
        vm.Tracer.Stop();
        Console.WriteLine($"{workload.Name,-24} ERROR: {error.GetType().Name}: {error.Message}");
        if (trace) foreach (var frame in vm.Tracer.Frames.TakeLast(25)) Console.WriteLine(frame);
        results.Add(new { workload.Name, Count = count, Error = error.GetType().Name, error.Message });
        Environment.ExitCode = 1;
    }
}
if (output is not null) {
    var path = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(),
        MeasuredAt = DateTimeOffset.UtcNow, OS = Environment.OSVersion.ToString(), SourceRevision = revision,
        HostTieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
        Protocol = "unified-25", LoadHostCoreLib = true, JitPromotionThreshold = 2,
        EnableJit = enableJit, Warmups = warmups, Samples = samples, Results = results },
        new JsonSerializerOptions { WriteIndented = true }));
}

static CoreClrMeasurement MeasureCoreClr(Func<int, int> run, int count, int expected, int warmups, int samples) {
    for (var i = 0; i < warmups; i++) Check(RunBatch(run, count, 1), expected);

    // Batch short CoreCLR calls so timer resolution and sampling overhead are negligible.
    const double targetMilliseconds = 50;
    const int maxIterations = 1_000_000;
    var iterations = 1;
    while (true) {
        var start = Stopwatch.GetTimestamp();
        var actual = RunBatch(run, count, iterations);
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Check(actual, expected);
        if (milliseconds >= targetMilliseconds || iterations == maxIterations) break;
        var scale = Math.Clamp(targetMilliseconds / Math.Max(milliseconds, 0.001), 2, 10);
        iterations = (int)Math.Min(maxIterations, Math.Ceiling(iterations * scale));
    }

    var elapsed = new double[samples];
    var allocated = new double[samples];
    for (var i = 0; i < samples; i++) {
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        var actual = RunBatch(run, count, iterations);
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        allocated[i] = (double)(GC.GetTotalAllocatedBytes(precise: true) - allocationBefore) / iterations;
        Check(actual, expected);
    }
    Array.Sort(elapsed);
    Array.Sort(allocated);
    return new CoreClrMeasurement(elapsed[samples / 2], allocated[samples / 2], iterations, elapsed);

    static int RunBatch(Func<int, int> run, int count, int iterations) {
        var actual = 0;
        for (var i = 0; i < iterations; i++) actual = run(count);
        return actual;
    }

    static void Check(int actual, int expected) {
        if (actual != expected) throw new InvalidOperationException($"CoreCLR returned {actual}, expected {expected}.");
    }
}

sealed record CoreClrMeasurement(double MedianMilliseconds, double MedianAllocatedBytes,
    int IterationsPerSample, double[] ElapsedMilliseconds);
