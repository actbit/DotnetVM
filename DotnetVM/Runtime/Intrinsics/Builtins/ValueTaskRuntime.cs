using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// VM adapter for ValueTask(IValueTaskSource[,T]).  A source-backed ValueTask deliberately keeps
/// only <c>{ source, token }</c> in its value representation.  It does not query the source or
/// allocate a Task until an operation that actually needs the source does so, matching CLR lazy
/// construction semantics.
/// </summary>
internal static class ValueTaskRuntime {
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int Faulted = 2;
    private const int Canceled = 3;

    // ValueTaskAwaiter.OnCompleted uses both flags; UnsafeOnCompleted still asks the source to
    // honor a captured scheduling context but does not flow ExecutionContext.
    internal const int UseSchedulingContext = 1;
    internal const int FlowExecutionContext = 2;

    private static bool IsSourceObject(object? value) =>
        value is not null and not VmTaskObject;

    internal static StackSlot FromSource(IntrinsicContext ctx, bool generic, VmType? resultType,
        StackSlot sourceSlot, short token) {
        var source = Read(sourceSlot);
        if (!IsSourceObject(source.ObjectValue))
            throw new UnhandledGuestException("System.ArgumentNullException", "source");
        var valueTaskType = ctx.Types.FindIntrinsicType(generic
            ? "System.Threading.Tasks.ValueTask`1" : "System.Threading.Tasks.ValueTask")
            ?? throw new InvalidOperationException("ValueTask facade が見つかりません。");
        VmType constructed = generic
            ? new VmConstructedType {
                Definition = valueTaskType,
                TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!],
            }
            : valueTaskType;
        return StackSlot.OfValueType(new VmStructValue(constructed,
            [source, StackSlot.OfInt32(token)],
            generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
    }

    internal static StackSlot? ConstructFromSource(IntrinsicContext ctx, bool generic, VmType? resultType,
        StackSlot receiver, StackSlot sourceSlot, short token) {
        var value = FromSource(ctx, generic, resultType, sourceSlot, token);
        if (receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef) {
            byRef.Write(value);
            return null;
        }
        return value;
    }

    internal static bool TryGetSourceValue(in StackSlot slot, out StackSlot source, out short token) {
        var value = Read(slot);
        if (value.ObjectValue is VmStructValue wrapper && wrapper.Fields.Length >= 2 &&
            wrapper.Fields[1].Kind == StackKind.Int32) {
            source = Read(wrapper.Fields[0]);
            if (!IsSourceObject(source.ObjectValue)) {
                source = default;
                token = default;
                return false;
            }
            token = unchecked((short)wrapper.Fields[1].AsInt32);
            return true;
        }
        source = default;
        token = default;
        return false;
    }

    /// <summary>Same representation as a source-backed ValueTaskAwaiter.</summary>
    internal static bool TryGetSourceAwaiter(in StackSlot slot, out StackSlot source, out short token) {
        var value = Read(slot);
        if (value.ObjectValue is VmStructValue awaiter && awaiter.Fields.Length >= 2 &&
            awaiter.Fields[1].Kind == StackKind.Int32) {
            source = Read(awaiter.Fields[0]);
            if (!IsSourceObject(source.ObjectValue)) {
                source = default;
                token = default;
                return false;
            }
            token = unchecked((short)awaiter.Fields[1].AsInt32);
            return true;
        }
        source = default;
        token = default;
        return false;
    }

    internal static int GetStatus(IntrinsicContext ctx, in StackSlot source, short token) =>
        Invoke(ctx, Read(source), "GetStatus", StackSlot.OfInt32(token)).AsInt32;

    internal static StackSlot GetResult(IntrinsicContext ctx, in StackSlot source, short token) =>
        Invoke(ctx, Read(source), "GetResult", StackSlot.OfInt32(token));

    /// <summary>
    /// Converts a source-backed ValueTask to a guest Task only when an API requires Task
    /// identity (AsTask/ConfigureAwait continuation).  <paramref name="flags"/> is forwarded
    /// unchanged to IValueTaskSource.OnCompleted.
    /// </summary>
    internal static VmTaskObject AsTask(IntrinsicContext ctx, in StackSlot source, bool generic,
        VmType? resultType, short token, int flags = 0, bool forceSourceRegistration = false) {
        var taskDefinition = ctx.Types.FindIntrinsicType(generic
            ? "System.Threading.Tasks.Task`1" : "System.Threading.Tasks.Task")
            ?? throw new InvalidOperationException("Task facade が見つかりません。");
        VmType taskType = generic
            ? new VmConstructedType {
                Definition = taskDefinition,
                TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!],
            }
            : taskDefinition;
        var task = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(taskType));
        var sourceValue = Read(source);
        if (forceSourceRegistration) {
            AttachPending(ctx, task, sourceValue, generic, token, flags);
            return task;
        }
        var status = GetStatus(ctx, sourceValue, token);
        switch (status) {
            case Succeeded:
                CompleteSuccess(ctx, task, sourceValue, generic, token);
                break;
            case Faulted:
                CompleteFailure(ctx, task, sourceValue, token);
                break;
            case Canceled:
                task.SetCanceled();
                ctx.Shared.GuestTasks.Complete(task);
                break;
            case Pending:
                AttachPending(ctx, task, sourceValue, generic, token, flags);
                break;
            default:
                throw new UnhandledGuestException("System.InvalidOperationException",
                    "IValueTaskSource.GetStatus が不正な値を返しました。");
        }
        return task;
    }

    private static void AttachPending(IntrinsicContext ctx, VmTaskObject task, StackSlot source,
        bool generic, short token, int flags) {
        var actionDefinition = ctx.Types.FindIntrinsicType("System.Action`1")
            ?? throw new InvalidOperationException("Action<T> facade が見つかりません。");
        var actionType = new VmConstructedType {
            Definition = actionDefinition,
            TypeArguments = [ctx.Types.FindIntrinsicType("System.Object")!],
        };
        GuestTaskRuntime.ExternalContinuation? registration = null;
        var callback = ctx.Heap.Allocate(new VmDelegate {
            DeclaredType = actionType,
            HostCallback = _ => {
                registration?.Signal();
                return null;
            },
        });
        registration = ctx.Shared.GuestTasks.RegisterExternalContinuation(source,
            [StackSlot.OfObject(task)], callback,
            _ => CompleteSource(ctx, task, source, generic, token));
        try {
            Invoke(ctx, source, "OnCompleted",
                StackSlot.OfObject(callback), StackSlot.OfObject(task),
                StackSlot.OfInt32(token), StackSlot.OfInt32(flags));
        } catch (VmGuestThrow guest) {
            registration.Cancel();
            ctx.Shared.GuestTasks.CompleteGuestException(task, StackSlot.OfObject(guest.ExceptionObject));
            throw;
        } catch (Exception ex) {
            registration.Cancel();
            ctx.Shared.GuestTasks.CompleteHostException(task, ex);
            throw;
        }
    }

    private static void CompleteSource(IntrinsicContext ctx, VmTaskObject task, StackSlot source,
        bool generic, short token) {
        try {
            var status = GetStatus(ctx, source, token);
            if (status == Canceled) {
                task.SetCanceled();
                ctx.Shared.GuestTasks.Complete(task);
            } else if (status == Faulted) {
                CompleteFailure(ctx, task, source, token);
            } else if (status == Succeeded) {
                CompleteSuccess(ctx, task, source, generic, token);
            } else {
                throw new UnhandledGuestException("System.InvalidOperationException",
                    "IValueTaskSource callback が pending 状態で呼ばれました。");
            }
        } catch (VmGuestThrow guest) {
            ctx.Shared.GuestTasks.CompleteGuestException(task, StackSlot.OfObject(guest.ExceptionObject));
        } catch (Exception ex) {
            ctx.Shared.GuestTasks.CompleteHostException(task, ex);
        }
    }

    private static void CompleteSuccess(IntrinsicContext ctx, VmTaskObject task, StackSlot source,
        bool generic, short token) {
        var result = GetResult(ctx, source, token);
        ctx.Shared.GuestTasks.Complete(task, generic ? result : default);
    }

    private static void CompleteFailure(IntrinsicContext ctx, VmTaskObject task, StackSlot source, short token) {
        try {
            _ = GetResult(ctx, source, token);
            ctx.Shared.GuestTasks.CompleteHostException(task,
                new InvalidOperationException("IValueTaskSource は Faulted だが GetResult が例外を送出しませんでした。"));
        } catch (VmGuestThrow guest) {
            ctx.Shared.GuestTasks.CompleteGuestException(task, StackSlot.OfObject(guest.ExceptionObject));
        }
    }

    private static StackSlot Invoke(IntrinsicContext ctx, StackSlot receiver, string name, params StackSlot[] args) =>
        ctx.InvokeGuestInstanceMethod?.Invoke(receiver, name, args)
        ?? throw new InvalidOperationException("guest instance method runner が初期化されていません。");

    private static StackSlot Read(in StackSlot slot) =>
        slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef ? byRef.Read() : slot;
}
