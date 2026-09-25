using System.Globalization;
using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

public sealed class CultureDifferentialTests {
    private const string Source = """
        using System;
        using System.Globalization;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Vm.CultureChecks {
            public static class Ops {
                public static string TurkishCase() =>
                    "iIİı".ToUpper() + "|" + "iIİı".ToLower() + "|" +
                    char.ToUpper('i') + "|" + char.ToLower('I');
                public static int Compare(string left, string right) => string.Compare(left, right);
                public static string Sort() {
                    string[] values = ["i", "I", "İ", "ı", "ä", "z", "å", "ö"];
                    Array.Sort(values);
                    return string.Join("|", values);
                }
                public static string ParseDate(string value) => DateTime.Parse(value).ToString("D");
                public static decimal ParseDecimal(string value) => decimal.Parse(value);
                public static string ParseTime(string value) => TimeSpan.Parse(value).ToString();
                public static decimal ParseCommaDecimal() => decimal.Parse("1,5");
                public static string NumericCulture() =>
                    12345.67.ToString("N2") + "|" + int.Parse("1.234", NumberStyles.Number).ToString("N0") +
                    "|" + string.Format("{0:N2}", 12345.67);
                public static string NumericDoubleParse() =>
                    double.Parse("1.234,5", NumberStyles.Number).ToString("N1");
                public static string NumericConvert() =>
                    Convert.ToDouble("1.234,5").ToString("N1");
                public static decimal TaskWorkerCulture() =>
                    Task.Run(ParseCommaDecimal).GetAwaiter().GetResult();
                public static decimal ThreadWorkerCulture() {
                    decimal result = 0;
                    var thread = new Thread(() => result = ParseCommaDecimal());
                    thread.Start();
                    thread.Join();
                    return result;
                }
                public static void ThrowAfterCultureWork() {
                    _ = "i".ToUpper();
                    throw new InvalidOperationException("culture scope restore");
                }
                public static string Normalize(string value) => value.Normalize();
                public static bool IsNormalized(string value) => value.IsNormalized();
            }
        }
        """;

    private static readonly Lazy<(Assembly Clr, byte[] Bytes)> Compiled = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "CultureDifferentialAsm");
        return (Assembly.Load(bytes), bytes);
    });

    private static VirtualMachine CreateVm(CultureInfo culture, MemoryPolicy? memory = null,
        bool loadHostCoreLib = true) {
        var vm = new VirtualMachine(new VmHostOptions {
            Culture = culture,
            LoadHostCoreLib = loadHostCoreLib,
            Memory = memory ?? new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(Compiled.Value.Bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(CultureInfo culture, string method, params object?[] args) {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return Compiled.Value.Clr.GetType("Vm.CultureChecks.Ops")!
                .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, args);
        } finally {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static void AssertMatchesClr(CultureInfo culture, string method, params object?[] args) {
        var expected = RunClr(culture, method, args);
        using var vm = CreateVm(culture);
        Assert.Equal(expected, vm.Invoke("Vm.CultureChecks.Ops", method, args));
    }

    [Fact]
    public void TurkishCaseMappingAndComparison_MatchClr() {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        AssertMatchesClr(culture, "TurkishCase");
        AssertMatchesClr(culture, "Compare", "i", "I");
        AssertMatchesClr(culture, "Compare", "ı", "I");
        AssertMatchesClr(culture, "Sort");
    }

    [Fact]
    public void GermanStringSortAndDateTimeDecimalTimeSpanParsing_MatchClr() {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        AssertMatchesClr(culture, "Compare", "ä", "z");
        AssertMatchesClr(culture, "Sort");
        AssertMatchesClr(culture, "ParseDate", "31.12.2024");
        AssertMatchesClr(culture, "ParseDecimal", "1.234,50");
        AssertMatchesClr(culture, "ParseTime", "1:02:03,5");
    }

    [Fact]
    public void NumericFormattingAndParsing_UseConfiguredCultureWithOrWithoutHostCoreLib() {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var expected = RunClr(culture, "NumericCulture");
        foreach (var loadHostCoreLib in new[] { true, false }) {
            using var vm = CreateVm(culture, loadHostCoreLib: loadHostCoreLib);
            Assert.Equal(expected, vm.Invoke("Vm.CultureChecks.Ops", "NumericCulture"));
            Assert.Equal(RunClr(culture, "NumericDoubleParse"),
                vm.Invoke("Vm.CultureChecks.Ops", "NumericDoubleParse"));
            Assert.Equal(RunClr(culture, "NumericConvert"),
                vm.Invoke("Vm.CultureChecks.Ops", "NumericConvert"));
        }
    }

    [Fact]
    public void JapaneseDateAndStringCultureFaces_MatchClr() {
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        AssertMatchesClr(culture, "ParseDate", "令和6年5月1日");
        AssertMatchesClr(culture, "Compare", "あ", "ア");
        AssertMatchesClr(culture, "Sort");
    }

    [Fact]
    public async Task ConcurrentVms_KeepCultureIsolatedAndRestoreCallingThreads() {
        var turkish = CreateVm(CultureInfo.GetCultureInfo("tr-TR"));
        var english = CreateVm(CultureInfo.GetCultureInfo("en-US"));
        using (turkish)
        using (english) {
            using var barrier = new Barrier(2);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Task RunWorker(VirtualMachine vm, decimal expected, string hostCulture) => Task.Run(() => {
                var oldCulture = CultureInfo.CurrentCulture;
                var oldUiCulture = CultureInfo.CurrentUICulture;
                var ambient = CultureInfo.GetCultureInfo(hostCulture);
                CultureInfo.CurrentCulture = ambient;
                CultureInfo.CurrentUICulture = ambient;
                try {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    for (var i = 0; i < 250; i++) {
                        var actual = vm.Invoke("Vm.CultureChecks.Ops", "ParseCommaDecimal");
                        if (!Equals(expected, actual))
                            failures.Enqueue($"{hostCulture}: expected {expected}, got {actual}");
                        if (CultureInfo.CurrentCulture.Name != hostCulture ||
                            CultureInfo.CurrentUICulture.Name != hostCulture)
                            failures.Enqueue($"{hostCulture}: caller culture was not restored");
                    }
                } finally {
                    CultureInfo.CurrentCulture = oldCulture;
                    CultureInfo.CurrentUICulture = oldUiCulture;
                }
            });

            await Task.WhenAll(
                RunWorker(turkish, 1.5m, "fr-FR"),
                RunWorker(english, 15m, "nl-NL"));
            Assert.Empty(failures);
        }
    }

    [Fact]
    public void GuestThreadAndTaskWorkers_UseVmCultureAndRestoreHostCulture() {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        using var vm = CreateVm(culture);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal(1.5m, vm.Invoke("Vm.CultureChecks.Ops", "ThreadWorkerCulture"));
            Assert.Equal(1.5m, vm.Invoke("Vm.CultureChecks.Ops", "TaskWorkerCulture"));
            Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
            Assert.Equal("ja-JP", CultureInfo.CurrentUICulture.Name);
        } finally {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void GuestException_RestoresHostCurrentCultureAndCurrentUICulture() {
        using var vm = CreateVm(CultureInfo.GetCultureInfo("tr-TR"));
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Throws<UnhandledGuestException>(() =>
                vm.Invoke("Vm.CultureChecks.Ops", "ThrowAfterCultureWork"));
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
            Assert.Equal("ja-JP", CultureInfo.CurrentUICulture.Name);
        } finally {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void UnicodeNormalization_HostWorkQuotaAllowsExactBoundaryAndRejectsExcess() {
        var input = string.Concat(Enumerable.Repeat("e\u0301", 2048));
        var cost = input.Length;
        using (var exact = CreateVm(CultureInfo.InvariantCulture, new MemoryPolicy {
            InstructionQuota = 100_000_000,
            HostWorkBudget = cost,
        })) {
            Assert.Equal(input.Normalize(), exact.Invoke("Vm.CultureChecks.Ops", "Normalize", input));
        }
        using (var over = CreateVm(CultureInfo.InvariantCulture, new MemoryPolicy {
            InstructionQuota = 100_000_000,
            HostWorkBudget = cost - 1,
        })) {
            Assert.Throws<MemoryQuotaExceededException>(() =>
                over.Invoke("Vm.CultureChecks.Ops", "IsNormalized", input));
        }
    }
}
