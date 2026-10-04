using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class BclRuntimeRepresentationTests {
    private static VmIntrinsicInstance Object(VmHeap heap) => heap.Allocate(new VmIntrinsicInstance(new VmIntrinsicType { Namespace = "Tests", Name = "Object", IsValue = false }, 0));

    [Fact]
    public void DependentHandlesReachAFixedPointAndDoNotKeepDeadCyclesAlive() {
        var handles = new DependentHandleTable();
        var heap = new VmHeap(new MemoryPolicy()) { DependentHandles = handles };
        var first = Object(heap); var second = Object(heap); var third = Object(heap);
        var a = handles.Allocate(StackSlot.OfObject(first), StackSlot.OfObject(second));
        var b = handles.Allocate(StackSlot.OfObject(second), StackSlot.OfObject(third));
        var c = handles.Allocate(StackSlot.OfObject(third), StackSlot.OfObject(first));
        VmObject? root = first;
        heap.AddRootObjectSource(() => [root]);
        heap.Collect();
        Assert.Equal(3, heap.TrackedObjects.Count);
        root = null; heap.Collect();
        Assert.Empty(heap.TrackedObjects);
        Assert.Null(handles.Get(a).Target.ObjectValue);
        Assert.Null(handles.Get(b).Dependent.ObjectValue);
        Assert.Null(handles.Get(c).Target.ObjectValue);
    }

    [Fact]
    public void StrongHandlesKeepTargetsAliveUntilReleased() {
        var handles = new DependentHandleTable();
        var heap = new VmHeap(new MemoryPolicy()) { DependentHandles = handles };
        var value = Object(heap);
        var handle = handles.Allocate(StackSlot.OfObject(value), default, strong: true);
        heap.Collect(); Assert.Contains(value, heap.TrackedObjects);
        handles.Free(handle); heap.Collect(); Assert.Empty(heap.TrackedObjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideLocalOperandsExecuteInInterpreterAndJit(bool jit) {
        using var vm = new VirtualMachine(new VmHostOptions { EnableJit = jit, JitPromotionThreshold = 1 });
        vm.LoadAssembly(new MemoryStream(TestAssemblyCompiler.CompileToBytes("public static class Input { public static int Run() => 0; }", "WideLocalOperands")));
        var loader = Assert.Single(vm.Loaders);
        var builder = new VmDynamicMethodBuilder(loader, vm.Heap, "WideLocal", new SigType(SigKind.I4), [], 32);
        for (int i = 0; i < 300; i++) builder.DeclareLocal(new SigType(SigKind.I4));
        builder.EmitInt32((ushort)ILOp.Ldc_I4, 42);
        builder.EmitInt32((ushort)ILOp.Stloc, 299);
        builder.EmitInt32((ushort)ILOp.Ldloc, 299);
        builder.EmitOpcode((ushort)ILOp.Ret);
        Assert.Equal(42, vm.Execute(builder.CreateMethod()).ReturnValue);
    }
}
