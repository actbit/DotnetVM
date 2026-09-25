using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>VM 型に関する純粋判定群 (状態を持たない)。castclass/isinst/配列共変/例外 catch/
/// デリゲート判定/例外ファサード判定など、型システムの面だけを見る共通判定。</summary>
internal static class TypeChecks {

    /// <summary>
    /// boxed 値を unbox/unbox.any する際の型判定。代入可能性とは異なり、基底の
    /// ValueType や実装インターフェースへ unbox することは許さない。CoreLib の
    /// facade と実型が混在する場合だけ、統合済みの完全名一致を許容する。
    /// </summary>
    public static bool IsExactUnboxType(VmType actual, VmType target) {
        if (ReferenceEquals(actual, target))
            return true;
        if (actual.FullName != target.FullName)
            return false;

        // Intrinsic types are the VM's explicit facade for a CoreLib type.  A box
        // created through the real CoreLib TypeDef must still unbox through the
        // corresponding facade (primitive facades are the common case).
        if (actual is VmIntrinsicType || target is VmIntrinsicType)
            return true;

        if (actual is VmConstructedType actualConstructed &&
            target is VmConstructedType targetConstructed)
            return IsExactUnboxType(actualConstructed.Definition, targetConstructed.Definition) &&
                actualConstructed.TypeArguments.Length == targetConstructed.TypeArguments.Length &&
                actualConstructed.TypeArguments.Zip(targetConstructed.TypeArguments)
                    .All(pair => IsExactUnboxType(pair.First, pair.Second));

        // Two guest TypeDefs with the same name from different images are not the
        // same CLR type. Use assembly identity + TypeDef RID rather than the broad
        // FullName-based assignability rule used for facade compatibility.
        return actual is VmClassType actualClass && target is VmClassType targetClass &&
            actualClass.TypeDefRid == targetClass.TypeDefRid &&
            actualClass.Image.Identity.MatchesExactly(targetClass.Image.Identity);
    }

    /// <summary>VM オブジェクトがターゲット型に代入可能か (castclass/isinst/配列共変/例外 catch の共通判定)。
    /// ジェネリック型のインスタンスは実引数を記録した構築型を作って判定する (変性込み・M5)。
    /// stringType は呼出元 VM の System.String 実型 (InterpreterServices.StringType。VM 単位で
    /// 保持される — 静的に持つと並列実行する VM 間で Dispose 競合が起きる)。</summary>
    public static bool IsAssignableToType(object vmValue, VmType target, VmType? stringType = null) => vmValue switch {
        VmClassInstance ci => ci.RuntimeType.IsAssignableTo(target),
        VmStructValue sv => sv.RuntimeType.IsAssignableTo(target),
        VmIntrinsicInstance intrinsic => intrinsic.RuntimeType.IsAssignableTo(target),
        VmTaskObject task => task.Type.IsAssignableTo(target),
        DotnetVM.Runtime.Objects.VmExceptionObject e => e.ExceptionType.IsAssignableTo(target),
        VmString => target.FullName is "System.String" or "System.Object" ||
            (stringType is { } st && st.IsAssignableTo(target)),
        // typeof(X) / GetType() の結果は CLR では System.RuntimeType の実体。
        // VM の VmRuntimeObject を同一視する (Enum.ToString(string) 等の実 IL が
        // castclass System.RuntimeType する経路のため。fake 型との混同は起きない —
        // 参照自体が VM 型系へのポインタであり別アセンブリの型実体ではない)。
        VmRuntimeObject => target.FullName is "System.RuntimeType"
            or "System.Type" or "System.Object" or "System.Reflection.MemberInfo",
        // MethodBase.GetCurrentMethod() 等の結果は CLR では System.Reflection.RuntimeMethodInfo の実体。
        VmRuntimeMethod => target.FullName is "System.Reflection.RuntimeMethodInfo"
            or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo"
            or "System.Reflection.MethodBase" or "System.Reflection.MemberInfo" or "System.Object",
        VmRuntimeField => target.FullName is "System.Reflection.RuntimeFieldInfo"
            or "System.Reflection.FieldInfo" or "System.Reflection.MemberInfo" or "System.Object",
        VmRuntimeProperty => target.FullName is "System.Reflection.RuntimePropertyInfo"
            or "System.Reflection.PropertyInfo" or "System.Reflection.MemberInfo" or "System.Object",
        VmAssemblyObject => target.FullName is "System.Reflection.Assembly" or "System.Object",
        VmAssemblyLoadContext => target.FullName is "System.Runtime.Loader.AssemblyLoadContext" or "System.Object",
        VmAssemblyNameObject => target.FullName is "System.Reflection.AssemblyName" or "System.Object",
        VmMemoryStreamObject => target.FullName is "System.IO.MemoryStream" or "System.IO.Stream" or "System.Object",
        VmExpressionObject expression => ExpressionAssignable(expression, target),
        VmArray array => target switch {
            VmArrayType other => array.ArrayType.ElementType.IsAssignableTo(other.ElementType),
            _ => target.FullName is "System.Array" or "System.Object" or "System.ICloneable"
                or "System.Collections.IList" or "System.Collections.ICollection",
        },
        VmBoxedValue boxed => boxed.Type.IsAssignableTo(target) || target.FullName is "System.Object" or "System.ValueType",
        VmDelegate @delegate => @delegate.DeclaredType.IsAssignableTo(target) ||
            target.FullName is "System.Object" or "System.Delegate" or "System.MulticastDelegate",
        _ => false,
    };

    private static bool ExpressionAssignable(VmExpressionObject expression, VmType target) {
        if (target.FullName is "System.Object" or "System.Linq.Expressions.Expression")
            return true;
        if (target.FullName.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal) &&
            target.FullName.EndsWith("Expression", StringComparison.Ordinal))
            return true;
        if (expression.Kind == VmExpressionKind.Lambda) {
            if (target.FullName == "System.Linq.Expressions.LambdaExpression")
                return true;
            if (target is VmConstructedType { Definition.FullName: "System.Linq.Expressions.Expression`1" } generic)
                return expression.DelegateType?.FullName == generic.TypeArguments[0].FullName;
            return false;
        }
        return expression.Kind switch {
            VmExpressionKind.Parameter => target.FullName == "System.Linq.Expressions.ParameterExpression",
            VmExpressionKind.Constant => target.FullName == "System.Linq.Expressions.ConstantExpression",
            _ => target.FullName == "System.Linq.Expressions.BinaryExpression",
        };
    }

    /// <summary>型がデリゲートか (System.Delegate / MulticastDelegate 派生。ゲストのカスタム
    /// delegate 宣言と Action/Func ファサードの構築型の両方を判定する)。</summary>
    public static bool IsDelegateType(VmType? type) {
        for (VmType? t = type; t is not null; t = t.BaseType)
            if (t.FullName is "System.Delegate" or "System.MulticastDelegate")
                return true;
        return false;
    }

    /// <summary>例外ファサード型か (System.Exception 自身と XxxException)。</summary>
    public static bool IsExceptionFacade(VmType type) =>
        type.FullName == "System.Exception" || type.FullName.EndsWith("Exception", StringComparison.Ordinal);
}
