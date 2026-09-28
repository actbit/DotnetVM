using DotnetVM.Diagnostics;
using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using DotnetVM.Policy;
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
    public void PreparedMethodByteBudgetAllowsExecutionWithoutRetainingCacheEntry() {
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { MaxPreparedMethodBytes = 1 },
        });
        vm.LoadAssembly(new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 7; }", "PreparedBudgetInput")));

        Assert.Equal(7, vm.Invoke("Input", "Run"));
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
    public void CollectibleHandleDoesNotLeakHostDisposedExceptionAcrossGuestBoundary() {
        var image = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "namespace Vm.StaleGuest { public static class Ops { public static int Run() => 1; } }",
            "StaleGuestHandleInput"));
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException());
        var loader = new TypeLoader(image);
        context.Register(loader);
        loader.CompletePendingTypes();
        var type = Assert.IsType<VmClassType>(loader.FindTypeByFullName("Vm.StaleGuest.Ops"));
        var loadContext = new VmAssemblyLoadContext {
            Context = context, Name = "stale-guest", IsCollectible = true, IsDefault = false,
        };

        loadContext.Unload();

        var error = Assert.Throws<UnhandledGuestException>(() =>
            VmLifetime.EnsureLiveForGuest(new VmRuntimeObject { Target = type }));
        Assert.Equal("System.ObjectDisposedException", error.ExceptionTypeName);

        var byRef = VmByRef.OwnedStorage(
            new VmRuntimeObject { Target = type }, [StackSlot.Null], 0);
        Assert.Throws<UnhandledGuestException>(() =>
            VmLifetime.EnsureLiveForGuest(StackSlot.OfByRef(byRef)));
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

    [Fact]
    public void DebuggerBreakpointsAreBounded() {
        using var vm = new VirtualMachine(new VmHostOptions { MaxBreakpoints = 1 });

        _ = vm.Debugger.AddBreakpoint("Vm.Input", "Run", 0);
        Assert.Throws<OperationNotAllowedException>(() =>
            vm.Debugger.AddBreakpoint("Vm.Input", "Run", 1));
    }

    private static VirtualMachine NewVm() => new();
}
