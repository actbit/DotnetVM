using DotnetVM.Diagnostics;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class AuditHardeningTests {
    [Fact]
    public void VerifierRejectsReturnTypeMismatchBeforeExecution() {
        using var vm = NewVm();
        vm.LoadAssembly(new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "VerifierReturnInput")));
        var loader = Assert.Single(vm.Loaders);
        var builder = new VmDynamicMethodBuilder(loader, vm.Heap, "badReturn",
            new SigType(SigKind.I4), [], maxMethodBodyBytes: 32);
        builder.EmitOpcode((ushort)ILOp.Ldnull);
        builder.EmitOpcode((ushort)ILOp.Ret);

        var method = builder.CreateMethod();
        var error = Assert.Throws<BadImageFormatException>(() => vm.Execute(method));
        Assert.Contains("ret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsObjectStoredIntoIntegerLocal() {
        using var vm = NewVm();
        vm.LoadAssembly(new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "VerifierLocalInput")));
        var loader = Assert.Single(vm.Loaders);
        var builder = new VmDynamicMethodBuilder(loader, vm.Heap, "badLocal",
            new SigType(SigKind.Void), [], maxMethodBodyBytes: 32);
        builder.DeclareLocal(new SigType(SigKind.I4));
        builder.EmitOpcode((ushort)ILOp.Ldnull);
        builder.EmitOpcode((ushort)ILOp.Stloc_0);
        builder.EmitOpcode((ushort)ILOp.Ret);

        var method = builder.CreateMethod();
        Assert.Throws<BadImageFormatException>(() => vm.Execute(method));
    }

    [Fact]
    public void CollectibleHandleFailsClosedAfterUnload() {
        var image = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "namespace Vm.Stale { public static class Ops { public static int Run() => 1; } }",
            "StaleHandleInput"));
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException());
        var loader = new TypeLoader(image);
        context.Register(loader);
        loader.CompletePendingTypes();
        var type = Assert.IsType<VmClassType>(loader.FindTypeByFullName("Vm.Stale.Ops"));
        var method = Assert.Single(type.Methods, candidate => candidate.Name == "Run");
        var loadContext = new VmAssemblyLoadContext {
            Context = context, Name = "stale", IsCollectible = true, IsDefault = false,
        };

        loadContext.Unload();

        Assert.Throws<ObjectDisposedException>(() => VmLifetime.EnsureLive(new VmRuntimeObject { Target = type }));
        Assert.Throws<ObjectDisposedException>(() => VmLifetime.EnsureLive(new VmMethodHandle { Target = method }));
        Assert.Throws<ObjectDisposedException>(() => _ = context.Loaders);
        Assert.Throws<ObjectDisposedException>(() => context.Register(new TypeLoader(image)));
    }

    [Fact]
    public void ExecutionTracerIsBounded() {
        var tracer = new ExecutionTracer(maxEvents: 2);
        tracer.Start();
        tracer.Record("A", "T", "one");
        tracer.Record("A", "T", "two");
        tracer.Record("A", "T", "three");

        Assert.Equal(2, tracer.Frames.Count);
        Assert.Equal("one", tracer.Frames[0].MethodName);
        Assert.Equal("two", tracer.Frames[1].MethodName);
        Assert.Equal(1, tracer.DroppedFrameCount);
    }

    private static VirtualMachine NewVm() => new();
}
