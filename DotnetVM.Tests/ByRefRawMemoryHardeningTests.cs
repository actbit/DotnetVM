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

/// <summary>ByRef/raw-memory の越境、overflow、readonly を guest 例外へ閉じ込めるテスト。</summary>
public sealed class ByRefRawMemoryHardeningTests {
    private const string Source = """
        using System;
        namespace Vm;
        public static unsafe class Raw {
            public static int DereferenceLast() {
                byte* p = stackalloc byte[4];
                p[3] = 42;
                return p[3];
            }

            public static void DereferenceOnePast() {
                byte* p = stackalloc byte[4];
                p[4] = 1;
            }

            public static void DereferenceNegative() {
                byte* p = stackalloc byte[4];
                p[-1] = 1;
            }

            public static int OverlapCopy() {
                byte* p = stackalloc byte[8];
                for (var i = 0; i < 8; i++) p[i] = (byte)i;
                for (var i = 5; i >= 0; i--) p[i + 2] = p[i];
                return p[2] * 100 + p[3] * 10 + p[7];
            }

            public static void NegativeAllocation() {
                var n = -1;
                int* p = stackalloc int[n];
                _ = p;
            }

            public static int ArrayElementAddress() {
                var values = new[] { 1, 2, 3 };
                ref var element = ref values[1];
                element = 42;
                return values[1];
            }

            // The array local disappears at the return boundary. The returned managed
            // ByRef must retain the VmArray as its GC/accounting owner.
            public static ref int ReturnArrayElementReference() {
                var values = new[] { 41 };
                return ref values[0];
            }

            public static ref int ReturnInstanceFieldReference() {
                var holder = new Holder { Value = 43 };
                return ref holder.Value;
            }

            public static int ReadOnlyArrayElementAddress() {
                var values = new[] { 7, 8, 9 };
                ref readonly var element = ref values[1];
                return element;
            }
        }

        public sealed class Holder {
            public int Value;
        }

        public struct Cell { public int Value; }
        public ref struct ReferenceCell { public ref int Value; }
        [System.Runtime.CompilerServices.InlineArray(4)]
        public struct InlineCells<T> { private T first; }
        """;

    private static readonly byte[] Bytes = TestAssemblyCompiler.CompileToBytes(Source, "ByRefRawMemoryHardening", allowUnsafe: true);

    private static VirtualMachine CreateVm(bool enableJit = false) {
        var vm = new VirtualMachine(new VmHostOptions {
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
            Memory = new MemoryPolicy { InstructionQuota = 100_000 },
        });
        vm.LoadAssembly(new MemoryStream(Bytes));
        return vm;
    }

    [Fact]
    public void ValidLastByteAccessStillWorks() {
        using var vm = CreateVm();
        Assert.Equal(42, vm.Invoke("Vm.Raw", "DereferenceLast"));
    }

    [Theory]
    [InlineData("DereferenceOnePast", "System.IndexOutOfRangeException")]
    [InlineData("DereferenceNegative", "System.OverflowException")]
    public void NativePointerBoundaryIsGuestFailure(string method, string expectedException) {
        using var vm = CreateVm();
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Raw", method));
        Assert.Equal(expectedException, error.ExceptionTypeName);
    }

    [Fact]
    public void NegativeLocallocCannotReachHostAllocation() {
        using var vm = CreateVm();
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Raw", "NegativeAllocation"));
        Assert.Equal("System.OverflowException", error.ExceptionTypeName);
    }

    [Fact]
    public void ArrayElementByRefMutatesOnlyTheSelectedElement() {
        using var vm = CreateVm();
        Assert.Equal(42, vm.Invoke("Vm.Raw", "ArrayElementAddress"));
    }

    [Fact]
    public void ReadOnlyArrayByRefCanBeRead() {
        using var vm = CreateVm();
        Assert.Equal(8, vm.Invoke("Vm.Raw", "ReadOnlyArrayElementAddress"));
    }

    [Fact]
    public void ManagedByRefMethodPromotesToJitWithoutChangingResult() {
        using var vm = CreateVm(enableJit: true);
        Assert.Equal(42, vm.Invoke("Vm.Raw", "ArrayElementAddress"));
        var method = vm.Loaders[0].FindTypeByFullName("Vm.Raw")!.Methods
            .Single(candidate => candidate.Name == "ArrayElementAddress");
        Assert.True(vm.IsJitCompiled(method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedArrayByRefKeepsArrayAliveAcrossCollection(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var byRef = Assert.IsType<VmByRef>(vm.Invoke("Vm.Raw", "ReturnArrayElementReference"));
        var array = Assert.IsType<VmArray>(byRef.Owner);
        Assert.Same(array.Elements, byRef.Container);

        var roots = new Func<IEnumerable<StackSlot[]>>(() => [
            [StackSlot.OfByRef(byRef)],
        ]);
        vm.Heap.AddRootSlotSource(roots);
        try {
            vm.CollectGarbage();
            Assert.Contains(array, vm.Heap.TrackedObjects);
            Assert.Equal(41, byRef.Read().AsInt32);
            byRef.Write(StackSlot.OfInt32(42));
            Assert.Equal(42, byRef.Read().AsInt32);
        } finally {
            vm.Heap.RemoveRootSlotSource(roots);
        }

        if (enableJit)
            Assert.True(vm.IsJitCompiled(vm.Loaders[0].FindTypeByFullName("Vm.Raw")!.Methods
                .Single(method => method.Name == "ReturnArrayElementReference")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedInstanceFieldByRefKeepsOwnerAliveAcrossCollection(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var byRef = Assert.IsType<VmByRef>(vm.Invoke("Vm.Raw", "ReturnInstanceFieldReference"));
        var owner = Assert.IsType<VmClassInstance>(byRef.Owner);
        Assert.Same(owner.Fields, byRef.Container);

        var roots = new Func<IEnumerable<StackSlot[]>>(() => [[StackSlot.OfByRef(byRef)]]);
        vm.Heap.AddRootSlotSource(roots);
        try {
            vm.CollectGarbage();
            Assert.Contains(owner, vm.Heap.TrackedObjects);
            Assert.Equal(43, byRef.Read().AsInt32);
        } finally {
            vm.Heap.RemoveRootSlotSource(roots);
        }

        if (enableJit)
            Assert.True(vm.IsJitCompiled(vm.Loaders[0].FindTypeByFullName("Vm.Raw")!.Methods
                .Single(method => method.Name == "ReturnInstanceFieldReference")));
    }

    [Fact]
    public void InvalidAndReadonlyByRefsFailClosed() {
        var readOnly = new VmByRef([StackSlot.OfInt32(1)], 0, isReadOnly: true);
        Assert.Throws<UnhandledGuestException>(() => readOnly.Write(StackSlot.OfInt32(2)));

        var onePast = new VmByRef([], 0);
        Assert.Throws<UnhandledGuestException>(() => _ = onePast.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFieldStoreCannotBypassReadonlyArrayReference(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var loader = vm.Loaders[0];
        var cell = Assert.IsType<VmClassType>(loader.FindTypeByFullName("Vm.Cell"));
        var builder = new VmDynamicMethodBuilder(loader, vm.Heap, "WriteReadonlyField",
            new SigType(SigKind.Void), [], maxMethodBodyBytes: 128);
        builder.EmitOpcode((ushort)ILOp.Ldc_I4_1);
        builder.EmitReference((ushort)ILOp.Newarr, cell);
        builder.EmitOpcode((ushort)ILOp.Ldc_I4_0);
        builder.EmitOpcode((ushort)ILOp.Readonly);
        builder.EmitReference((ushort)ILOp.Ldelema, cell);
        builder.EmitInt32((ushort)ILOp.Ldc_I4, 42);
        builder.EmitReference((ushort)ILOp.Stfld, cell.Fields.Single(field => field.Name == "Value"));
        builder.EmitOpcode((ushort)ILOp.Ret);
        var method = builder.CreateMethod();

        var error = Assert.Throws<UnhandledGuestException>(() => vm.Execute(method));
        Assert.Equal("System.InvalidProgramException", error.ExceptionTypeName);
        if (enableJit) Assert.True(vm.IsJitCompiled(method));
    }

    [Fact]
    public void ReadonlyNativeMemoryRejectsIndirectAndBlockWrites() {
        var memory = new VmLocallocMemory { Bytes = new byte[8] };
        var pointer = new VmNativePointer { Memory = memory, ByteOffset = 0, IsReadOnly = true };
        var address = StackSlot.OfObject(pointer);

        Assert.Throws<UnhandledGuestException>(() => MemoryOps.StoreIndirect(
            ILOp.Stind_I4, address, StackSlot.OfInt32(1)));
        Assert.Throws<UnhandledGuestException>(() => MemoryOps.CopyMemoryBlock(
            address, StackSlot.OfObject(new VmNativePointer { Memory = memory, ByteOffset = 0 }), 4));
        Assert.Throws<UnhandledGuestException>(() => MemoryOps.InitMemoryBlock(
            address, StackSlot.OfInt32(0), 4));
    }

    [Fact]
    public void OverlappingNativeCopyUsesMemmoveSemantics() {
        using var vm = CreateVm();
        Assert.Equal(15, vm.Invoke("Vm.Raw", "OverlapCopy"));
    }

    [Fact]
    public void TemporaryNativeReferencesRootOwnersAndByteWritesInvalidateThem() {
        using var vm = CreateVm();
        var type = vm.Loaders[0].FindTypeByFullName("Vm.Cell")!;
        var owner = vm.Heap.Allocate(new VmClassInstance(type, new ObjectModel().CreateInstanceStorage(type, vm.Loaders[0])));
        var memory = vm.Heap.Allocate(new VmLocallocMemory { Bytes = new byte[16] });
        var first = StackSlot.OfObject(new VmNativePointer { Memory = memory });
        var second = StackSlot.OfObject(new VmNativePointer { Memory = memory, ByteOffset = 8 });
        MemoryOps.StoreIndirect(ILOp.Stind_I, second, StackSlot.OfByRef(VmByRef.OwnedStorage(owner, owner.Fields, 0)));
        MemoryOps.CopyMemoryBlock(first, second, 8);
        MemoryOps.InitMemoryBlock(second, StackSlot.OfInt32(0), 8);
        Func<IEnumerable<StackSlot[]>> roots = () => [[first]];
        vm.Heap.AddRootSlotSource(roots);
        try {
            vm.CollectGarbage();
            Assert.Contains(owner, vm.Heap.TrackedObjects);
            Assert.Same(owner, Assert.IsType<VmByRef>(MemoryOps.LoadIndirect(ILOp.Ldind_I, first).ObjectValue).Owner);
            MemoryOps.StoreIndirect(ILOp.Stind_I1, first, StackSlot.OfInt32(0));
            vm.CollectGarbage();
            Assert.DoesNotContain(owner, vm.Heap.TrackedObjects);
        } finally { vm.Heap.RemoveRootSlotSource(roots); }
    }

    [Fact]
    public void GenericInlineArrayRawLayoutUsesElementTypeAndDeclaredLength() {
        using var vm = CreateVm();
        var type = new VmConstructedType {
            Definition = vm.Loaders[0].FindTypeByFullName("Vm.InlineCells`1")!,
            TypeArguments = [vm.Loaders[0].ResolveToken(new SigType(SigKind.I8))],
        };
        Assert.Equal(32, MemoryOps.SizeOfRawType(type));
        var pointer = new VmNativePointer { Memory = new VmLocallocMemory { Bytes = new byte[32] } };
        MemoryOps.WritePointerValue(pointer, type, StackSlot.OfValueType(new VmStructValue(type,
            [StackSlot.OfInt64(11), StackSlot.OfInt64(22), StackSlot.OfInt64(33), StackSlot.OfInt64(44)])));
        var value = Assert.IsType<VmStructValue>(MemoryOps.ReadPointerValue(pointer, type).ObjectValue);
        Assert.Equal(new long[] {11, 22, 33, 44}, value.Fields.Select(field => field.Int64Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedNativeCopyPreservesManagedReferences(bool enableJit) {
        using var vm = CreateVm(enableJit);
        var loader = vm.Loaders[0];
        var type = loader.FindTypeByFullName("Vm.ReferenceCell")!;
        var ownerType = loader.FindTypeByFullName("Vm.Cell")!;
        var owner = vm.Heap.Allocate(new VmClassInstance(ownerType, new ObjectModel().CreateInstanceStorage(ownerType, loader)));
        var reference = VmByRef.OwnedStorage(owner, owner.Fields, 0);
        var source = new VmNativePointer { Memory = vm.Heap.Allocate(new VmLocallocMemory { Bytes = new byte[VmPrimitiveTypes.NativeIntSizeBytes] }) };
        var destination = new VmNativePointer { Memory = vm.Heap.Allocate(new VmLocallocMemory { Bytes = new byte[VmPrimitiveTypes.NativeIntSizeBytes] }) };
        MemoryOps.WritePointerValue(source, type, StackSlot.OfValueType(new VmStructValue(type, [StackSlot.OfByRef(reference)])));
        var builder = new VmDynamicMethodBuilder(loader, vm.Heap, "CopyReferences", new SigType(SigKind.Void),
            [new SigType(SigKind.I), new SigType(SigKind.I)], maxMethodBodyBytes: 128);
        builder.EmitOpcode((ushort)ILOp.Ldarg_0);
        builder.EmitOpcode((ushort)ILOp.Ldarg_1);
        builder.EmitReference((ushort)ILOp.Cpobj, type);
        builder.EmitOpcode((ushort)ILOp.Ret);
        var method = builder.CreateMethod();
        vm.Execute(method, destination, source);
        var copied = Assert.IsType<VmStructValue>(MemoryOps.ReadPointerValue(destination, type).ObjectValue);
        Assert.Same(owner, Assert.IsType<VmByRef>(copied.Fields[0].ObjectValue).Owner);
        if (enableJit) Assert.True(vm.IsJitCompiled(method));
    }
}
