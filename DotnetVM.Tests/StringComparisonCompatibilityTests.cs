using System.Globalization;
using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

public sealed class StringComparisonCompatibilityTests {
    private static readonly string[] ComparisonMethods = [
        "StaticEquals", "InstanceEquals", "Compare", "IndexOf", "LastIndexOf", "StartsWith", "EndsWith",
    ];
    private static readonly string[] DefaultMethods = [
        "CompareTo", "DefaultIndexOf", "DefaultLastIndexOf", "DefaultStartsWith", "DefaultEndsWith",
    ];
    private static readonly Lazy<(Assembly Clr, byte[] Bytes)> Guest = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes("""
            using System;
            public static class StringComparisons {
                public static bool StaticEquals(string left, string right, int comparison) =>
                    string.Equals(left, right, (StringComparison)comparison);
                public static bool InstanceEquals(string left, string right, int comparison) =>
                    left.Equals(right, (StringComparison)comparison);
                public static int Compare(string left, string right, int comparison) =>
                    string.Compare(left, right, (StringComparison)comparison);
                public static int IndexOf(string left, string right, int comparison) =>
                    left.IndexOf(right, (StringComparison)comparison);
                public static int LastIndexOf(string left, string right, int comparison) =>
                    left.LastIndexOf(right, (StringComparison)comparison);
                public static bool StartsWith(string left, string right, int comparison) =>
                    left.StartsWith(right, (StringComparison)comparison);
                public static bool EndsWith(string left, string right, int comparison) =>
                    left.EndsWith(right, (StringComparison)comparison);
                public static int CompareTo(string left, string right, int comparison) => left.CompareTo(right);
                public static int DefaultIndexOf(string left, string right, int comparison) => left.IndexOf(right);
                public static int DefaultLastIndexOf(string left, string right, int comparison) => left.LastIndexOf(right);
                public static bool DefaultStartsWith(string left, string right, int comparison) => left.StartsWith(right);
                public static bool DefaultEndsWith(string left, string right, int comparison) => left.EndsWith(right);
                public static int CatchInvalidComparison() {
                    try { return "a".IndexOf("A", (StringComparison)6); }
                    catch (ArgumentException) { return 42; }
                }
                public static int CatchNullValue() {
                    try { return "a".IndexOf((string)null, StringComparison.Ordinal); }
                    catch (ArgumentNullException) { return 43; }
                }
            }
            """, "StringComparisonCompatibilityGuest");
        return (Assembly.Load(bytes), bytes);
    });

    private static VirtualMachine CreateVm(bool loadHostCoreLib, bool enableJit, CultureInfo culture) {
        var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = loadHostCoreLib,
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Culture = culture,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        vm.LoadAssembly(new MemoryStream(Guest.Value.Bytes));
        return vm;
    }

    private static void AssertMatchesClr(VirtualMachine vm, string method, string? left, string? right,
        int comparison) {
        object?[] args = [left, right, comparison];
        var clrMethod = Guest.Value.Clr.GetType("StringComparisons")!.GetMethod(method)!;
        object? expected = null;
        var clrFailure = Record.Exception(() => expected = clrMethod.Invoke(null, args));
        object? actual = null;
        var vmFailure = Record.Exception(() => actual = vm.Invoke("StringComparisons", method, args));
        if (clrFailure is TargetInvocationException { InnerException: { } inner }) {
            var guest = Assert.IsType<UnhandledGuestException>(vmFailure);
            Assert.Equal(inner.GetType().FullName, guest.ExceptionTypeName);
        } else {
            Assert.Null(clrFailure);
            Assert.Null(vmFailure);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullArgumentsAndReceiversMatchClr(bool loadHostCoreLib, bool enableJit) {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        using var vm = CreateVm(loadHostCoreLib, enableJit, CultureInfo.InvariantCulture);
        foreach (var method in ComparisonMethods.Concat(DefaultMethods)) {
            AssertMatchesClr(vm, method, "abc", null, (int)StringComparison.Ordinal);
            AssertMatchesClr(vm, method, null, "abc", (int)StringComparison.Ordinal);
            AssertMatchesClr(vm, method, null, null, (int)StringComparison.Ordinal);
        }
        Assert.Equal(43, vm.Invoke("StringComparisons", "CatchNullValue"));
        AssertJitCompiled(vm, enableJit, ComparisonMethods.Concat(DefaultMethods));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvalidComparisonValuesMatchClrValidationOrder(bool loadHostCoreLib, bool enableJit) {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        using var vm = CreateVm(loadHostCoreLib, enableJit, CultureInfo.InvariantCulture);
        (string? Left, string? Right)[] values = [
            ("abc", "A"), ("same", "same"), ("", ""), ("abc", null), (null, "abc"), (null, null),
        ];
        foreach (var method in ComparisonMethods)
            foreach (var comparison in new[] { -1, 6, int.MinValue, int.MaxValue })
                foreach (var (left, right) in values)
                    AssertMatchesClr(vm, method, left, right, comparison);
        Assert.Equal(42, vm.Invoke("StringComparisons", "CatchInvalidComparison"));
        AssertJitCompiled(vm, enableJit, ComparisonMethods);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ValidComparisonsRespectConfiguredCulture(bool loadHostCoreLib, bool enableJit) {
        (string? Left, string? Right)[] values = [
            ("i", "I"), ("ı", "I"), ("ä", "z"), ("a\0b", "\0"), ("abc", ""), ("abc", null),
        ];
        foreach (var cultureInfo in new[] {
            CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("tr-TR"), CultureInfo.GetCultureInfo("de-DE"),
        }) {
            using var culture = new CultureScope(cultureInfo);
            using var vm = CreateVm(loadHostCoreLib, enableJit, cultureInfo);
            foreach (var method in ComparisonMethods)
                foreach (var comparison in Enum.GetValues<StringComparison>())
                    foreach (var (left, right) in values)
                        AssertMatchesClr(vm, method, left, right, (int)comparison);
            AssertJitCompiled(vm, enableJit, ComparisonMethods);
        }
    }

    private static void AssertJitCompiled(VirtualMachine vm, bool enableJit, IEnumerable<string> methods) {
        if (!enableJit)
            return;
        var type = vm.Loaders.Select(loader => loader.FindTypeByFullName("StringComparisons"))
            .First(type => type is not null)!;
        foreach (var name in methods)
            Assert.True(vm.IsJitCompiled(type.Methods.Single(method => method.Name == name)), name);
    }

    private sealed class CultureScope : IDisposable {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture) {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose() {
            CultureInfo.CurrentCulture = _previous;
            CultureInfo.CurrentUICulture = _previousUi;
        }
    }
}
