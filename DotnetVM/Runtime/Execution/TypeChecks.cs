using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>VM 型に関する純粋判定群 (状態を持たない)。castclass/isinst/配列共変/例外 catch/
/// デリゲート判定/例外ファサード判定など、型システムの面だけを見る共通判定。</summary>
internal static class TypeChecks {

    /// <summary>VM オブジェクトがターゲット型に代入可能か (castclass/isinst/配列共変/例外 catch の共通判定)。
    /// ジェネリック型のインスタンスは実引数を記録した構築型を作って判定する (変性込み・M5)。</summary>
    public static bool IsAssignableToType(object vmValue, VmType target) => vmValue switch {
        VmClassInstance ci => ci.RuntimeType.IsAssignableTo(target),
        VmStructValue sv => sv.RuntimeType.IsAssignableTo(target),
        DotnetVM.Runtime.Objects.VmExceptionObject e => e.ExceptionType.IsAssignableTo(target),
        VmString => target.FullName is "System.String" or "System.Object",
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
