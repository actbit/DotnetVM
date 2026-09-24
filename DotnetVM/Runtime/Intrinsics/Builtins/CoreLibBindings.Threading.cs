using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterTimeSpanConstruction(IntrinsicRegistry r) {
        const string timeSpan = "System.TimeSpan";
        static StackSlot FromMilliseconds(IntrinsicContext ctx, double milliseconds) {
            var type = ctx.Types.FindIntrinsicType(timeSpan)
                ?? throw new InvalidOperationException("TimeSpan facade が見つかりません。");
            return StackSlot.OfValueType(new VmStructValue(type,
                [StackSlot.OfInt64(TimeSpan.FromMilliseconds(milliseconds).Ticks)]));
        }
        r.RegisterBinding(BindingKey.StaticWithReturn(timeSpan, "FromMilliseconds", timeSpan,
                ["System.Int64"]),
            (ctx, a) => FromMilliseconds(ctx, a[0].Int64Value), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(timeSpan, "FromMilliseconds", "System.Int64"),
            (ctx, a) => FromMilliseconds(ctx, a[0].Int64Value), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(timeSpan, "FromMilliseconds", timeSpan,
                ["System.Double"]),
            (ctx, a) => FromMilliseconds(ctx, a[0].DoubleValue), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(timeSpan, "FromMilliseconds", "System.Double"),
            (ctx, a) => FromMilliseconds(ctx, a[0].DoubleValue), BindingOrigin.Managed);
    }

    // ---- System.Threading.Thread / Monitor ----

    private static void RegisterThreading(IntrinsicRegistry r) {
        RegisterTimeSpanConstruction(r);
        // Thread は VM delegate をホスト worker thread 上で実行する。ゲスト IL への再入は
        // Interpreter のスレッド別フレームと共有 quota / heap を通る。
        const string thread = "System.Threading.Thread";
        static VmObject Receiver(StackSlot[] a) => a[0].ObjectValue as VmObject
            ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        static VmDelegate StartDelegate(StackSlot[] a, int index) => a[index].ObjectValue as VmDelegate
            ?? throw new UnhandledGuestException("System.ArgumentNullException", "start");
        static bool Start(IntrinsicContext ctx, StackSlot[] a, bool hasState) {
            var run = ctx.RunGuestThreadDelegate ?? throw new InvalidOperationException("Guest Thread runner が初期化されていません。");
            ctx.Shared.GuestThreads.Start(Receiver(a), hasState ? a[1] : default, run, hasState);
            return true;
        }
        static bool Join(IntrinsicContext ctx, StackSlot[] a, int timeout) {
            var completed = false;
            SuspendHostWait(ctx, () => completed = ctx.Shared.GuestThreads.Join(Receiver(a), timeout));
            return completed;
        }
        r.RegisterBinding(BindingKey.Instance(thread, ".ctor", "System.Threading.ThreadStart"),
            static (ctx, a) => {
                ctx.Shared.GuestThreads.Configure(Receiver(a), StartDelegate(a, 1), parameterized: false);
                return null;
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, ".ctor", "System.Threading.ParameterizedThreadStart"),
            static (ctx, a) => {
                ctx.Shared.GuestThreads.Configure(Receiver(a), StartDelegate(a, 1), parameterized: true);
                return null;
            }, BindingOrigin.InternalCall);
        r.Register(new IntrinsicKey(thread, ".ctor", 2, true), static (ctx, a) => {
            var start = StartDelegate(a, 1);
            ctx.Shared.GuestThreads.Configure(Receiver(a), start,
                parameterized: start.DeclaredType.FullName == "System.Threading.ParameterizedThreadStart");
            return null;
        });
        r.RegisterBinding(BindingKey.Instance(thread, "Start"),
            static (ctx, a) => { Start(ctx, a, hasState: false); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Start", 0), static (ctx, a) => { Start(ctx, a, hasState: false); return null; });
        r.RegisterBinding(BindingKey.Instance(thread, "Start", "System.Object"),
            static (ctx, a) => { Start(ctx, a, hasState: true); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Start", 1), static (ctx, a) => { Start(ctx, a, hasState: true); return null; });
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", []),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, Timeout.Infinite) ? 1 : 0), BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Join", 0), static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, Timeout.Infinite) ? 1 : 0));
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", ["System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")) ? 1 : 0), BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Instance(thread, "Join", 1), static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")) ? 1 : 0));
        r.RegisterBinding(BindingKey.InstanceWithReturn(thread, "Join", "System.Boolean", ["System.TimeSpan"]),
            static (ctx, a) => StackSlot.OfInt32(Join(ctx, a, ValidateTimeout(TimeSpanMilliseconds(a[1]), "timeout")) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, "get_IsAlive"),
            static (ctx, a) => StackSlot.OfInt32(ctx.Shared.GuestThreads.IsAlive(Receiver(a)) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(thread, "get_ManagedThreadId"),
            static (ctx, a) => StackSlot.OfInt32(ctx.Shared.GuestThreads.ManagedThreadId(Receiver(a))), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.Threading.Thread", "Sleep", "System.Int32"),
            static (ctx, a) => { var timeout = ValidateTimeout(a[0].AsInt32, "millisecondsTimeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; }, BindingOrigin.InternalCall);
        r.Register(IntrinsicKey.Static("System.Threading.Thread", "Sleep", 1), static (ctx, a) => { var timeout = ValidateTimeout(a[0].AsInt32, "millisecondsTimeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; });
        r.RegisterBinding(BindingKey.Static("System.Threading.Thread", "Sleep", "System.TimeSpan"),
            static (ctx, a) => { var timeout = ValidateTimeout(TimeSpanMilliseconds(a[0]), "timeout"); SuspendHostWait(ctx, () => Thread.Sleep(timeout)); return null; }, BindingOrigin.InternalCall);

        // Monitor はゲストオブジェクト identity ごとの CLR Monitor を同期ブロックとして持つ。
        // Enter の lockTaken overload も直接受け、CoreLib fast path / slow path に依存しない。
        const string T = "System.Threading.Monitor";
        r.RegisterBinding(BindingKey.Static(T, "TryEnter_FastPath", "System.Object"),
            static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return StackSlot.OfInt32(1); }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Static(T, "TryEnter_FastPath_WithTimeout", "System.Object", "System.Int32"),
                static (ctx, a) => SuspendHostWait(ctx, () => Monitor.TryEnter(
                    ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")))
                ? StackSlot.OfInt32(1) : StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Exit_FastPath", "System.Object"),
            static (ctx, a) => { ExitMonitor(ctx, a[0]); return StackSlot.OfInt32(0); }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "IsEnteredNative", "System.Object"),
            static (ctx, a) => StackSlot.OfInt32(Monitor.IsEntered(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);

        r.RegisterBinding(BindingKey.Static(T, "Enter", "System.Object"),
            static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Enter", "System.Object", "System.Boolean&"),
            static (ctx, a) => {
                SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)));
                WriteByRef(a[1], StackSlot.OfInt32(1));
                return null;
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Exit", "System.Object"),
            static (ctx, a) => { ExitMonitor(ctx, a[0]); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx,
                () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Boolean&"]),
            static (ctx, a) => {
                var entered = Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue));
                WriteByRef(a[1], StackSlot.OfInt32(entered ? 1 : 0));
                return StackSlot.OfInt32(entered ? 1 : 0);
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "TryEnter", "System.Boolean", ["System.Object", "System.Int32", "System.Boolean&"]),
            static (ctx, a) => {
                var entered = SuspendHostWait(ctx,
                    () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")));
                WriteByRef(a[2], StackSlot.OfInt32(entered ? 1 : 0));
                return StackSlot.OfInt32(entered ? 1 : 0);
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Wait", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => Monitor.Wait(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Wait", "System.Boolean", ["System.Object", "System.Int32"]),
            static (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => Monitor.Wait(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "Pulse", "System.Object"),
            static (ctx, a) => { Monitor.Pulse(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "PulseAll", "System.Object"),
            static (ctx, a) => { Monitor.PulseAll(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "IsEntered", "System.Boolean", ["System.Object"]),
            static (ctx, a) => StackSlot.OfInt32(Monitor.IsEntered(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0), BindingOrigin.InternalCall);

        // Fallback facade 経路 (LoadHostCoreLib=false) の legacy key 群。CoreLib ロード時は
        // 上の署名バインドが優先される。
        r.Register(IntrinsicKey.Static(T, "Enter", 1), static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); return null; });
        r.Register(IntrinsicKey.Static(T, "Enter", 2), static (ctx, a) => { SuspendHostWait(ctx, () => Monitor.Enter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue))); WriteByRef(a[1], StackSlot.OfInt32(1)); return null; });
        r.Register(IntrinsicKey.Static(T, "Exit", 1), static (ctx, a) => { ExitMonitor(ctx, a[0]); return null; });
        r.Register(IntrinsicKey.Static(T, "TryEnter", 1), static (ctx, a) => StackSlot.OfInt32(Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)) ? 1 : 0));
        r.Register(IntrinsicKey.Static(T, "TryEnter", 2), static (ctx, a) => {
            var sync = ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue);
            var taken = a[1].Kind == StackKind.ByRef
                ? Monitor.TryEnter(sync)
                : SuspendHostWait(ctx, () => Monitor.TryEnter(sync, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout")));
            if (a[1].Kind == StackKind.ByRef)
                WriteByRef(a[1], StackSlot.OfInt32(taken ? 1 : 0));
            return StackSlot.OfInt32(taken ? 1 : 0);
        });
        r.Register(IntrinsicKey.Static(T, "TryEnter", 3), static (ctx, a) => { var taken = SuspendHostWait(ctx, () => Monitor.TryEnter(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue), ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"))); WriteByRef(a[2], StackSlot.OfInt32(taken ? 1 : 0)); return StackSlot.OfInt32(taken ? 1 : 0); });
        r.Register(IntrinsicKey.Static(T, "Pulse", 1), static (ctx, a) => { Monitor.Pulse(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; });
        r.Register(IntrinsicKey.Static(T, "PulseAll", 1), static (ctx, a) => { Monitor.PulseAll(ctx.Shared.Monitors.SyncRoot(a[0].ObjectValue)); return null; });
    }


    // ---- System.Threading.Tasks.Task / async state machines ----

    private static void RegisterTaskBindings(IntrinsicRegistry r) {
        const string task = "System.Threading.Tasks.Task";
        const string taskOfT = "System.Threading.Tasks.Task`1";
        const string valueTask = "System.Threading.Tasks.ValueTask";
        const string valueTaskOfT = "System.Threading.Tasks.ValueTask`1";
        const string awaiter = "System.Runtime.CompilerServices.TaskAwaiter";
        const string awaiterOfT = "System.Runtime.CompilerServices.TaskAwaiter`1";
        const string valueTaskAwaiter = "System.Runtime.CompilerServices.ValueTaskAwaiter";
        const string valueTaskAwaiterOfT = "System.Runtime.CompilerServices.ValueTaskAwaiter`1";
        const string builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder";
        const string builderOfT = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1";
        const string valueTaskBuilder = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder";
        const string valueTaskBuilderOfT = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1";
        const string configuredTask = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable";
        const string configuredTaskOfT = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1";
        const string configuredValueTask = "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable";
        const string configuredValueTaskOfT = "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1";

        static StackSlot ReadValue(in StackSlot slot) =>
            slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef ? byRef.Read() : slot;
        static bool IsZeroInitialized(StackSlot slot) {
            if (slot.Kind == StackKind.ValueType)
                return slot.ObjectValue is null || slot.ObjectValue is VmStructValue structure &&
                    structure.Fields.All(IsZeroInitialized);
            return slot.Kind switch {
                StackKind.Empty => true,
                StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt or StackKind.IntPtr => slot.Int64Value == 0,
                StackKind.Float => slot.DoubleValue == 0,
                StackKind.Object => slot.ObjectValue is null,
                _ => false,
            };
        }
        static bool IsDefaultValueTask(in StackSlot slot) {
            var value = ReadValue(slot);
            return value.ObjectValue is VmStructValue wrapper &&
                wrapper.Fields.All(IsZeroInitialized);
        }
        static VmType? ValueTaskResultType(IntrinsicContext ctx, in StackSlot slot) {
            var value = ReadValue(slot);
            if (value.ObjectValue is VmStructValue wrapper)
                return wrapper.TypeArguments.FirstOrDefault()
                    ?? (wrapper.StructType as VmConstructedType)?.TypeArguments.FirstOrDefault()
                    ?? ctx.ClassTypeArguments.FirstOrDefault();
            return ctx.ClassTypeArguments.FirstOrDefault();
        }
        static StackSlot DefaultValueTaskResult(IntrinsicContext ctx, bool generic, VmType? resultType) {
            if (!generic)
                return default;
            return new ObjectModel().DefaultForType(
                resultType ?? ctx.Types.FindIntrinsicType("System.Object"), ctx.Types);
        }
        static VmTaskObject AsTask(IntrinsicContext ctx, in StackSlot slot,
            bool generic = false, bool valueTask = false) {
            var value = ReadValue(slot);
            if (value.ObjectValue is VmTaskObject task)
                return task;
            if (value.ObjectValue is VmStructValue wrapper) {
                foreach (var field in wrapper.Fields)
                    if (field.ObjectValue is VmTaskObject wrappedTask)
                        return wrappedTask;

                if (valueTask && ValueTaskRuntime.TryGetSourceValue(value, out var source, out var token)) {
                    var resultType = generic ? ValueTaskResultType(ctx, value) : null;
                    return ValueTaskRuntime.AsTask(ctx, source, generic, resultType, token);
                }

                // default(ValueTask) / default(ValueTask<T>) has no backing Task. CLR treats
                // that zero-initialized value as completed successfully. Only AsTask needs a
                // Task-shaped representation; direct completion/result queries stay allocation-free.
                if (valueTask && IsDefaultValueTask(value)) {
                    var resultType = generic ? ValueTaskResultType(ctx, value) : null;
                    var result = DefaultValueTaskResult(ctx, generic, resultType);
                    return CompletedTask(ctx, generic, resultType, result);
                }
            }
            throw new UnhandledGuestException("System.InvalidOperationException", "Task / ValueTask の VM 実体がありません。");
        }
        static VmType FindType(IntrinsicContext ctx, string name) =>
            (ctx.Types.IsTrustedCoreLib
                ? (VmType?)ctx.Types.FindTypeByFullName(name) ?? ctx.Types.FindIntrinsicType(name)
                : (VmType?)ctx.Types.FindIntrinsicType(name) ?? ctx.Types.FindTypeByFullName(name))
            ?? throw new InvalidOperationException($"{name} type が見つかりません。");
        static VmType TaskType(IntrinsicContext ctx, bool generic, VmType? resultType) {
            var taskDefinition = FindType(ctx, generic ? taskOfT : task);
            return generic
                ? new VmConstructedType {
                    Definition = taskDefinition,
                    TypeArguments = [resultType ?? ctx.ClassTypeArguments.FirstOrDefault()
                        ?? ctx.Types.FindIntrinsicType("System.Object")!],
                }
                : taskDefinition;
        }
        static VmTaskObject NewTask(IntrinsicContext ctx, bool generic, VmType? resultType = null, bool completed = false,
            StackSlot result = default) {
            var taskType = TaskType(ctx, generic, resultType);
            return ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(taskType, completed, result));
        }
        static VmTaskObject CompletedTask(IntrinsicContext ctx, bool generic, VmType? resultType, StackSlot result) =>
            ctx.Shared.GuestTasks.CompletedSentinel(TaskType(ctx, generic, resultType), result, ctx.Heap);
        static VmType AwaiterType(IntrinsicContext ctx, bool generic, VmType? resultType, bool valueTask) {
            var definition = FindType(ctx, valueTask
                ? (generic ? valueTaskAwaiterOfT : valueTaskAwaiter)
                : (generic ? awaiterOfT : awaiter));
            return generic
                ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                : definition;
        }
        static bool IsValueTaskAwaiterType(VmType type) =>
            type.FullName.Contains("ValueTaskAwaiter", StringComparison.Ordinal);
        static VmType? AwaiterResultType(IntrinsicContext ctx, in StackSlot slot) {
            var value = ReadValue(slot);
            return value.ObjectValue is VmStructValue awaiter
                ? awaiter.TypeArguments.FirstOrDefault()
                    ?? (awaiter.StructType as VmConstructedType)?.TypeArguments.FirstOrDefault()
                    ?? ctx.ClassTypeArguments.FirstOrDefault()
                : ctx.ClassTypeArguments.FirstOrDefault();
        }
        static bool IsCompletedAwaiter(in StackSlot slot) {
            var awaiterValue = ReadValue(slot);
            return awaiterValue.Kind == StackKind.ValueType &&
                awaiterValue.ObjectValue is VmStructValue value &&
                value.Fields.Length > 0 && IsValueTaskAwaiterType(value.StructType) &&
                IsZeroInitialized(value.Fields[0]);
        }
        static bool CapturesSynchronizationContext(in StackSlot slot) {
            var value = ReadValue(slot);
            if (value.ObjectValue is VmStructValue awaiter &&
                awaiter.StructType.FullName.Contains("Configured", StringComparison.Ordinal) &&
                awaiter.Fields.Length > 1)
                return awaiter.Fields[^1].AsInt32 != 0;
            return true;
        }
        static VmTaskObject AwaitedTask(IntrinsicContext ctx, in StackSlot slot) {
            var awaiterValue = slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
                ? byRef.Read() : slot;
            if (awaiterValue.Kind != StackKind.ValueType || awaiterValue.ObjectValue is not VmStructValue value || value.Fields.Length == 0)
                throw new UnhandledGuestException("System.InvalidOperationException", "Task awaiter が初期化されていません。");
            if (value.Fields[0].ObjectValue is VmTaskObject taskObject)
                return taskObject;
            if (ValueTaskRuntime.TryGetSourceAwaiter(awaiterValue, out var source, out var token)) {
                var generic = value.TypeArguments.Length > 0 ||
                    (value.StructType as VmConstructedType)?.TypeArguments.Length > 0;
                var resultType = generic ? AwaiterResultType(ctx, awaiterValue) : null;
                return ValueTaskRuntime.AsTask(ctx, source, generic, resultType, token);
            }
            if (IsCompletedAwaiter(awaiterValue)) {
                var generic = value.TypeArguments.Length > 0 ||
                    (value.StructType as VmConstructedType)?.TypeArguments.Length > 0;
                var resultType = generic ? AwaiterResultType(ctx, awaiterValue) : null;
                return CompletedTask(ctx, generic, resultType,
                    DefaultValueTaskResult(ctx, generic, resultType));
            }
            throw new UnhandledGuestException("System.InvalidOperationException", "Task awaiter が初期化されていません。");
        }
        static bool TryAwaitedTask(IntrinsicContext ctx, in StackSlot slot, out VmTaskObject task) {
            try {
                task = AwaitedTask(ctx, slot);
                return true;
            } catch (UnhandledGuestException) {
                task = null!;
                return false;
            }
        }
        static VmStructValue BuilderValue(in StackSlot slot) {
            var value = slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
                ? byRef.Read() : slot;
            return value.Kind == StackKind.ValueType && value.ObjectValue is VmStructValue builderValue
                ? builderValue : throw new UnhandledGuestException("System.InvalidOperationException", "Async builder が初期化されていません。");
        }
        static VmTaskObject EnsureBuilderTask(IntrinsicContext ctx, StackSlot[] args) {
            var builderValue = BuilderValue(args[0]);
            if (builderValue.Fields.Length > 0 && builderValue.Fields[0].ObjectValue is VmTaskObject taskObject)
                return taskObject;
            var generic = builderValue.StructType is VmConstructedType { Definition.FullName: builderOfT or valueTaskBuilderOfT } ||
                builderValue.StructType.FullName is builderOfT or valueTaskBuilderOfT;
            var resultType = builderValue.TypeArguments.FirstOrDefault() ?? ctx.ClassTypeArguments.FirstOrDefault();
            var newTask = NewTask(ctx, generic, resultType);
            if (builderValue.Fields.Length == 0) {
                var replacement = new VmStructValue(builderValue.StructType, [StackSlot.OfObject(newTask)], builderValue.TypeArguments);
                if (args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef emptyDestination)
                    emptyDestination.Write(StackSlot.OfValueType(replacement));
                return newTask;
            } else {
                builderValue.Fields[0] = StackSlot.OfObject(newTask);
            }
            if (args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef destination)
                destination.Write(StackSlot.OfValueType(builderValue));
            return newTask;
        }
        static StackSlot TaskResult(IntrinsicContext ctx, VmTaskObject taskObject) {
            if (!taskObject.IsCompleted)
                SuspendHostWait(ctx, taskObject.Wait);
            var result = taskObject.Snapshot();
            if (taskObject.IsCanceled)
                throw new UnhandledGuestException("System.OperationCanceledException", null);
            if (result.GuestException.Kind != StackKind.Empty) {
                var exceptionObject = result.GuestException.ObjectValue;
                var typeName = exceptionObject switch {
                    VmExceptionObject guestException => guestException.ExceptionType.FullName,
                    VmClassInstance guestException => guestException.ClassType.FullName,
                    VmIntrinsicInstance guestException => guestException.InstanceType.FullName,
                    _ => "System.Exception",
                };
                throw new UnhandledGuestException(typeName, null);
            }
            if (result.HostException is not null)
                throw result.HostException;
            return result.Result;
        }
        static CancellationToken CancellationTokenOf(in StackSlot slot) =>
            CancellationRuntime.State(slot)?.Token ?? default;
        static VmTaskObject[] TaskArray(IntrinsicContext ctx, in StackSlot slot) {
            var value = ReadValue(slot);
            if (value.ObjectValue is not VmArray array)
                throw new UnhandledGuestException("System.ArgumentException", "Task 配列が必要です。");
            // guest VmArray のサイズだけでは、ここから作る host task 配列・roots・registration
            // の fan-out を制限できない。host 側配列を確保する前に VM-wide quota を検査する。
            ctx.Shared.GuestTasks.ValidateTaskCombinatorInputCount(array.Length);
            var tasks = new VmTaskObject[array.Length];
            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = array.Elements[i].ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "Task 配列に null / 非 Task 要素があります。");
            return tasks;
        }
        static VmType TaskArrayResultType(IntrinsicContext ctx, in StackSlot slot) {
            var value = ReadValue(slot);
            var element = value.ObjectValue is VmArray array ? array.ArrayType.ElementType : null;
            return element is VmConstructedType { Definition.FullName: "System.Threading.Tasks.Task`1" } task
                ? task.TypeArguments[0]
                : ctx.Types.FindIntrinsicType("System.Object")!;
        }
        static string ReadOnlySpanType(string elementType) => $"System.ReadOnlySpan`1<{elementType}>";
        static (StackSlot Reference, int Length, VmType? ElementType) ReadOnlySpanParts(IntrinsicContext ctx, in StackSlot slot) {
            if (slot.ObjectValue is not VmStructValue span)
                throw new UnhandledGuestException("System.ArgumentException", "ReadOnlySpan が必要です。");
            if (span.StructType is VmConstructedType constructed && constructed.TypeArguments.Length == 1)
                return (span.Fields.Length > 0 ? span.Fields[0] : default,
                    span.Fields.Length > 1 ? span.Fields[1].AsInt32 : 0, constructed.TypeArguments[0]);
            return (span.Fields.Length > 0 ? span.Fields[0] : default,
                span.Fields.Length > 1 ? span.Fields[1].AsInt32 : 0,
                span.TypeArguments.FirstOrDefault());
        }
        static VmTaskObject[] TaskSpan(IntrinsicContext ctx, in StackSlot slot) {
            var (reference, length, _) = ReadOnlySpanParts(ctx, slot);
            if (reference.ObjectValue is not VmByRef byRef || length < 0 || byRef.Index < 0 ||
                byRef.Index > byRef.Container.Length || length > byRef.Container.Length - byRef.Index)
                throw new UnhandledGuestException("System.ArgumentException", "ReadOnlySpan の範囲が不正です。");
            ctx.Shared.GuestTasks.ValidateTaskCombinatorInputCount(length);
            var tasks = new VmTaskObject[length];
            for (var i = 0; i < length; i++)
                tasks[i] = byRef.Container[byRef.Index + i].ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "ReadOnlySpan に null / 非 Task 要素があります。");
            return tasks;
        }
        static VmType TaskSpanResultType(IntrinsicContext ctx, in StackSlot slot) {
            var element = ReadOnlySpanParts(ctx, slot).ElementType;
            return element is VmConstructedType { Definition.FullName: "System.Threading.Tasks.Task`1" } task
                ? task.TypeArguments[0]
                : ctx.Types.FindIntrinsicType("System.Object")!;
        }
        static void ThrowTaskFailures(IEnumerable<VmTaskObject> tasks) {
            // Task.WaitAll always reports child failures as AggregateException.  Keep this as a
            // guest exception even when a child is a VM exception, so guest catch blocks observe
            // the same surface as CLR code instead of the first child exception directly.
            foreach (var task in tasks) {
                var snapshot = task.Snapshot();
                if (task.IsCanceled || snapshot.GuestException.Kind != StackKind.Empty ||
                    snapshot.HostException is not null)
                    throw new UnhandledGuestException("System.AggregateException", null);
            }
        }
        static VmType? ResultType(IntrinsicContext ctx, bool fromMethod) => fromMethod
            ? ctx.MethodTypeArguments.FirstOrDefault()
            : ctx.ClassTypeArguments.FirstOrDefault();
        static void RegisterBinding(IntrinsicRegistry registry, BindingKey key, IntrinsicImpl impl) =>
            registry.RegisterBinding(key, impl, BindingOrigin.Managed);

        static VmType ValueTaskType(IntrinsicContext ctx, bool generic, VmType? resultType) {
            var definition = FindType(ctx, generic ? valueTaskOfT : valueTask);
            return generic
                ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                : definition;
        }
        static StackSlot NewValueTask(IntrinsicContext ctx, bool generic, VmType? resultType, VmTaskObject taskObject) {
            var taskType = ValueTaskType(ctx, generic, resultType);
            return StackSlot.OfValueType(new VmStructValue(taskType, [StackSlot.OfObject(taskObject)],
                generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
        }

        static StackSlot NewConfiguredAwaitable(IntrinsicContext ctx, bool generic, bool valueTask,
            VmType? resultType, VmTaskObject? taskObject, StackSlot source, short token,
            bool continueOnCapturedContext) {
            var name = valueTask
                ? (generic ? configuredValueTaskOfT : configuredValueTask)
                : (generic ? configuredTaskOfT : configuredTask);
            var definition = FindType(ctx, name);
            var type = generic
                ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                : definition;
            var fields = taskObject is not null
                ? new[] { StackSlot.OfObject(taskObject), StackSlot.OfInt32(continueOnCapturedContext ? 1 : 0) }
                : source.ObjectValue is not null
                    ? new[] { source, StackSlot.OfInt32(token), StackSlot.OfInt32(continueOnCapturedContext ? 1 : 0) }
                    : new[] { default(StackSlot), StackSlot.OfInt32(continueOnCapturedContext ? 1 : 0) };
            return StackSlot.OfValueType(new VmStructValue(type, fields,
                generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
        }
        static VmType ConfiguredAwaiterType(IntrinsicContext ctx, bool generic, bool valueTask, VmType? resultType) {
            var name = valueTask
                ? (generic
                    ? "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter"
                    : "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter")
                : (generic
                    ? "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter"
                    : "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter");
            var definition = FindType(ctx, name);
            return generic
                ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                : definition;
        }
        static StackSlot NewAwaiter(IntrinsicContext ctx, bool generic, bool valueTask,
            VmType? resultType, in StackSlot source) {
            var completed = valueTask && IsDefaultValueTask(source);
            if (valueTask && ValueTaskRuntime.TryGetSourceValue(source, out var valueTaskSource, out var valueTaskToken))
                return StackSlot.OfValueType(new VmStructValue(AwaiterType(ctx, generic, resultType, valueTask),
                    [valueTaskSource, StackSlot.OfInt32(valueTaskToken)],
                    generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
            var taskObject = completed ? null : AsTask(ctx, source, generic, valueTask);
            return StackSlot.OfValueType(new VmStructValue(AwaiterType(ctx, generic, resultType, valueTask),
                [taskObject is null ? default : StackSlot.OfObject(taskObject)],
                generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
        }

        static bool ValueTaskIsCompleted(IntrinsicContext ctx, in StackSlot value,
            bool generic) {
            if (IsDefaultValueTask(value))
                return true;
            if (ValueTaskRuntime.TryGetSourceValue(value, out var source, out var token))
                return ValueTaskRuntime.GetStatus(ctx, source, token) != 0;
            return AsTask(ctx, value, generic, valueTask: true).IsCompleted;
        }

        static void RegisterTaskType(IntrinsicRegistry registry, string typeName, bool generic, bool valueTask) {
            RegisterBinding(registry, BindingKey.Instance(typeName, "get_IsCompleted"),
                (ctx, a) => StackSlot.OfInt32(valueTask
                    ? (ValueTaskIsCompleted(ctx, a[0], generic) ? 1 : 0)
                    : (AsTask(ctx, a[0], generic, valueTask).IsCompleted ? 1 : 0)));
            RegisterBinding(registry, BindingKey.Instance(typeName, "GetAwaiter"),
                (ctx, a) => NewAwaiter(ctx, generic, valueTask,
                        generic ? (valueTask ? ValueTaskResultType(ctx, a[0]) : ResultType(ctx, fromMethod: false)) : null,
                        a[0]));
            if (!valueTask) {
                RegisterBinding(registry, BindingKey.Instance(typeName, "Wait"),
                    (ctx, a) => { SuspendHostWait(ctx, AsTask(ctx, a[0]).Wait); return null; });
                RegisterBinding(registry, BindingKey.InstanceWithReturn(typeName, "Wait", "System.Boolean", ["System.Int32"]),
                    (ctx, a) => {
                        var target = AsTask(ctx, a[0]);
                        var milliseconds = a[1].AsInt32;
                        if (milliseconds < Timeout.Infinite)
                            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "timeout");
                        return StackSlot.OfInt32(SuspendHostWait(ctx, () => target.Wait(milliseconds)) ? 1 : 0);
                    });
                RegisterBinding(registry, BindingKey.InstanceWithReturn(typeName, "Wait", "System.Boolean", ["System.TimeSpan"]),
                    (ctx, a) => StackSlot.OfInt32(SuspendHostWait(ctx, () => AsTask(ctx, a[0]).Wait(
                        ValidateTimeout(TimeSpanMilliseconds(a[1]), "timeout"))) ? 1 : 0));
            } else {
                RegisterBinding(registry, BindingKey.Instance(typeName, "AsTask"),
                    (ctx, a) => StackSlot.OfObject(AsTask(ctx, a[0], generic, valueTask)));
            }
            RegisterBinding(registry, BindingKey.Instance(typeName, "ConfigureAwait", "System.Boolean"),
                (ctx, a) => {
                    var resultType = generic
                        ? (valueTask ? ValueTaskResultType(ctx, a[0]) : ResultType(ctx, fromMethod: false))
                        : null;
                    var source = default(StackSlot);
                    short token = 0;
                    VmTaskObject? taskObject = null;
                    var isDefault = valueTask && IsDefaultValueTask(a[0]);
                    if (!isDefault && valueTask && ValueTaskRuntime.TryGetSourceValue(a[0], out source, out token)) {
                        // Keep source/token in the configured awaitable; ConfigureAwait itself is
                        // not allowed to query the source.
                    } else if (!isDefault) {
                        taskObject = AsTask(ctx, a[0], generic, valueTask);
                    }
                    return NewConfiguredAwaitable(ctx, generic, valueTask, resultType,
                        taskObject, source, token, a[1].AsInt32 != 0);
                });
        }

        static StackSlot StartTaskWorker(IntrinsicContext ctx, StackSlot[] a, VmType? resultType, bool generic,
            CancellationToken cancellationToken = default) {
            if (a[0].ObjectValue is not VmDelegate guestDelegate)
                throw new UnhandledGuestException("System.ArgumentNullException", "function");
            var running = NewTask(ctx, generic, resultType);
            if (cancellationToken.IsCancellationRequested) {
                running.SetCanceled();
                ctx.Shared.GuestTasks.Complete(running);
                return StackSlot.OfObject(running);
            }
            var invoke = ctx.InvokeGuestDelegate ?? throw new InvalidOperationException("guest delegate runner が初期化されていません。");
            ctx.Shared.GuestTasks.Run(running, [StackSlot.OfObject(running), a[0]], () => {
                var result = invoke(guestDelegate, [StackSlot.OfObject(guestDelegate)]) ?? default;
                if (result.ObjectValue is VmTaskObject nestedTask) {
                    nestedTask.Wait();
                    if (nestedTask.IsCanceled)
                        throw new UnhandledGuestException("System.OperationCanceledException", null);
                    var nested = nestedTask.Snapshot();
                    if (nested.GuestException.Kind != StackKind.Empty)
                        throw new UnhandledGuestException(nested.GuestException.ObjectValue is VmExceptionObject exception
                            ? exception.ExceptionType.FullName : "System.Exception", null);
                    if (nested.HostException is not null)
                        throw nested.HostException;
                    return nested.Result;
                }
                return result;
            });
            return StackSlot.OfObject(running);
        }

        RegisterBinding(r, BindingKey.InstanceByArity(valueTask, ".ctor", 2),
            (ctx, a) => ValueTaskRuntime.ConstructFromSource(ctx, generic: false, resultType: null,
                a[0], a[1], unchecked((short)a[2].AsInt32)));
        RegisterBinding(r, BindingKey.InstanceByArity(valueTaskOfT, ".ctor", 2),
            (ctx, a) => ValueTaskRuntime.ConstructFromSource(ctx, generic: true,
                ResultType(ctx, fromMethod: false), a[0], a[1], unchecked((short)a[2].AsInt32)));

        RegisterTaskType(r, task, generic: false, valueTask: false);
        RegisterTaskType(r, taskOfT, generic: true, valueTask: false);
        RegisterTaskType(r, valueTask, generic: false, valueTask: true);
        RegisterTaskType(r, valueTaskOfT, generic: true, valueTask: true);
        foreach (var (typeName, generic, isValueTask) in new[] {
            (configuredTask, false, false), (configuredTaskOfT, true, false),
            (configuredValueTask, false, true), (configuredValueTaskOfT, true, true),
        }) {
            RegisterBinding(r, BindingKey.Instance(typeName, "GetAwaiter"), (ctx, a) => {
                var resultType = generic
                    ? (isValueTask ? ValueTaskResultType(ctx, a[0]) : ResultType(ctx, fromMethod: false))
                    : null;
                var configuredValue = ReadValue(a[0]);
                var configured = configuredValue.ObjectValue as VmStructValue
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "Configured awaitable が初期化されていません。");
                var captures = configured.Fields.Length == 0 ? 1 : configured.Fields[^1].AsInt32;
                StackSlot[] fields;
                if (isValueTask && ValueTaskRuntime.TryGetSourceValue(configuredValue, out var source, out var token))
                    fields = [source, StackSlot.OfInt32(token), StackSlot.OfInt32(captures)];
                else {
                    var taskObject = configured.Fields.Length > 0
                        ? configured.Fields[0].ObjectValue as VmTaskObject : null;
                    fields = [taskObject is null ? default : StackSlot.OfObject(taskObject),
                        StackSlot.OfInt32(captures)];
                }
                return StackSlot.OfValueType(new VmStructValue(
                    ConfiguredAwaiterType(ctx, generic, isValueTask, resultType),
                    fields,
                    generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
            });
        }
        RegisterBinding(r, BindingKey.Instance(taskOfT, "get_Result"),
            static (ctx, a) => TaskResult(ctx, AsTask(ctx, a[0])));
        RegisterBinding(r, BindingKey.Instance(valueTaskOfT, "get_Result"),
            static (ctx, a) => IsDefaultValueTask(a[0])
                ? DefaultValueTaskResult(ctx, generic: true, ValueTaskResultType(ctx, a[0]))
                : TaskResult(ctx, AsTask(ctx, a[0], generic: true, valueTask: true)));
        RegisterBinding(r, BindingKey.StaticWithReturn(task, "get_CompletedTask", task, []),
            static (ctx, _) => StackSlot.OfObject(NewTask(ctx, generic: false, completed: true)));
        RegisterBinding(r, BindingKey.StaticWithReturn(valueTask, "get_CompletedTask", valueTask, []),
            static (ctx, _) => NewValueTask(ctx, generic: false, resultType: null,
                NewTask(ctx, generic: false, completed: true)));
        RegisterBinding(r, BindingKey.Static(valueTask, "FromResult", "!!0"),
            static (ctx, a) => NewValueTask(ctx, generic: true,
                ResultType(ctx, fromMethod: true),
                NewTask(ctx, generic: true, ResultType(ctx, fromMethod: true), completed: true, result: a[0])));
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.Int32"),
            static (ctx, a) => {
                var milliseconds = a[0].AsInt32;
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.Delay(delayed, milliseconds);
                return StackSlot.OfObject(delayed);
            });
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.TimeSpan"),
            static (ctx, a) => {
                var milliseconds = TimeSpanMilliseconds(a[0]);
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                 ctx.Shared.GuestTasks.Delay(delayed, milliseconds);
                 return StackSlot.OfObject(delayed);
             });
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.Int32", "System.Threading.CancellationToken"),
            (ctx, a) => {
                var milliseconds = a[0].AsInt32;
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.Delay(delayed, milliseconds, CancellationTokenOf(a[1]));
                return StackSlot.OfObject(delayed);
            });
        RegisterBinding(r, BindingKey.Static(task, "Delay", "System.TimeSpan", "System.Threading.CancellationToken"),
            (ctx, a) => {
                var milliseconds = TimeSpanMilliseconds(a[0]);
                if (milliseconds < Timeout.Infinite)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
                var delayed = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.Delay(delayed, milliseconds, CancellationTokenOf(a[1]));
                return StackSlot.OfObject(delayed);
            });
        RegisterBinding(r, BindingKey.Static(task, "FromResult", "!!0"),
            static (ctx, a) => StackSlot.OfObject(NewTask(ctx, generic: true,
                ResultType(ctx, fromMethod: true), completed: true, result: a[0])));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Action"),
            static (ctx, a) => StartTaskWorker(ctx, a, resultType: null, generic: false));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Action", "System.Threading.CancellationToken"),
            (ctx, a) => StartTaskWorker(ctx, [a[0]], resultType: null, generic: false,
                CancellationTokenOf(a[1])));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<System.Threading.Tasks.Task>"),
            static (ctx, a) => StartTaskWorker(ctx, a, resultType: null, generic: false));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<System.Threading.Tasks.Task>", "System.Threading.CancellationToken"),
            (ctx, a) => StartTaskWorker(ctx, [a[0]], resultType: null, generic: false,
                CancellationTokenOf(a[1])));
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<!!0>"),
            static (ctx, a) => {
                var resultType = ResultType(ctx, fromMethod: true);
                var generic = resultType is not null;
                return StartTaskWorker(ctx, a, resultType, generic);
            });
        RegisterBinding(r, BindingKey.Static(task, "Run", "System.Func`1<!!0>", "System.Threading.CancellationToken"),
            (ctx, a) => {
                var resultType = ResultType(ctx, fromMethod: true);
                var generic = resultType is not null;
                return StartTaskWorker(ctx, [a[0]], resultType, generic, CancellationTokenOf(a[1]));
            });

        RegisterBinding(r, BindingKey.Static(task, "WhenAll", "System.Threading.Tasks.Task[]"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var composite = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.WhenAll(composite, tasks, static () => default);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAll", "System.Threading.Tasks.Task`1<!!0>[]"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var resultType = TaskArrayResultType(ctx, a[0]);
                var compositeResultType = new VmArrayType { ElementType = resultType };
                var composite = NewTask(ctx, generic: true, resultType: compositeResultType);
                ctx.Shared.GuestTasks.WhenAll(composite, tasks, () => {
                    var values = tasks.Select(task => task.Snapshot().Result).ToArray();
                    return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(compositeResultType, values)));
                });
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny", "System.Threading.Tasks.Task[]"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var taskType = FindType(ctx, task);
                var composite = NewTask(ctx, generic: true, resultType: taskType);
                ctx.Shared.GuestTasks.WhenAny(composite, tasks);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny", "System.Threading.Tasks.Task`1<!!0>[]"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var resultType = TaskArrayResultType(ctx, a[0]);
                var elementTaskType = TaskType(ctx, generic: true, resultType);
                var composite = NewTask(ctx, generic: true, elementTaskType);
                ctx.Shared.GuestTasks.WhenAny(composite, tasks);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAll", ReadOnlySpanType("System.Threading.Tasks.Task")),
            (ctx, a) => {
                var tasks = TaskSpan(ctx, a[0]);
                var composite = NewTask(ctx, generic: false);
                ctx.Shared.GuestTasks.WhenAll(composite, tasks, static () => default);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAll",
                ReadOnlySpanType("System.Threading.Tasks.Task`1<!!0>")),
            (ctx, a) => {
                var tasks = TaskSpan(ctx, a[0]);
                var resultType = TaskSpanResultType(ctx, a[0]);
                var compositeResultType = new VmArrayType { ElementType = resultType };
                var composite = NewTask(ctx, generic: true, resultType: compositeResultType);
                ctx.Shared.GuestTasks.WhenAll(composite, tasks, () => {
                    var values = tasks.Select(item => item.Snapshot().Result).ToArray();
                    return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(compositeResultType, values)));
                });
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny", ReadOnlySpanType("System.Threading.Tasks.Task")),
            (ctx, a) => {
                var tasks = TaskSpan(ctx, a[0]);
                var taskType = FindType(ctx, task);
                var composite = NewTask(ctx, generic: true, resultType: taskType);
                ctx.Shared.GuestTasks.WhenAny(composite, tasks);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny",
                ReadOnlySpanType("System.Threading.Tasks.Task`1<!!0>")),
            (ctx, a) => {
                var tasks = TaskSpan(ctx, a[0]);
                var resultType = TaskSpanResultType(ctx, a[0]);
                var elementTaskType = TaskType(ctx, generic: true, resultType);
                var composite = NewTask(ctx, generic: true, elementTaskType);
                ctx.Shared.GuestTasks.WhenAny(composite, tasks);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WaitAll", ReadOnlySpanType("System.Threading.Tasks.Task")),
            (ctx, a) => {
                var tasks = TaskSpan(ctx, a[0]);
                SuspendHostWait(ctx, () => ctx.Shared.GuestTasks.WaitAll(tasks, Timeout.Infinite, default));
                ThrowTaskFailures(tasks);
                return null;
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny", "System.Threading.Tasks.Task", "System.Threading.Tasks.Task"),
            (ctx, a) => {
                var first = ReadValue(a[0]).ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "Task が必要です.");
                var second = ReadValue(a[1]).ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "Task が必要です.");
                ctx.Shared.GuestTasks.ValidateTaskCombinatorInputCount(2);
                var composite = NewTask(ctx, generic: true, FindType(ctx, task));
                ctx.Shared.GuestTasks.WhenAny(composite, [first, second]);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WhenAny",
                "System.Threading.Tasks.Task`1<!!0>", "System.Threading.Tasks.Task`1<!!0>"),
            (ctx, a) => {
                var first = ReadValue(a[0]).ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "Task が必要です.");
                var second = ReadValue(a[1]).ObjectValue as VmTaskObject
                    ?? throw new UnhandledGuestException("System.ArgumentException", "Task が必要です.");
                ctx.Shared.GuestTasks.ValidateTaskCombinatorInputCount(2);
                var resultType = ResultType(ctx, fromMethod: true) ?? ctx.Types.FindIntrinsicType("System.Object")!;
                var composite = NewTask(ctx, generic: true, TaskType(ctx, generic: true, resultType));
                ctx.Shared.GuestTasks.WhenAny(composite, [first, second]);
                return StackSlot.OfObject(composite);
            });
        RegisterBinding(r, BindingKey.Static(task, "WaitAll", "System.Threading.Tasks.Task[]"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                SuspendHostWait(ctx, () => ctx.Shared.GuestTasks.WaitAll(tasks, Timeout.Infinite, default));
                ThrowTaskFailures(tasks);
                return null;
            });
        RegisterBinding(r, BindingKey.Static(task, "WaitAll", "System.Threading.Tasks.Task[]", "System.Int32"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var completed = SuspendHostWait(ctx, () => ctx.Shared.GuestTasks.WaitAll(
                    tasks, ValidateTimeout(a[1].AsInt32, "millisecondsTimeout"), default));
                if (completed) ThrowTaskFailures(tasks);
                return StackSlot.OfInt32(completed ? 1 : 0);
            });
        RegisterBinding(r, BindingKey.Static(task, "WaitAll", "System.Threading.Tasks.Task[]", "System.TimeSpan"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                var completed = SuspendHostWait(ctx, () => ctx.Shared.GuestTasks.WaitAll(
                    tasks, ValidateTimeout(TimeSpanMilliseconds(a[1]), "timeout"), default));
                if (completed) ThrowTaskFailures(tasks);
                return StackSlot.OfInt32(completed ? 1 : 0);
            });
        RegisterBinding(r, BindingKey.Static(task, "WaitAll", "System.Threading.Tasks.Task[]", "System.Threading.CancellationToken"),
            (ctx, a) => {
                var tasks = TaskArray(ctx, a[0]);
                SuspendHostWait(ctx, () => ctx.Shared.GuestTasks.WaitAll(tasks, Timeout.Infinite,
                    CancellationTokenOf(a[1])));
                ThrowTaskFailures(tasks);
                return null;
            });

        foreach (var (typeName, generic, awaiterIsValueTask) in new[] {
            (awaiter, false, false), (awaiterOfT, true, false),
            (valueTaskAwaiter, false, true), (valueTaskAwaiterOfT, true, true),
            ("System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter", false, false),
            ("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter", true, false),
            ("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter", false, true),
            ("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter", true, true),
        }) {
            RegisterBinding(r, BindingKey.Instance(typeName, "get_IsCompleted"),
                (ctx, a) => {
                    if (awaiterIsValueTask && ValueTaskRuntime.TryGetSourceAwaiter(a[0], out var source, out var token)) {
                        return StackSlot.OfInt32(ValueTaskRuntime.GetStatus(ctx, source, token) != 0 ? 1 : 0);
                    }
                    return StackSlot.OfInt32(IsCompletedAwaiter(a[0])
                        || AwaitedTask(ctx, a[0]).IsCompleted ? 1 : 0);
                });
            RegisterBinding(r, BindingKey.Instance(typeName, "GetResult"),
                (ctx, a) => {
                    if (awaiterIsValueTask && IsCompletedAwaiter(a[0])) {
                        // ValueTask (非 generic) の GetResult は void。空の StackSlot を
                        // 「戻り値あり」として push すると、ループごとに評価スタックへ
                        // 残るため、ここでは明示的に null を返す。
                        if (!generic)
                            return null;
                        return DefaultValueTaskResult(ctx, generic: true, AwaiterResultType(ctx, a[0]));
                    }
                    if (awaiterIsValueTask && ValueTaskRuntime.TryGetSourceAwaiter(a[0], out var source, out var token)) {
                        var result = ValueTaskRuntime.GetResult(ctx, source, token);
                        return generic ? result : null;
                    }
                    return TaskResult(ctx, AwaitedTask(ctx, a[0]));
                });
            RegisterBinding(r, BindingKey.Instance(typeName, "OnCompleted", "System.Action"),
                (ctx, a) => RegisterAwaiterCallback(ctx, a,
                    ValueTaskRuntime.UseSchedulingContext | ValueTaskRuntime.FlowExecutionContext));
            RegisterBinding(r, BindingKey.Instance(typeName, "UnsafeOnCompleted", "System.Action"),
                (ctx, a) => RegisterAwaiterCallback(ctx, a, ValueTaskRuntime.UseSchedulingContext));
        }

        RegisterBuilderType(r, builder, generic: false, valueTask: false);
        RegisterBuilderType(r, builderOfT, generic: true, valueTask: false);
        RegisterBuilderType(r, valueTaskBuilder, generic: false, valueTask: true);
        RegisterBuilderType(r, valueTaskBuilderOfT, generic: true, valueTask: true);

        static void RegisterBuilderType(IntrinsicRegistry registry, string typeName, bool generic, bool valueTask) {
            RegisterBinding(registry, BindingKey.Static(typeName, "Create"),
                (ctx, _) => {
                    var resultType = generic ? ResultType(ctx, fromMethod: false) : null;
                    var definition = FindType(ctx, typeName);
                    VmType builderType = generic
                        ? new VmConstructedType { Definition = definition, TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] }
                        : definition;
                    return StackSlot.OfValueType(new VmStructValue(builderType, [default], generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "get_Task"),
                (ctx, a) => {
                    var taskObject = EnsureBuilderTask(ctx, a);
                    if (!valueTask)
                        return StackSlot.OfObject(taskObject);
                    var resultType = generic ? BuilderValue(a[0]).TypeArguments.FirstOrDefault() : null;
                    return NewValueTask(ctx, generic, resultType, taskObject);
                });
            RegisterBinding(registry, generic
                    ? BindingKey.Instance(typeName, "SetResult", "!0")
                    : BindingKey.Instance(typeName, "SetResult"),
                (ctx, a) => {
                    var target = EnsureBuilderTask(ctx, a);
                    var result = generic && a.Length > 1 ? a[1] : default;
                    ctx.Shared.GuestTasks.Complete(target, result);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "SetException", "System.Exception"),
                (ctx, a) => {
                    var target = EnsureBuilderTask(ctx, a);
                    ctx.Shared.GuestTasks.CompleteGuestException(target, a.Length > 1 ? a[1] : default);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "Start", "!!0&"),
                (ctx, a) => {
                    var run = ctx.RunGuestStateMachine ?? throw new InvalidOperationException("guest state machine runner が初期化されていません。");
                    run(a[1]);
                    return null;
                });
            RegisterBinding(registry, BindingKey.Instance(typeName, "AwaitOnCompleted", "!!0&", "!!1&"),
                (ctx, a) => RegisterContinuation(ctx, a, flowExecutionContext: true));
            RegisterBinding(registry, BindingKey.Instance(typeName, "AwaitUnsafeOnCompleted", "!!0&", "!!1&"),
                (ctx, a) => RegisterContinuation(ctx, a, flowExecutionContext: false));
            RegisterBinding(registry, BindingKey.Instance(typeName, "SetStateMachine",
                    "System.Runtime.CompilerServices.IAsyncStateMachine"),
                static (_, _) => null);
        }

        static StackSlot? RegisterContinuation(IntrinsicContext ctx, StackSlot[] a, bool flowExecutionContext) {
            var resume = ctx.RunGuestStateMachine ?? throw new InvalidOperationException("guest state machine runner が初期化されていません。");
            var capturedContext = CapturesSynchronizationContext(a[1])
                ? ctx.Shared.CurrentSynchronizationContext : null;
            if (ValueTaskRuntime.TryGetSourceAwaiter(a[1], out var source, out var token)) {
                var awaiter = ReadValue(a[1]);
                var generic = awaiter.ObjectValue is VmStructValue value &&
                    (value.TypeArguments.Length > 0 || (value.StructType as VmConstructedType)?.TypeArguments.Length > 0);
                var resultType = generic ? AwaiterResultType(ctx, a[1]) : null;
                var sourceFlags = (CapturesSynchronizationContext(a[1])
                    ? ValueTaskRuntime.UseSchedulingContext : 0) |
                    (flowExecutionContext ? ValueTaskRuntime.FlowExecutionContext : 0);
                var awaitedSourceTask = ValueTaskRuntime.AsTask(ctx, source, generic, resultType, token,
                    sourceFlags);
                var continuation = new Action<StackSlot>(state => ctx.Shared.RunWithSynchronizationContext(
                    capturedContext, () => resume(state)));
                ctx.Shared.GuestTasks.ScheduleContinuation(awaitedSourceTask, a[2], continuation,
                    capturedContext is null ? null : StackSlot.OfObject(capturedContext));
                return null;
            }
            if (TryAwaitedTask(ctx, a[1], out var awaited)) {
                var continuation = new Action<StackSlot>(state => ctx.Shared.RunWithSynchronizationContext(
                    capturedContext, () => resume(state)));
                ctx.Shared.GuestTasks.ScheduleContinuation(awaited, a[2], continuation,
                    capturedContext is null ? null : StackSlot.OfObject(capturedContext));
                return null;
            }

            var callbackType = ctx.Types.FindIntrinsicType("System.Action")
                ?? throw new InvalidOperationException("Action facade が見つかりません。");
            GuestTaskRuntime.ExternalContinuation? registration = null;
            var callback = ctx.Heap.Allocate(new VmDelegate {
                DeclaredType = callbackType,
                HostCallback = _ => {
                    registration?.Signal();
                    return null;
                },
            });
            var extraRoots = capturedContext is null
                ? Array.Empty<StackSlot>()
                : [StackSlot.OfObject(capturedContext)];
            registration = ctx.Shared.GuestTasks.RegisterExternalContinuation(a[2], extraRoots, callback,
                state => {
                    ctx.Shared.RunWithSynchronizationContext(capturedContext, () => resume(state));
                });
            try {
                StackSlot? result;
                try {
                    result = ctx.InvokeGuestInstanceMethod?.Invoke(a[1], "UnsafeOnCompleted",
                        [StackSlot.OfObject(callback)]);
                } catch (UnhandledGuestException ex) when (ex.ExceptionTypeName == "System.NotSupportedException") {
                    result = ctx.InvokeGuestInstanceMethod?.Invoke(a[1], "OnCompleted",
                        [StackSlot.OfObject(callback)]);
                }
                _ = result;
            } catch (UnhandledGuestException) {
                registration.Cancel();
                throw;
            } catch (VmGuestThrow guest) {
                registration.Cancel();
                throw;
            }
            return null;
        }

        static StackSlot? RegisterAwaiterCallback(IntrinsicContext ctx, StackSlot[] a, int sourceFlags) {
            if (ValueTaskRuntime.TryGetSourceAwaiter(a[0], out var source, out var token)) {
                var awaiter = ReadValue(a[0]);
                var generic = awaiter.ObjectValue is VmStructValue value &&
                    (value.TypeArguments.Length > 0 || (value.StructType as VmConstructedType)?.TypeArguments.Length > 0);
                var resultType = generic ? AwaiterResultType(ctx, a[0]) : null;
                if (!CapturesSynchronizationContext(a[0]))
                    sourceFlags &= ~ValueTaskRuntime.UseSchedulingContext;
                var sourceTask = ValueTaskRuntime.AsTask(ctx, source, generic, resultType, token, sourceFlags,
                    forceSourceRegistration: true);
                var sourceCallback = a.Length > 1 ? a[1] : default;
                var sourceInvoke = ctx.InvokeGuestDelegate ?? throw new InvalidOperationException("guest delegate runner が初期化されていません。");
                var sourceContext = CapturesSynchronizationContext(a[0])
                    ? ctx.Shared.CurrentSynchronizationContext : null;
                ctx.Shared.GuestTasks.ScheduleCallback(sourceTask, sourceCallback, value => {
                    void Run() {
                        if (value.ObjectValue is VmDelegate continuation)
                            _ = sourceInvoke(continuation, [value]);
                    }
                    ctx.Shared.RunWithSynchronizationContext(sourceContext, Run);
                }, sourceContext is null ? null : StackSlot.OfObject(sourceContext));
                return null;
            }
            var awaited = AwaitedTask(ctx, a[0]);
            var callback = a.Length > 1 ? a[1] : default;
            var invoke = ctx.InvokeGuestDelegate ?? throw new InvalidOperationException("guest delegate runner が初期化されていません。");
            var capturedContext = CapturesSynchronizationContext(a[0])
                ? ctx.Shared.CurrentSynchronizationContext : null;
            ctx.Shared.GuestTasks.ScheduleCallback(awaited, callback, value => {
                void Run() {
                    if (value.ObjectValue is VmDelegate continuation)
                        _ = invoke(continuation, [value]);
                }
                ctx.Shared.RunWithSynchronizationContext(capturedContext, Run);
            }, capturedContext is null ? null : StackSlot.OfObject(capturedContext));
            return null;
        }
    }

    private static int TimeSpanMilliseconds(StackSlot slot) {
        var value = slot.ObjectValue switch {
            VmStructValue vm => vm,
            VmByRef byRef when byRef.Read().ObjectValue is VmStructValue vm => vm,
            _ => throw new UnhandledGuestException("System.ArgumentException", "TimeSpan value is not available."),
        };
        long ticks = 0;
        if (value.Fields.Length > 0)
            ticks = value.Fields[0].Int64Value;
        if (ticks == -TimeSpan.TicksPerMillisecond)
            return Timeout.Infinite;
        if (ticks < 0)
            return -2;
        return (int)Math.Min(int.MaxValue, (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
    }

    private static int ValidateTimeout(int milliseconds, string parameterName) => milliseconds >= Timeout.Infinite
        ? milliseconds
        : throw new UnhandledGuestException("System.ArgumentOutOfRangeException", parameterName);

    private static void SuspendHostWait(IntrinsicContext context, Action wait) {
        if (context.SuspendExecution is { } suspend)
            suspend(wait);
        else
            wait();
    }

    private static T SuspendHostWait<T>(IntrinsicContext context, Func<T> wait) {
        T result = default!;
        SuspendHostWait(context, () => { result = wait(); });
        return result;
    }

    private static void WriteByRef(StackSlot byRefSlot, StackSlot value) {
        if (byRefSlot.ObjectValue is VmByRef byRef)
            byRef.Write(value);
        else
            throw new UnhandledGuestException("System.ArgumentException", "A writable byref argument is required.");
    }

    private static void ExitMonitor(IntrinsicContext context, StackSlot target) {
        try {
            Monitor.Exit(context.Shared.Monitors.SyncRoot(target.ObjectValue));
        } catch (SynchronizationLockException) {
            throw new UnhandledGuestException("System.Threading.SynchronizationLockException", null);
        }
    }
}
