using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>バインド監査 (診断用)。</summary>
public class BindingDumpTests {
    [Fact]
    public void Dump_DecimalLikeBindings() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        var lines = vm.Bindings
            .Where(b =>
                b.Key.TypeFullName == "System.Math")
            .Select(b => b.Origin + " " + b.Key)
            .ToList();
        System.Console.WriteLine("MATH: " + (lines.Count > 0 ? string.Join("; ", lines) : "(none)"));
        foreach (var l in lines)
            System.Console.WriteLine(l);
        var legacy = vm.IntrinsicKeys
            .Where(k => k.TypeFullName.Contains("Decimal"))
            .Select(k => k.ToString())
            .ToList();
        foreach (var l in legacy)
            System.Console.WriteLine("LEGACY " + l);
        Assert.NotEmpty(lines);
    }
}
