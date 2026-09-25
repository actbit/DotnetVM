using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using DotnetVM.Policy;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterCancellationAndSynchronization(IntrinsicRegistry r) {
        RegisterCancellation(r);
        RegisterSynchronizationContext(r);
    }

    private static void RegisterCancellation(IntrinsicRegistry r) {
        const string token = "System.Threading.CancellationToken";
        const string source = "System.Threading.CancellationTokenSource";

        static VmObject SourceObject(in StackSlot slot) =>
            slot.ObjectValue as VmObject
            ?? throw new UnhandledGuestException("System.NullReferenceException", null);

        static VmCancellationState EnsureState(IntrinsicContext ctx, in StackSlot slot) {
            var instance = SourceObject(slot);
            var existing = CancellationRuntime.State(slot);
            if (existing is not null)
                return existing;
            var type = ctx.Types.FindIntrinsicType(source) ?? instance.Type;
            var state = ctx.Heap.Allocate(new VmCancellationState(type));
            switch (instance) {
                case VmIntrinsicInstance intrinsic when intrinsic.State.Length > 0:
                    intrinsic.State[0] = StackSlot.OfObject(state);
                    break;
                case VmClassInstance @class when @class.Fields.Length > 0:
                    @class.Fields[0] = StackSlot.OfObject(state);
                    break;
                default:
                    throw new UnhandledGuestException("System.InvalidOperationException", "CancellationTokenSource の状態を初期化できません。");
            }
            return state;
        }

        static int ConstructorDelay(IntrinsicContext ctx, in StackSlot slot) {
            if (slot.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt)
                return slot.AsInt32;
            return TimeSpanMilliseconds(slot);
        }

        static VmCancellationState LiveState(in StackSlot slot) {
            var state = CancellationRuntime.State(slot)
                ?? throw new UnhandledGuestException("System.InvalidOperationException", "CancellationTokenSource が初期化されていません。");
            if (state.IsDisposed)
                throw new UnhandledGuestException("System.ObjectDisposedException", "CancellationTokenSource");
            return state;
        }

        static void Initialize(IntrinsicContext ctx, StackSlot[] args, int delay) {
            var state = EnsureState(ctx, args[0]);
            if (delay < Timeout.Infinite)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "millisecondsDelay");
            if (delay != Timeout.Infinite)
                ctx.Shared.GuestTasks.ScheduleCancellation(state, delay);
        }

        r.Register(IntrinsicKey.Instance(source, ".ctor", 0), (ctx, a) => {
            _ = EnsureState(ctx, a[0]);
            return null;
        });
        r.Register(IntrinsicKey.Instance(source, ".ctor", 1), (ctx, a) => {
            Initialize(ctx, a, ConstructorDelay(ctx, a[1]));
            return null;
        });
        r.RegisterBinding(BindingKey.Instance(source, ".ctor", "System.Int32"),
            (ctx, a) => { Initialize(ctx, a, a[1].AsInt32); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, ".ctor", "System.TimeSpan"),
            (ctx, a) => { Initialize(ctx, a, TimeSpanMilliseconds(a[1])); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "get_Token"), (ctx, a) => {
            return CancellationRuntime.Token(ctx, LiveState(a[0]));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "get_IsCancellationRequested"),
            (ctx, a) => StackSlot.OfInt32(LiveState(a[0]).IsCancellationRequested ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "Cancel"), (ctx, a) => {
            LiveState(a[0]).Cancel();
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "CancelAfter", "System.Int32"), (ctx, a) => {
            var state = LiveState(a[0]);
            var delay = a[1].AsInt32;
            if (delay < Timeout.Infinite)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "millisecondsDelay");
            ctx.Shared.GuestTasks.ScheduleCancellation(state, delay);
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "CancelAfter", "System.TimeSpan"), (ctx, a) => {
            var state = LiveState(a[0]);
            var delay = TimeSpanMilliseconds(a[1]);
            if (delay < Timeout.Infinite)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "delay");
            ctx.Shared.GuestTasks.ScheduleCancellation(state, delay);
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(source, "Dispose"), (ctx, a) => {
            CancellationRuntime.State(a[0])?.Dispose();
            return null;
        }, BindingOrigin.Managed);

        r.RegisterBinding(BindingKey.Instance(token, "get_IsCancellationRequested"),
            (ctx, a) => StackSlot.OfInt32(CancellationRuntime.State(a[0])?.IsCancellationRequested == true ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(token, "get_CanBeCanceled"),
            (ctx, a) => StackSlot.OfInt32(CancellationRuntime.State(a[0]) is not null ? 1 : 0),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(token, "ThrowIfCancellationRequested"), (ctx, a) => {
            if (CancellationRuntime.State(a[0])?.IsCancellationRequested == true)
                throw new UnhandledGuestException("System.OperationCanceledException", null);
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(token, "get_None"),
            static (ctx, _) => CancellationRuntime.EmptyToken(ctx), BindingOrigin.Managed);
    }

    private static void RegisterSynchronizationContext(IntrinsicRegistry r) {
        const string context = "System.Threading.SynchronizationContext";
        const string callback = "System.Threading.SendOrPostCallback";

        r.Register(IntrinsicKey.Instance(context, ".ctor", 0), (ctx, a) => {
            var instance = (VmIntrinsicInstance?)a[0].ObjectValue
                ?? throw new UnhandledGuestException("System.NullReferenceException", null);
            var type = ctx.Types.FindIntrinsicType(context)!;
            instance.State[0] = StackSlot.OfObject(ctx.Heap.Allocate(new VmSynchronizationContextState(type)));
            return null;
        });
        r.RegisterBinding(BindingKey.Static(context, "get_Current"),
            (ctx, _) => ctx.Shared.CurrentSynchronizationContext is { } current
                ? StackSlot.OfObject(current) : StackSlot.Null, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(context, "SetSynchronizationContext", context), (ctx, a) => {
            ctx.Shared.CurrentSynchronizationContext = a[0].ObjectValue as VmObject;
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(context, "CreateCopy"),
            static (_, a) => a[0], BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(context, "OperationStarted"), static (_, _) => null, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(context, "OperationCompleted"), static (_, _) => null, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(context, "Post", callback, "System.Object"),
            (ctx, a) => Post(ctx, a, synchronous: false), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(context, "Send", callback, "System.Object"),
            (ctx, a) => Post(ctx, a, synchronous: true), BindingOrigin.Managed);
    }

    private static StackSlot? Post(IntrinsicContext ctx, StackSlot[] args, bool synchronous) {
        var context = args[0].ObjectValue as VmObject
            ?? throw new UnhandledGuestException("System.NullReferenceException", null);
        var callback = args[1].ObjectValue as VmDelegate
            ?? throw new UnhandledGuestException("System.ArgumentNullException", "d");
        var state = args.Length > 2 ? args[2] : StackSlot.Null;
        var invoke = ctx.InvokeGuestDelegate ?? throw new InvalidOperationException("guest delegate runner が初期化されていません。");
        void Run() => ctx.Shared.RunWithSynchronizationContext(context,
            () => _ = invoke(callback, [StackSlot.OfObject(callback), state]));
        if (synchronous)
            Run();
        else
            ctx.Shared.GuestTasks.Post(context, callback, state, Run);
        return null;
    }
}

internal static class CancellationRuntime {
    internal static VmCancellationState? State(in StackSlot slot) {
        var value = slot.Kind == StackKind.ByRef && slot.ObjectValue is VmByRef byRef
            ? byRef.Read() : slot;
        return value.ObjectValue switch {
            VmStructValue wrapper when wrapper.Fields.Length > 0 &&
                wrapper.Fields[0].ObjectValue is VmCancellationState state => state,
            VmIntrinsicInstance instance when instance.State.Length > 0 &&
                instance.State[0].ObjectValue is VmCancellationState state => state,
            _ => null,
        };
    }

    internal static StackSlot Token(IntrinsicContext ctx, VmCancellationState state) {
        var type = ctx.Types.FindIntrinsicType("System.Threading.CancellationToken")!;
        _ = state.SourceToken; // CTS.Token throws after CTS.Dispose; an already-issued token does not.
        return StackSlot.OfValueType(new VmStructValue(type, [StackSlot.OfObject(state)]));
    }

    internal static StackSlot EmptyToken(IntrinsicContext ctx) {
        var type = ctx.Types.FindIntrinsicType("System.Threading.CancellationToken")!;
        return StackSlot.OfValueType(new VmStructValue(type, [default]));
    }
}
