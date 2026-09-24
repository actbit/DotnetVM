using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// ValueTask(IValueTaskSource[,T]) の VM adapter。host object を取り込まず、source の
/// GetStatus / OnCompleted / GetResult を guest IL として呼び出して通常の VmTaskObject に
/// 正規化する。以後の await / ConfigureAwait / AsTask は既存 Task 経路を共有する。
/// </summary>
internal static class ValueTaskRuntime {
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int Faulted = 2;
    private const int Canceled = 3;

    internal static StackSlot FromSource(IntrinsicContext ctx, bool generic, VmType? resultType,
        StackSlot sourceSlot, short token) {
        var source = Read(sourceSlot);
        if (source.ObjectValue is not VmClassInstance && source.ObjectValue is not VmIntrinsicInstance)
            throw new UnhandledGuestException("System.ArgumentNullException", "source");

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
        var getStatus = Invoke(ctx, source, "GetStatus", StackSlot.OfInt32(token));
        switch (getStatus.AsInt32) {
            case Succeeded:
                CompleteSuccess(ctx, task, source, generic, token);
                break;
            case Faulted:
                CompleteFailure(ctx, task, source, token);
                break;
            case Canceled:
                task.SetCanceled();
                ctx.Shared.GuestTasks.Complete(task);
                break;
            case Pending:
                AttachPending(ctx, task, source, generic, token);
                break;
            default:
                throw new UnhandledGuestException("System.InvalidOperationException", "IValueTaskSource.GetStatus が不正な値を返しました。");
        }

        var valueTaskType = ctx.Types.FindIntrinsicType(generic
            ? "System.Threading.Tasks.ValueTask`1" : "System.Threading.Tasks.ValueTask")
            ?? throw new InvalidOperationException("ValueTask facade が見つかりません。");
        VmType constructed = generic
            ? new VmConstructedType {
                Definition = valueTaskType,
                TypeArguments = [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!],
            }
            : valueTaskType;
        return StackSlot.OfValueType(new VmStructValue(constructed, [StackSlot.OfObject(task)],
            generic ? [resultType ?? ctx.Types.FindIntrinsicType("System.Object")!] : []));
    }

    private static void AttachPending(IntrinsicContext ctx, VmTaskObject task, StackSlot source,
        bool generic, short token) {
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
                StackSlot.OfInt32(token), StackSlot.OfInt32(0));
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
            var status = Invoke(ctx, source, "GetStatus", StackSlot.OfInt32(token)).AsInt32;
            if (status == Canceled) {
                task.SetCanceled();
                ctx.Shared.GuestTasks.Complete(task);
            } else if (status == Faulted) {
                CompleteFailure(ctx, task, source, token);
            } else if (status == Succeeded) {
                CompleteSuccess(ctx, task, source, generic, token);
            } else {
                throw new UnhandledGuestException("System.InvalidOperationException", "IValueTaskSource callback が pending 状態で呼ばれました。");
            }
        } catch (VmGuestThrow guest) {
            ctx.Shared.GuestTasks.CompleteGuestException(task, StackSlot.OfObject(guest.ExceptionObject));
        } catch (Exception ex) {
            ctx.Shared.GuestTasks.CompleteHostException(task, ex);
        }
    }

    private static void CompleteSuccess(IntrinsicContext ctx, VmTaskObject task, StackSlot source,
        bool generic, short token) {
        var result = Invoke(ctx, source, "GetResult", StackSlot.OfInt32(token));
        ctx.Shared.GuestTasks.Complete(task, generic ? result : default);
    }

    private static void CompleteFailure(IntrinsicContext ctx, VmTaskObject task, StackSlot source, short token) {
        try {
            _ = Invoke(ctx, source, "GetResult", StackSlot.OfInt32(token));
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
