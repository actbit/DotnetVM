using System.Buffers.Binary;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    /// <summary>
    /// IDisposable::Dispose の面。using/foreach の leave 時に呼ばれる。
    /// ゲスト実装があれば Call の仮想ディスパッチが優先されるため、この intrinsic は
    /// 「レシーバにゲスト実装が無かった場合の面」(リソースを持たない 既定 = no-op) となる。
    /// </summary>
    private static void RegisterDisposable(IntrinsicRegistry registry) {
        registry.Register(IntrinsicKey.Instance("System.IDisposable", "Dispose", 0),
            (ctx, a) => null);
    }

    // ---- System.Delegate (デリゲート機構の面。呼出実体は VmDelegate + Interpreter.InvokeDelegate) ----

    /// <summary>イベント (+=/-=) とデリゲート比較 (==/!=) がコンパイルされる Delegate 面の登録。
    /// Combine/Remove は CLR と同じく新しいインスタンスを返す (実質イミュータブル)。</summary>
    private static void RegisterDelegates(IntrinsicRegistry registry) {
        registry.Register(IntrinsicKey.Static("System.Delegate", "Combine", 2),
            static (ctx, a) => CombineDelegates(ctx, a[0], a[1]));
        registry.Register(IntrinsicKey.Static("System.Delegate", "Combine", 3),
            static (ctx, a) => CombineDelegates(ctx, CombineDelegates(ctx, a[0], a[1]), a[2]));
        registry.Register(IntrinsicKey.Static("System.Delegate", "Remove", 2),
            static (ctx, a) => RemoveDelegates(ctx, a[0], a[1], all: false));
        registry.Register(IntrinsicKey.Static("System.Delegate", "RemoveAll", 2),
            static (ctx, a) => RemoveDelegates(ctx, a[0], a[1], all: true));
        registry.Register(IntrinsicKey.Static("System.Delegate", "op_Equality", 2),
            static (_, a) => StackSlot.OfInt32(DelegatesEqual(a[0], a[1]) ? 1 : 0));
        registry.Register(IntrinsicKey.Static("System.Delegate", "op_Inequality", 2),
            static (_, a) => StackSlot.OfInt32(DelegatesEqual(a[0], a[1]) ? 0 : 1));
        registry.Register(IntrinsicKey.Instance("System.Delegate", "Equals", 1),
            static (_, a) => StackSlot.OfInt32(DelegatesEqual(a[0], a[1]) ? 1 : 0));
    }

    /// <summary>スロットをデリゲートの呼出エントリ列に解決する。null スロットは null。</summary>
    private static DelegateInvocation[]? InvocationListOf(StackSlot slot) => slot.ObjectValue switch {
        VmDelegate @delegate => @delegate.CopyInvocations(),
        null => null,
        _ => throw new UnhandledGuestException("System.ArgumentException",
            "Delegate 操作の引数がデリゲートではありません。"),
    };

    /// <summary>System.Threading.Interlocked 面。フィールド風イベントの add/remove でコンパイラが
    /// 生成する CompareExchange&lt;T&gt; (MethodSpec 経由・ジェネリック引数はキーに含まれない) を含む。
    /// 第 1 引数は常に ref → ByRef スロット経由で読み書きする。VM の共有 atomic gate と
    /// スロットロックを使い、ゲストスレッド / 並列ホスト呼出の間でも比較交換を原子的に行う。</summary>
    private static void RegisterInterlocked(IntrinsicRegistry registry) {
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

        // CompareExchange<T>(ref T, T, T) → 旧値
        registry.Register(IntrinsicKey.Static(T, "CompareExchange", 3), static (_, a) => {
            var loc = Location(a[0], "CompareExchange");
            var original = loc.Slot;
            if (SlotEquals(original, a[2]))
                loc.Slot = a[1];
            return original;
        });
        // CompareExchange<T>(ref T, T, T, out bool succeeded) → 旧値
        registry.Register(IntrinsicKey.Static(T, "CompareExchange", 4), static (_, a) => {
            var loc = Location(a[0], "CompareExchange");
            var original = loc.Slot;
            var equal = SlotEquals(original, a[2]);
            if (equal)
                loc.Slot = a[1];
            if (a[3].ObjectValue is VmByRef succeeded)
                succeeded.Write(StackSlot.OfInt32(equal ? 1 : 0));
            return original;
        });
        // Exchange<T>(ref T, T) → 旧値
        registry.Register(IntrinsicKey.Static(T, "Exchange", 2), static (_, a) => {
            var loc = Location(a[0], "Exchange");
            var original = loc.Slot;
            loc.Slot = a[1];
            return original;
        });
        // Add(ref int/long, n) → 新値
        registry.Register(IntrinsicKey.Static(T, "Add", 2), static (_, a) => {
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
        registry.Register(IntrinsicKey.Static(T, "Increment", 1), static (_, a) => {
            var loc = Location(a[0], "Increment");
            var updated = loc.Slot.Kind == StackKind.Int64
                ? StackSlot.OfInt64(loc.Slot.Int64Value + 1)
                : StackSlot.OfInt32((int)loc.Slot.Int64Value + 1);
            loc.Slot = updated;
            return updated;
        });
        registry.Register(IntrinsicKey.Static(T, "Decrement", 1), static (_, a) => {
            var loc = Location(a[0], "Decrement");
            var updated = loc.Slot.Kind == StackKind.Int64
                ? StackSlot.OfInt64(loc.Slot.Int64Value - 1)
                : StackSlot.OfInt32((int)loc.Slot.Int64Value - 1);
            loc.Slot = updated;
            return updated;
        });
        // And/Or (ref int/long, n) → 旧値
        registry.Register(IntrinsicKey.Static(T, "And", 2), static (_, a) => {
            var loc = Location(a[0], "And");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value & a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value & a[1].AsInt32);
            return original;
        });
        registry.Register(IntrinsicKey.Static(T, "Or", 2), static (_, a) => {
            var loc = Location(a[0], "Or");
            var original = loc.Slot;
            loc.Slot = original.Kind == StackKind.Int64
                ? StackSlot.OfInt64(original.Int64Value | a[1].Int64Value)
                : StackSlot.OfInt32((int)original.Int64Value | a[1].AsInt32);
            return original;
        });
        registry.Register(IntrinsicKey.Static(T, "MemoryBarrier", 0), static (_, _) => { Thread.MemoryBarrier(); return null; });
        registry.Register(IntrinsicKey.Static(T, "ReadMemoryBarrier", 0), static (_, _) => { Thread.MemoryBarrier(); return null; });
        registry.Register(IntrinsicKey.Static(T, "WriteMemoryBarrier", 0), static (_, _) => { Thread.MemoryBarrier(); return null; });
    }

    private static StackSlot CombineDelegates(IntrinsicContext ctx, StackSlot left, StackSlot right) {
        // CLR 規約: 片側が null なら他方をそのまま返す (両側 null は null)
        var leftList = InvocationListOf(left);
        var rightList = InvocationListOf(right);
        if (leftList is null)
            return right;
        if (rightList is null)
            return left;
        var combined = new VmDelegate { DeclaredType = ((VmDelegate)left.ObjectValue!).DeclaredType };
        foreach (var invocation in leftList)
            combined.AddInvocation(invocation);
        foreach (var invocation in rightList)
            combined.AddInvocation(invocation);
        return StackSlot.OfObject(ctx.Heap.Allocate(combined));
    }

    /// <summary>Delegate.Remove / RemoveAll。value の呼出リストと一致する部分列を末尾側から除去する。
    /// 全エントリが消えた場合 CLR 規約どおり null を返す。</summary>
    private static StackSlot RemoveDelegates(IntrinsicContext ctx, StackSlot source, StackSlot value, bool all) {
        var sourceList = InvocationListOf(source);
        var valueList = InvocationListOf(value);
        if (sourceList is null || valueList is null || valueList.Length == 0)
            return source;
        var remaining = new List<DelegateInvocation>(sourceList);
        var removed = false;
        while (TryRemoveLastSubsequence(remaining, valueList)) {
            removed = true;
            if (!all)
                break;
        }
        if (!removed)
            return source;
        if (remaining.Count == 0)
            return StackSlot.Null;
        var result = new VmDelegate { DeclaredType = ((VmDelegate)source.ObjectValue!).DeclaredType };
        foreach (var invocation in remaining)
            result.AddInvocation(invocation);
        return StackSlot.OfObject(ctx.Heap.Allocate(result));
    }

    private static bool TryRemoveLastSubsequence(List<DelegateInvocation> list, DelegateInvocation[] pattern) {
        for (var start = list.Count - pattern.Length; start >= 0; start--) {
            var matched = true;
            for (var i = 0; i < pattern.Length && matched; i++)
                matched = SameInvocation(list[start + i], pattern[i]);
            if (matched) {
                list.RemoveRange(start, pattern.Length);
                return true;
            }
        }
        return false;
    }

    private static bool DelegatesEqual(StackSlot left, StackSlot right) {
        var leftList = InvocationListOf(left);
        var rightList = InvocationListOf(right);
        if (leftList is null || rightList is null)
            return leftList is null && rightList is null;
        if (leftList.Length != rightList.Length)
            return false;
        for (var i = 0; i < leftList.Length; i++)
            if (!SameInvocation(leftList[i], rightList[i]))
                return false;
        return true;
    }

    /// <summary>呼出エントリの同一性 (束縛先メソッド + レシーバ)。</summary>
    private static bool SameInvocation(DelegateInvocation x, DelegateInvocation y) {
        if (!ReferenceEquals(x.Method, y.Method))
            return false;
        return x.Target.Kind == y.Target.Kind &&
            x.Target.Int64Value == y.Target.Int64Value &&
            x.Target.DoubleValue == y.Target.DoubleValue &&
            ReferenceEquals(x.Target.ObjectValue, y.Target.ObjectValue);
    }

    // ---- System.Runtime.CompilerServices.RuntimeHelpers ----

    /// <summary>配列初期化子 (ldtoken Field + InitializeArray) の面。FieldRVA データを要素に展開する。</summary>
    private static void RegisterRuntimeHelpers(IntrinsicRegistry registry) {
        registry.Register(IntrinsicKey.Static(
                "System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray", 2),
            static (_, a) => {
                if (a[0].ObjectValue is not VmArray array)
                    throw new InvalidOperationException("InitializeArray の第1引数が配列ではありません。");
                if (a[1].ObjectValue is not VmFieldRvaData handle)
                    throw new InvalidOperationException("InitializeArray の第2引数がフィールドハンドルではありません。");
                CopyInitializerData(array, handle.Data.Span);
                return null;
            });
    }

    /// <summary>FieldRVA 初期データを配列要素へリトルエンディアンで展開する。</summary>
    private static void CopyInitializerData(VmArray array, ReadOnlySpan<byte> data) {
        var offset = 0;
        for (var i = 0; i < array.Length; i++) {
            var size = 0;
            switch (array.ArrayType.ElementType.FullName) {
                case "System.Byte":
                    array.Elements[i] = StackSlot.OfInt32(data[offset]);
                    size = 1;
                    break;
                case "System.SByte":
                    array.Elements[i] = StackSlot.OfInt32((sbyte)data[offset]);
                    size = 1;
                    break;
                case "System.Boolean":
                    array.Elements[i] = StackSlot.OfInt32(data[offset] != 0 ? 1 : 0);
                    size = 1;
                    break;
                case "System.Char":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.Int16":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.UInt16":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.Int32":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
                    size = 4;
                    break;
                case "System.UInt32":
                    array.Elements[i] = StackSlot.OfInt32(unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])));
                    size = 4;
                    break;
                case "System.Int64":
                    array.Elements[i] = StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(data[offset..]));
                    size = 8;
                    break;
                case "System.UInt64":
                    array.Elements[i] = StackSlot.OfInt64(unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])));
                    size = 8;
                    break;
                case "System.Single":
                    array.Elements[i] = StackSlot.OfFloat(BinaryPrimitives.ReadSingleLittleEndian(data[offset..]));
                    size = 4;
                    break;
                case "System.Double":
                    array.Elements[i] = StackSlot.OfFloat(BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]));
                    size = 8;
                    break;
                default:
                    throw new NotSupportedException(
                        $"InitializeArray はプリミティブ要素配列のみ対応しています ({array.ArrayType.ElementType.FullName})。");
            }
            offset += size;
        }
        if (offset > data.Length)
            throw new BadImageFormatException(
                $"FieldRVA 初期データが不足しています (必要 {offset} バイト, 実際 {data.Length} バイト)。");
    }
}
