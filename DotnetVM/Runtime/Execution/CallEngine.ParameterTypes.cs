using DotnetVM.Metadata;
using System.Threading;
using DotnetVM.Metadata.Signatures;
using DotnetVM.IL;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class CallEngine {
    /// <summary>解決済みメソッドの宣言パラメータ型名 (i4 統合面の判別 / バインドキー構築用)。
    /// 呼出トークン解決で確定済みならそれを使い、無い場合は定義ローダで署名を解決する。</summary>
    private string[]? ParamTypeNamesOf(VmMethod method, VmType[]? methodArgs, VmType[]? classArgs = null) {
        var loader = method.Loader;
        if (loader is null)
            return null;
        var names = new string[method.Signature.ParamTypes.Length];
        for (var i = 0; i < names.Length; i++) {
            var name = DescribeBindingType(method.Signature.ParamTypes[i], methodArgs, classArgs, loader);
            if (name is null)
                return null;
            names[i] = name;
        }
        return names;
    }

    /// <summary>バインドキー用のパラメータ型名 (完全名)。ジェネリック変数は実引数 (MethodSpec) で
    /// 置換し、実引数が無い場合は開いた名 (!!n / !n) のまま返す。クラス実引数 (構築型の !n) も
    /// 置換する (例: IComparable`1&lt;uint&gt;::CompareTo の !0 → System.UInt32)。
    /// トークンは「そのメソッドを定義したローダ」で解決する (署名の解決は定義ローダの原則)。</summary>
    private static string? DescribeBindingType(SigType type, VmType[]? methodArgs, VmType[]? classArgs, TypeLoader loader) => type.Kind switch {
        SigKind.Void => "System.Void",
        SigKind.Boolean => "System.Boolean",
        SigKind.Char => "System.Char",
        SigKind.I1 => "System.SByte",
        SigKind.U1 => "System.Byte",
        SigKind.I2 => "System.Int16",
        SigKind.U2 => "System.UInt16",
        SigKind.I4 => "System.Int32",
        SigKind.U4 => "System.UInt32",
        SigKind.I8 => "System.Int64",
        SigKind.U8 => "System.UInt64",
        SigKind.R4 => "System.Single",
        SigKind.R8 => "System.Double",
        SigKind.I => "System.IntPtr",
        SigKind.U => "System.UIntPtr",
        SigKind.String => "System.String",
        SigKind.Object => "System.Object",
        SigKind.TypedByRef => "System.TypedReference",
        SigKind.SzArray => DescribeBindingType(type.Inner!, methodArgs, classArgs, loader) is { } inner ? inner + "[]" : null,
        SigKind.ByRef => DescribeBindingType(type.Inner!, methodArgs, classArgs, loader) is { } element ? element + "&" : null,
        SigKind.Pointer => DescribeBindingType(type.Inner!, methodArgs, classArgs, loader) is { } pointee ? pointee + "*" : null,
        SigKind.Array => DescribeBindingType(type.Inner!, methodArgs, classArgs, loader) is { } multi ? $"{multi}[{type.Rank}]" : null,
        SigKind.GenericMethodVar => type.VarNumber < (methodArgs?.Length ?? 0)
            ? methodArgs![type.VarNumber].FullName
            : $"!!{type.VarNumber}",
        SigKind.GenericVar => type.VarNumber < (classArgs?.Length ?? 0)
            ? classArgs![type.VarNumber].FullName
            : $"!{type.VarNumber}",
        SigKind.GenericInst or SigKind.TypeToken => TryDescribeToken(type, loader),
        _ => null,
    };

    /// <summary>トークン型の完全名 (定義ローダで解決。解決不能な面はバインド照合を諦める → fail-closed)。</summary>
    private static string? TryDescribeToken(SigType type, TypeLoader loader) {
        try {
            return loader.ResolveToken(type)?.FullName;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
            or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
            return null;
        }
    }

    // ---- 宣言上のパラメータ型名 (i4 統合面のオーバーロード判別) ----

    /// <summary>戻り型の完全名 (FindMethodThroughChain の戻り型照合用)。candidate のトークン解決は
    /// 候補メソッドの宣言ローダで行う (トークンは自画像の TypeDef rid を指すため)。解決不能 /
    /// 例外時は null (照合をワイルドカードに倒す)。</summary>
    private string? SafeDescribeReturn(SigType? type, TypeLoader? loader = null) {
        if (type is null)
            return null;
        try {
            if (type.Kind == SigKind.Void)
                return "";
            return type.Kind == SigKind.TypeToken || type.Kind == SigKind.GenericInst
                ? (loader ?? _loader).ResolveToken(type)?.FullName
                : DescribeBindingType(type, null, null, loader ?? _loader);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>署名上のパラメータ型名を得る (intrinsic ゲートが IntrinsicContext に渡し、
    /// char / bool 等の i4 統合面のオーバーロード判別に使われる)。</summary>
    private string ParamTypeName(SigType type, GenericContext? context) => type.Kind switch {
        SigKind.Boolean => "System.Boolean",
        SigKind.Char => "System.Char",
        SigKind.I1 => "System.SByte",
        SigKind.U1 => "System.Byte",
        SigKind.I2 => "System.Int16",
        SigKind.U2 => "System.UInt16",
        SigKind.I4 => "System.Int32",
        SigKind.U4 => "System.UInt32",
        SigKind.I8 => "System.Int64",
        SigKind.U8 => "System.UInt64",
        SigKind.R4 => "System.Single",
        SigKind.R8 => "System.Double",
        SigKind.String => "System.String",
        SigKind.Object => "System.Object",
        SigKind.SzArray => ParamTypeName(type.Inner!, context) + "[]",
        SigKind.ByRef => ParamTypeName(type.Inner!, context) + "&",
        SigKind.Pointer => ParamTypeName(type.Inner!, context) + "*",
        SigKind.TypeToken => TryResolveTypeName(type.Token, context),
        SigKind.GenericMethodVar => type.VarNumber < (context?.MethodArgs.Length ?? 0)
            ? context!.MethodArgs[type.VarNumber].FullName : $"!!{type.VarNumber}",
        SigKind.GenericVar => type.VarNumber < (context?.ClassArgs.Length ?? 0)
            ? context!.ClassArgs[type.VarNumber].FullName : $"!{type.VarNumber}",
        SigKind.GenericInst when type.Args is not null =>
            TryResolveTypeName(type.Token, context) + "<" +
            string.Join(", ", type.Args.Select(argument => ParamTypeName(argument, context))) + ">",
        _ => "",
    };

    /// <summary>MethodSpec の宣言パラメータ型名を、メソッド型引数 (!!n) を実引数で置換してから求める
    /// (AppendFormatted&lt;char&gt; と AppendFormatted&lt;int&gt; 等 i4 統合面の intrinsic 判別に使う)。</summary>
    private string SubstitutedParamTypeName(SigType type, VmType[] methodArgs) => type.Kind switch {
        SigKind.GenericMethodVar when type.VarNumber < methodArgs.Length => methodArgs[type.VarNumber].FullName,
        SigKind.SzArray => SubstitutedParamTypeName(type.Inner!, methodArgs) + "[]",
        SigKind.ByRef => SubstitutedParamTypeName(type.Inner!, methodArgs) + "&",
        SigKind.Pointer => SubstitutedParamTypeName(type.Inner!, methodArgs) + "*",
        _ => ParamTypeName(type, null),
    };

    /// <summary>トークン型の名前解決 (未対応のアセンブリ外参照 / 依存アセンブリ欠落は
    /// 型名不要のため空文字列にフォールバック。fail-closed は実際の呼出解決が担う)。</summary>
    private string TryResolveTypeName(uint token, GenericContext? context) {
        try {
            return _objectEngine.ResolveTypeToken((int)token, context)?.FullName ?? "";
        } catch (NotSupportedException) {
            return "";
        } catch (AssemblyDependencyNotFoundException) {
            // intrinsic ファサード面での呼出がまだあり得るため、ここでは即拒否しない
            // (解決順 ①実アセンブリ → ③intrinsic → ④fail-closed の ③ を生かす)
            return "";
        }
    }
}
