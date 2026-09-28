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
    public void LoadFromStream_RejectsOversizeBeforeCallingLoaderAndKeepsPosition() {
        var memory = new MemoryPolicy { MaxAssemblyBytes = 8 };
        var heap = new VmHeap(memory);
        var loader = CreateLoader("StreamQuotaInput");
        using var shared = new VmSharedState();
        var callbackCount = 0;
        var context = CreateIntrinsicContext(heap, loader, shared, memory, (_, _) => {
            callbackCount++;
            return loader;
        });
        var registry = CreateAssemblyIntrinsicRegistry();
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "LoadFromStream", 1), out var invoke));
        var stream = new VmMemoryStreamObject { Bytes = new byte[18], Position = 9 };

        Assert.Throws<OperationNotAllowedException>(() => invoke(context,
            [StackSlot.OfObject(NewLoadContext("oversize")), StackSlot.OfObject(stream)]));

        Assert.Equal(0, callbackCount);
        Assert.Equal(9, stream.Position);
    }

    [Fact]
    public void LoadFromStream_ExactLimitPassesOnlyUnreadSliceAndAdvancesOnSuccess() {
        var memory = new MemoryPolicy { MaxAssemblyBytes = 8 };
        var heap = new VmHeap(memory);
        var loader = CreateLoader("StreamSliceInput");
        using var shared = new VmSharedState();
        byte[]? observed = null;
        var context = CreateIntrinsicContext(heap, loader, shared, memory, (_, bytes) => {
            observed = bytes.ToArray();
            return loader;
        });
        var registry = CreateAssemblyIntrinsicRegistry();
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "LoadFromStream", 1), out var invoke));
        var stream = new VmMemoryStreamObject {
            Bytes = [99, 98, 97, 1, 2, 3, 4, 5, 6, 7, 8],
            Position = 3,
        };

        var result = invoke(context,
            [StackSlot.OfObject(NewLoadContext("slice")), StackSlot.OfObject(stream)]);

        Assert.IsType<VmAssemblyObject>(result?.ObjectValue);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, observed);
        Assert.Equal(stream.Bytes.Length, stream.Position);
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
    public void DisposeRetiresNamedContextsAndReleasesLoadedImageBudget() {
        var input = TestAssemblyCompiler.CompileToBytes(
            "public static class Input { public static int Run() => 0; }", "DisposeNamedInput");
        using var vm = new VirtualMachine();
        using var driver = new MemoryStream(TestAssemblyCompiler.CompileToBytes("""
            using System.IO;
            using System.Runtime.Loader;
            public static class Ops {
                public static void Load(byte[] input) {
                    var alc = new AssemblyLoadContext("dispose", true);
                    _ = alc.LoadFromStream(new MemoryStream(input));
                }
            }
            """, "DisposeNamedDriver"));
        vm.LoadAssembly(driver);

        vm.Invoke("Ops", "Load", input);

        Assert.True(vm.Loaders.Count >= 2);
        Assert.True(vm.Heap.LoadedAssemblyBytes > 0);

        vm.Dispose();

        Assert.Empty(vm.Loaders);
        Assert.Equal(0, vm.Heap.LoadedAssemblyBytes);
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
        var arrayType = new VmArrayType { ElementType = type };
        shared.TypeFacades[arrayType] = new VmRuntimeObject { Target = arrayType };
        staticStorage.GetOrCreate(type, [arrayType], () => [StackSlot.OfObject(facade)]);

        var retainedImage = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
            "namespace Vm.AlcCacheOther { public class RetainedType { } }", "AlcCacheRetainedImage"));
        var retainedContext = new VmAssemblyContext(_ => throw new InvalidOperationException());
        var retainedLoader = new TypeLoader(retainedImage);
        retainedContext.Register(retainedLoader);
        retainedLoader.CompletePendingTypes();
        var retainedType = Assert.IsType<VmClassType>(retainedLoader.FindTypeByFullName("Vm.AlcCacheOther.RetainedType"));
        var retainedFacade = new VmRuntimeObject { Target = retainedType };
        shared.TypeFacades[retainedType] = retainedFacade;
        shared.TypeInitialization.Ensure(retainedType, static () => { });
        staticStorage.GetOrCreate(retainedType, null, () => [StackSlot.OfObject(retainedFacade)]);

        var unloadActionCount = 0;
        var loadContext = new VmAssemblyLoadContext {
            Context = context,
            Name = "cache",
            IsCollectible = true,
            IsDefault = false,
            UnloadAction = () => {
                unloadActionCount++;
                shared.RemoveAssemblyContextCaches(context);
                staticStorage.RemoveForContext(context);
                context.Unregister(loader);
            },
        };
        loadContext.Unload();
        loadContext.Unload();

        Assert.True(loadContext.IsUnloaded);
        Assert.Equal(1, unloadActionCount);
        Assert.DoesNotContain(type, shared.TypeFacades.Keys);
        Assert.DoesNotContain(arrayType, shared.TypeFacades.Keys);
        Assert.Same(retainedFacade, shared.TypeFacades[retainedType]);
        Assert.Equal(TypeInitializationStatus.NotStarted, shared.TypeInitialization.GetStatus(type));
        Assert.Equal(TypeInitializationStatus.Completed, shared.TypeInitialization.GetStatus(retainedType));
        Assert.Single(staticStorage.EnumerateRoots());
    }

    [Fact]
    public void CompletedTaskCache_UsesStructuredTypeIdentityAndDropsCollectibleTypes() {
        static (VmAssemblyContext Context, VmClassType Type) LoadMarker(string assemblyName) {
            var image = AssemblyImage.Parse(TestAssemblyCompiler.CompileToBytes(
                "namespace Vm.SameName { public sealed class Marker { } }", assemblyName));
            var context = new VmAssemblyContext(_ => throw new InvalidOperationException());
            var loader = new TypeLoader(image);
            context.Register(loader);
            loader.CompletePendingTypes();
            return (context, Assert.IsType<VmClassType>(loader.FindTypeByFullName("Vm.SameName.Marker")));
        }

        var (collectibleContext, collectibleType) = LoadMarker("SameName.Collectible");
        var (retainedContext, retainedType) = LoadMarker("SameName.Retained");
        Assert.Equal(collectibleType.FullName, retainedType.FullName);
        Assert.NotSame(collectibleType, retainedType);

        using var shared = new VmSharedState();
        var heap = new VmHeap(new MemoryPolicy());
        var taskDefinition = new VmIntrinsicType {
            Namespace = "System.Threading.Tasks",
            Name = "Task`1",
            IsValue = false,
        };
        var collectibleTaskType = new VmConstructedType {
            Definition = taskDefinition,
            TypeArguments = [collectibleType],
        };
        var retainedTaskType = new VmConstructedType {
            Definition = taskDefinition,
            TypeArguments = [retainedType],
        };

        var collectibleTask = shared.GuestTasks.CompletedSentinel(collectibleTaskType, default, heap);
        var retainedTask = shared.GuestTasks.CompletedSentinel(retainedTaskType, default, heap);
        Assert.NotSame(collectibleTask, retainedTask);
        Assert.Collection(shared.GuestTasks.EnumerateRoots(), _ => { }, _ => { });
        Assert.Collection(heap.TrackedObjects, _ => { }, _ => { });

        shared.RemoveAssemblyContextCaches(collectibleContext);

        Assert.DoesNotContain(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, collectibleTask)));
        Assert.Contains(shared.GuestTasks.EnumerateRoots(), roots =>
            roots.Any(slot => ReferenceEquals(slot.ObjectValue, retainedTask)));
        Assert.Single(shared.GuestTasks.EnumerateRoots());
        Assert.Same(retainedContext, retainedType.Loader!.Context);
    }

    [Fact]
    public void CollectibleUnload_ThroughGuestApiRemovesLoaderAndTypeCachesAcrossCycles() {
        var childBytes = TestAssemblyCompiler.CompileToBytes("""
            using System.Threading.Tasks;
            namespace Vm.AlcUnloadChild {
                public class Marker { public static object Root = new object(); }
                public static class Ops {
                    public static Task<Marker> AsTask() => default(ValueTask<Marker>).AsTask();
                }
            }
            """, "AlcUnloadChild");
        using var vm = new VirtualMachine();
        using var driver = new MemoryStream(TestAssemblyCompiler.CompileToBytes("""
            using System.IO;
            using System.Runtime.Loader;
            namespace Vm.AlcUnloadDriver {
                public static class Ops {
                    private static AssemblyLoadContext? current;
                    public static void Load(byte[] bytes) {
                        current = new AssemblyLoadContext("cycle", true);
                        current.LoadFromStream(new MemoryStream(bytes));
                    }
                    public static void Unload() => current!.Unload();
                }
            }
            """, "AlcUnloadDriver"));
        vm.LoadAssembly(driver);

        for (var cycle = 0; cycle < 3; cycle++) {
            vm.Invoke("Vm.AlcUnloadDriver.Ops", "Load", childBytes);
            var childLoader = Assert.Single(vm.Loaders, loader => loader.Image.Name == "AlcUnloadChild");
            var childType = Assert.IsType<VmClassType>(childLoader.FindTypeByFullName("Vm.AlcUnloadChild.Marker"));
            var sentinel = Assert.IsType<VmTaskObject>(vm.Invoke("Vm.AlcUnloadChild.Ops", "AsTask"));
            Assert.Contains(vm.SharedState.GuestTasks.EnumerateRoots(), roots =>
                roots.Any(slot => ReferenceEquals(slot.ObjectValue, sentinel)));
            vm.SharedState.TypeFacades[childType] = new VmRuntimeObject { Target = childType };
            vm.SharedState.TypeInitialization.Ensure(childType, static () => { });

            vm.Invoke("Vm.AlcUnloadDriver.Ops", "Unload");

            Assert.DoesNotContain(vm.Loaders, loader => loader.Image.Name == "AlcUnloadChild");
            Assert.DoesNotContain(childType, vm.SharedState.TypeFacades.Keys);
            Assert.DoesNotContain(vm.SharedState.GuestTasks.EnumerateRoots(), roots =>
                roots.Any(slot => ReferenceEquals(slot.ObjectValue, sentinel)));
            Assert.Equal(TypeInitializationStatus.NotStarted, vm.SharedState.TypeInitialization.GetStatus(childType));
        }
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
    public void LoadFromAssemblyBytes_ExactLimitReservesHostBufferBeforeLoaderCallback() {
        const int length = 8;
        var memory = new MemoryPolicy { MaxAssemblyBytes = length };
        var heap = new VmHeap(memory);
        var loader = CreateLoader("AssemblyBytesExactInput");
        using var shared = new VmSharedState();
        long? bytesChargedAtCallback = null;
        var context = CreateIntrinsicContext(heap, loader, shared, memory, (_, bytes) => {
            bytesChargedAtCallback = heap.Snapshot().TotalAllocatedBytes;
            Assert.Equal(length, bytes.Length);
            return loader;
        });
        var registry = CreateAssemblyIntrinsicRegistry();
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyBytes", 1), out var invoke));
        var before = heap.Snapshot().TotalAllocatedBytes;

        var result = invoke(context,
            [StackSlot.OfObject(NewLoadContext("exact")), StackSlot.OfObject(ByteArray(length))]);

        Assert.IsType<VmAssemblyObject>(result?.ObjectValue);
        Assert.True(bytesChargedAtCallback > before);
    }

    [Fact]
    public void LoadFromAssemblyName_DoesNotFallbackToSameSimpleNameWithDifferentVersion() {
        var memory = new MemoryPolicy();
        var heap = new VmHeap(memory);
        var loaded = new TypeLoader(AssemblyImage.Parse(CompileVersionedLibrary("1.0.0.0")));
        var vmContext = new VmAssemblyContext(_ => throw new InvalidOperationException());
        vmContext.Register(loaded);
        using var shared = new VmSharedState();
        var context = CreateIntrinsicContext(heap, loaded, shared, memory);
        var registry = CreateAssemblyIntrinsicRegistry();
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyName", 1), out var invoke));

        var error = Assert.Throws<UnhandledGuestException>(() => invoke(context, [
            StackSlot.OfObject(NewLoadContext("identity", vmContext)),
            StackSlot.OfObject(new VmAssemblyNameObject {
                FullName = "Vm.IdentityLib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null",
            }),
        ]));

        Assert.Equal("System.IO.FileNotFoundException", error.ExceptionTypeName);
        Assert.Single(vmContext.Loaders);
    }

    [Fact]
    public void AssemblyIdentityExactMatch_RequiresVersionCultureAndPublicKeyToken() {
        var expected = AssemblyIdentity.ParseFullName(
            "Example, Version=1.2.3.4, Culture=neutral, PublicKeyToken=0011223344556677");

        Assert.False(expected.MatchesExactly(AssemblyIdentity.ParseFullName(
            "Example, Version=1.2.3.5, Culture=neutral, PublicKeyToken=0011223344556677")));
        Assert.False(expected.MatchesExactly(AssemblyIdentity.ParseFullName(
            "Example, Version=1.2.3.4, Culture=fr-FR, PublicKeyToken=0011223344556677")));
        Assert.False(expected.MatchesExactly(AssemblyIdentity.ParseFullName(
            "Example, Version=1.2.3.4, Culture=neutral, PublicKeyToken=8899aabbccddeeff")));
        Assert.True(expected.MatchesExactly(AssemblyIdentity.ParseFullName(
            "example, Version=1.2.3.4, Culture=neutral, PublicKeyToken=0011223344556677")));
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
    public void StorageRead_RejectsBridgeOverResponseWithoutChargingQuota() {
        var bridge = new RecordingStorageBridge { ReadHandler = (_, _) => new byte[5] };
        var gateway = new StorageGateway(new StoragePolicy {
            MaxBytesPerOperation = 10,
            TotalByteLimit = 100,
        }, bridge);

        Assert.Throws<StorageQuotaExceededException>(() => gateway.Read("oversized", callerMaxBytes: 4));

        Assert.Equal(new long[] { 4 }, bridge.ReadLimits);
        Assert.Equal(0, gateway.TotalBytes);
    }

    [Fact]
    public void StorageRead_RejectsNegativeCallerLimitBeforeCallingBridge() {
        var bridge = new RecordingStorageBridge();
        var gateway = new StorageGateway(new StoragePolicy(), bridge);

        Assert.Throws<ArgumentOutOfRangeException>(() => gateway.Read("negative", callerMaxBytes: -1));

        Assert.Empty(bridge.ReadLimits);
        Assert.Equal(0, gateway.TotalBytes);
    }

    [Fact]
    public void AssemblyLoadContextPathRead_PassesAssemblyLimitToStorageBridge() {
        var driver = TestAssemblyCompiler.CompileToBytes("""
            using System.Runtime.Loader;
            namespace Vm.AlcPathBudget {
                public static class Ops {
                    public static void Read() {
                        var alc = new AssemblyLoadContext("path-budget", true);
                        alc.LoadFromAssemblyPath("/untrusted/oversized.dll");
                    }
                }
            }
            """, "AlcPathBudgetDriver");
        var assemblyLimit = driver.LongLength + 64;
        var bridge = new RecordingStorageBridge { ReadHandler = (_, _) => [] };
        using var vm = new VirtualMachine(new VmHostOptions {
            Memory = new() { MaxAssemblyBytes = assemblyLimit },
            Storage = new() { MaxBytesPerOperation = assemblyLimit * 4, TotalByteLimit = assemblyLimit * 4 },
            StorageBridge = bridge,
        });
        using var stream = new MemoryStream(driver);
        vm.LoadAssembly(stream);

        Assert.ThrowsAny<Exception>(() => vm.Invoke("Vm.AlcPathBudget.Ops", "Read"));

        Assert.Equal(new long[] { assemblyLimit }, bridge.ReadLimits);
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

    [Fact]
    public void DynamicMethod_RejectsMalformedEvaluationStackBeforeDelegateCreation() {
        var loader = CreateLoader("DynamicMethodStackInput");
        var heap = new VmHeap(new MemoryPolicy());

        var underflow = NewDynamicMethod(loader, heap, "stackUnderflow");
        underflow.EmitOpcode((ushort)ILOp.Pop);
        underflow.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => underflow.CreateMethod());

        var nonVoid = new VmDynamicMethodBuilder(loader, heap, "invalidRet",
            new SigType(SigKind.I4), [], maxMethodBodyBytes: 128);
        nonVoid.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => nonVoid.CreateMethod());

        var merge = NewDynamicMethod(loader, heap, "stackMerge");
        var target = merge.DefineLabel();
        merge.EmitOpcode((ushort)ILOp.Ldc_I4_0);
        merge.EmitLabel((ushort)ILOp.BrFalse, target);
        merge.EmitOpcode((ushort)ILOp.Ldc_I4_1);
        merge.MarkLabel(target);
        merge.EmitOpcode((ushort)ILOp.Pop);
        merge.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => merge.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_RejectsStackOverflowAndDanglingPrefix() {
        var loader = CreateLoader("DynamicMethodStackLimitInput");
        var heap = new VmHeap(new MemoryPolicy());

        var overflow = NewDynamicMethod(loader, heap, "stackOverflow");
        for (var i = 0; i < 9; i++)
            overflow.EmitOpcode((ushort)ILOp.Ldc_I4_0);
        Assert.Throws<BadImageFormatException>(() => overflow.CreateMethod());

        var prefix = NewDynamicMethod(loader, heap, "danglingPrefix");
        prefix.EmitSByte((ushort)ILOp.Unaligned, 3);
        prefix.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => prefix.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_RejectsShortAndWideArgumentIndexesOutsideSignature() {
        var loader = CreateLoader("DynamicMethodArgumentInput");
        var heap = new VmHeap(new MemoryPolicy());
        var shortArgument = NewDynamicMethod(loader, heap, "badShortArgument");
        shortArgument.EmitByte((ushort)ILOp.Ldarg_S, 0);
        shortArgument.EmitOpcode((ushort)ILOp.Pop);
        shortArgument.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => shortArgument.CreateMethod());

        var wideArgument = NewDynamicMethod(loader, heap, "badWideArgument");
        wideArgument.EmitInt32((ushort)ILOp.Ldarg, 0);
        wideArgument.EmitOpcode((ushort)ILOp.Pop);
        wideArgument.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => wideArgument.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_RejectsReferenceTokenWithWrongOperandKind() {
        var loader = CreateLoader("DynamicMethodReferenceKindInput");
        var heap = new VmHeap(new MemoryPolicy());
        var method = NewDynamicMethod(loader, heap, "badReferenceKind");
        method.EmitReference((ushort)ILOp.Call, "not a VM method");
        method.EmitOpcode((ushort)ILOp.Ret);

        Assert.Throws<BadImageFormatException>(() => method.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_RejectsUnregisteredStringTokenAndUnmarkedBranchLabel() {
        var loader = CreateLoader("DynamicMethodStructureInput");
        var heap = new VmHeap(new MemoryPolicy());
        var stringToken = NewDynamicMethod(loader, heap, "badStringToken");
        stringToken.EmitToken((ushort)ILOp.Ldstr, 0x7000_0001);
        stringToken.EmitOpcode((ushort)ILOp.Pop);
        stringToken.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<BadImageFormatException>(() => stringToken.CreateMethod());

        var branch = NewDynamicMethod(loader, heap, "unmarkedBranch");
        branch.EmitLabel((ushort)ILOp.Br, branch.DefineLabel());
        branch.EmitOpcode((ushort)ILOp.Ret);
        Assert.Throws<UnhandledGuestException>(() => branch.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_RejectsShortBranchOutsideSignedByteRange() {
        var loader = CreateLoader("DynamicMethodBranchRangeInput");
        var heap = new VmHeap(new MemoryPolicy());
        var method = NewDynamicMethod(loader, heap, "longShortBranch", maxMethodBodyBytes: 256);
        var target = method.DefineLabel();
        method.EmitLabel((ushort)ILOp.Br_S, target);
        for (var i = 0; i < 128; i++)
            method.EmitOpcode((ushort)ILOp.Nop);
        method.MarkLabel(target);
        method.EmitOpcode((ushort)ILOp.Ret);

        Assert.Throws<UnhandledGuestException>(() => method.CreateMethod());
    }

    [Fact]
    public void DynamicMethod_BodyLimitRejectsNextInstructionWithoutDamagingPriorCode() {
        var loader = CreateLoader("DynamicMethodBodyLimitInput");
        var heap = new VmHeap(new MemoryPolicy());
        var method = NewDynamicMethod(loader, heap, "bodyLimit", maxMethodBodyBytes: 1);
        method.EmitOpcode((ushort)ILOp.Ret);

        Assert.Throws<OperationNotAllowedException>(() => method.EmitOpcode((ushort)ILOp.Nop));
        Assert.NotNull(method.CreateMethod().Body);
    }

    [Fact]
    public void NamedNonCollectibleUnloadIntrinsic_ThrowsGuestExceptionWithoutCleanup() {
        var loader = CreateLoader("NonCollectibleUnloadInput");
        var memory = new MemoryPolicy();
        var heap = new VmHeap(memory);
        using var shared = new VmSharedState();
        var context = CreateIntrinsicContext(heap, loader, shared, memory);
        var registry = CreateAssemblyIntrinsicRegistry();
        Assert.True(registry.TryGet(IntrinsicKey.Instance(
            "System.Runtime.Loader.AssemblyLoadContext", "Unload", 0), out var invoke));
        var cleanupCount = 0;
        var alc = NewLoadContext("named-default-semantics", isCollectible: false);
        alc = new VmAssemblyLoadContext {
            Context = alc.Context,
            Name = alc.Name,
            IsCollectible = false,
            IsDefault = false,
            UnloadAction = () => cleanupCount++,
        };

        var error = Assert.Throws<UnhandledGuestException>(() => invoke(context, [StackSlot.OfObject(alc)]));

        Assert.Equal("System.InvalidOperationException", error.ExceptionTypeName);
        Assert.Equal(0, cleanupCount);
        Assert.False(alc.IsUnloaded);
    }

    private static byte[] CompileVersionedLibrary(string version) => TestAssemblyCompiler.CompileToBytes($$"""
        [assembly: System.Reflection.AssemblyVersion("{{version}}")]
        namespace Vm.Identity { public class Marker { } }
        """, "Vm.IdentityLib");

    private static IntrinsicContext CreateIntrinsicContext(VmHeap heap, TypeLoader loader,
        VmSharedState shared, MemoryPolicy memory,
        Func<VmAssemblyLoadContext, ReadOnlyMemory<byte>, TypeLoader>? loadAssembly = null) => new() {
        Console = new(),
        Strings = new VmStringPool(heap),
        Heap = heap,
        Types = loader,
        Shared = shared,
        MemoryPolicy = memory,
        LoadAssemblyInContext = loadAssembly,
    };

    private static TypeLoader CreateLoader(string assemblyName) => new(AssemblyImage.Parse(
        TestAssemblyCompiler.CompileToBytes("public static class Input { public static int Run() => 0; }", assemblyName)));

    private static IntrinsicRegistry CreateAssemblyIntrinsicRegistry() {
        var registry = new IntrinsicRegistry();
        AssemblyLoadContextRuntime.RegisterAll(registry);
        return registry;
    }

    private static VmAssemblyLoadContext NewLoadContext(string name, VmAssemblyContext? context = null,
        bool isCollectible = true) => new() {
        Context = context ?? new VmAssemblyContext(_ => throw new InvalidOperationException()),
        Name = name,
        IsCollectible = isCollectible,
        IsDefault = false,
    };

    private static VmDynamicMethodBuilder NewDynamicMethod(TypeLoader loader, VmHeap heap, string name,
        int maxMethodBodyBytes = 128) => new(loader, heap, name, new SigType(SigKind.Void), [], maxMethodBodyBytes);

    private static VmArray ByteArray(int length) => new(
        new VmArrayType {
            ElementType = new VmIntrinsicType { Namespace = "System", Name = "Byte", IsValue = true },
        },
        Enumerable.Range(0, length).Select(_ => StackSlot.OfInt32(0)).ToArray());

    private sealed class RecordingStorageBridge : IStorageBridge {
        public List<long> ReadLimits { get; } = [];
        public Func<string, long, byte[]>? ReadHandler { get; init; }
        public bool Exists(string path) => true;
        public byte[] Read(string path, long maxBytes) {
            ReadLimits.Add(maxBytes);
            return ReadHandler?.Invoke(path, maxBytes) ?? new byte[checked((int)maxBytes)];
        }
        public void Write(string path, ReadOnlyMemory<byte> contents) { }
        public void Delete(string path) { }
    }
}
