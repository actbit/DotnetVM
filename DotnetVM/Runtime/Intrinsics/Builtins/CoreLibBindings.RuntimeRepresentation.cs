using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    internal static readonly string[] AdditionalIsaCapabilities = typeof(object).Assembly.GetTypes()
        .Where(t => t.Namespace?.StartsWith("System.Runtime.Intrinsics.", StringComparison.Ordinal) == true &&
            t.GetMethod("get_IsSupported", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly) is not null)
        .Select(t => t.FullName!).Except(new[] {
            "System.Runtime.Intrinsics.X86.Sse2", "System.Runtime.Intrinsics.X86.Ssse3", "System.Runtime.Intrinsics.X86.Sse41",
            "System.Runtime.Intrinsics.X86.Avx", "System.Runtime.Intrinsics.X86.Avx2", "System.Runtime.Intrinsics.X86.Lzcnt", "System.Runtime.Intrinsics.X86.Lzcnt+X64",
        }).ToArray();
    // ---- System.Runtime.CompilerServices.RuntimeHelpers (InternalCall 面) ----

    private static void RegisterRuntimeHelpers(IntrinsicRegistry r) {
        RegisterAttributeReflection(r);
        RegisterSerializationReflection(r);
        r.RegisterBinding(BindingKey.Instance("System.Exception", "CaptureDispatchState"), static (ctx, _) => {
            var type = FindAnyType(ctx, "System.Exception+DispatchState")!;
            return StackSlot.OfValueType(new VmStructValue(type, type.Fields.Where(f => !f.IsStatic && !f.IsLiteral).Select(SlotDefaultZero).ToArray()));
        }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Exception", "RestoreDispatchState", "System.Exception+DispatchState&"), static (_, _) => null, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Reflection.Assembly", "Equals", "System.Object"), static (_, a) => StackSlot.OfInt32(a[0].ObjectValue is VmAssemblyObject left && a[1].ObjectValue is VmAssemblyObject right && ReferenceEquals(left.Loader, right.Loader) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Reflection.Assembly", "GetHashCode"), static (ctx, a) => StackSlot.OfInt32(ctx.IdentityHash(((VmAssemblyObject)a[0].ObjectValue!).Loader)), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.ComAwareWeakReference", "PossiblyComObject", "System.Object"), static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Enum", "GetHashCode"), static (_, a) => {
            var value = a[0];
            if (value.ObjectValue is VmByRef reference) value = reference.Read();
            if (value.ObjectValue is VmBoxedValue boxed) value = boxed.Fields[0];
            if (value.ObjectValue is VmStructValue structure) value = structure.Fields[0];
            return StackSlot.OfInt32(value.Int64Value.GetHashCode());
        }, BindingOrigin.InternalCall);
        foreach (var type in new[] { "System.Type", "System.RuntimeType" }) {
            foreach (var property in new[] { "IsGenericType", "IsGenericTypeDefinition", "ContainsGenericParameters", "IsConstructedGenericType" })
                r.RegisterBinding(BindingKey.Instance(type, "get_" + property), (_, a) => {
                    var t = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                    bool Open(VmType v) => v is VmGenericParameterType || v is VmConstructedType c && c.TypeArguments.Any(Open) || v.GenericParamCount > 0 && v is not VmConstructedType;
                    return StackSlot.OfInt32((property switch {
                        "IsGenericType" => t is VmConstructedType || t.GenericParamCount > 0,
                        "IsGenericTypeDefinition" => t is not VmConstructedType && t.GenericParamCount > 0,
                        "IsConstructedGenericType" => t is VmConstructedType,
                        _ => Open(t),
                    }) ? 1 : 0);
                }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "GetGenericArguments"), static (ctx, a) => {
                var t = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                var arguments = t is VmConstructedType c ? c.TypeArguments : Enumerable.Range(0, t.GenericParamCount).Select(i => (VmType)new VmGenericParameterType { Number = i, IsMethodParameter = false }).ToArray();
                return MetadataArray(ctx, "System.Type", arguments.Select(t => DefaultIntrinsics.MakeRuntimeObject(ctx, t)));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "GetGenericTypeDefinition"), static (ctx, a) => {
                var t = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                if (t is VmConstructedType c) t = c.Definition;
                if (t.GenericParamCount == 0) throw new UnhandledGuestException("System.InvalidOperationException", "Type is not generic.");
                return DefaultIntrinsics.MakeRuntimeObject(ctx, t);
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "MakeGenericType", "System.Type[]"), static (ctx, a) => {
                var t = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                var args = ((VmArray)a[1].ObjectValue!).Elements.Select(v => ((VmRuntimeObject)v.ObjectValue!).Target).ToArray();
                if (t is VmConstructedType || t.GenericParamCount == 0 || args.Length != t.GenericParamCount) throw new UnhandledGuestException("System.ArgumentException", "Invalid generic type arguments.");
                return DefaultIntrinsics.MakeRuntimeObject(ctx, new VmConstructedType { Definition = t, TypeArguments = args });
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "GetConstructors", "System.Reflection.BindingFlags"), static (ctx, a) => {
                var t = ((VmRuntimeObject)a[0].ObjectValue!).Target; if (t is VmConstructedType c) t = c.Definition;
                var flags = (BindingFlags)a[1].AsInt32;
                return MetadataArray(ctx, "System.Reflection.ConstructorInfo", t.Methods.Where(m => m.Name == ".ctor" && flags.HasFlag(BindingFlags.Instance) && (m.IsPublic ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic))).Select(m => StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = m }))));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "GetInterfaces"), static (ctx, a) => {
                var found = new Dictionary<string, VmType>();
                void Visit(VmType current) {
                    var c = current as VmConstructedType;
                    if ((c?.Definition ?? current) is not VmClassType definition) return;
                    var context = c is null ? null : new GenericContext { ClassArgs = c.TypeArguments };
                    var image = definition.Image; var loader = definition.Loader!;
                    for (int rid = 1; rid <= image.Tables.GetRowCount(TableKind.InterfaceImpl); rid++) {
                        if (image.Tables.GetRowIndex(TableKind.InterfaceImpl, rid, 0) != definition.TypeDefRid) continue;
                        var token = image.Tables.DecodeCoded(TableKind.InterfaceImpl, rid, 1, CodedIndexKind.TypeDefOrRef);
                        var iface = GenericSubstitutor.Substitute(loader.ResolveToken(new DotnetVM.Metadata.Signatures.SigType(DotnetVM.Metadata.Signatures.SigKind.TypeToken, Token: Token.From(token.Table, token.Rid).Value)), context);
                        if (found.TryAdd(iface.FullName, iface)) Visit(iface);
                    }
                    if (definition.BaseType is { } parent) Visit(GenericSubstitutor.Substitute(parent, context));
                }
                Visit(((VmRuntimeObject)a[0].ObjectValue!).Target);
                using var allocation = ctx.Heap.ReserveArray(found.Count);
                return StackSlot.OfObject(allocation.Commit(new VmArray(new VmArrayType { ElementType = FindAnyType(ctx, "System.Type")! }, found.Values.Select(t => DefaultIntrinsics.MakeRuntimeObject(ctx, t)).ToArray())));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "get_Namespace"), static (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                if (target is VmConstructedType c) target = c.Definition;
                var name = target.FullName.Split('+')[0];
                var dot = name.LastIndexOf('.');
                return dot < 0 ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(name[..dot]));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "GetConstructorImpl", "System.Reflection.BindingFlags", "System.Reflection.Binder", "System.Reflection.CallingConventions", "System.Type[]", "System.Reflection.ParameterModifier[]"), static (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                var definition = target is VmConstructedType c ? c.Definition : target;
                var wanted = ((VmArray)a[4].ObjectValue!).Elements.Select(e => ((VmRuntimeObject)e.ObjectValue!).Target.FullName).ToArray();
                var flags = (BindingFlags)a[1].AsInt32;
                var constructor = definition.Methods.FirstOrDefault(m => m.Name == ".ctor" && (m.IsPublic ? flags.HasFlag(BindingFlags.Public) : flags.HasFlag(BindingFlags.NonPublic)) &&
                    m.Signature.ParamTypes.Length == wanted.Length && m.Signature.ParamTypes.Select(t => (m.Loader ?? ctx.Types).ResolveToken(t).FullName).SequenceEqual(wanted));
                return constructor is null ? StackSlot.Null : StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = constructor, ReflectedType = target }));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "get_Assembly"), static (ctx, a) => {
                var target = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                if (target is VmConstructedType constructed) target = constructed.Definition;
                var loader = (target as VmClassType)?.Loader ?? ctx.Types;
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyObject { Loader = loader }));
            }, BindingOrigin.InternalCall);
            r.RegisterBinding(BindingKey.Instance(type, "get_IsByRefLike"), static (_, a) => {
                var t = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                if (t is VmConstructedType constructed) t = constructed.Definition;
                return StackSlot.OfInt32(t.FullName.StartsWith("System.Span`1", StringComparison.Ordinal) || t.FullName.StartsWith("System.ReadOnlySpan`1", StringComparison.Ordinal) ? 1 : 0);
            }, BindingOrigin.InternalCall);
            foreach (var method in new[] { "IsPointerImpl", "IsByRefImpl", "IsArrayImpl", "HasElementTypeImpl", "IsPrimitiveImpl", "IsCOMObjectImpl" })
                r.RegisterBinding(BindingKey.Instance(type, method), (_, a) => {
                    var target = ((VmRuntimeObject)a[0].ObjectValue!).Target;
                    var answer = method switch {
                        "IsPointerImpl" => target.FullName.EndsWith("*", StringComparison.Ordinal),
                        "IsByRefImpl" => target is VmByRefType,
                        "IsArrayImpl" => target is VmArrayType,
                        "HasElementTypeImpl" => target is VmArrayType or VmByRefType || target.FullName.EndsWith("*", StringComparison.Ordinal),
                        "IsPrimitiveImpl" => target.FullName is "System.Boolean" or "System.Char" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.IntPtr" or "System.UIntPtr",
                        _ => false,
                    };
                    return StackSlot.OfInt32(answer ? 1 : 0);
                }, BindingOrigin.InternalCall);
        }
        const string dependent = "System.Runtime.DependentHandle";
        const string handle = "System.Runtime.InteropServices.GCHandle";
        r.RegisterBinding(BindingKey.Static(handle, "_InternalAlloc", "System.Object", "System.Runtime.InteropServices.GCHandleType"), static (ctx, a) => {
            if ((uint)a[1].AsInt32 > 3) throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "type");
            ctx.Heap.ChargeHostBuffer(64); ctx.Heap.DependentHandles = ctx.Shared.DependentHandles;
            return StackSlot.OfNativeInt(ctx.Shared.DependentHandles.Allocate(a[0], default, a[1].AsInt32 >= 2));
        }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(handle, "InternalGet", "System.IntPtr"), static (ctx, a) => ctx.Shared.DependentHandles.Get(a[0].Int64Value).Target, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(handle, "InternalSet", "System.IntPtr", "System.Object"), static (ctx, a) => { ctx.Shared.DependentHandles.SetTarget(a[0].Int64Value, a[1]); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(handle, "InternalCompareExchange", "System.IntPtr", "System.Object", "System.Object"), static (ctx, a) => ctx.Shared.DependentHandles.CompareExchange(a[0].Int64Value, a[1], a[2]), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(handle, "_InternalFree", "System.IntPtr"), static (ctx, a) => StackSlot.OfInt32(ctx.Shared.DependentHandles.Free(a[0].Int64Value) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(dependent, "InternalAlloc", "System.Object", "System.Object"), static (ctx, a) => {
            ctx.Heap.ChargeHostBuffer(64); ctx.Heap.DependentHandles = ctx.Shared.DependentHandles;
            return StackSlot.OfNativeInt(ctx.Shared.DependentHandles.Allocate(a[0], a[1]));
        }, BindingOrigin.InternalCall);
        foreach (var method in new[] { "InternalGetTarget", "InternalGetDependent" })
            r.RegisterBinding(BindingKey.Static(dependent, method, "System.IntPtr"), (ctx, a) => {
                var pair = ctx.Shared.DependentHandles.Get(a[0].Int64Value);
                return method == "InternalGetTarget" ? pair.Target : pair.Dependent;
            }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(dependent, "InternalGetTargetAndDependent", "System.IntPtr", "System.Object&"), static (ctx, a) => {
            var pair = ctx.Shared.DependentHandles.Get(a[0].Int64Value); ((VmByRef)a[1].ObjectValue!).Write(pair.Dependent); return pair.Target;
        }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(dependent, "InternalSetDependent", "System.IntPtr", "System.Object"), static (ctx, a) => { ctx.Shared.DependentHandles.SetDependent(a[0].Int64Value, a[1]); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(dependent, "InternalSetTargetToNull", "System.IntPtr"), static (ctx, a) => { ctx.Shared.DependentHandles.ClearTarget(a[0].Int64Value); return null; }, BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(dependent, "InternalFree", "System.IntPtr"), static (ctx, a) => StackSlot.OfInt32(ctx.Shared.DependentHandles.Free(a[0].Int64Value) ? 1 : 0), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "TryGetHashCode", "System.Object"), static (ctx, a) => StackSlot.OfInt32(ctx.IdentityHash(a[0].ObjectValue)), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "ObjectHasComponentSize", "System.Object"),
            static (_, a) => StackSlot.OfInt32(a[0].ObjectValue is VmArray or VmString ? 1 : 0), BindingOrigin.Managed);
        // internal static RuntimeType GetMethodTable(object obj)
        // CoreLib 上の IL は「ldarg.0; call 自分自身; ret」のダミー自己再帰本体で、実 CLR では
        // JIT が [Intrinsic] として置き換えるため決して実行されない。VM では実行時型ファサード
        // (VmRuntimeObject) を返すことで同意味論を提供する (未バインドだと無限再帰に落ちる)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetMethodTable", "System.Object"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.InternalCall);
        // internal static bool IsReferenceOrContainsReferences(RuntimeType type)
        // (ジェネリック ラッパー IsReferenceOrContainsReferences<T>() の IL から呼ばれる)。
        // 「参照型であるか、参照型フィールドを再帰的に含むか」を VM 型モデルで判定する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences", "System.RuntimeType"),
            static (_, a) => StackSlot.OfInt32(
                a[0].ObjectValue is VmRuntimeObject runtimeType && ContainsReferences(runtimeType.Target) ? 1 : 0),
            BindingOrigin.InternalCall);
        // ジェネリック ラッパー IsReferenceOrContainsReferences<T>() (MethodSpec 面)。
        // 実 IL がランタイム内部表現に直接依存するため IL 実行させず、T の実引数型名
        // (MethodSpec 解決で置換される。未解決なら開いた名 !!0) から VM 型モデルで判定する。
        // 値パラメータ 0 個のため無引数の正確キーで登録する (AnyParams 不要)
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsReferenceOrContainsReferences"),
            static (ctx, _) => {
                // ジェネリック ラッパーは値パラメータ 0 個のため T はメソッド型実引数で来る
                var name = ctx.MethodTypeArgAt(0);
                var type = ctx.MethodTypeArguments.FirstOrDefault() ?? (name is "" or "!!0" ? null : FindAnyType(ctx, name));
                // 未解決は安全側 (参照を含む = 遅い経路を選ぶだけなので過大判定は安全)
                return StackSlot.OfInt32(type is null || ContainsReferences(type) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static T? IsBitwiseEquatable<T>()  ([Intrinsic]: 実 IL はダミー throw = JIT intrinsic)。
        // SpanHelpers.SequenceEqual 等が T を bitwise 比較可能か判定する面 (typeof(T) の呼び出し
        // はメソッド型実引数で判別可能)。プリミティブ数値 / char / bool 系のみ true (Span IL の
        // 早抜け経路に誘導。非 primitive は false = 比較 delegate 経路にフォールバック)。
        // 値パラメータ 0 個のため無引数の正確キーで登録する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsBitwiseEquatable"),
            static (ctx, _) => {
                var name = ctx.MethodTypeArgAt(0);
                var primitive = name is "System.Byte" or "System.SByte" or "System.Char" or "System.Int16"
                    or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64"
                    or "System.UInt64" or "System.Boolean" or "System.IntPtr" or "System.UIntPtr"
                    or "System.Single" or "System.Double";
                return StackSlot.OfInt32(primitive ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // static bool IsKnownConstant<T>(T value)  ([Intrinsic]):
        // 本家 IL はダミー throw の JIT intrinsic。VM は JIT 定数畳み込みを持たないため false を返す
        // (未定義でも呼出 IL の fallback 分岐が動くが、false 固定にして恒常経路に誘導する)。
        // .NET 10 の既知署名 4 件を列挙する (非ジェネリック 3 overload + ジェネリック面は開いたキー)
        foreach (var knownConstantParams in new[] {
            new[] { "System.Char" },
            new[] { "System.String" },
            new[] { "System.Type" },
            new[] { "!!0" },
        })
            r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "IsKnownConstant", knownConstantParams),
                static (_, _) => StackSlot.OfInt32(0),
                BindingOrigin.InternalCall);
        // static ReadOnlySpan<T> RuntimeHelpers.CreateSpan<T>(RuntimeFieldHandle fieldHandle)
        // 本家 IL は GetSpanDataFrom (MethodTable / ネイティブ型ハンドルのランタイム内部) を
        // 辿るため VM 表現境界。ldtoken Field の結果 (VmFieldRvaData の初期データ列) から
        // ReadOnlySpan<T> 構造体値 (_reference + _length) を直接構築する。
        // HashHelpers の素数表等の FieldRVA 静的テーブル面が使う。T はメソッド型実引数で判別する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "CreateSpan", "System.RuntimeFieldHandle"),
            static (ctx, a) => CreateSpanImpl(ctx, a),
            BindingOrigin.InternalCall);
        // static ref byte RuntimeHelpers.GetRawData(object obj)
        // 本家 IL は box への生ポインタを Unsafe.As で基底型幅ごとに読む。
        // VM では box / インスタンスの先頭フィールドへの ByRef、文字列はバイト実体で提供する
        r.RegisterBinding(BindingKey.Static(RuntimeHelpersType, "GetRawData", "System.Object"),
            static (ctx, a) => a[0].ObjectValue switch {
                VmArray array when ctx.Shared.RuntimeMetadata.TryArrayData(array, out var pointer) => StackSlot.OfObject(pointer),
                VmObject { ManagedInstance: { } instance } when instance.Fields.Length > 0 =>
                    StackSlot.OfByRef(VmByRef.OwnedStorage(instance, instance.Fields, 0)),
                VmBoxedValue box => StackSlot.OfByRef(VmByRef.BoxedValue(box)),
                VmClassInstance instance when instance.Fields.Length > 0 =>
                    StackSlot.OfByRef(VmByRef.OwnedStorage(instance, instance.Fields, 0)),
                VmString str => StackSlot.OfObject(new VmNativePointer {
                    Memory = str.PointerMemory,
                    ByteOffset = VmString.CharDataByteOffset,
                }),
                _ => throw new InvalidOperationException(
                    $"RuntimeHelpers.GetRawData の引数を生データ参照にできません ({a[0].Kind}, {SlotOps.Describe(a[0])})。"),
            },
            BindingOrigin.InternalCall);
    }

    private static void RegisterMemoryMarshal(IntrinsicRegistry r) {
        // static ReadOnlySpan<T> MemoryMarshal.CreateReadOnlySpan(ref T reference, int length)
        // .NET 10 uses this intrinsic when params ReadOnlySpan<T> overloads are selected (notably
        // Task.WaitAll/WhenAll/WhenAny).  The VM span representation is a value containing the
        // source byref and logical length; no host span or raw pointer escapes the VM.
        r.RegisterBinding(BindingKey.Static(
                "System.Runtime.InteropServices.MemoryMarshal", "CreateReadOnlySpan", "!!0&", "System.Int32"),
            static (ctx, a) => CreateReadOnlySpanFromReference(ctx, a),
            BindingOrigin.InternalCall);
    }

    private static StackSlot CreateReadOnlySpanFromReference(IntrinsicContext ctx, StackSlot[] args, bool readOnly = true) {
        if (args[0].ObjectValue is not VmByRef reference)
            throw new InvalidOperationException("MemoryMarshal.CreateReadOnlySpan の参照引数が ByRef ではありません。");
        var length = args[1].AsInt32;
        if (length < 0 || reference.Index + length > reference.Container.Length)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", "length");

        var elementName = ctx.MethodTypeArgAt(0);
        if (string.IsNullOrEmpty(elementName) || elementName is "!!0")
            throw new InvalidOperationException("MemoryMarshal.CreateReadOnlySpan の型引数 T を判別できませんでした。");
        var elementType = FindAnyType(ctx, elementName)
            ?? throw new InvalidOperationException($"span の要素型 {elementName} を解決できません。");
        var definition = FindAnyType(ctx, readOnly ? "System.ReadOnlySpan`1" : "System.Span`1")
            ?? throw new InvalidOperationException("System.ReadOnlySpan`1 がロードされていません。");
        var spanType = new VmConstructedType { Definition = definition, TypeArguments = [elementType] };
        return StackSlot.OfValueType(new VmStructValue(spanType,
            [StackSlot.OfByRef(reference), StackSlot.OfInt32(length)], [elementType]));
    }

    private static StackSlot CreateSpanImpl(IntrinsicContext ctx, StackSlot[] a) {
        if (a[0].ObjectValue is not VmFieldRvaData rva)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の引数がフィールド RVA データではありません ({a[0].Kind})。");
        var tName = ctx.MethodTypeArgAt(0);
        if (string.IsNullOrEmpty(tName) || tName is "!!0")
            throw new InvalidOperationException(
                "RuntimeHelpers.CreateSpan のジェネリック型引数 T を判別できませんでした。");
        var stride = SlotStride(tName);
        var bytes = rva.Data.ToArray();
        if (bytes.Length % stride != 0)
            throw new InvalidOperationException(
                $"RuntimeHelpers.CreateSpan の初期データ ({bytes.Length} バイト) が要素幅 {stride} で割り切れません。");
        var t = FindAnyType(ctx, tName)
            ?? throw new InvalidOperationException($"RuntimeHelpers.CreateSpan の要素型 {tName} を解決できません。");
        var def = ctx.Types.FindTypeByFullName("System.ReadOnlySpan`1") as VmClassType
            ?? FindAnyType(ctx, "System.ReadOnlySpan`1") as VmClassType
            ?? throw new InvalidOperationException("System.ReadOnlySpan`1 がロードされていません。");
        if (def.GenericParamCount != 1)
            throw new InvalidOperationException("System.ReadOnlySpan`1 の型引数個数が不正です。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [t] };
        var native = new VmNativePointer {
            Memory = new VmLocallocMemory { Bytes = bytes },
            ByteOffset = 0,
        };
        // フィールド配置は GetLayout (正規レイアウト) に合わせる
        // (FieldLocation 等の読み側と一致させるため宣言順の独自採番はしない)
        var layout = new ObjectModel().GetLayout(def);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var field in def.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            fields[layout[field]] = field.Name switch {
                "_reference" => StackSlot.OfObject(native),
                "_length" => StackSlot.OfInt32(bytes.Length / stride),
                _ => throw new InvalidOperationException(
                    $"System.ReadOnlySpan`1 の未知フィールド {field.Name} があります。"),
            };
        }
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }


    // ---- System.Runtime.Intrinsics.Vector64/128/256/512 (JIT intrinsic 判定面) ----

    /// <summary>本家 get_IsHardwareAccelerated は [Intrinsic] 付きのダミー自己再帰 IL
    /// (return IsHardwareAccelerated;) で、実 CLR では JIT がハードウェア判定へ置換するため
    /// 決して実行されない (RuntimeHelpers.GetMethodTable と同型)。VM は SIMD 値型
    /// (Vector128&lt;T&gt; 等の 16/32/64 バイト inline 表現) を持たないため SIMD パスの IL を
    /// 実行できず、true を返すと SpanHelpers 等が SIMD 面で fail-closed に落ちる。
    /// そこで false を返して scalar フォールバック IL に誘導する (string SCI overload 群が
    /// 同一結果を得る経路)。SIMD 演算面自体は未バインド → fail-closed。ゲストがこの面を
    /// 直接観測した場合の実 x64 CLR (true) との既知差異は SIMD 表現境界として監査表に記載。</summary>
    private static void RegisterVectorIntrinsics(IntrinsicRegistry r) {
        foreach (var vectorType in new[] {
            "System.Numerics.Vector",
            "System.Runtime.Intrinsics.Vector64",
            "System.Runtime.Intrinsics.Vector128",
            "System.Runtime.Intrinsics.Vector256",
            "System.Runtime.Intrinsics.Vector512",
        }) {
            r.RegisterBinding(BindingKey.Static(vectorType, "get_IsHardwareAccelerated"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
        // X86.Sse2.get_IsSupported は同じくダミー自己再帰 IL (return IsSupported;) で、
        // SpanHelpers.IndexOfValueType 等が PackedSpanHelpers 経由で参照する。
        // false を返して scalar フォールバック IL に誘導する (Vector 面と同一方針)。
        // X86 の他面 (Sse41 等) は到達した面から順次追加する (fail-driven)。
        r.RegisterBinding(BindingKey.Static("System.Runtime.Intrinsics.X86.Sse2", "get_IsSupported"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        foreach (var x86Type in new[] {
            "System.Runtime.Intrinsics.X86.Ssse3",
            "System.Runtime.Intrinsics.X86.Sse41",
            "System.Runtime.Intrinsics.X86.Avx",
            "System.Runtime.Intrinsics.X86.Avx2",
            "System.Runtime.Intrinsics.X86.Lzcnt",
            "System.Runtime.Intrinsics.X86.Lzcnt+X64",
        }.Concat(AdditionalIsaCapabilities)) {
            r.RegisterBinding(BindingKey.Static(x86Type, "get_IsSupported"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        }
    }


    /// <summary>CLR の IsReferenceOrContainsReferences 意味論: 型が参照型であるか、
    /// 参照型フィールド (再帰的に) を含むか。プリミティブ / enum は false。
    /// 未解決フィールドは安全側 (true) に倒す (呼出側は遅い経路を選ぶだけなので過大判定は安全)。</summary>
    private static bool ContainsReferences(VmType type) {
        var definition = type is VmConstructedType constructed ? constructed.Definition : type;
        if (definition is VmArrayType or VmMultiDimArrayType or VmByRefType)
            return true;
        if (!definition.IsValueType)
            return true;
        if (definition.FullName == "System.Enum" || definition.IsEnum ||
            VmPrimitiveTypes.IsSlotPrimitive(definition.FullName))
            return false;
        var visited = new HashSet<VmType>();
        return ContainsFields(definition, visited);
    }

    private static bool ContainsFields(VmType definition, HashSet<VmType> visited) {
        if (!visited.Add(definition))
            return false; // 循環するフィールド構造 (異常画像) は打ち切り
        foreach (var field in definition.Fields) {
            if (field.IsStatic || field.IsLiteral)
                continue;
            var fieldType = field.FieldType;
            if (fieldType is null)
                return true; // 未解決フィールドは安全側に倒す
            // 構築ジェネリック型のフィールド (!0 等) は実引数で置換して判定する
            if (definition is VmConstructedType constructed)
                fieldType = GenericSubstitutor.Substitute(fieldType,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
            if (ContainsReferences(fieldType))
                return true;
        }
        return false;
    }
}
