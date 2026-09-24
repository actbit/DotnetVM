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
    // ---- 仮想ディスパッチ ----

    /// <summary>callvirt の実行時型ディスパッチ。宣言メソッドのスロットキー (名前 + 署名) を
    /// レシーバの VTable / InterfaceMap で解決し (署名精度)、解決できない場合は
    /// 名前+引数個数の従来照合にフォールバックする (ファサード系 / 表外メソッドの救済)。</summary>
    public VmMethod DispatchVirtual(VmMethod declared, in StackSlot receiver) =>
        TryDispatchDeclared(declared, receiver) ??
        TryDispatchVirtual(declared.Name, declared.Signature.ParamTypes.Length, receiver) ??
        declared;

    /// <summary>レシーバスロット (ByRef / 構造体を含む) から実行時型を取り出す。</summary>
    private static VmType? ReceiverRuntimeType(in StackSlot receiver) {
        var value = receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef
            ? byRef.Slot
            : receiver;
        return value.Kind switch {
            StackKind.ValueType => value.ObjectValue is VmStructValue sv ? (VmType)sv.StructType : null,
            StackKind.Object => value.ObjectValue switch {
                VmClassInstance ci => (VmType)ci.ClassType,
                VmBoxedValue bv => bv.Type,
                // VmString の仮想ディスパッチ (名前+引数個数) はオーバーロード誤解決の恐れが
                // あるため実型を与えない (バインド / 置換面 / legacy の従来経路を優先)。
                // インターフェースキー照合 (署名完全一致) は TryDispatchInterfaceKey で別途解決する
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>宣言メソッド (仮想 / インターフェース) をレシーバの実行時型のディスパッチ表で解決する。
    /// 宣言型がレシーバの継承チェーンに属さない (ファサード宣言等) 場合は null。
    /// VmString レシーバも宣言スロットキー (署名完全一致) なら安全に解決できるため
    /// 実型 (System.String) を与える (C5.5 Wave 4: Object::ToString の IL 化で
    /// "str".ToString() が宣言どおり Object::ToString に着地し、② IL の GetType() 経路が
    /// 文字列自身でなく型名を返す退行のため。名前+引数個数の TryDispatchVirtual へは
    /// 従来どおり VmString を渡さない — InterfaceReceiverType のコメント参照)。</summary>
    private VmMethod? TryDispatchDeclared(VmMethod declared, in StackSlot receiver) {
        var receiverType = ReceiverRuntimeType(receiver) ?? InterfaceReceiverType(receiver);
        if (receiverType is null || declared.DeclaringType is not VmClassType declaringClass)
            return null;
        var definition = receiverType is VmConstructedType constructed ? constructed.Definition : receiverType;
        if (definition is not VmClassType receiverClass)
            return null;
        var maps = (receiverClass.Loader ?? _loader).EnsureDispatchMaps(receiverClass);

        if (declaringClass.IsInterface) {
            // インターフェース呼出: 宣言スロットはインターフェース定義文脈のキーでそのまま照合する
            var parameters = (declared.Loader ?? _loader).TryResolveSlotParams(declared.Signature.ParamTypes);
            return parameters is null
                ? null
                : maps.InterfaceMap.GetValueOrDefault(VmSlotKeys.InterfaceSlotKey(declaringClass.FullName, declared.Name, parameters));
        }

        // 仮想呼出: 宣言型文脈のスロットキーを継承パスの型引数でレシーバ文脈へ置換して照合する
        var pathArgs = InheritanceTypeArguments(receiverType, declaringClass);
        if (pathArgs is null)
            return null;
        var declaredParams = (declared.Loader ?? _loader).TryResolveSlotParams(declared.Signature.ParamTypes);
        if (declaredParams is null)
            return null;
        var substitution = pathArgs.Length > 0 ? new GenericContext { ClassArgs = pathArgs } : null;
        var query = substitution is null ? declaredParams
            : [.. declaredParams.Select(p => GenericSubstitutor.Substitute(p, substitution))];
        return maps.VTable.GetValueOrDefault(VmSlotKeys.Of(declared.Name, query))?.Method;
    }

    /// <summary>receiverType から targetClass (宣言型) までの継承パスで、targetClass の
    /// ジェネリックパラメータが receiverType 文脈で何に実体化するかを求める
    /// (例: IntRepo : Repo&lt;int&gt; のレシーバで宣言型 Repo&lt;T&gt; の !0 → System.Int32)。
    /// チェーンに無い場合は null。</summary>
    private static VmType[]? InheritanceTypeArguments(VmType receiverType, VmType targetClass) {
        var current = receiverType;
        GenericContext? substitution = null;
        while (current is not null) {
            VmType definition;
            VmType[] args;
            if (current is VmConstructedType constructed) {
                definition = constructed.Definition;
                args = [.. constructed.TypeArguments.Select(a => GenericSubstitutor.Substitute(a, substitution))];
            } else {
                definition = current;
                args = [];
            }
            if (ReferenceEquals(definition, targetClass) || definition.FullName == targetClass.FullName)
                return args;
            substitution = args.Length > 0 ? new GenericContext { ClassArgs = args } : null;
            current = definition.BaseType;
        }
        return null;
    }

    /// <summary>実行時型から最派生のゲスト実装を探す。見つからなければ null (intrinsic 宣装にフォールバック)。
    /// ディスパッチ表が構築できる実行時型は VTable から選び (同一 名前+引数個数 のオーバーロードは
    /// 最派生の宣言を優先)、ファサード系 (表外) は従来どおり基底連鎖の名前照合にフォールバックする。
    /// 構築ジェネリック型のインスタンス (VmBoxedValue の VmConstructedType 型 等) も定義側に解いて探索する。</summary>
    public VmMethod? TryDispatchVirtual(string name, int paramCount, in StackSlot receiver) {
        var receiverType = ReceiverRuntimeType(receiver);
        if (receiverType is null)
            return null;
        var definition = receiverType is VmConstructedType constructed ? constructed.Definition : receiverType;
        if (definition is VmClassType receiverClass) {
            var maps = (receiverClass.Loader ?? _loader).EnsureDispatchMaps(receiverClass);
            VmMethod? best = null;
            var bestDepth = -1;
            foreach (var slot in maps.VTable.Values) {
                if (slot.Method.Name != name || slot.Method.Signature.ParamTypes.Length != paramCount)
                    continue;
                var depth = InheritanceDepth(receiverType, slot.Method.DeclaringType);
                // 最派生の宣言を優先 (同深度 = 同じクラス内のオーバーロードは宣言順 = rid 順)
                if (depth > bestDepth || (depth == bestDepth && best is not null && slot.Method.MethodDefRid < best.MethodDefRid)) {
                    best = slot.Method;
                    bestDepth = depth;
                }
            }
            if (best is not null)
                return best;
        }
        return FindMethodByScanThroughChain(receiverType, name, paramCount);
    }

    /// <summary>配列レシーバのインターフェース面を SZArrayHelper 経由で合成する。
    /// CLR では SZArray が IList&lt;T&gt; 等を暗黙実装し、呼出は SZArrayHelper の実体へ
    /// 振り分けられる (コンパイラ支援)。VM も同一に振り分ける:
    /// GetEnumerator (IEnumerable/IEnumerable&lt;T&gt;) は SZArrayHelper.GetEnumerator&lt;T&gt; の
    /// 実 IL を実行し、get_Count (ICollection 系) は配列長を直接返す。
    /// 非該当 (非配列レシーバ等) は null (従来フローへ)。</summary>
    private StackSlot? TryInvokeArrayInterface(string? declaringTypeName, string? name, int paramCount,
        VmType[]? classArgs, VmType[]? methodArgs, StackSlot[] args) {
        _ = classArgs;
        _ = methodArgs;
        if (declaringTypeName is null || name is null || args.Length == 0 ||
            args[0].ObjectValue is not VmArray array)
            return null;
        var isEnumerable = declaringTypeName is "System.Collections.IEnumerable"
            or "System.Collections.Generic.IEnumerable`1";
        var isCountable = declaringTypeName is "System.Collections.ICollection"
            or "System.Collections.Generic.ICollection`1"
            or "System.Collections.Generic.IReadOnlyCollection`1";
        if (name == "GetEnumerator" && paramCount == 0 && isEnumerable) {
            var helper = FindSZArrayHelper();
            if (helper is null)
                return null;
            // SZArrayHelper.GetEnumerator<T>() はインスタンス面 (0 引数。this が配列)。
            // 本体が ldarg.0 から T[] を取り出して SZArrayEnumerator<T> を構築する
            var elementType = array.ArrayType.ElementType;
            var method = helper.Methods.FirstOrDefault(m =>
                m.Name == "GetEnumerator" && m.Signature.HasThis &&
                m.Signature.GenericParamCount == 1 &&
                m.Signature.ParamTypes.Length == 0 && m.Body is not null);
            if (method is null)
                return null;
            var context = GenericContext.Of(null, [elementType]);
            var result = invoker.Invoke(method, [args[0]], context);
            return SlotOps.SignatureReturnsValue(method.Signature) ? result : null;
        }
        if (name == "get_Count" && paramCount == 0 && isCountable)
            return StackSlot.OfInt32(array.Length);
        return null;
    }

    /// <summary>SZArrayHelper の TypeDef を探す (CoreLib 実装画像を優先)。</summary>
    private VmClassType? FindSZArrayHelper() {
        const string name = "System.SZArrayHelper";
        try {
            if (_loader.FindTypeByFullName(name) is VmClassType direct)
                return direct;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
            or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
        }
        if (_loader.Context is { } context) {
            foreach (var loader in context.Loaders) {
                try {
                    if (loader.FindTypeByFullName(name) is VmClassType found)
                        return found;
                } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
                    or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
                    continue;
                }
            }
        }
        return null;
    }

    /// <summary>ファサード インターフェースの明示的実装 (EII) 用: 宣言型名 + パラメータ型名から
    /// インターフェーススロットキーを組み、レシーバの InterfaceMap で解決する。
    /// 型名が解決できていないパラメータが混ざる場合は照合を諦める (null)。</summary>
    private VmMethod? TryDispatchInterfaceKey(string? declaringTypeName, string name, string[]? paramTypeNames, in StackSlot receiver) {
        if (declaringTypeName is null || paramTypeNames is null || paramTypeNames.Any(string.IsNullOrEmpty))
            return null;
        var receiverType = ReceiverRuntimeType(receiver) ?? InterfaceReceiverType(receiver);
        if (receiverType is null)
            return null;
        var definition = receiverType is VmConstructedType constructed ? constructed.Definition : receiverType;
        if (definition is not VmClassType receiverClass)
            return null;
        var maps = (receiverClass.Loader ?? _loader).EnsureDispatchMaps(receiverClass);
        return maps.InterfaceMap.GetValueOrDefault(
            declaringTypeName + "::" + name + "(" + string.Join(",", paramTypeNames) + ")");
    }

    /// <summary>署名精度照合 (インターフェーススロットキー / 宣言スロットキー) 専用のレシーバ
    /// 実行時型。VmString の場合のみ実型 (System.String CoreLib TypeDef) を返す。String は
    /// IConvertible 等を EII 実装し、キーは署名完全一致なのでオーバーロード誤解決がない。
    /// 名前+引数個数の TryDispatchVirtual には VmString を渡さない (Replace 等の
    /// (string,string)/(char,char) オーバーロードを実型 IL へ誤解決させる恐れ)。
    /// StringType は VM 単位 (InterpreterServices) で保持する。</summary>
    private VmType? InterfaceReceiverType(in StackSlot receiver) =>
        receiver.Kind == StackKind.Object && receiver.ObjectValue is VmString && _services.StringType is { } s
            ? s : null;

    /// <summary>レシーバ型の継承チェーン上で宣言型 declaring が現れるまでのステップ数
    /// (最派生 = 0。チェーンに無い場合は -1)。同一型は参照または完全名で判定する
    /// (ファサード⇔実型の同一視は IsAssignableTo と同じ緩和)。</summary>
    private static int InheritanceDepth(VmType receiverType, VmType declaring) {
        var depth = 0;
        for (VmType? t = receiverType; t is not null; depth++) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (ReferenceEquals(t, declaring) || t.FullName == declaring.FullName)
                return depth;
            t = t.BaseType;
        }
        return -1;
    }

    /// <summary>従来照合: 名前+引数個数で基底連鎖を辿る (ファサード系 / ディスパッチ表外の救済)。</summary>
    private static VmMethod? FindMethodByScanThroughChain(VmType receiverType, string name, int paramCount) {
        for (VmType? t = receiverType; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (t is not VmClassType cls)
                break;
            var found = cls.Methods.FirstOrDefault(m =>
                m.Name == name &&
                m.Signature.ParamTypes.Length == paramCount &&
                !m.IsAbstract && m.Body is not null);
            if (found is not null)
                return found;
            t = cls.BaseType;
        }
        return null;
    }

    /// <summary>ゲストオブジェクトの暗黙 ToString (Console.Write(object) / String.Concat(object) 用)。
    /// レシーバの実行時型にゲスト実装 (override) があればそれを仮想ディスパッチし、
    /// 無ければ null を返して intrinsic 側の既定書式にフォールバックする。</summary>
    public VmString? InvokeToStringSlot(StackSlot slot) {
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmString str)
            return str;
        var guest = TryDispatchVirtual("ToString", 0, slot);
        if (guest is null)
            return null;
        var ret = invoker.Invoke(guest, [slot], GenericContext.Of(
            SlotOps.TryGetReceiverTypeArguments(slot, guest.DeclaringType.GenericParamCount, out var args) ? args : [], null));
        return ret.ObjectValue as VmString;
    }
}
