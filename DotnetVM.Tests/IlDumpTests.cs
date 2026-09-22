using DotnetVM.Metadata;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>IL ダンプ (診断用)。削除対象 (診断テスト)。</summary>
public class IlDumpTests {
    [Fact]
    public void Dump_MathFaces() {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        var image = vm.Loaders
            .Select(l => l.Image)
            .First(i => i.SourcePath?.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) == true);
        var sb2 = new System.Text.StringBuilder();
        var count = image.Tables.GetRowCount(TableKind.MethodDef);
        for (var rid = 1; rid <= count; rid++) {
            var name = image.GetMethodName(rid);
            if (name is not ("ModF" or "ScaleB" or "ILogB" or "BigMul")) continue;
            var owner = DotnetVM.Diagnostics.IlDisassembler.GetOwnerTypeDefOfMethod(image, rid);
            var (ns, n) = image.GetTypeDefName(owner);
            if (n != "Math") continue;
            sb2.AppendLine($"--- CoreLib Math::{name} ---");
            // abstract/pinvoke メソッドはメソッド本体がないためスキップ
            var body = image.GetMethodBody(rid);
            if (body is null) {
                sb2.AppendLine("// (abstract/pinvoke — IL body なし)");
                continue;
            }
            sb2.AppendLine(DotnetVM.Diagnostics.IlDisassembler.DisassembleMethod(image, rid));
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ildump_math.txt"), sb2.ToString());
        Assert.True(true);
    }
}
