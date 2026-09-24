using DotnetVM.Host;
using DotnetVM.Policy;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

public sealed class RuntimeBindingProvenanceTests {
    private const string FakeSurfaceSource = """
        namespace System {
            public class Type {
                public static Type GetType(string name) => null!;
            }
        }
        namespace System.Threading {
            public class CancellationTokenSource {
                public static int Marker;
                public void Cancel() => Marker = 23;
            }
            public class SynchronizationContext {
                public static int Marker;
                public static void SetSynchronizationContext(SynchronizationContext value) => Marker = 31;
            }
        }
        namespace System.Runtime.Loader {
            public class AssemblyLoadContext {
                public static int Marker;
                public static AssemblyLoadContext Default {
                    get { Marker = 47; return null!; }
                }
            }
        }
        namespace Vm.FakeSurfaces {
            public static class Ops {
                public static int TypeSurface() => System.Type.GetType("System.Object") is null ? 1 : 0;
                public static int CancellationSurface() {
                    var source = new System.Threading.CancellationTokenSource();
                    source.Cancel();
                    return System.Threading.CancellationTokenSource.Marker;
                }
                public static int SynchronizationSurface() {
                    System.Threading.SynchronizationContext.SetSynchronizationContext(
                        new System.Threading.SynchronizationContext());
                    return System.Threading.SynchronizationContext.Marker;
                }
                public static int AssemblyLoadContextSurface() {
                    _ = System.Runtime.Loader.AssemblyLoadContext.Default;
                    return System.Runtime.Loader.AssemblyLoadContext.Marker;
                }
            }
        }
        """;

    [Fact]
    public void SameFullNameGuestTypeDefsDoNotReachRuntimeBindings() {
        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(
            TestAssemblyCompiler.CompileToBytes(FakeSurfaceSource, "FakeRuntimeSurfaces"));
        vm.LoadAssembly(stream);

        Assert.Equal(1, vm.Invoke("Vm.FakeSurfaces.Ops", "TypeSurface"));
        Assert.Equal(23, vm.Invoke("Vm.FakeSurfaces.Ops", "CancellationSurface"));
        Assert.Equal(31, vm.Invoke("Vm.FakeSurfaces.Ops", "SynchronizationSurface"));
        Assert.Equal(47, vm.Invoke("Vm.FakeSurfaces.Ops", "AssemblyLoadContextSurface"));
    }

    [Fact]
    public void MissingAssemblyRefCannotUseFacadeFullNameToReachRuntimeBinding() {
        var contract = TestAssemblyCompiler.CompileToBytes("""
            namespace System.Threading {
                public class SynchronizationContext {
                    public static SynchronizationContext Current => new SynchronizationContext();
                }
            }
            namespace System.Threading.Tasks {
                public class Task<T> {
                    public static T FromResult(T value) => value;
                }
            }
            """, "MissingSurfaceContract");
        var contractReference = MetadataReference.CreateFromImage(contract).WithAliases(["missing"]);
        var driver = TestAssemblyCompiler.CompileToBytes("""
            extern alias missing;
            public static class MissingSurfaceDriver {
                public static bool Run() =>
                    missing::System.Threading.SynchronizationContext.Current is not null;
                public static int RunGeneric() =>
                    missing::System.Threading.Tasks.Task<int>.FromResult(1);
            }
            """, "MissingSurfaceDriver", [contractReference]);

        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(driver);
        vm.LoadAssembly(stream);

        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Invoke("MissingSurfaceDriver", "Run"));
        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Invoke("MissingSurfaceDriver", "RunGeneric"));
    }
}
