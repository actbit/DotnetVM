using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Policy;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>VM-owned identities and storage for the CoreCLR metadata ABI.
/// Addresses identify VM data and are never dereferenced as host pointers.</summary>
internal sealed class VmRuntimeMetadata {
    private readonly object _gate = new();
    private readonly Dictionary<object, long> _identities = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, object> _handles = [];
    private readonly Dictionary<long, VmStructValue> _storage = [];
    private readonly Dictionary<TypeLoader, VmClassInstance> _modules = [];
    private readonly Dictionary<VmArray, VmNativePointer> _runtimeArrayData = [];
    private VmArray? _castCache;
    private long _nextAddress = 0x10000;
    private readonly ObjectModel _objects = new();

    internal long Identity(IntrinsicContext ctx, object value) {
        lock (_gate) {
            if (_identities.TryGetValue(value, out var existing)) return existing;
            ctx.Heap.ChargeHostBuffer(64);
            var address = _nextAddress;
            _nextAddress = checked(_nextAddress + 0x1000);
            _identities.Add(value, address);
            _handles.Add(address, value);
            return address;
        }
    }

    internal T Resolve<T>(long address) where T : class {
        lock (_gate) {
            if (!_handles.TryGetValue(address, out var value) || value is not T result)
                throw new UnhandledGuestException("System.ArgumentException", "Invalid runtime metadata handle.");
            if (result is TypeLoader loader) loader.EnsureLive();
            else VmLifetime.EnsureLiveForGuest(result);
            return result;
        }
    }

    internal bool TryStorage(long address, out VmStructValue storage) {
        lock (_gate) return _storage.TryGetValue(address, out storage!);
    }

    internal bool TryArrayData(VmArray array, out VmNativePointer pointer) {
        lock (_gate) return _runtimeArrayData.TryGetValue(array, out pointer!);
    }

    internal StackSlot EmptyCastCache(IntrinsicContext ctx) {
        lock (_gate) {
        if (_castCache is not null) return StackSlot.OfObject(_castCache);
        // CoreCLR initializes CastHelpers.s_table before executing managed IL.
        // A two-bucket empty table returns MaybeCast, leaving correctness to QCall.
        // Header and entries occupy three 24-byte CastCacheEntry slots on 64-bit.
        var entry = CoreType(ctx, "System.Runtime.CompilerServices.CastCache+CastCacheEntry");
        var entrySize = MemoryOps.SizeOfRawType(entry);
        var count = checked(3 * entrySize / sizeof(int));
        using var allocation = ctx.Heap.ReserveArray(count);
        var values = new StackSlot[count];
        Array.Fill(values, StackSlot.OfInt32(0));
        values[0] = StackSlot.OfInt32(63); values[1] = StackSlot.OfInt32(1);
        var array = allocation.Commit(new VmArray(new VmArrayType { ElementType = CoreType(ctx, "System.Int32") }, values));
        using var memory = ctx.Heap.ReserveLocalloc(checked(IntPtr.Size + count * sizeof(int)));
        var bytes = new byte[checked(IntPtr.Size + count * sizeof(int))];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
        for (var i = 0; i < count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(IntPtr.Size + i * sizeof(int)), values[i].AsInt32);
        var block = memory.Commit(new VmLocallocMemory { Bytes = bytes });
        _runtimeArrayData[array] = new VmNativePointer { Memory = block, IsReadOnly = true };
        _castCache = array;
        return StackSlot.OfObject(array);
        }
    }

    private static VmClassType CoreType(IntrinsicContext ctx, string name) =>
        ctx.Types.TryResolveTrustedUnifiedType(name) as VmClassType
        ?? throw new UnhandledGuestException("System.TypeLoadException", name);

    internal void InitializeType(IntrinsicContext ctx, VmRuntimeObject type) {
        if (ctx.Types.TryResolveTrustedUnifiedType("System.RuntimeType") is not VmClassType { Loader.IsTrustedCoreLib: true } runtimeType)
            return;
        lock (_gate) {
            var backing = ctx.Heap.Allocate(new VmClassInstance(runtimeType,
                _objects.CreateInstanceStorage(runtimeType, runtimeType.Loader!)));
            type.ManagedInstance = backing;
            var address = Identity(ctx, type.Target);
            Set(backing, "m_handle", StackSlot.OfNativeInt(address));
            var methodTable = CoreType(ctx, "System.Runtime.CompilerServices.MethodTable");
            var auxiliary = CoreType(ctx, "System.Runtime.CompilerServices.MethodTableAuxiliaryData");
            ctx.Heap.ChargeHostBuffer(checked((int)ObjectModel.EstimateFieldStorageSize(
                _objects.GetLayout(methodTable).Count + _objects.GetLayout(auxiliary).Count)));
            var table = _objects.DefaultStruct(methodTable, methodTable.Loader!);
            var data = _objects.DefaultStruct(auxiliary, auxiliary.Loader!);
            _storage[address] = table;
            var dataAddress = Identity(ctx, data);
            _storage[dataAddress] = data;
            uint flags = type.Target.IsInterface ? 0xC0000u
                : type.Target.IsValueType ? 0x40000u : 0;
            if (VmPrimitiveTypes.IsSlotPrimitive(type.Target.FullName)) flags |= 0x20000;
            Set(table, "Flags", StackSlot.OfInt32(unchecked((int)flags)));
            Set(table, "AuxiliaryData", StackSlot.OfNativeInt(dataAddress));
            Set(data, "ExposedClassObjectRaw", StackSlot.OfObject(type));
            var parent = type.Target.BaseType;
            Set(table, "ParentMethodTable", parent is null ? StackSlot.OfNativeInt(0)
                : StackSlot.OfNativeInt(TypeAddress(ctx, parent)));
        }
    }

    internal long TypeAddress(IntrinsicContext ctx, VmType type) {
        DefaultIntrinsics.MakeRuntimeObject(ctx, type);
        return Identity(ctx, type);
    }

    internal VmClassInstance Module(IntrinsicContext ctx, TypeLoader loader) {
        lock (_gate) {
            loader.EnsureLive();
            if (_modules.TryGetValue(loader, out var existing)) return existing;
            var type = CoreType(ctx, "System.Reflection.RuntimeModule");
            var module = ctx.Heap.Allocate(new VmClassInstance(type, _objects.CreateInstanceStorage(type, type.Loader!)));
            Set(module, "m_pData", StackSlot.OfNativeInt(Identity(ctx, loader)));
            _modules.Add(loader, module);
            return module;
        }
    }

    internal StackSlot Get(VmClassInstance instance, string name) => instance.Fields[FieldIndex(instance.ClassType, name)];
    internal StackSlot Get(VmStructValue value, string name) => value.Fields[FieldIndex((VmClassType)value.StructType, name)];
    internal void Set(VmClassInstance instance, string name, StackSlot value) => instance.Fields[FieldIndex(instance.ClassType, name)] = value;
    private void Set(VmStructValue instance, string name, StackSlot value) => instance.Fields[FieldIndex((VmClassType)instance.StructType, name)] = value;

    private int FieldIndex(VmClassType type, string name) {
        var layout = _objects.GetLayout(type);
        var field = layout.Keys.Single(field => field.Name == name);
        return layout[field];
    }

    internal IEnumerable<VmObject> Roots() {
        lock (_gate) {
            var roots = _modules.Values.Cast<VmObject>().ToList();
            foreach (var pair in _runtimeArrayData) { roots.Add(pair.Key); roots.Add(pair.Value.Memory); }
            foreach (var value in _storage.Values)
                DotnetVM.Runtime.Heap.ObjectGraphWalker.CollectFromSlots(value.Fields, roots.Add);
            return roots;
        }
    }

    internal void RemoveContext(VmAssemblyContext context) {
        lock (_gate) {
            foreach (var entry in _identities.ToArray()) {
                var owned = entry.Key switch {
                    VmType type => context.OwnsType(type),
                    TypeLoader loader => ReferenceEquals(loader.Context, context),
                    VmStructValue value => value.Fields.Any(slot => slot.ObjectValue is VmRuntimeObject type && context.OwnsType(type.Target)),
                    _ => false,
                };
                if (!owned) continue;
                _identities.Remove(entry.Key); _handles.Remove(entry.Value); _storage.Remove(entry.Value);
            }
            foreach (var loader in _modules.Keys.Where(loader => ReferenceEquals(loader.Context, context)).ToArray())
                _modules.Remove(loader);
        }
    }

    internal void Clear() {
        lock (_gate) {
            _identities.Clear(); _handles.Clear(); _storage.Clear(); _modules.Clear();
            _runtimeArrayData.Clear(); _castCache = null;
        }
    }
}
