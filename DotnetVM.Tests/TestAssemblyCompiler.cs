using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetVM.Tests;

/// <summary>
/// C# ソース文字列をインメモリの DLL アセンブリにコンパイルする。
/// VM の入力生成と、同一アセンブリの CLR 反射実行 (ground truth) の両方に使う。
/// </summary>
public static class TestAssemblyCompiler {
    /// <summary>コンパイルして PE バイト列を返す。診断エラー時は例外。
    /// extraReferences で追加の契約アセンブリ (intrinsic ファサードの C# 側シグネチャ等) を参照できる。</summary>
    public static byte[] CompileToBytes(string source, string assemblyName = "TestAsm",
        IReadOnlyList<MetadataReference>? extraReferences = null) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            GetReferences(extraReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        if (!result.Success) {
            var sb = new StringBuilder("コンパイル失敗:");
            foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine().AppendFormat("  {0}: {1}", diag.Id, diag.GetMessage());
            throw new InvalidOperationException(sb.ToString());
        }
        return peStream.ToArray();
    }

    /// <summary>コンパイルして VM 用にパースし、ついでに CLR 反射用にもロードする。</summary>
    public static (byte[] Pe, Assembly ClrAssembly) Compile(string source, string assemblyName = "TestAsm") {
        var pe = CompileToBytes(source, assemblyName);
        var clrAssembly = Assembly.Load(pe);
        return (pe, clrAssembly);
    }

    private static IReadOnlyList<MetadataReference> GetReferences(IReadOnlyList<MetadataReference>? extraReferences = null) {
        // ランタイムディレクトリから必要な基本アセンブリを参照する
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var required = new[] { "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Runtime.Extensions", "System.Linq" };
        var references = new List<MetadataReference>();
        foreach (var name in required) {
            var path = Path.Combine(runtimeDir, name + ".dll");
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }
        if (extraReferences is not null)
            references.AddRange(extraReferences);
        return references;
    }
}
