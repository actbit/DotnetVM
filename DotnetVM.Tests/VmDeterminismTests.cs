using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

public sealed class VmDeterminismTests {
    private const string Source = """
        using System;
        namespace Vm.DeterminismChecks {
            public static class Ops {
                public static long LocalNowTicks() => DateTime.Now.Ticks;
                public static long UtcNowTicks() => DateTime.UtcNow.Ticks;
                public static long TodayTicks() => DateTime.Today.Ticks;
                public static string NewGuidText() => Guid.NewGuid().ToString("D");
            }
        }
        """;

    private static readonly byte[] AssemblyBytes =
        TestAssemblyCompiler.CompileToBytes(Source, "VmDeterminismAsm");

    private static VirtualMachine CreateVm(VmHostOptions options) {
        var vm = new VirtualMachine(options);
        using var stream = new MemoryStream(AssemblyBytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    [Fact]
    public void DateTimeClockCanBeFixedPerVm() {
        var instant = new DateTimeOffset(2026, 9, 24, 12, 34, 56, TimeSpan.FromHours(-5));
        var vmTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "VmPlusNine", TimeSpan.FromHours(9), "VM +09", "VM +09");
        var vmLocal = TimeZoneInfo.ConvertTime(instant, vmTimeZone);
        using var vm = CreateVm(new VmHostOptions {
            LoadHostCoreLib = true,
            ClockProvider = () => instant,
            TimeZone = vmTimeZone,
        });

        Assert.Equal(vmLocal.DateTime.Ticks, vm.Invoke("Vm.DeterminismChecks.Ops", "LocalNowTicks"));
        Assert.Equal(instant.UtcDateTime.Ticks, vm.Invoke("Vm.DeterminismChecks.Ops", "UtcNowTicks"));
        Assert.Equal(vmLocal.Date.Date.Ticks, vm.Invoke("Vm.DeterminismChecks.Ops", "TodayTicks"));
    }

    [Fact]
    public void GuidNewGuidUsesTheVmRandomProviderAndSetsVersionBits() {
        var fillCount = 0;
        using var vm = CreateVm(new VmHostOptions {
            LoadHostCoreLib = true,
            RandomFill = bytes => {
                fillCount++;
                bytes.Fill(0x11);
            },
        });

        var actual = vm.Invoke("Vm.DeterminismChecks.Ops", "NewGuidText");

        Assert.Equal("11111111-1111-4111-9111-111111111111", actual);
        Assert.True(fillCount > 0);
    }
}
