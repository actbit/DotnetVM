using System.Reflection;
using System.Reflection.Emit;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime;
using DotnetVM.Runtime.Intrinsics;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

public sealed class AbiImportTests {
    private const string DriverSource = """
        public static class AbiDriver {
            public static int Run() => External.Calculator.Compute(2);
        }
        """;

    private const string ProviderSource = """
        namespace External {
            public static class Calculator {
                public static int Compute(int value) => value + 1;
            }
        }
        """;

    private const string DriverASource = """
        public static class AbiDriverA {
            public static int Run() => External.Calculator.Compute(2);
        }
        """;

    private const string DriverBSource = """
        public static class AbiDriverB {
            public static int Run() => External.Calculator.Compute(2);
        }
        """;

    [Fact]
    public void ImportAssemblyAbi_FromHostAssembly_UsesItsMetadataIdentity() {
        using var vm = new VirtualMachine();

        var image = vm.ImportAssemblyAbi(typeof(Uri).Assembly);

        Assert.Equal(typeof(Uri).Assembly.GetName().Name, image.Identity.Name);
        Assert.Contains(vm.Loaders, loader =>
            ReferenceEquals(loader.Image, image) && loader.Image.Identity.Name == image.Identity.Name);
    }

    [Fact]
    public void ImportAssemblyAbi_FromStream_PreservesDependencySearchHint() {
        var path = typeof(Uri).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(path));

        using var vm = new VirtualMachine();
        using var stream = File.OpenRead(path);
        var image = vm.ImportAssemblyAbi(stream, path);

        Assert.Equal(typeof(Uri).Assembly.GetName().Name, image.Identity.Name);
        Assert.Equal(Path.GetFullPath(path), image.SourcePath);
    }

    [Fact]
    public void ImportAssemblyAbi_RejectsDynamicAssemblyWithoutLocation() {
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("DynamicAbiImport"), AssemblyBuilderAccess.Run);
        using var vm = new VirtualMachine();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            vm.ImportAssemblyAbi(dynamicAssembly));

        Assert.Contains("ImportAssemblyAbi(Stream", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedIlMode_ReexecutesImportedMethodBody() {
        var provider = TestAssemblyCompiler.CompileToBytes(ProviderSource, "AbiProviderIl");
        var driver = TestAssemblyCompiler.CompileToBytes(
            DriverSource, "AbiDriverIl", [MetadataReference.CreateFromImage(provider)]);
        using var vm = new VirtualMachine();

        vm.ImportAssemblyAbi(new MemoryStream(provider), executionMode: AbiExecutionMode.ManagedIl);
        vm.RegisterBinding(
            BindingKey.Static("External.Calculator", "Compute", "System.Int32"),
            static (_, _) => DotnetVM.Runtime.Execution.StackSlot.OfInt32(99),
            BindingOrigin.Device);
        vm.LoadAssembly(new MemoryStream(driver));

        Assert.Equal(3, vm.Invoke("AbiDriver", "Run"));
    }

    [Fact]
    public void HostBridgeMode_UsesRegisteredBridgeInsteadOfImportedMethodBody() {
        var provider = TestAssemblyCompiler.CompileToBytes(ProviderSource, "AbiProviderBridge");
        var driver = TestAssemblyCompiler.CompileToBytes(
            DriverSource, "AbiDriverBridge", [MetadataReference.CreateFromImage(provider)]);
        using var vm = new VirtualMachine();

        var providerImage = vm.ImportAssemblyAbi(new MemoryStream(provider), executionMode: AbiExecutionMode.HostBridge);
        vm.RegisterAbiBridge(
            providerImage,
            BindingKey.Static("External.Calculator", "Compute", "System.Int32"),
            static (_, _) => DotnetVM.Runtime.Execution.StackSlot.OfInt32(99),
            BindingOrigin.Device);
        vm.LoadAssembly(new MemoryStream(driver));

        Assert.Contains(vm.AbiBindings, binding =>
            binding.Assembly == providerImage.Identity &&
            binding.Key == BindingKey.Static("External.Calculator", "Compute", "System.Int32"));
        Assert.Equal(99, vm.Invoke("AbiDriver", "Run"));
    }

    [Fact]
    public void HostBridgeMode_RejectsImportedMethodWithoutBridge() {
        var provider = TestAssemblyCompiler.CompileToBytes(ProviderSource, "AbiProviderMissingBridge");
        var driver = TestAssemblyCompiler.CompileToBytes(
            DriverSource, "AbiDriverMissingBridge", [MetadataReference.CreateFromImage(provider)]);
        using var vm = new VirtualMachine();

        vm.ImportAssemblyAbi(new MemoryStream(provider), executionMode: AbiExecutionMode.HostBridge);
        vm.LoadAssembly(new MemoryStream(driver));

        Assert.Contains(vm.Loaders, loader => loader.AbiExecutionMode == AbiExecutionMode.HostBridge);
        var providerType = vm.Loaders.Single(loader => loader.Image.Identity.Name == "AbiProviderMissingBridge")
            .FindTypeByFullName("External.Calculator")!;
        Assert.Equal(AbiExecutionMode.HostBridge, providerType.Methods.Single(method => method.Name == "Compute").Loader?.AbiExecutionMode);

        Assert.Throws<OperationNotAllowedException>(() => vm.Invoke("AbiDriver", "Run"));
    }

    [Fact]
    public void HostBridges_AreScopedToTheImportedAssemblyIdentity() {
        var providerA = TestAssemblyCompiler.CompileToBytes(ProviderSource, "AbiProviderA");
        var providerB = TestAssemblyCompiler.CompileToBytes(ProviderSource, "AbiProviderB");
        var driverA = TestAssemblyCompiler.CompileToBytes(
            DriverASource, "AbiDriverA", [MetadataReference.CreateFromImage(providerA)]);
        var driverB = TestAssemblyCompiler.CompileToBytes(
            DriverBSource, "AbiDriverB", [MetadataReference.CreateFromImage(providerB)]);
        using var vm = new VirtualMachine();

        var imageA = vm.ImportAssemblyAbi(new MemoryStream(providerA), executionMode: AbiExecutionMode.HostBridge);
        var imageB = vm.ImportAssemblyAbi(new MemoryStream(providerB), executionMode: AbiExecutionMode.HostBridge);
        vm.RegisterAbiBridge(
            imageA,
            BindingKey.Static("External.Calculator", "Compute", "System.Int32"),
            static (_, _) => DotnetVM.Runtime.Execution.StackSlot.OfInt32(11),
            BindingOrigin.Device);
        vm.RegisterAbiBridge(
            imageB,
            BindingKey.Static("External.Calculator", "Compute", "System.Int32"),
            static (_, _) => DotnetVM.Runtime.Execution.StackSlot.OfInt32(22),
            BindingOrigin.Device);
        vm.LoadAssembly(new MemoryStream(driverA));
        vm.LoadAssembly(new MemoryStream(driverB));

        Assert.Equal(11, vm.Invoke("AbiDriverA", "Run"));
        Assert.Equal(22, vm.Invoke("AbiDriverB", "Run"));
    }
}
