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
    // ---- ランタイムバインド (優先順位 ① / legacy 救済 ③) ----

    /// <summary>呼出元フレームの loader から caller domain を求める。
    /// 特権 binding (TrustedCoreLib) の可否は callee (method.Loader) ではなく
    /// 実際に呼んだ側 (caller.Method.Loader) で判定する。</summary>
    private static BindingDomain CallerDomainOf(InterpreterFrame? caller) =>
        caller?.Method?.Loader?.IsTrustedCoreLib == true
            ? BindingDomain.TrustedCoreLib : BindingDomain.Guest;

    /// <summary>呼出元 loader から caller domain を求める (ResolveCallTarget 経路用。
    /// ResolveCallTarget を実行する CallEngine インスタンスは呼出元メソッド所属の
    /// エンジンであり _loader が呼出元 loader そのもの)。</summary>
    private BindingDomain CallerDomainOfLoader() =>
        _loader.IsTrustedCoreLib ? BindingDomain.TrustedCoreLib : BindingDomain.Guest;

    /// <summary>
    /// Runtime binding は FullName だけで guest TypeDef に適用してはいけない。CoreLib
    /// 実型は trusted loader、CoreLib 未ロード時は TypeLoader が明示的に合成した intrinsic
    /// facade のみを許可する。対象型を Task だけに列挙しないことで、CancellationToken、
    /// SynchronizationContext、ValueTask など全 runtime surface に同じ provenance policy を
    /// 適用する。
    /// </summary>
    private static bool IsAllowedRuntimeBindingType(VmType? type) => type switch {
        VmIntrinsicType => true,
        VmClassType cls => cls.Loader?.IsTrustedCoreLib == true,
        VmConstructedType constructed => IsAllowedRuntimeBindingType(constructed.Definition),
        _ => false,
    };

    /// <summary>
    /// A guest type may still use an ordinary legacy intrinsic when no signature binding exists
    /// for that surface (for example the CoreLib Unsafe compatibility shim).  Once a signature
    /// binding is registered, however, a colliding guest type must not reach it unless its
    /// provenance is trusted or intrinsic.
    /// </summary>
    private bool CanAttemptRuntimeBinding(VmType? type) => type is not null &&
        (IsAllowedRuntimeBindingType(type) ||
         _intrinsics.HasBindingForType(type.FullName, BindingOrigin.InternalCall) ||
         !_intrinsics.HasBindingForType(type.FullName));

    // Some CoreLib reference TypeRefs (notably Unsafe) intentionally have no VM TypeDef when the
    // host CoreLib is not loaded.  The absence of a resolved type is not provenance evidence of a
    // fake definition, so retain the legacy unresolved-TypeRef lookup in that narrow case.
    private bool CanAttemptRuntimeBinding(string typeName, VmType? type) => type is null ||
        CanAttemptRuntimeBinding(type);

    private VmType? TryResolveTypeRefForBinding(int typeRefRid) {
        try {
            return _loader.ResolveTypeRefType(typeRefRid);
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
            or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
            return null;
        }
    }

    /// <summary>caller domain を考慮したバインド照合。trusted caller は特権面を優先し、
    /// 汎用面にも到達できる。guest caller は汎用面のみ (特権面は遮断)。</summary>
    private bool TryGetBindingWithCaller(BindingKey guestKey, BindingDomain callerDomain, out IntrinsicImpl impl) {
        if (callerDomain == BindingDomain.TrustedCoreLib) {
            var trustedKey = guestKey.WithDomain(BindingDomain.TrustedCoreLib);
            if (_intrinsics.TryGetBinding(trustedKey, out impl, out _, callerDomain))
                return true;
        }
        return _intrinsics.TryGetBinding(guestKey, out impl, out _, callerDomain);
    }

    /// <summary>呼出解決地点 (TypeRef / 構築ファサード親) でのランタイムバインド照合。
    /// 型名 + 宣言パラメータ型名から署名キーを組み、完全一致 → 全引数一致面の順で解決する。
    /// 型名が解決できていないパラメータ (空文字列) が混ざる場合は照合しない。
    /// callerDomain は呼出元 loader から明示的に渡す (callee 基準にしない)。</summary>
    private bool TryGetResolvedBinding(string typeName, string name, bool hasThis, string[] paramTypeNames,
        BindingDomain callerDomain, out IntrinsicImpl impl) {
        impl = null!;
        if (paramTypeNames.Any(string.IsNullOrEmpty))
            return TryGetBindingWithCaller(hasThis
                ? BindingKey.InstanceAnyParams(typeName, name)
                : BindingKey.StaticAnyParams(typeName, name), callerDomain, out impl);
        var key = hasThis ? BindingKey.Instance(typeName, name, paramTypeNames)
                          : BindingKey.Static(typeName, name, paramTypeNames);
        return TryGetBindingWithCaller(key, callerDomain, out impl);
    }

    /// <summary>解決済みメソッドをランタイムバインド (署名キー) で呼び出す (優先順位 ①)。
    /// パラメータ型名は「そのメソッドを定義したローダ」で解決する (TypeToken は自画像の
    /// TypeDef rid を指すため)。ジェネリック変数は MethodSpec の実引数で置換し、実引数が無い
    /// 場合は開いた名 (!n / !!n) のままキー化する (登録側の開いたキーと一致)。
    /// callerDomain は呼出元フレームの loader から明示的に渡す (callee 基準にしない)。</summary>
    private bool TryInvokeBinding(VmMethod method, VmType[]? methodArgs, StackSlot[] args, out StackSlot? result,
        BindingDomain callerDomain, VmType[]? classArgs = null) {
        result = null;
        if (!CanAttemptRuntimeBinding(method.DeclaringType))
            return false;
        var names = ParamTypeNamesOf(method, methodArgs, classArgs);
        if (names is null || names.Any(string.IsNullOrEmpty)) {
            // ジェネリック未解決 (!!) 等で正確キーが構築できない場合でも、全引数一致面
            // (AnyParams バインド) は引数型名なしで受けられる (Interlocked.CompareExchange<T>
            // 等、JIT intrinsic ダミー IL を持つ面を ② IL 実行に落とさないための救済)
            // callerDomain を明示する (trusted 特権 AnyParams があれば trusted caller のみ)。
            var anyKey = method.Signature.HasThis
                ? BindingKey.InstanceAnyParams(method.DeclaringType.FullName, method.Name)
                : BindingKey.StaticAnyParams(method.DeclaringType.FullName, method.Name);
            if (!TryGetBindingWithCaller(anyKey, callerDomain, out var anyImpl))
                return false;
            NormalizeByRefReceiver(method, args);
            _intrinsicContext.MethodTypeArgumentNames = methodArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.ClassTypeArgumentNames = classArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.MethodTypeArguments = methodArgs ?? [];
            _intrinsicContext.ClassTypeArguments = classArgs ?? [];
            result = InvokeDelegated(anyImpl, [], args);
            return true;
        }
        var declaringName = method.DeclaringType.FullName;
        // 戻り型名をキーに含める (op_Implicit / op_Explicit 群のように同一パラメータ列で
        // 戻り型のみ異なる面を区別する)。既存の全登録面は戻り型ワイルドカードなので、
        // 実引数キー (精密) → 戻り型ワイルドカードキーの順に照合して後方互換を保つ
        var returnName = DescribeBindingType(method.Signature.ReturnType, methodArgs, classArgs, method.Loader) ?? "";
        var key = method.Signature.HasThis
            ? BindingKey.InstanceWithReturn(declaringName, method.Name, returnName, names)
            : BindingKey.StaticWithReturn(declaringName, method.Name, returnName, names);
        // callerDomain は呼出元フレーム基準 (引数で明示)。trusted caller は特権面を優先し、
        // 汎用面にも到達できる。guest caller は汎用面のみ。
        var hasBinding = TryGetBindingWithCaller(key, callerDomain, out var impl);
        if (!hasBinding && returnName.Length != 0)
            hasBinding = TryGetBindingWithCaller(key.WithAnyReturn(), callerDomain, out impl);
        if (!hasBinding)
            hasBinding = TryInvokeOpenGenericBinding(method, methodArgs, names, returnName, callerDomain, args, out impl);
        if (!hasBinding && TryGetBindingWithCaller(method.Signature.HasThis
                ? BindingKey.InstanceAnyParams(declaringName, method.Name)
                : BindingKey.StaticAnyParams(declaringName, method.Name), callerDomain, out impl)) {
            NormalizeByRefReceiver(method, args);
            _intrinsicContext.MethodTypeArgumentNames = methodArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.ClassTypeArgumentNames = classArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.MethodTypeArguments = methodArgs ?? [];
            _intrinsicContext.ClassTypeArguments = classArgs ?? [];
            result = InvokeMethodDelegated(method, impl, names, args);
            return true;
        }
        if (!hasBinding) {
            // 特権面が存在するのに guest から呼ばれた場合は fail-closed を明示する
            // (InternalCall 未登録との区別 = 監査性のため OperationNotAllowed)。
            if (callerDomain == BindingDomain.Guest && HasTrustedBinding(method, methodArgs, names, returnName, classArgs))
                throw new OperationNotAllowedException(
                    $"面 {declaringName}::{method.Name} は trusted CoreLib 専用の特権面であり、ゲストからの直接呼出は許可されていません。");
            return false;
        }
        NormalizeByRefReceiver(method, args);
        // メソッド型実引数 (MethodSpec の T 等) を intrinsic 側に渡す (値パラメータ 0 個の
        // ジェネリック面でも T を判別できるようにする)。クラス型実引数 (!0 等) も同様に
        // 渡す (EqualityComparer<T>.get_Default 等のクラスジェネリック面のため)
        _intrinsicContext.MethodTypeArgumentNames = methodArgs?.Select(t => t.FullName).ToArray() ?? [];
        _intrinsicContext.ClassTypeArgumentNames = classArgs?.Select(t => t.FullName).ToArray() ?? [];
        _intrinsicContext.MethodTypeArguments = methodArgs ?? [];
        _intrinsicContext.ClassTypeArguments = classArgs ?? [];
        result = InvokeMethodDelegated(method, impl, names, args);
        return true;
    }

    /// <summary>guest から呼ばれたが trusted 特権面が存在するかを調べる
    /// (fail-closed の監査性: 未登録 InternalCall と特権遮断を区別する)。</summary>
    private bool HasTrustedBinding(VmMethod method, VmType[]? methodArgs, string[] concreteNames,
        string concreteReturn, VmType[]? classArgs) {
        var trustedDomain = BindingDomain.TrustedCoreLib;
        var declaringName = method.DeclaringType.FullName;
        var key = method.Signature.HasThis
            ? BindingKey.InstanceWithReturn(declaringName, method.Name, concreteReturn, concreteNames)
            : BindingKey.StaticWithReturn(declaringName, method.Name, concreteReturn, concreteNames);
        if (_intrinsics.TryGetBinding(key.WithDomain(trustedDomain), out _, out _, trustedDomain))
            return true;
        if (concreteReturn.Length != 0 &&
            _intrinsics.TryGetBinding(key.WithAnyReturn().WithDomain(trustedDomain), out _, out _, trustedDomain))
            return true;
        // 開いたジェネリック面の特権有無も確認する
        var loader = method.Loader;
        if (loader is null)
            return false;
        var openNames = new string[concreteNames.Length];
        for (var i = 0; i < openNames.Length; i++) {
            var open = DescribeBindingType(method.Signature.ParamTypes[i], null, null, loader);
            if (open is null)
                return false;
            openNames[i] = open;
        }
        var openReturn = DescribeBindingType(method.Signature.ReturnType, null, null, loader) ?? "";
        var openKey = method.Signature.HasThis
            ? BindingKey.InstanceWithReturn(declaringName, method.Name, openReturn, openNames)
            : BindingKey.StaticWithReturn(declaringName, method.Name, openReturn, openNames);
        if (_intrinsics.TryGetBinding(openKey.WithDomain(trustedDomain), out _, out _, trustedDomain))
            return true;
        return openReturn.Length != 0 &&
            _intrinsics.TryGetBinding(openKey.WithAnyReturn().WithDomain(trustedDomain), out _, out _, trustedDomain);
    }

    /// <summary>開いたジェネリックキー (!!0 / !0) へのフォールバック照合 (優先順位 ① の救済)。
    /// ジェネリック面 (Unsafe.Add&lt;T&gt; 等) は開いたキーで 1 件登録し、呼出側の具体名
    /// (例: System.Char&amp;) とは一致しないため、実引数なしの開いた名で再照合する。
    /// 実行時の判別は具体名 (ParameterTypeNames) とメソッド型実引数で行う。
    /// AnyParams ワイルドカードを使わず既知のオーバーロード形状のみに限定するための機構。</summary>
    private bool TryInvokeOpenGenericBinding(VmMethod method, VmType[]? methodArgs, string[] concreteNames,
        string concreteReturn, BindingDomain callerDomain, StackSlot[] args, out IntrinsicImpl impl) {
        impl = null!;
        var loader = method.Loader;
        if (loader is null || method.Signature.ParamTypes.Length != concreteNames.Length)
            return false;
        // 開いた名を構築する。パラメータ名だけでなく戻り型がジェネリック変数 (!!0 等)
        // の場合も実引数で具体化されているため、開いた戻り型キーを照合する必要がある。
        var openNames = new string[concreteNames.Length];
        var differs = false;
        for (var i = 0; i < openNames.Length; i++) {
            var open = DescribeBindingType(method.Signature.ParamTypes[i], null, null, loader);
            if (open is null)
                return false;
            openNames[i] = open;
            if (!string.Equals(open, concreteNames[i], StringComparison.Ordinal))
                differs = true;
        }
        var openReturn = DescribeBindingType(method.Signature.ReturnType, null, null, loader) ?? "";
        if (!differs && string.Equals(openReturn, concreteReturn, StringComparison.Ordinal))
            return false;
        var openKey = method.Signature.HasThis
            ? BindingKey.InstanceWithReturn(method.DeclaringType.FullName, method.Name, openReturn, openNames)
            : BindingKey.StaticWithReturn(method.DeclaringType.FullName, method.Name, openReturn, openNames);
        if (!TryGetBindingWithCaller(openKey, callerDomain, out impl) &&
            (openReturn.Length == 0 || !TryGetBindingWithCaller(openKey.WithAnyReturn(), callerDomain, out impl)))
            return false;
        return true;
    }

    /// <summary>解決済みメソッドを legacy intrinsic (名前 + 引数個数キー) で呼び出す (優先順位 ③)。
    /// 基底面からの継承解決 (TryGetIntrinsicThroughHierarchy) も含む。</summary>
    private bool TryInvokeLegacyIntrinsic(VmMethod method, StackSlot[] args, out StackSlot? result) {
        result = null;
        var declaringName = method.DeclaringType.FullName;
        var arity = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0);
        if (!_intrinsics.TryGet(new IntrinsicKey(declaringName, method.Name, arity, method.Signature.HasThis), out var impl) &&
            !_objectEngine.TryGetIntrinsicThroughHierarchy(declaringName, method.Name, arity, method.Signature.HasThis, out impl))
            return false;
        NormalizeByRefReceiver(method, args);
        result = InvokeMethodDelegated(method, impl, ParamTypeNamesOf(method, null) ?? [], args);
        return true;
    }

    private StackSlot? InvokeMethodDelegated(VmMethod method, IntrinsicImpl impl, string[] paramNames, StackSlot[] args) {
        if (method.DeclaringType.FullName == "System.Threading.Interlocked")
            return InvokeInterlocked(impl, paramNames, args);
        return InvokeDelegated(impl, paramNames, args);
    }

    private StackSlot? InvokeInterlocked(IntrinsicImpl impl, string[] paramNames, StackSlot[] args, bool alreadyGated = false) {
        StackSlot? Invoke() => alreadyGated ? impl(_intrinsicContext, args) : InvokeDelegated(impl, paramNames, args);
        lock (_intrinsicContext.Shared.InterlockedGate) {
            if (args.Length > 0 && args[0].ObjectValue is VmByRef location) {
                lock (location.Container)
                    return Invoke();
            }
            return Invoke();
        }
    }

    /// <summary>委譲実装 (ランタイムバインド / legacy intrinsic) を intrinsic 呼出ゲート経由で実行する。
    /// IL 実行と完全に等価な制約 (① 追加クォータ消費 ② セーフポイント検査 ③ 値は VM オブジェクト
    /// モデルに正規化) を受ける。</summary>
    private StackSlot? InvokeDelegated(IntrinsicImpl impl, string[] paramNames, StackSlot[] args) {
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        _intrinsicContext.ParameterTypeNames = paramNames;
        using var roots = _intrinsicContext.RegisterTransientRoots?.Invoke(args);
        return impl(_intrinsicContext, args);
    }

    /// <summary>プリミティブ等の instance 面 (ByRef レシーバ) を値に読み替える。可変状態を
    /// ローカルスロットに保持する構造体ファサード (補間ハンドラ等) は除く。既存 intrinsic
    /// 経路と同じ規約。実行が確定した後でのみ呼ぶ (失敗経路で引数を壊さない)。</summary>
    private void NormalizeByRefReceiver(VmMethod method, StackSlot[] args) {
        if (method.Signature.HasThis && args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef thisByRef &&
            !PreservesByRefReceiver(method.DeclaringType.FullName))
            args[0] = thisByRef.Read();
    }
}
