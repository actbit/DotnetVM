using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetVM.Metadata;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// 自作メタデータパーサを System.Reflection.Metadata (オラクル) と照合する。
/// プロダクト本体は SRM に依存せず、テストのみで使用する。
/// </summary>
public class MetadataParsingTests {
    private static readonly string SampleSource = """
        using System;
        namespace MyApp {
            public class Base {
                public virtual int GetValue() => 1;
                public int Twice() => GetValue() * 2;
            }
            public class Derived : Base, IDisposable {
                private int _field = 42;
                public static string Prefix = "P:";
                public override int GetValue() => _field;
                public void Dispose() { }
            }
            public static class Calc {
                public static int Add(int a, int b) => a + b;
                public static string Describe(int n) => n switch {
                    < 0 => "negative",
                    0 => "zero",
                    _ => "positive",
                };
            }
            internal class Generic<T, U> where T : class {
                public T Merge(T a, U b) => a;
            }
            public enum Color { Red, Green, Blue }
        }
        """;

    private static PEReader OpenPe(byte[] pe) => new(new MemoryStream(pe));

    private static byte[] CompileSample() => TestAssemblyCompiler.CompileToBytes(SampleSource);

    [Fact]
    public void ParsesSampleAssembly() {
        var image = AssemblyImage.Parse(CompileSample());
        Assert.Equal("TestAsm", image.Name);
    }

    [Fact]
    public void TableRowCounts_MatchOracle() {
        var pe = CompileSample();
        var image = AssemblyImage.Parse(pe);
        using var peReader = OpenPe(pe);
        var reader = peReader.GetMetadataReader();

        Assert.Equal(reader.GetTableRowCount(TableIndex.TypeDef), image.Tables.GetRowCount(TableKind.TypeDef));
        Assert.Equal(reader.GetTableRowCount(TableIndex.MethodDef), image.Tables.GetRowCount(TableKind.MethodDef));
        Assert.Equal(reader.GetTableRowCount(TableIndex.Field), image.Tables.GetRowCount(TableKind.Field));
        Assert.Equal(reader.GetTableRowCount(TableIndex.MemberRef), image.Tables.GetRowCount(TableKind.MemberRef));
        Assert.Equal(reader.GetTableRowCount(TableIndex.TypeRef), image.Tables.GetRowCount(TableKind.TypeRef));
        Assert.Equal(reader.GetTableRowCount(TableIndex.TypeSpec), image.Tables.GetRowCount(TableKind.TypeSpec));
        Assert.Equal(reader.GetTableRowCount(TableIndex.StandAloneSig), image.Tables.GetRowCount(TableKind.StandAloneSig));
        Assert.Equal(reader.GetTableRowCount(TableIndex.AssemblyRef), image.Tables.GetRowCount(TableKind.AssemblyRef));
        Assert.Equal(reader.GetTableRowCount(TableIndex.NestedClass), image.Tables.GetRowCount(TableKind.NestedClass));
    }

    [Fact]
    public void TypeDefNames_MatchOracle() {
        var pe = CompileSample();
        var image = AssemblyImage.Parse(pe);
        var count = image.Tables.GetRowCount(TableKind.TypeDef);
        var vmNames = new List<string>();
        for (var rid = 1; rid <= count; rid++) {
            var (ns, name) = image.GetTypeDefName(rid);
            vmNames.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);
        }

        using var peReader = OpenPe(pe);
        var reader = peReader.GetMetadataReader();
        var oracleNames = reader.TypeDefinitions
            .Select(h => {
                var td = reader.GetTypeDefinition(h);
                var ns = reader.GetString(td.Namespace);
                var name = reader.GetString(td.Name);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            })
            .ToList();

        Assert.Equal(oracleNames, vmNames);
    }

    [Fact]
    public void MethodDefNames_MatchOracle() {
        var pe = CompileSample();
        var image = AssemblyImage.Parse(pe);
        var count = image.Tables.GetRowCount(TableKind.MethodDef);
        var vmNames = new List<string>();
        for (var rid = 1; rid <= count; rid++)
            vmNames.Add(image.GetMethodName(rid));

        using var peReader = OpenPe(pe);
        var reader = peReader.GetMetadataReader();
        var oracleNames = reader.MethodDefinitions
            .Select(h => reader.GetString(reader.GetMethodDefinition(h).Name))
            .ToList();

        Assert.Equal(oracleNames, vmNames);
    }

    [Fact]
    public void MethodBodies_MatchOracle() {
        var pe = CompileSample();
        var image = AssemblyImage.Parse(pe);

        using var peReader = OpenPe(pe);
        var reader = peReader.GetMetadataReader();

        var methodHandles = reader.MethodDefinitions.ToArray();
        for (var rid = 1; rid <= methodHandles.Length; rid++) {
            var md = reader.GetMethodDefinition(methodHandles[rid - 1]);
            var vmBody = image.GetMethodBody(rid);
            if (md.RelativeVirtualAddress == 0) {
                Assert.Null(vmBody); // abstract/pinvoke
                continue;
            }
            var body = vmBody ?? throw new Xunit.Sdk.XunitException($"MethodDef rid={rid} の本体がありません。");
            var oracleBody = peReader.GetMethodBody(md.RelativeVirtualAddress)!;
            var oracleIl = oracleBody.GetILBytes()
                ?? throw new Xunit.Sdk.XunitException($"MethodDef rid={rid} のoracle IL本体がありません。");
            Assert.Equal(oracleIl.Length, body.IlCode.Length);
            // IL 本体のバイト一致
            Assert.True(oracleIl.AsSpan().SequenceEqual(body.IlCode.ToArray()),
                $"MethodDef rid={rid} の IL 本体が不一致");
            Assert.Equal(oracleBody.ExceptionRegions.Length, body.ExceptionClauses?.Length ?? 0);
        }
    }

    [Fact]
    public void ExceptionClauses_MatchOracle() {
        // try/catch/filter/finally を含むソースで EH 句を検証する
        const string ehSource = """
            using System;
            public static class Eh {
                public static string Run(int mode) {
                    var log = "";
                    try {
                        log += "try;";
                        if (mode == 1) throw new InvalidOperationException("x");
                        if (mode == 2) throw new ArgumentException("y");
                    } catch (InvalidOperationException e) {
                        log += "catch1:" + e.Message + ";";
                    } catch (ArgumentException e) when (e.ParamName == "y") {
                        log += "filtercatch;";
                    } finally {
                        log += "finally;";
                    }
                    return log;
                }
            }
            """;
        var pe = TestAssemblyCompiler.CompileToBytes(ehSource);
        var image = AssemblyImage.Parse(pe);

        using var peReader = OpenPe(pe);
        var reader = peReader.GetMetadataReader();

        foreach (var mh in reader.MethodDefinitions) {
            var md = reader.GetMethodDefinition(mh);
            if (md.RelativeVirtualAddress == 0)
                continue;
            if (reader.GetString(md.Name) != "Run")
                continue;

            var oracleBody = peReader.GetMethodBody(md.RelativeVirtualAddress);
            var oracleClauses = oracleBody.ExceptionRegions;
            Assert.True(oracleClauses.Length >= 3, "テストソースに try/catch/finally が必要");

            var rid = MetadataTokens.GetRowNumber(mh);
            var vmBody = image.GetMethodBody(rid)!;
            var vmClauses = vmBody.ExceptionClauses!;
            Assert.Equal(oracleClauses.Length, vmClauses.Length);

            for (var i = 0; i < oracleClauses.Length; i++) {
                var o = oracleClauses[i];
                var v = vmClauses[i];
                Assert.Equal(o.TryOffset, v.TryOffset);
                Assert.Equal(o.TryLength, v.TryLength);
                Assert.Equal(o.HandlerOffset, v.HandlerOffset);
                Assert.Equal(o.HandlerLength, v.HandlerLength);
                var expectedKind = o.Kind switch {
                    ExceptionRegionKind.Catch => ExceptionClauseKind.Catch,
                    ExceptionRegionKind.Filter => ExceptionClauseKind.Filter,
                    ExceptionRegionKind.Finally => ExceptionClauseKind.Finally,
                    ExceptionRegionKind.Fault => ExceptionClauseKind.Fault,
                    _ => throw new InvalidOperationException(),
                };
                Assert.Equal(expectedKind, v.Kind);
                if (o.Kind == ExceptionRegionKind.Catch)
                    Assert.Equal(MetadataTokens.GetToken(o.CatchType), v.ClassTokenOrFilterOffset);
            }
            return;
        }
        Assert.Fail("Run メソッドが見つかりません。");
    }

    [Fact]
    public void ParsesRealCoreLib() {
        // 実在の大規模アセンブリが例外なくパースできること
        var coreLibPath = typeof(object).Assembly.Location;
        var image = AssemblyImage.Parse(File.ReadAllBytes(coreLibPath));
        Assert.Equal("System.Private.CoreLib", image.Name);
        Assert.True(image.Tables.GetRowCount(TableKind.TypeDef) > 1000);
        Assert.True(image.Tables.GetRowCount(TableKind.MethodDef) > 10000);

        // 検索が壊れていないか: 既知型を確認
        var foundString = false;
        var foundObject = false;
        var count = image.Tables.GetRowCount(TableKind.TypeDef);
        for (var rid = 1; rid <= count; rid++) {
            var (ns, name) = image.GetTypeDefName(rid);
            if (ns == "System" && name == "String")
                foundString = true;
            if (ns == "System" && name == "Object")
                foundObject = true;
        }
        Assert.True(foundString, "System.String が TypeDef に見つかりません。");
        Assert.True(foundObject, "System.Object が TypeDef に見つかりません。");
    }

    [Fact]
    public void NestedClasses_AreResolvable() {
        const string source = """
            public class Outer {
                public class Inner {
                    public int Val() => 7;
                }
                private class Hidden;
            }
            """;
        var nestedImage = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(source, "NestedAsm"));

        var count = nestedImage.Tables.GetRowCount(TableKind.TypeDef);
        var innerRid = 0;
        for (var rid = 1; rid <= count; rid++) {
            var (_, name) = nestedImage.GetTypeDefName(rid);
            if (name == "Inner")
                innerRid = rid;
        }
        Assert.NotEqual(0, innerRid);
        var enclosing = nestedImage.GetEnclosingTypeDef(innerRid);
        Assert.NotEqual(0, enclosing);
        Assert.Equal("Outer", nestedImage.GetTypeDefName(enclosing).Name);
    }
}
