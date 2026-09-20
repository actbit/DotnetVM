namespace DotnetVM.Runtime.Types;

/// <summary>
/// ジェネリックパラメータの置換コンテキスト。実行中のメソッドに紐付き、
/// 署名中の !n (型ジェネリックパラメータ) / !!n (メソッドジェネリックパラメータ) を
/// 実引数に解決するために使う。呼出ごとに構築され InterpreterFrame に保持する。
/// </summary>
public sealed class GenericContext {
    public static readonly GenericContext Empty = new();

    /// <summary>宣言型の型引数 (VmClassType.GenericParamCount と同数)。</summary>
    public VmType[] ClassArgs = [];

    /// <summary>メソッドの型引数 (MethodSpec の Instantiation)。</summary>
    public VmType[] MethodArgs = [];

    public static GenericContext? Of(VmType[]? classArgs, VmType[]? methodArgs) =>
        classArgs is { Length: > 0 } || methodArgs is { Length: > 0 }
            ? new GenericContext { ClassArgs = classArgs ?? [], MethodArgs = methodArgs ?? [] }
            : null;
}

/// <summary>
/// 署名型 (VmType 木) 中の VmGenericParameterType をコンテキストの型引数で置換する。
/// 構築型・配列・ByRef は再帰的に辿る (要素型/引数型も置換される)。
/// </summary>
public static class GenericSubstitutor {
    public static VmType Substitute(VmType type, GenericContext? context) {
        if (context is null)
            return type;
        switch (type) {
            case VmGenericParameterType var: {
                var args = var.IsMethodParameter ? context.MethodArgs : context.ClassArgs;
                return (uint)var.Number < (uint)args.Length ? args[var.Number] : var;
            }
            case VmConstructedType constructed: {
                var args = new VmType[constructed.TypeArguments.Length];
                var changed = false;
                for (var i = 0; i < args.Length; i++) {
                    args[i] = Substitute(constructed.TypeArguments[i], context);
                    changed |= !ReferenceEquals(args[i], constructed.TypeArguments[i]);
                }
                return changed ? new VmConstructedType { Definition = constructed.Definition, TypeArguments = args } : constructed;
            }
            case VmArrayType array: {
                var element = Substitute(array.ElementType, context);
                return ReferenceEquals(element, array.ElementType) ? array : new VmArrayType { ElementType = element };
            }
            case VmMultiDimArrayType multi: {
                var element = Substitute(multi.ElementType, context);
                return ReferenceEquals(element, multi.ElementType) ? multi : new VmMultiDimArrayType { ElementType = element, Rank = multi.Rank };
            }
            case VmByRefType byRef: {
                var element = Substitute(byRef.ElementType, context);
                return ReferenceEquals(element, byRef.ElementType) ? byRef : new VmByRefType { ElementType = element };
            }
            default:
                return type;
        }
    }
}
