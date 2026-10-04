using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // Reflection is confined to these host-selected framework types, never to a
    // Type supplied by a guest or to guest assemblies loaded into the host CLR.
    internal static string BoundaryTypeName(Type t) => t.IsByRef ? BoundaryTypeName(t.GetElementType()!) + "&" :
        t.IsArray ? BoundaryTypeName(t.GetElementType()!) + "[]" :
        t.IsGenericParameter ? (t.DeclaringMethod is null ? "!" : "!!") + t.GenericParameterPosition :
        t.IsConstructedGenericType ? t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(BoundaryTypeName)) + ">" : t.FullName!;

    internal static IEnumerable<MethodBase> BoundaryMembers(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsGenericMethod && !m.ReturnType.IsByRefLike && !m.ReturnType.IsPointer && !m.ReturnType.IsByRef &&
                !(type == typeof(HttpClient) && m.Name is "get_DefaultProxy" or "set_DefaultProxy"))
            .Cast<MethodBase>().Concat(type == typeof(HttpClient) || type == typeof(HttpMessageInvoker) ? [] : type.GetConstructors())
            .Where(m => m.GetParameters().All(p => !p.ParameterType.IsByRefLike && !p.ParameterType.IsPointer));

    private static void RegisterHostBoundary(IntrinsicRegistry r, Type type, Func<MethodBase, bool>? include = null) {
        foreach (var member in BoundaryMembers(type).Where(m => include?.Invoke(m) ?? true)) {
            var parameters = member.GetParameters().Select(p => BoundaryTypeName(p.ParameterType)).ToArray();
            IntrinsicImpl call = (ctx, a) => {
                var actual = member;
                if (type.IsGenericTypeDefinition) {
                    var receiver = BclValue<object>(a[0]);
                    var closed = type.IsInterface ? receiver.GetType().GetInterfaces().Single(t => t.IsGenericType && t.GetGenericTypeDefinition() == type) : receiver.GetType();
                    actual = closed.GetMethods().Single(m => m.MetadataToken == member.MetadataToken);
                }
                var offset = actual.IsStatic ? 0 : 1;
                var parameterInfo = actual.GetParameters();
                var values = parameterInfo.Select((p, i) => p.IsOut ? null : BoundaryToHost(ctx, a[i + offset], p.ParameterType)).ToArray();
                void CopyBack() {
                    for (int i = 0; i < parameterInfo.Length; i++) {
                        if (parameterInfo[i].ParameterType.IsByRef && a[i + offset].ObjectValue is VmByRef reference)
                            reference.Write(BoundaryToVm(ctx, values[i], parameterInfo[i].ParameterType.GetElementType()!));
                        else if (values[i] is byte[] bytes && a[i + offset].ObjectValue is VmArray array)
                            for (int j = 0; j < bytes.Length; j++) array.Elements[j] = StackSlot.OfInt32(bytes[j]);
                        else if (values[i] is Memory<byte> memory) {
                            var destination = MemorySpan(ctx, a[i + offset], false);
                            WriteBoundaryBytes(destination, memory.Span);
                        }
                    }
                }
                StackSlot Result(object? result, Type returnType) {
                    CopyBack();
                    var converted = BoundaryToVm(ctx, result, returnType);
                    if (converted.ObjectValue is VmObject owner) KeepBoundaryRoots(owner, a);
                    return converted;
                }
                object? InvokeHost() {
                    try {
                        object? hostResult = null;
                        void Call() => hostResult = actual is ConstructorInfo constructor ? constructor.Invoke(values) :
                            ((MethodInfo)actual).Invoke(actual.IsStatic ? null : type == typeof(Stream) ? GuestStream(ctx, a[0]) : BclValue<object>(a[0]), values);
                        if (actual.Name == "Send" && ctx.SuspendExecution is { } suspend) suspend(Call); else Call();
                        return hostResult;
                    } catch (TargetInvocationException ex) when (ex.InnerException is not null) {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw;
                    }
                }
                StackSlot Invoke() {
                    object? result;
                    try {
                        result = InvokeHost();
                        if (result is Task task) {
                            task.GetAwaiter().GetResult();
                            result = actual is MethodInfo { ReturnType.IsGenericType: true } info ? info.ReturnType.GetProperty("Result")!.GetValue(task) : null;
                        }
                    } catch (TargetInvocationException ex) when (ex.InnerException is not null) {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw;
                    }
                    CopyBack();
                    if (!actual.IsStatic && a[0].ObjectValue is VmObject owner) {
                        KeepBoundaryRoots(owner, a.Skip(1).ToArray());
                    }
                    if (actual is ConstructorInfo) { SetBclValue(a[0], result!); return default; }
                    var returnType = ((MethodInfo)actual).ReturnType;
                    if (typeof(Task).IsAssignableFrom(returnType)) returnType = returnType.IsGenericType ? returnType.GetGenericArguments()[0] : typeof(void);
                    return Result(result, returnType);
                }
                if (actual is MethodInfo method && (typeof(Task).IsAssignableFrom(method.ReturnType) || method.ReturnType == typeof(ValueTask) || method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))) {
                    var valueTask = method.ReturnType == typeof(ValueTask) || method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
                    var resultType = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : typeof(void);
                    var definition = FindAnyType(ctx, resultType == typeof(void) ? "System.Threading.Tasks.Task" : "System.Threading.Tasks.Task`1")!;
                    VmType vmTaskType = resultType == typeof(void) ? definition : new VmConstructedType { Definition = definition, TypeArguments = [FindAnyType(ctx, BoundaryTypeName(resultType))!] };
                    var guestTask = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(vmTaskType));
                    ctx.Shared.GuestTasks.KeepTaskRoots(guestTask, a);
                    void Fail(Exception ex) {
                        if (ex is OperationCanceledException && !ctx.Shared.ShutdownToken.IsCancellationRequested) { guestTask.SetCanceled(); ctx.Shared.GuestTasks.Complete(guestTask); }
                        else {
                            try { BclCall(() => throw ex); }
                            catch (Exception normalized) { ctx.Shared.GuestTasks.CompleteHostException(guestTask, normalized); }
                        }
                    }
                    try {
                        var invoked = InvokeHost()!;
                        var hostTask = valueTask ? (Task)method.ReturnType.GetMethod("AsTask")!.Invoke(invoked, null)! : (Task)invoked;
                        void Finish() {
                            try {
                                var normalized = BclCall(() => {
                                    hostTask.GetAwaiter().GetResult();
                                    return Result(resultType == typeof(void) ? null : hostTask.GetType().GetProperty("Result")!.GetValue(hostTask), resultType);
                                }) ?? default;
                                ctx.Shared.GuestTasks.Complete(guestTask, normalized);
                            } catch (Exception ex) { Fail(ex); }
                        }
                        if (hostTask.IsCompleted) Finish(); else hostTask.GetAwaiter().OnCompleted(Finish);
                    } catch (Exception ex) {
                        Fail(ex);
                        return BclCall(() => throw ex);
                    }
                    if (!valueTask) return StackSlot.OfObject(guestTask);
                    var valueType = FindAnyType(ctx, resultType == typeof(void) ? "System.Threading.Tasks.ValueTask" : BoundaryTypeName(method.ReturnType))!;
                    return StackSlot.OfValueType(new VmStructValue(valueType, [StackSlot.OfObject(guestTask)], resultType == typeof(void) ? [] : [FindAnyType(ctx, BoundaryTypeName(resultType))!]));
                }
                var synchronousResult = Invoke();
                return actual is ConstructorInfo || actual is MethodInfo { ReturnType: var returnType } && returnType == typeof(void) ? null : synchronousResult;
            };
            if (member is ConstructorInfo) BclConstructor(r, type.FullName!, parameters, call);
            else BclFace(r, type.FullName!, member.Name, !member.IsStatic, parameters, call);
        }
    }

    private static void KeepBoundaryRoots(VmObject owner, StackSlot[] values) {
        var roots = new List<VmObject>(owner.BclReferences);
        ObjectGraphWalker.CollectFromSlots(values, roots.Add);
        owner.BclReferences = roots.Where(root => !ReferenceEquals(root, owner)).Distinct().ToArray();
    }

    private static void WriteBoundaryBytes(in StackSlot destination, ReadOnlySpan<byte> bytes) {
        var (reference, length) = ReadSpanParts(destination);
        if (bytes.Length > length) throw new ArgumentException("Destination is too short.");
        var (pointer, location) = ResolvePointerBase(reference, "framework output");
        if (pointer is not null) { pointer.EnsureWritable(); bytes.CopyTo(pointer.Bytes.AsSpan(pointer.ByteOffset, bytes.Length)); }
        else { location!.EnsureWritable(); for (int i = 0; i < bytes.Length; i++) location.Container[location.Index + i] = StackSlot.OfInt32(bytes[i]); }
    }

    private static object? BoundaryToHost(IntrinsicContext ctx, StackSlot value, Type type) {
        if (type.IsByRef) type = type.GetElementType()!;
        if (value.ObjectValue is VmByRef reference) value = reference.Read();
        if (type == typeof(object)) return value.ObjectValue is null ? null : new BoundaryGuestValue(SlotOps.PushCopyOfValue(value));
        if (value.ObjectValue is VmBoxedValue boxed) value = boxed.Type.IsValueType && boxed.Fields.Length == 1 ? boxed.Fields[0] : StackSlot.OfValueType(new VmStructValue(boxed.Type, boxed.Fields));
        if (value.ObjectValue is VmObject obj && BclStates.TryGetValue(obj, out var state)) { RememberBclWrapper(ctx, state.Value, obj); return state.Value; }
        if (type == typeof(string)) { var text = StringValue(value); if (text is not null) { ctx.Heap.ChargeHostWork(text.Length); ctx.Heap.ChargeHostBuffer(checked(text.Length * 2)); } return text; }
        if (type == typeof(CancellationToken)) return CancellationRuntime.State(value)?.Token ?? default(CancellationToken);
        if (type == typeof(Stream)) return value.ObjectValue is null ? null : GuestStream(ctx, value);
        if (type == typeof(Memory<byte>) || type == typeof(ReadOnlyMemory<byte>)) {
            var bytes = EncodingBytes(ctx, MemorySpan(ctx, value, type == typeof(ReadOnlyMemory<byte>)), "span");
            return type == typeof(Memory<byte>) ? (object)new Memory<byte>(bytes) : new ReadOnlyMemory<byte>(bytes);
        }
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) {
            if (value.ObjectValue is not VmStructValue n || n.Fields[0].AsInt32 == 0) return null;
            return BoundaryToHost(ctx, n.Fields[1], nullable);
        }
        if (type.IsEnum) return Enum.ToObject(type, value.Int64Value);
        if (type == typeof(bool)) return value.AsInt32 != 0;
        if (type == typeof(char)) return (char)value.AsInt32;
        if (type == typeof(byte)) return (byte)value.AsInt32;
        if (type == typeof(sbyte)) return (sbyte)value.AsInt32;
        if (type == typeof(short)) return (short)value.AsInt32;
        if (type == typeof(ushort)) return (ushort)value.AsInt32;
        if (type == typeof(int)) return value.AsInt32;
        if (type == typeof(uint)) return (uint)value.AsInt32;
        if (type == typeof(long)) return value.Int64Value;
        if (type == typeof(ulong)) return (ulong)value.Int64Value;
        if (type == typeof(float)) return (float)value.DoubleValue;
        if (type == typeof(double)) return value.DoubleValue;
        if (type.IsValueType && value.ObjectValue is VmStructValue structure) {
            var result = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            var vmFields = structure.StructType.Fields.Where(f => !f.IsStatic && !f.IsLiteral).ToArray();
            for (int i = 0; i < vmFields.Length; i++) {
                var field = type.GetField(vmFields[i].Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field is not null) field.SetValue(result, BoundaryToHost(ctx, structure.Fields[i], field.FieldType));
            }
            // The no-CoreLib TimeSpan facade carries a single tick field.
            if (type == typeof(TimeSpan) && vmFields.Length == 0) return new TimeSpan(structure.Fields[0].Int64Value);
            return result;
        }
        if (value.ObjectValue is null) return null;
        var element = type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type.GetGenericArguments()[0] : null;
        if (element is not null) {
            var items = new List<object?>();
            if (value.ObjectValue is VmArray array) {
                ctx.Heap.ChargeHostBuffer(checked(array.Length * 16));
                foreach (var slot in array.Elements) items.Add(BoundaryToHost(ctx, slot, element));
            } else {
                var enumerator = ctx.InvokeGuestInstanceMethod!(value, "GetEnumerator", [])!.Value;
                try {
                    while (ctx.InvokeGuestInstanceMethod!(enumerator, "MoveNext", [])!.Value.AsInt32 != 0) {
                        ctx.Heap.ChargeHostWork(1); ctx.Heap.ChargeHostBuffer(16);
                        items.Add(BoundaryToHost(ctx, ctx.InvokeGuestInstanceMethod!(enumerator, "get_Current", [])!.Value, element));
                    }
                } finally { ctx.InvokeGuestInstanceMethod!(enumerator, "Dispose", []); }
            }
            var result = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++) result.SetValue(items[i], i);
            return result;
        }
        throw new UnhandledGuestException("System.NotSupportedException", "Unsupported framework boundary argument: " + type.FullName);
    }

    private static StackSlot BoundaryToVm(IntrinsicContext ctx, object? value, Type type) {
        if (type == typeof(void)) return default;
        if (value is BoundaryGuestValue guest) return SlotOps.PushCopyOfValue(guest.Value);
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) {
            var n = FindAnyType(ctx, BoundaryTypeName(type))!;
            return StackSlot.OfValueType(new VmStructValue(n, [StackSlot.OfInt32(value is null ? 0 : 1), value is null ? new ObjectModel().DefaultForType(FindAnyType(ctx, BoundaryTypeName(nullable)), ctx.Types) : BoundaryToVm(ctx, value, nullable)], [FindAnyType(ctx, BoundaryTypeName(nullable))!]));
        }
        if (value is null) return StackSlot.Null;
        if (type == typeof(object)) type = value.GetType();
        if (type.IsEnum) return StackSlot.OfInt32(Convert.ToInt32(value));
        if (value is string text) return StackSlot.OfObject(ctx.MakeString(text));
        if (value is byte[] bytes) return StackSlot.OfObject(ctx.MakeByteArray(bytes));
        if (value is bool boolean) return StackSlot.OfInt32(boolean ? 1 : 0);
        if (value is char ch) return StackSlot.OfInt32(ch);
        if (value is byte or sbyte or short or ushort or int) return StackSlot.OfInt32(Convert.ToInt32(value));
        if (value is uint u) return StackSlot.OfInt32(unchecked((int)u));
        if (value is long l) return StackSlot.OfInt64(l);
        if (value is ulong ul) return StackSlot.OfInt64(unchecked((long)ul));
        if (value is float or double) return StackSlot.OfFloat(Convert.ToDouble(value));
        if (type.IsValueType) {
            var vmType = FindAnyType(ctx, BoundaryTypeName(type)) ?? throw new InvalidOperationException("Framework value type is unavailable: " + type);
            var fields = vmType.Fields.Where(f => !f.IsStatic && !f.IsLiteral).Select(f => {
                var hostField = type.GetField(f.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                return BoundaryToVm(ctx, hostField.GetValue(value), hostField.FieldType);
            }).ToArray();
            if (value is TimeSpan time && fields.Length == 0) fields = [StackSlot.OfInt64(time.Ticks)];
            return StackSlot.OfValueType(new VmStructValue(vmType, fields));
        }
        if (value is IEnumerable sequence && (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))) {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var slots = new List<StackSlot>();
            foreach (var item in sequence) { ctx.Heap.ChargeHostWork(1); ctx.Heap.ChargeHostBuffer(32); slots.Add(BoundaryToVm(ctx, item, element)); }
            using var allocation = ctx.Heap.ReserveArray(slots.Count);
            return StackSlot.OfObject(allocation.Commit(new VmArray(new VmArrayType { ElementType = FindAnyType(ctx, BoundaryTypeName(element))! }, slots.ToArray())));
        }
        return WrapBcl(ctx, BoundaryTypeName(type), value);
    }
    private sealed record BoundaryGuestValue(StackSlot Value);
}
