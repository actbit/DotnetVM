using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Threading.Interlocked (JIT intrinsic 面) ----

    /// <summary>本家 Interlocked は IL 本体が JIT intrinsic ダミー (typeof(T); throw new
    /// PlatformNotSupportedException()) のため、② IL 実行に落ちるとダミー本体が表出する。
    /// 実 CLR では JIT が命令列へ置換するため IL は決して実行されない (監査表 (c) jit-intrinsic)。
    /// legacy intrinsic (③) は IL 本体なしの面しか受けないため、ここに ① バインドで登録する。
    /// VM の共有 atomic gate と参照スロットロックで比較と交換を原子的に行う (CLR と同じ意味論)。</summary>
    private static void RegisterInterlockedBindings(IntrinsicRegistry r) {
        const string T = "System.Threading.Interlocked";
        static VmByRef Location(StackSlot slot, string method) =>
            slot.ObjectValue as VmByRef
            ?? throw new UnhandledGuestException("System.ArgumentException",
                $"Interlocked.{method} の第 1 引数は ref フィールド (ByRef) である必要があります。");
        // 比較対象の等価判定 (プリミティブは値、参照は同一性)
        static bool SlotEquals(in StackSlot x, in StackSlot y) {
            if (x.Kind != y.Kind)
                return false;
            return x.Kind switch {
                StackKind.Empty => true,
                StackKind.Object or StackKind.ByRef => ReferenceEquals(x.ObjectValue, y.ObjectValue),
                StackKind.Float => x.DoubleValue.Equals(y.DoubleValue),
                _ => x.Int64Value == y.Int64Value,
            };
        }
        // ---- Interlocked 面 (タスク 2 hardening: AnyParams → .NET 10 既知署名の列挙)。
        // 本家 CoreLib v10 の Interlocked 公開面を実署名列挙で登録する (wildecard 廃止)。
        // パラメータ型名は VM 表現と CoreLib 署名の実型 (統合後) 完全名で鍵化する。
        // i4 統合面 (byte/sbyte/short/ushort/bool/char/int が i4 スロットに載る) は 1 面で受け、
        // i8 / float / double / nint / nuint は別キーで鍵化
        const string I4 = "System.Int32", I8 = "System.Int64";
        const string Ref1 = "&";

        // Increment(ref T) / Decrement(ref T): (ref int,int) と (ref long,long) の両面.
        foreach (var (ty, tyName) in new[] { (I4, "System.Int32"), (I8, "System.Int64") }) {
            var vt = tyName;
            r.RegisterBinding(BindingKey.StaticWithReturn(T, "Increment", vt, [vt + Ref1]), static (_, a) => {
                var loc = Location(a[0], "Increment");
                var updated = loc.Slot.Kind == StackKind.Int64
                    ? StackSlot.OfInt64(loc.Slot.Int64Value + 1)
                    : StackSlot.OfInt32((int)loc.Slot.Int64Value + 1);
                loc.Slot = updated;
                return updated;
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.StaticWithReturn(T, "Decrement", vt, [vt + Ref1]), static (_, a) => {
                var loc = Location(a[0], "Decrement");
                var updated = loc.Slot.Kind == StackKind.Int64
                    ? StackSlot.OfInt64(loc.Slot.Int64Value - 1)
                    : StackSlot.OfInt32((int)loc.Slot.Int64Value - 1);
                loc.Slot = updated;
                return updated;
            }, BindingOrigin.InternalCall);
        }
        // Exchange / CompareExchange / Add / And / Or: int / long / uint / ulong / float / double / nint / nuint 面水準
        static void RegExchangeFamily(IntrinsicRegistry reg, string type, IntrinsicImpl exchange, IntrinsicImpl compareExchange, IntrinsicImpl add)
        {
            foreach (var param in new[] {
                // ref int, int
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
                new[] { "System.Single&", "System.Single" },
                new[] { "System.Double&", "System.Double" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "Exchange", param[0].TrimEnd('&'), param), exchange, BindingOrigin.InternalCall);
            }
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32", "System.Int32" },
                new[] { "System.Int64&", "System.Int64", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64", "System.UInt64" },
                new[] { "System.Single&", "System.Single", "System.Single" },
                new[] { "System.Double&", "System.Double", "System.Double" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "CompareExchange", param[0].TrimEnd('&'), param), compareExchange, BindingOrigin.InternalCall);
            }
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, "Add", param[0].TrimEnd('&'), param), add, BindingOrigin.InternalCall);
            }
        }
        var exchangeImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Exchange");
            var original = loc.Slot;
            loc.Slot = a[1];
            return original;
        });
        var compareExchange = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "CompareExchange");
            var original = loc.Slot;
            var equal = SlotEquals(original, a[2]);
            if (equal)
                loc.Slot = a[1];
            if (a.Length >= 4 && a[3].ObjectValue is VmByRef succeeded)
                succeeded.Write(StackSlot.OfInt32(equal ? 1 : 0));
            return original;
        });
        var addImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Add");
            StackSlot updated;
            if (loc.Slot.Kind == StackKind.Int64) {
                updated = StackSlot.OfInt64(loc.Slot.Int64Value + a[1].Int64Value);
            } else {
                int sum;
                try { sum = checked((int)loc.Slot.Int64Value + a[1].AsInt32); }
                catch (OverflowException) { throw new UnhandledGuestException("System.OverflowException", null); }
                updated = StackSlot.OfInt32(sum);
            }
            loc.Slot = updated;
            return updated;
        });
        RegExchangeFamily(r, T, exchangeImpl, compareExchange, addImpl);

        // 参照型 CompareExchange<T>/Exchange<T> ( CultureInfo 初期化等の lock-free 起動経路)。
        // 本家はジェネリック面のため開いたキー (!!0) で 1 件ずつ登録する。共有 atomic gate
        // の中で参照同一性 (SlotEquals の ReferenceEquals 側) による比較交換を行う。
        // 値型の具体面は上の正確キーが先に一致する
        r.RegisterBinding(BindingKey.Static(T, "Exchange", "!!0&", "!!0"),
            exchangeImpl, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "CompareExchange", "!!0&", "!!0", "!!0"),
            compareExchange, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(T, "CompareExchange", "!!0&", "!!0", "!!0", "System.Boolean&"),
            compareExchange, BindingOrigin.InternalCall);

        static void RegAndOrFamily(IntrinsicRegistry reg, string type, string method, IntrinsicImpl impl)
        {
            foreach (var param in new[] {
                new[] { "System.Int32&", "System.Int32" },
                new[] { "System.Int64&", "System.Int64" },
                new[] { "System.UInt32&", "System.UInt32" },
                new[] { "System.UInt64&", "System.UInt64" },
            }) {
                reg.RegisterBinding(BindingKey.StaticWithReturn(T, method, param[0].TrimEnd('&'), param), impl, BindingOrigin.InternalCall);
            }
        }
        var andImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "And");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value & a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value & a[1].AsInt32);
            return original;
        });
        var orImpl = (IntrinsicImpl)((_, a) => {
            var loc = Location(a[0], "Or");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value | a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value | a[1].AsInt32);
            return original;
        });
        RegAndOrFamily(r, T, "And", andImpl);
        RegAndOrFamily(r, T, "Or", orImpl);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "MemoryBarrier", "System.Void", Array.Empty<string>()),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ReadMemoryBarrier", "System.Void", []),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "WriteMemoryBarrier", "System.Void", []),
            static (_, _) => { Thread.MemoryBarrier(); return null; }, BindingOrigin.InternalCall);
    }
}
