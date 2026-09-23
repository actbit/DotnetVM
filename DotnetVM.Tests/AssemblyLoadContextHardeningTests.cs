using DotnetVM.Host;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

public sealed class AssemblyLoadContextHardeningTests {
    [Fact]
    public void LoadFromStream_RejectsOversizeBeforeReadingAssemblyBytes() {
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new() { MaxAssemblyBytes = 32 * 1024 },
        });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes("""
            using System.IO;
            using System.Runtime.Loader;
            namespace Vm.AlcBudget {
                public static class Ops {
                    public static long Load(byte[] input) {
                        var alc = new AssemblyLoadContext("budget", true);
                        return alc.LoadFromStream(new MemoryStream(input)).GetName().Version!.Major;
                    }
                }
            }
            """, "AlcBudgetDriver"));
        vm.LoadAssembly(stream);

        var error = Assert.Throws<OperationNotAllowedException>(
            () => vm.Invoke("Vm.AlcBudget.Ops", "Load", new byte[64 * 1024]));
        Assert.Contains("上限", error.Message);
    }

    [Fact]
    public void LoadFromAssemblyName_UsesFullIdentityForSameSimpleName() {
        var first = CompileVersionedLibrary("1.0.0.0");
        var second = CompileVersionedLibrary("2.0.0.0");
        using var vm = new VirtualMachine();
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes("""
            using System.IO;
            using System.Runtime.Loader;
            using System.Reflection;
            namespace Vm.AlcIdentity {
                public static class Ops {
                    public static string Resolve(byte[] first, byte[] second) {
                        var alc = new AssemblyLoadContext("identity", true);
                        alc.LoadFromStream(new MemoryStream(first));
                        alc.LoadFromStream(new MemoryStream(second));
                        return alc.LoadFromAssemblyName(new AssemblyName(
                            "Vm.IdentityLib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null"))
                            .FullName!;
                    }
                }
            }
            """, "AlcIdentityDriver"));
        vm.LoadAssembly(stream);

        var resolved = Assert.IsType<string>(vm.Invoke("Vm.AlcIdentity.Ops", "Resolve", first, second));
        Assert.Contains("Version=2.0.0.0", resolved);
    }

    [Fact]
    public void CollectibleUnload_RemovesTypeInitializationAndStaticRoots() {
        var image = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes("""
            namespace Vm.AlcCache {
                public class CachedType { public static object Root = new object(); }
            }
            """, "AlcCacheImage"));
        var context = new VmAssemblyContext(_ => throw new InvalidOperationException());
        var loader = new TypeLoader(image);
        context.Register(loader);
        loader.CompletePendingTypes();
        var type = Assert.IsType<VmClassType>(loader.FindTypeByFullName("Vm.AlcCache.CachedType"));

        using var shared = new VmSharedState();
        var facade = new VmRuntimeObject { Target = type };
        shared.TypeFacades[type] = facade;
        shared.TypeInitialization.Ensure(type, static () => { });
        var staticStorage = new UnifiedStaticStorage();
        staticStorage.GetOrCreate(type, null, () => [StackSlot.OfObject(facade)]);

        var loadContext = new VmAssemblyLoadContext {
            Context = context,
            Name = "cache",
            IsCollectible = true,
            IsDefault = false,
            UnloadAction = () => {
                shared.RemoveAssemblyContextCaches(context);
                staticStorage.RemoveForContext(context);
                context.Unregister(loader);
            },
        };
        loadContext.Unload();

        Assert.True(loadContext.IsUnloaded);
        Assert.Empty(shared.TypeFacades);
        Assert.Equal(TypeInitializationStatus.NotStarted, shared.TypeInitialization.GetStatus(type));
        Assert.Empty(staticStorage.EnumerateRoots());
    }

    [Fact]
    public void NamedNonCollectibleLoadContext_CannotUnload() {
        var loadContext = new VmAssemblyLoadContext {
            Context = new VmAssemblyContext(_ => throw new InvalidOperationException()),
            Name = "noncollectible",
            IsCollectible = false,
            IsDefault = false,
        };

        Assert.Throws<InvalidOperationException>(loadContext.Unload);
        Assert.False(loadContext.IsUnloaded);
    }

    [Fact]
    public void LoadFromAssemblyBytes_RejectsByLengthBeforeHostCopy() {
        var memory = new MemoryPolicy { MaxAssemblyBytes = 8 };
        var heap = new VmHeap(memory);
        var loader = new TypeLoader(AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "AlcByteInput")));
        using var shared = new VmSharedState();
        var context = CreateIntrinsicContext(heap, loader, shared, memory);
        var registry = new IntrinsicRegistry();
        AssemblyLoadContextRuntime.RegisterAll(registry);
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyBytes", 1), out var invoke));

        var vmArray = ByteArray(16);
        var loadContext = new VmAssemblyLoadContext {
            Context = new VmAssemblyContext(_ => throw new InvalidOperationException()),
            Name = "bytes",
            IsCollectible = true,
            IsDefault = false,
        };
        var before = heap.Snapshot();

        Assert.Throws<OperationNotAllowedException>(() => invoke(context,
            [StackSlot.OfObject(loadContext), StackSlot.OfObject(vmArray)]));
        Assert.Equal(before.TotalAllocatedBytes, heap.Snapshot().TotalAllocatedBytes);
    }

    [Fact]
    public void MemoryStreamByteArrayConstructor_ReservesHostBufferBeforeCopy() {
        var memory = new MemoryPolicy { HostTempAllocationByteLimit = 64 };
        var heap = new VmHeap(memory);
        var loader = new TypeLoader(AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "MemoryStreamInput")));
        using var shared = new VmSharedState();
        var context = CreateIntrinsicContext(heap, loader, shared, memory);
        var registry = new IntrinsicRegistry();
        AssemblyLoadContextRuntime.RegisterAll(registry);
        Assert.True(registry.TryGet(IntrinsicKey.Instance("System.IO.MemoryStream", ".ctor", 1), out var invoke));

        var before = heap.Snapshot();
        var instance = new VmMemoryStreamObject { Bytes = [] };
        Assert.Throws<MemoryQuotaExceededException>(() => invoke(context,
            [StackSlot.OfObject(instance), StackSlot.OfObject(ByteArray(64))]));
        Assert.Empty(instance.Bytes);
        Assert.Equal(before.TotalAllocatedBytes, heap.Snapshot().TotalAllocatedBytes);
    }

    [Fact]
    public void StorageRead_CombinesCallerLimitWithStorageLimits() {
        var bridge = new RecordingStorageBridge();
        var gateway = new StorageGateway(new StoragePolicy {
            MaxBytesPerOperation = 10,
            TotalByteLimit = 6,
        }, bridge);

        Assert.Equal(4, gateway.Read("first", callerMaxBytes: 4).Length);
        Assert.Equal(2, gateway.Read("second", callerMaxBytes: 9).Length);
        Assert.Equal(new long[] { 4, 2 }, bridge.ReadLimits);
    }

    [Fact]
    public void DynamicMethod_RejectsInvalidLocalAndTokenBeforeDelegateCreation() {
        var loader = new TypeLoader(AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "DynamicMethodInput")));
        var heap = new VmHeap(new MemoryPolicy());
        var local = new VmDynamicMethodBuilder(loader, heap, "badLocal",
            new SigType(SigKind.Void), [], maxMethodBodyBytes: 128);
        local.EmitOpcode((ushort)ILOp.Ldloc_0);
        local.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => local.CreateMethod());

        var token = new VmDynamicMethodBuilder(loader, heap, "badToken",
            new SigType(SigKind.Void), [], maxMethodBodyBytes: 128);
        token.EmitToken((ushort)ILOp.Call, 0x06FF_FFFF);
        token.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => token.CreateMethod());
    }

    private static byte[] CompileVersionedLibrary(string version) => TestAssemblyCompiler.CompileToBytes($$"""
        [assembly: System.Reflection.AssemblyVersion("{{version}}")]
        namespace Vm.Identity { public class Marker { } }
        """, "Vm.IdentityLib");

    private static IntrinsicContext CreateIntrinsicContext(VmHeap heap, TypeLoader loader,
        VmSharedState shared, MemoryPolicy memory) => new() {
        Console = new(),
        Strings = new VmStringPool(heap),
        Heap = heap,
        Types = loader,
        Shared = shared,
        MemoryPolicy = memory,
    };

    private static VmArray ByteArray(int length) => new(
        new VmArrayType {
            ElementType = new VmIntrinsicType { Namespace = "System", Name = "Byte", IsValue = true },
        },
        Enumerable.Range(0, length).Select(_ => StackSlot.OfInt32(0)).ToArray());

    private sealed class RecordingStorageBridge : IStorageBridge {
        public List<long> ReadLimits { get; } = [];
        public bool Exists(string path) => true;
        public byte[] Read(string path, long maxBytes) {
            ReadLimits.Add(maxBytes);
            return new byte[checked((int)maxBytes)];
        }
        public void Write(string path, ReadOnlyMemory<byte> contents) { }
        public void Delete(string path) { }
    }
}
