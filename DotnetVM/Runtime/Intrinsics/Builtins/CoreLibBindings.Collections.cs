using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Array (コア面: 全 overload がここへ集約される) ----

    /// <summary>Array のコア面 (Sort 5 引数 / Reverse 3 引数 / IndexOf 4 引数 / Copy 5 引数)。
    /// 本家は全 overload をここへ集約する (List.Sort / Array.Sort(T[]) 等の実 IL が辿る先)。
    /// 実 IL は introsort / 比較子生成 (CreateArraySortHelper) / MethodTable 内部表現
    /// (CopyImpl) で構成され VM 表現境界のため、同一意味論を直接提供する:
    /// 既定順序はプリミティブ数値 + guest 呼出スコープの CurrentCulture による文字列比較。カスタム
    /// IComparer がある面はゲスト委譲機構が無いため fail-closed (ホスト例外)。
    /// 多次元配列は RankException (SZArray のみ対応)。</summary>
    private static void RegisterArrayBindings(IntrinsicRegistry r) {
        const string T = "System.Array";
        r.RegisterBinding(BindingKey.Static(T, "Sort",
                "System.Array", "System.Array", "System.Int32", "System.Int32", "System.Collections.IComparer"),
            static (ctx, a) => { SortImpl(ctx, a[0], a[1], a[2].AsInt32, a[3].AsInt32, a[4]); return null; }, BindingOrigin.Managed);
        // ジェネリック Sort (List.Sort / Array.Sort(T[]) 等が辿る実面。比較子生成の
        // ランタイム内部 (CreateArraySortHelper) を迂回し既定順序を直接提供する)
        r.RegisterBinding(BindingKey.Static(T, "Sort", "!!0[]"),
            static (ctx, a) => { SortImpl(ctx, a[0], StackSlot.Null, 0, RequireSzArray(a[0], "Array.Sort").Length, StackSlot.Null); return null; }, BindingOrigin.Managed);
        // Sort(T[], int, int, IComparer<T>): 呼出元の文脈で型変数の綴り (!!0 / !0) が
        // 変わるため両形を登録する (実引数は実行時に判別する)
        foreach (var comparerParam in new[] {
            "System.Collections.Generic.IComparer`1<!!0>",
            "System.Collections.Generic.IComparer`1<!0>",
        })
            r.RegisterBinding(BindingKey.Static(T, "Sort", "!!0[]", "System.Int32", "System.Int32", comparerParam),
                static (ctx, a) => { SortImpl(ctx, a[0], StackSlot.Null, a[1].AsInt32, a[2].AsInt32, a[3]); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Reverse",
                "System.Array", "System.Int32", "System.Int32"),
            static (_, a) => { ReverseImpl(a); return null; }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "IndexOf",
                "System.Array", "System.Object", "System.Int32", "System.Int32"),
            static (_, a) => StackSlot.OfInt32(IndexOfImpl(a)), BindingOrigin.Managed);
        // ジェネリック IndexOf<T>(T[], T, int, int) (List<T>.Contains/IndexOf 等が辿る実面。
        // SpanHelpers の SIMD/static-abstract 依存を迂回し同一意味論を直接提供する)
        r.RegisterBinding(BindingKey.Static(T, "IndexOf",
                "!!0[]", "!!0", "System.Int32", "System.Int32"),
            static (_, a) => StackSlot.OfInt32(IndexOfImpl(a)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Copy",
                "System.Array", "System.Int32", "System.Array", "System.Int32", "System.Int32"),
            static (_, a) => { CopyImpl(a); return null; }, BindingOrigin.Managed);
    }

    private static VmArray RequireSzArray(in StackSlot slot, string face) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is null)
            throw new UnhandledGuestException("System.ArgumentNullException", null);
        return slot.ObjectValue is VmArray array
            ? array
            : throw new UnhandledGuestException("System.RankException",
                $"{face} は 1 次元配列 (SZArray) のみ対応しています。");
    }

    private static void CheckRange(int index, int length, int arrayLength, string face) {
        if (index < 0 || length < 0)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
        if (index + length > arrayLength)
            throw new UnhandledGuestException("System.ArgumentException",
                $"{face} の範囲 (index={index}, length={length}) が配列長 {arrayLength} を超えています。");
    }

    private static void SortImpl(IntrinsicContext ctx, in StackSlot keysSlot, in StackSlot itemsSlot,
        int index, int length, in StackSlot comparerSlot) {
        var keys = RequireSzArray(keysSlot, "Array.Sort");
        VmArray? items = itemsSlot.ObjectValue is null ? null : RequireSzArray(itemsSlot, "Array.Sort");
        CheckRange(index, length, keys.Length, "Array.Sort");
        if (items is not null)
            CheckRange(index, length, items.Length, "Array.Sort");
        if (comparerSlot.ObjectValue is not null)
            throw new NotSupportedException(
                "Array.Sort のカスタム IComparer 面は未対応です (ゲスト比較子への委譲機構が無いため fail-closed)。");
        if (length <= 1)
            return;
        var elementType = keys.ArrayType.ElementType.FullName;
        var order = new int[length];
        for (var i = 0; i < length; i++)
            order[i] = i;
        var elements = keys.Elements;
        Array.Sort(order, (x, y) => CompareElement(ctx, elements[index + x], elements[index + y], elementType));
        var sortedKeys = new StackSlot[length];
        for (var i = 0; i < length; i++)
            sortedKeys[i] = elements[index + order[i]];
        for (var i = 0; i < length; i++)
            elements[index + i] = sortedKeys[i];
        if (items is not null) {
            var itemElements = items.Elements;
            var sortedItems = new StackSlot[length];
            for (var i = 0; i < length; i++)
                sortedItems[i] = itemElements[index + order[i]];
            for (var i = 0; i < length; i++)
                itemElements[index + i] = sortedItems[i];
        }
    }

    private static void ReverseImpl(StackSlot[] a) {
        var array = RequireSzArray(a[0], "Array.Reverse");
        var index = a[1].AsInt32;
        var length = a[2].AsInt32;
        CheckRange(index, length, array.Length, "Array.Reverse");
        var elements = array.Elements;
        for (var i = 0; i < length / 2; i++)
            (elements[index + i], elements[index + length - 1 - i]) =
                (elements[index + length - 1 - i], elements[index + i]);
    }

    private static int IndexOfImpl(StackSlot[] a) {
        var array = RequireSzArray(a[0], "Array.IndexOf");
        var start = a[2].AsInt32;
        var count = a[3].AsInt32;
        CheckRange(start, count, array.Length, "Array.IndexOf");
        var elements = array.Elements;
        for (var i = 0; i < count; i++) {
            if (SlotsEqual(elements[start + i], a[1]))
                return start + i;
        }
        return -1;
    }

    private static void CopyImpl(StackSlot[] a) {
        var src = RequireSzArray(a[0], "Array.Copy");
        var srcIndex = a[1].AsInt32;
        var dst = RequireSzArray(a[2], "Array.Copy");
        var dstIndex = a[3].AsInt32;
        var length = a[4].AsInt32;
        CheckRange(srcIndex, length, src.Length, "Array.Copy");
        CheckRange(dstIndex, length, dst.Length, "Array.Copy");
        var srcName = src.ArrayType.ElementType.FullName;
        var dstName = dst.ArrayType.ElementType.FullName;
        if (VmPrimitiveTypes.IsSlotPrimitive(srcName) || VmPrimitiveTypes.IsSlotPrimitive(dstName)) {
            // プリミティブ配列は完全一致のみ (int[]→uint[] 等の同一幅も CLR は拒否する)
            if (!string.Equals(srcName, dstName, StringComparison.Ordinal))
                throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                    $"Array.Copy の要素型が一致しません ({srcName}[] → {dstName}[])。");
            Array.Copy(src.Elements, srcIndex, dst.Elements, dstIndex, length);
            return;
        }
        // 参照配列は要素ごとに共変検査 (不一致はその場で ArrayTypeMismatch)
        for (var i = 0; i < length; i++) {
            var value = src.Elements[srcIndex + i];
            if (value.ObjectValue is not null &&
                !TypeChecks.IsAssignableToType(value.ObjectValue, dst.ArrayType.ElementType))
                throw new UnhandledGuestException("System.ArrayTypeMismatchException",
                    $"Array.Copy の要素 {i} を {dstName} に格納できません。");
        }
        Array.Copy(src.Elements, srcIndex, dst.Elements, dstIndex, length);
    }

    private static bool IsNullSlot(in StackSlot slot) =>
        slot.Kind == StackKind.Object && slot.ObjectValue is null;

    /// <summary>既定順序の要素比較 (CLR の既定比較子と同一順序)。
    /// 数値は符号どおり、浮動小数点は host CompareTo (NaN 順序を含む)、文字列は
    /// guest 呼出スコープの CurrentCulture。box 化プリミティブは開いて比較する。構造体等は fail-closed。</summary>
    private static int CompareElement(IntrinsicContext ctx, in StackSlot x, in StackSlot y, string elementType) {
        var (xv, xn) = UnwrapForCompare(x, elementType);
        var (yv, yn) = UnwrapForCompare(y, elementType);
        if (IsNullSlot(xv) || IsNullSlot(yv)) {
            if (IsNullSlot(xv) && IsNullSlot(yv))
                return 0;
            return IsNullSlot(xv) ? -1 : 1; // null は先頭 (CLR 規約)
        }
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys) {
            ctx.Heap.ChargeHostWork((long)xs.Value.Length + ys.Value.Length);
            return string.Compare(xs.Value, ys.Value, StringComparison.CurrentCulture);
        }
        if (xv.Kind is StackKind.Object or StackKind.ValueType or StackKind.ByRef ||
            yv.Kind is StackKind.Object or StackKind.ValueType or StackKind.ByRef)
            throw new InvalidOperationException(
                $"Array.Sort の要素 ({SlotKindName(xv)}, {SlotKindName(yv)}) は既定順序に対応していません。");
        var name = xn ?? yn ?? elementType;
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue.CompareTo(yv.DoubleValue);
        var signed = name is not ("System.UInt32" or "System.UInt64");
        if (!signed)
            return ((ulong)xv.Int64Value).CompareTo((ulong)yv.Int64Value);
        return xv.Int64Value.CompareTo(yv.Int64Value);
    }

    private static (StackSlot Slot, string? TypeName) UnwrapForCompare(in StackSlot slot, string elementType) {
        if (slot.ObjectValue is VmBoxedValue box)
            return (box.Fields[0], box.Type.FullName);
        return (slot, elementType);
    }

    private static bool SlotsEqual(in StackSlot x, in StackSlot y) {
        var (xv, _) = UnwrapForCompare(x, "");
        var (yv, _) = UnwrapForCompare(y, "");
        if (IsNullSlot(xv) || IsNullSlot(yv))
            return IsNullSlot(xv) && IsNullSlot(yv);
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys)
            return string.Equals(xs.Value, ys.Value, StringComparison.Ordinal);
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue.Equals(yv.DoubleValue);
        if (xv.Kind == StackKind.Object || yv.Kind == StackKind.Object)
            return ReferenceEquals(xv.ObjectValue, yv.ObjectValue);
        return xv.Int64Value == yv.Int64Value;
    }

    private static string SlotKindName(in StackSlot slot) =>
        slot.Kind + (slot.ObjectValue is null ? "" : ":" + slot.ObjectValue.GetType().Name);

    /// <summary>== 演算子の観測意味論 (IEqualityOperators.op_Equality 用)。
    /// box 化値は開いて比較する。浮動小数点は == (NaN ペアは false)、文字列は ordinal。</summary>
    private static bool OperatorEqual(in StackSlot x, in StackSlot y) {
        var (xv, _) = UnwrapForCompare(x, "");
        var (yv, _) = UnwrapForCompare(y, "");
        if (IsNullSlot(xv) || IsNullSlot(yv))
            return IsNullSlot(xv) && IsNullSlot(yv);
        if (xv.ObjectValue is VmString xs && yv.ObjectValue is VmString ys)
            return string.Equals(xs.Value, ys.Value, StringComparison.Ordinal);
        if (xv.Kind == StackKind.Float || yv.Kind == StackKind.Float)
            return xv.DoubleValue == yv.DoubleValue;
        if (xv.Kind == StackKind.Object || yv.Kind == StackKind.Object ||
            xv.Kind == StackKind.ValueType || yv.Kind == StackKind.ValueType ||
            xv.Kind == StackKind.ByRef || yv.Kind == StackKind.ByRef)
            return ReferenceEquals(xv.ObjectValue, yv.ObjectValue);
        return xv.Int64Value == yv.Int64Value;
    }

    private static bool HasFlagImpl(StackSlot[] a) {
        var thisBox = a[0].ObjectValue as VmBoxedValue;
        var flagBox = a[1].ObjectValue as VmBoxedValue;
        // CLR の型検査 (GetType() != flag.GetType() で ArgumentException) に対応。
        // ボックス化されていない面の呼び出しは形状違反として同様に扱う
        if (thisBox is null || flagBox is null ||
            !string.Equals(thisBox.Type.FullName, flagBox.Type.FullName, StringComparison.Ordinal))
            throw new UnhandledGuestException("System.ArgumentException", null);
        var thisBits = RawBits(thisBox);
        var flagBits = RawBits(flagBox);
        return (thisBits & flagBits) == flagBits;
    }

    private static ulong RawBits(VmBoxedValue box) =>
        box.Fields[0].Kind switch {
            StackKind.Int64 => (ulong)box.Fields[0].Int64Value,
            _ => (ulong)box.Fields[0].AsInt32,
        };


    // ---- System.Buffers.ArrayPool<T> (ValueStringBuilder 等の作業域プール) ----

    /// <summary>ArrayPool&lt;T&gt;.Shared / Rent / Return の同等意味論。
    /// 本家は TlsOverPerCoreLockedStacksArrayPool (スレッド局所スタック + EventSource 診断)
    /// で構成され、初回利用で ArrayPoolEventSource (ETW) の静的初期化まで辿るため
    /// VM 表現境界。プールは性能機構であり意味論は「要求長以上の新規配列」なので、
    /// 要求長どおりの新規確保で代替する (再利用しないため Return は no-op。
    /// 再利用の有無は観測不能)。Shared 実体は不透明なプレースホルダ (Rent/Return は
    /// バインドが状態を見ずに処理するため .ctor 不実行で足りる)。</summary>
    private static void RegisterArrayPool(IntrinsicRegistry r) {
        const string T = "System.Buffers.ArrayPool`1";
        r.RegisterBinding(BindingKey.Static(T, "get_Shared"),
            static (ctx, a) => {
                _ = a;
                var tName = ctx.ClassTypeArgAt(0);
                if (string.IsNullOrEmpty(tName) || tName is "!!0" or "!0")
                    throw new InvalidOperationException(
                        "ArrayPool<T>.Shared の型引数 T を判別できませんでした。");
                var t = FindAnyType(ctx, tName)
                    ?? throw new InvalidOperationException(
                        $"ArrayPool<T>.Shared の型引数 {tName} を解決できません。");
                var def = FindAnyType(ctx, T) as VmClassType
                    ?? throw new InvalidOperationException(
                        "System.Buffers.ArrayPool`1 がロードされていません。");
                var storage = new ObjectModel().CreateInstanceStorage(def, ctx.Types,
                    new GenericContext { ClassArgs = [t] });
                return StackSlot.OfObject(ctx.Heap.Allocate(
                    new VmClassInstance(def, storage, [t])));
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance(T, "Rent", "System.Int32"),
            static (ctx, a) => {
                var length = a[1].AsInt32;
                if (length < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var element = RentElementType(a[0]);
                var elements = new StackSlot[length];
                var fill = element is not null && element.IsValueType
                    ? new ObjectModel().DefaultForType(element, ctx.Types)
                    : StackSlot.Null;
                for (var i = 0; i < length; i++)
                    elements[i] = fill;
                var arrayType = new VmArrayType {
                    ElementType = element ?? ctx.Types.ResolveWellKnownType("System.Object"),
                };
                try {
                    arrayType.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
                } catch {
                }
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(arrayType, elements)));
            },
            BindingOrigin.InternalCall);
        // クラスジェネリック面のため開いたキーはクラス変数 (!0) 形
        // (メソッド変数 !!0 ではない。TryInvokeOpenGenericBinding が !0 で再照合する)
        r.RegisterBinding(BindingKey.Instance(T, "Return", "!0[]", "System.Boolean"),
            static (_, _) => null,
            BindingOrigin.InternalCall);
        // .NET 10 追加面: Return(T[], int lengthToClear) (使用済み範囲のみクリアして返却。
        // ValueStringBuilder.Dispose が辿る。プール再利用しないため検証のみで no-op)。
        // instance 面のため a[0] はレシーバ (プール実体) で a[1]/a[2] が引数
        r.RegisterBinding(BindingKey.Instance(T, "Return", "!0[]", "System.Int32"),
            static (_, a) => {
                if (a[1].ObjectValue is not VmArray array)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                var lengthToClear = a[2].AsInt32;
                if (lengthToClear < 0 || lengthToClear > array.Length)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                return null;
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>Rent 呼出のレシーバ (ArrayPool&lt;T&gt; 実体) から要素型を取り出す。
    /// Shared プレースホルダは構築型の実引数を保持している。不明時は null。</summary>
    private static VmType? RentElementType(in StackSlot receiver) {
        if (receiver.ObjectValue is VmClassInstance instance && instance.TypeArguments.Length > 0)
            return instance.TypeArguments[0];
        return null;
    }
}
