using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Object (CoreLib managed IL が内部表現依存面を辿る面の代替) ----

    private static void RegisterObject(IntrinsicRegistry r) {
        // public extern Type Object.GetType()
        // CoreLib の managed IL は GetMethodTable → MethodTable::AuxiliaryData という
        // ランタイム内部表現への直接アクセスを辿るため、VM では実行時型ファサード
        // (VmRuntimeObject) を返すバインドで優先提供する (① ランタイムバインドが ② IL より先)
        r.RegisterBinding(BindingKey.Instance("System.Object", "GetType"),
            static (ctx, a) => DefaultIntrinsics.TypeFacadeOf(ctx, a[0]),
            BindingOrigin.Managed);
        // public virtual int Object.GetHashCode()
        // C5.5 Wave 4 で監査確定: 本体 IL (RuntimeHelpers.GetHashCode 呼び) は
        // TryGetHashCode (InternalCall) → GetHashCodeSlow (QCall ネイティブ =
        // ObjectNative_GetHashCodeSlow) を辿り identity hash の発行自体がランタイム内部。
        // P/Invoke 代替の原則で identity hash リーフとして ① バインドで優先提供する
        // (Object は IlPreferred に上がったため ① が無いと ② がこの面を辿ってしまう)
        r.RegisterBinding(BindingKey.Instance("System.Object", "GetHashCode"),
            static (ctx, a) => StackSlot.OfInt32(ctx.IdentityHash(a[0].ObjectValue)),
            BindingOrigin.Managed);

        // ---- System.Type の runtime-representation 面 (culture 機構 / Enum 書式 IL が辿る Type 面) ----
        // 本家 Type の get_* 面は RuntimeType 内部表現 (IL なし = ランタイム intrinsic) を辿るため
        // ② IL 実行に落ちず、VM 型モデルから同一要素を提示する (監査表 (c) runtime-representation)
        // get_BaseType: VM 型モデル (VmType.BaseType) の判定で返す (System.Object の親 = null)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_BaseType"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt && rt.Target.BaseType is { } baseType
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, baseType)
                    : null, // BaseType 無し (System.Object 等) = CLR と同一の null
            BindingOrigin.InternalCall);
        // Type::get_TypeHandle: 本家は RuntimeTypeHandle (runtime representation)。
        // VM 型への参照 (VmTypeHandle) を返す (GetCultureInfo 機構 IL 内の静的リテラル type handle 面)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_TypeHandle"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt
                    ? StackSlot.OfObject(ctx.Heap.Allocate(new VmTypeHandle { Target = rt.Target }))
                    : throw new InvalidOperationException("Type::get_TypeHandle の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        // Type 面の runtime-representation 補完 (culture 機構 IL / Dictionary cache ctor が辿る判定面):
        // IsValueTypeImpl / IsSubclassOf は RuntimeType 内部表現 (IL なし) を辿るため VM 型モデルで
        // 同一判定を提供する。IsValueType = IsValueTypeImpl と同値 (プリミティブは ValueType 派生)
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsValueTypeImpl"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt
                    ? StackSlot.OfInt32(rt.Target.IsValueType ? 1 : 0)
                    : throw new InvalidOperationException("Type::IsValueTypeImpl の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_IsValueType"),
            static (ctx, a) =>
                a[0].ObjectValue is VmRuntimeObject rt2
                    ? StackSlot.OfInt32(rt2.Target.IsValueType ? 1 : 0)
                    : throw new InvalidOperationException("Type::get_IsValueType の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsSubclassOf", "System.Type"),
            static (ctx, a) => {
                var self = a[0].ObjectValue is VmRuntimeObject s ? s.Target : null;
                var other = a[1].ObjectValue is VmRuntimeObject o ? o.Target : null;
                if (self is null || other is null)
                    throw new InvalidOperationException("IsSubclassOf の引数が Type ファサードではありません。");
                for (VmType? t = self.BaseType; t is not null;) {
                    if (t.FullName == other.FullName)
                        return StackSlot.OfInt32(1);
                    t = t.BaseType;
                }
                return StackSlot.OfInt32(0);
            },
            BindingOrigin.InternalCall);
        // Type::IsAssignableFrom: 本家は RuntimeType 内部表現を辿るため VM 型モデルで提供する。
        // null 引数は false (CLR 規約)。変性込みの構築型判定は VmType 側に委譲する
        r.RegisterBinding(BindingKey.Instance("System.Type", "IsAssignableFrom", "System.Type"),
            static (_, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject self)
                    throw new InvalidOperationException("Type::IsAssignableFrom の this が Type ファサードではありません。");
                if (a[1].ObjectValue is not VmRuntimeObject other)
                    return StackSlot.OfInt32(0);
                return StackSlot.OfInt32(other.Target.IsAssignableTo(self.Target) ? 1 : 0);
            },
            BindingOrigin.InternalCall);
        // Type::get_IsInterface: VM 型モデルの Flags 判定 (IEnumerable`1 等の構築型は定義側を見る)
        r.RegisterBinding(BindingKey.Instance("System.Type", "get_IsInterface"),
            static (_, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? StackSlot.OfInt32(rt.Target.IsInterface ? 1 : 0)
                : throw new InvalidOperationException("Type::get_IsInterface の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethod", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, name.Value, null)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethod", "System.String", "System.Type[]"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, name.Value, a[2])
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetConstructor", "System.Type[]"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeMethodObject(ctx, rt.Target, ".ctor", a[1])
                : throw new InvalidOperationException("Type::GetConstructor の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetMethods"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeMethodArray(ctx, rt.Target)
                : throw new InvalidOperationException("Type::GetMethods の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetField", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakeFieldObject(ctx, rt.Target, name.Value)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetProperty", "System.String"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt &&
                a[1].ObjectValue is VmString name
                    ? DefaultIntrinsics.MakePropertyObject(ctx, rt.Target, name.Value)
                    : throw new UnhandledGuestException("System.ArgumentNullException", null),
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetFields"),
            static (ctx, a) => a[0].ObjectValue is VmRuntimeObject rt
                ? DefaultIntrinsics.MakeFieldArray(ctx, rt.Target)
                : throw new InvalidOperationException("Type::GetFields の this が Type ファサードではありません。"),
            BindingOrigin.InternalCall);
        // Enum書式などで Type.GetEnumUnderlyingType が本家 GetFields 実装へ降りるが、
        // RuntimeType のフィールド反映は VM の Type ファサードに存在しないため、
        // enum 定義の value__ フィールドから基底型を直接返す。
        r.RegisterBinding(BindingKey.Instance("System.Type", "GetEnumUnderlyingType"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject { Target: VmClassType enumType } || !enumType.IsEnum)
                    throw new UnhandledGuestException("System.ArgumentException", null);
                var underlying = enumType.Fields.FirstOrDefault(f => !f.IsStatic && !f.IsLiteral)?.FieldType
                    ?? throw new InvalidOperationException($"enum {enumType.FullName} の基底型が解決できません。");
                return DefaultIntrinsics.MakeRuntimeObject(ctx, underlying);
            },
            BindingOrigin.InternalCall);
        // Type::GetType(string): 型名から VM 型を解決するランタイム面。
        // アセンブリ修飾名は指定アセンブリ内で、単純名は trusted CoreLib を先に
        // (fake BCL 名の混入防止)・次に全ロード画像で解決する。未解決は null
        // (throwOnError なし overload の CLR 規約。呼出側の !. は NRE になる)
        r.RegisterBinding(BindingKey.Static("System.Type", "GetType", "System.String"),
            static (ctx, a) => {
                var name = (a[0].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                return ResolveTypeName(ctx, name) is { } type
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, type)
                    : StackSlot.Null;
            },
            BindingOrigin.InternalCall);
        // IEqualityOperators<TSelf,TOther,TResult>.op_Equality (static abstract)。
        // GenericEqualityComparer<T>.Equals 等が constrained 呼出で辿る。本家 IL は
        // ダミー抽象に着地するため、観測意味論 (==) を直接提供する。NaN は == 規約
        // (Equals と異なり NaN ペアは false) どおり比較する
        r.RegisterBinding(BindingKey.Static("System.Numerics.IEqualityOperators`3", "op_Equality", "!0", "!1"),
            static (_, a) => StackSlot.OfInt32(OperatorEqual(a[0], a[1]) ? 1 : 0),
            BindingOrigin.InternalCall);
        // op_Inequality (== の否定。Guid 比較等が辿る)
        r.RegisterBinding(BindingKey.Static("System.Numerics.IEqualityOperators`3", "op_Inequality", "!0", "!1"),
            static (_, a) => StackSlot.OfInt32(OperatorEqual(a[0], a[1]) ? 0 : 1),
            BindingOrigin.InternalCall);
        // static abstract char IUtfChar<T>.CastFrom(T)  ([Intrinsic]: 実 IL はダミー throw。
        // String/span IL が T(char)→char の面を経由するため、char 系 T の値を i4 スロットで透過)
        // .NET 10 の既知署名 (IUtfChar<TSelf>.CastFrom の 5 overload) を列挙する
        foreach (var castFromParam in new[] {
            "System.Byte", "System.Char", "System.Int32", "System.UInt32", "System.UInt64",
        })
            r.RegisterBinding(BindingKey.Static("System.IUtfChar`1", "CastFrom", castFromParam),
                static (_, a) => StackSlot.OfInt32(a[0].AsInt32),
                BindingOrigin.InternalCall);
        // static uint IUtfChar<TSelf>.CastToUInt32(TSelf value) ([Intrinsic]:
        // Guid 16 進解析等が T(char) → uint の再解釈に使う。クラス変数 (!0) の開いた
        // キーで 1 件登録し、実引数は実行時の具体名で判別する)
        r.RegisterBinding(BindingKey.Static("System.IUtfChar`1", "CastToUInt32", "!0"),
            static (_, a) => StackSlot.OfInt32(unchecked((int)(a[0].Kind == StackKind.Int64
                ? (uint)a[0].Int64Value : (uint)a[0].AsInt32))),
            BindingOrigin.InternalCall);
    }

    // ---- System.Collections.Generic.EqualityComparer<T> (等値比較子面) ----

    /// <summary>EqualityComparer&lt;T&gt;.get_Default の同等意味論。
    /// 本家は .cctor → ComparerHelpers.CreateDefaultEqualityComparer (RuntimeType 内部表現・
    /// MakeGenericType・未初期化実体化の連鎖) を辿るため、VM 型モデルで直接振り分ける:
    /// string → StringEqualityComparer、enum → EnumEqualityComparer&lt;T&gt;、
    /// Nullable → NullableEqualityComparer&lt;T&gt;、その他 → GenericEqualityComparer&lt;T&gt;
    /// (本家と同一の振分順序)。実体は公開無引数 .ctor を NewInstanceHook で実行する。
    /// T はクラス型実引数 (!0) で判別する (値パラメータ 0 個のため)。</summary>
    private static void RegisterEqualityComparer(IntrinsicRegistry r) {
        r.RegisterBinding(BindingKey.Static("System.Collections.Generic.EqualityComparer`1", "get_Default"),
            static (ctx, a) => {
                _ = a;
                var tName = ctx.ClassTypeArgAt(0);
                if (string.IsNullOrEmpty(tName) || tName is "!!0" or "!0")
                    throw new InvalidOperationException(
                        "EqualityComparer<T>.get_Default の型引数 T を判別できませんでした。");
                var t = FindAnyType(ctx, tName);
                // 本家 ComparerHelpers.CreateDefaultEqualityComparer と同一の振分順序:
                // string → IEquatable<T> 実装 → Nullable<T> → enum → 既定
                string comparerBase;
                if (tName == "System.String") {
                    comparerBase = "System.Collections.Generic.StringEqualityComparer";
                } else if (t is not null && IsEquatableImpl(ctx, t)) {
                    comparerBase = "System.Collections.Generic.GenericEqualityComparer`1";
                } else if (t is VmConstructedType nullable &&
                           nullable.Definition.FullName == "System.Nullable`1") {
                    comparerBase = "System.Collections.Generic.NullableEqualityComparer`1";
                } else if (t is not null && t.IsEnum) {
                    comparerBase = "System.Collections.Generic.EnumEqualityComparer`1";
                } else {
                    comparerBase = "System.Collections.Generic.GenericEqualityComparer`1";
                }
                var def = FindAnyType(ctx, comparerBase) as VmClassType
                    ?? throw new InvalidOperationException(
                        $"等値比較子 {comparerBase} が CoreLib に見つかりません。");
                GenericContext? classContext = null;
                VmType[] typeArgs = [];
                if (def.GenericParamCount > 0) {
                    if (t is null)
                        throw new InvalidOperationException(
                            $"等値比較子の型引数 {tName} を解決できません。");
                    typeArgs = [t];
                    classContext = new GenericContext { ClassArgs = typeArgs };
                }
                var ctor = def.Methods.FirstOrDefault(m =>
                    m.Name == ".ctor" && !m.IsStatic && m.Signature.ParamTypes.Length == 0 && m.Body is not null);
                if (ctor is null)
                    throw new InvalidOperationException($"等値比較子 {comparerBase} に無引数 .ctor がありません。");
                var hook = ctx.NewInstanceHook
                    ?? throw new InvalidOperationException("等値比較子の生成フックが設定されていません。");
                var instance = typeArgs.Length > 0
                    ? hook(new VmConstructedType { Definition = def, TypeArguments = typeArgs }, ctor, [], classContext)
                    : hook(def, ctor, [], null);
                return StackSlot.OfObject(instance);
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>VM 型を全ロード画像から探す (trusted 優先)。ゲスト型の比較子等、
    /// 既知型に無い型引数の解決に使う。</summary>
    private static VmType? FindAnyType(IntrinsicContext ctx, string fullName) {
        try {
            if (ctx.Types.TryResolveTrustedUnifiedType(fullName) is { } trusted)
                return trusted;
        } catch {
        }
        var context = ctx.Types.Context;
        if (context is null) {
            try {
                return ctx.Types.FindTypeByFullName(fullName);
            } catch {
                return null;
            }
        }
        foreach (var loader in context.Loaders) {
            try {
                if (loader.FindTypeByFullName(fullName) is { } found)
                    return found;
            } catch {
                continue;
            }
        }
        try {
            return ctx.Types.ResolveWellKnownType(fullName);
        } catch {
            return null;
        }
    }

    private static bool IsEquatableImpl(IntrinsicContext ctx, VmType t) {
        var equatableDef = FindAnyType(ctx, "System.IEquatable`1");
        if (equatableDef is null)
            return false;
        var constructed = new VmConstructedType { Definition = equatableDef, TypeArguments = [t] };
        try {
            return t.IsAssignableTo(constructed);
        } catch {
            return false;
        }
    }
}
