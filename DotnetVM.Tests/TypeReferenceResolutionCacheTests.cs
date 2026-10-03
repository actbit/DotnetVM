using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Types;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

public sealed class TypeReferenceResolutionCacheTests {
    private static int FindTypeRef(TypeLoader loader, string fullName) {
        for (var rid = 1; rid <= loader.Image.Tables.GetRowCount(TableKind.TypeRef); rid++) {
            var (ns, name, _) = loader.Image.GetTypeRefName(rid);
            if ((string.IsNullOrEmpty(ns) ? name : ns + "." + name) == fullName)
                return rid;
        }
        throw new InvalidOperationException($"Missing TypeRef: {fullName}");
    }

    [Fact]
    public void SuccessfulFrameworkReferenceAvoidsRepeatedDirectoryProbesAndRechecksNewAssemblies() {
        var loader = new TypeLoader(AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "public static class ReferenceGuest { public static System.Exception Echo(System.Exception x) => x; }")));
        loader.Image.SourcePath = Path.Combine(Path.GetTempPath(), "ReferenceGuest.dll");
        var probes = 0;
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException(),
            pathExists: _ => { probes++; return false; });
        context.Register(loader);
        var rid = FindTypeRef(loader, "System.Exception");

        var type = loader.ResolveTypeRefType(rid);
        var initialProbes = probes;
        Assert.True(initialProbes > 0);
        Assert.Same(type, loader.ResolveTypeRefType(rid));
        Assert.Equal(initialProbes, probes);

        var other = new TypeLoader(AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "public class Unrelated { }", "ReferenceOther")));
        context.Register(other);
        Assert.Same(type, loader.ResolveTypeRefType(rid));
        Assert.True(probes > initialProbes);
    }

    [Fact]
    public void MissingDependencyCanBeRegisteredAndSuccessfulReferenceDoesNotRetainRemovedLoader() {
        var dependencyBytes = TestAssemblyCompiler.CompileToBytes(
            "namespace ReferenceDependency { public class Target { } }", "ReferenceDependency");
        var guestBytes = TestAssemblyCompiler.CompileToBytes(
            "public static class ReferenceGuest { public static ReferenceDependency.Target Echo(ReferenceDependency.Target x) => x; }",
            "ReferenceGuest", [MetadataReference.CreateFromImage(dependencyBytes)]);
        var loader = new TypeLoader(AssemblyImage.Parse(guestBytes));
        loader.Image.SourcePath = Path.Combine(Path.GetTempPath(), "ReferenceGuest.dll");
        var probes = 0;
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException(),
            pathExists: _ => { probes++; return false; });
        context.Register(loader);
        var rid = FindTypeRef(loader, "ReferenceDependency.Target");

        Assert.Throws<AssemblyDependencyNotFoundException>(() => loader.ResolveTypeRefType(rid));
        var initialProbes = probes;
        Assert.Throws<AssemblyDependencyNotFoundException>(() => loader.ResolveTypeRefType(rid));
        Assert.True(probes > initialProbes);

        var dependency = new TypeLoader(AssemblyImage.Parse(dependencyBytes));
        context.Register(dependency);
        var firstType = loader.ResolveTypeRefType(rid);
        Assert.Same(dependency.FindTypeByFullName("ReferenceDependency.Target"), firstType);
        Assert.Same(firstType, loader.ResolveTypeRefType(rid));

        context.Unregister(dependency);
        var replacement = new TypeLoader(AssemblyImage.Parse(dependencyBytes));
        context.Register(replacement);
        var replacementType = loader.ResolveTypeRefType(rid);
        Assert.NotSame(firstType, replacementType);
        Assert.Same(replacement.FindTypeByFullName("ReferenceDependency.Target"), replacementType);

        context.Retire();
        Assert.Throws<ObjectDisposedException>(() => loader.ResolveTypeRefType(rid));
    }
}
