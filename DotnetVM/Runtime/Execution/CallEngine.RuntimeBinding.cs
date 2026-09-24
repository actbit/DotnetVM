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
    /// Runtime binding は型の provenance が確認できた場合だけ試行する。以前は同じ FullName
    /// に InternalCall が 1 面でも登録されていれば guest TypeDef を許可していたため、fake
    /// System.Type 等から別の managed/runtime 面へ到達できた。また unresolved TypeRef を
    /// 一律許可していたため、任意の AssemblyRef を付けた TypeRef が intrinsic の FullName
    /// 照合へ落ちる抜け道になっていた。
    /// </summary>
    private static bool CanAttemptRuntimeBinding(VmType? type) => type is not null &&
        IsAllowedRuntimeBindingType(type);

    /// <summary>
    /// TypeRef の解決失敗は provenance の証拠ではない。CoreLib の TypeDef がロードされて
    /// いない構成で必要な Unsafe shim だけを、期待する AssemblyRef identity と組み合わせて
    /// 明示的に許可する。SynchronizationContext / CTS / AssemblyLoadContext などの通常の
    /// surface は既知 framework contract なら intrinsic facade として解決されるため、ここへ
    /// 追加して unresolved のまま許可してはいけない。
    /// </summary>
    private bool CanAttemptRuntimeBinding(string typeName, int typeRefRid, VmType? type) {
        if (!CanAttemptRuntimeBinding(type))
            return type is null && IsExplicitUnresolvedRuntimeSurface(typeName, typeRefRid);
        // An intrinsic facade is only trusted when its TypeRef ultimately points at a known
        // framework contract (or the current module). This closes the missing-AssemblyRef path
        // while preserving legacy intrinsic facades such as WebClient for ordinary dispatch.
        return type is not VmIntrinsicType || IsTrustedIntrinsicTypeRef(typeRefRid, typeName);
    }

    private bool IsExplicitUnresolvedRuntimeSurface(string typeName, int typeRefRid) {
        if (typeName is not ("System.Runtime.CompilerServices.Unsafe" or
            "System.Runtime.CompilerServices.RuntimeHelpers" or
            "System.Runtime.InteropServices.MemoryMarshal" or
            "System.Buffer"))
            return false;
        var scope = TerminalResolutionScope(typeRefRid);
        if (scope.Table != TableKind.AssemblyRef)
            return false;
        var identity = _loader.Image.GetAssemblyRefIdentity(scope.Rid);
        // The Unsafe package normally references System.Runtime on modern target packs, while
        // older target packs can carry a direct Unsafe AssemblyRef. Both are pinned by token.
        var token = identity.PublicKeyToken.ToLowerInvariant();
        return typeName switch {
            "System.Runtime.CompilerServices.Unsafe" =>
                (identity.Name, token) is
                    ("System.Private.CoreLib", "7cec85d7bea7798e") or
                    ("System.Runtime", "b03f5f7f11d50a3a") or
                    ("System.Runtime.CompilerServices.Unsafe", "b03f5f7f11d50a3a"),
            "System.Runtime.CompilerServices.RuntimeHelpers" or "System.Buffer" =>
                identity.Name == "System.Private.CoreLib" && token == "7cec85d7bea7798e",
            "System.Runtime.InteropServices.MemoryMarshal" =>
                (identity.Name, token) is
                    ("System.Private.CoreLib", "7cec85d7bea7798e") or
                    ("System.Runtime.InteropServices", "b03f5f7f11d50a3a"),
            _ => false,
        };
    }

    private bool IsTrustedIntrinsicTypeRef(int typeRefRid, string? typeName) {
        var scope = TerminalResolutionScope(typeRefRid);
        if (scope.Table != TableKind.AssemblyRef)
            // Module-scoped TypeRef は guest 自身の宣言を意味する。実 TypeDef が無い
            // 場合に intrinsic facade へ落ちるため、untrusted loader では FullName
            // runtime binding の provenance として扱わない。
            return scope.Table == TableKind.Module && _loader.IsTrustedCoreLib;
        var identity = _loader.Image.GetAssemblyRefIdentity(scope.Rid);
        // WebClient is an explicitly host-added device surface. Its test/contract assembly is
        // intentionally not a BCL strong-name contract, so allow only the exact surface/contract
        // pair when the VM has installed that facade; other names still require known BCL identity.
        return TypeLoader.IsKnownFrameworkContract(identity) ||
            (typeName == "System.Net.WebClient" && identity.Name == "WebClientContract" &&
             !identity.IsStrongNamed && _loader.FindIntrinsicType(typeName) is not null);
    }

    private bool IsKnownFrameworkTypeRef(int typeRefRid) {
        var scope = TerminalResolutionScope(typeRefRid);
        return scope.Table == TableKind.AssemblyRef &&
            TypeLoader.IsKnownFrameworkContract(_loader.Image.GetAssemblyRefIdentity(scope.Rid));
    }

    private bool IsTrustedTypeToken(uint token, int depth = 0) {
        if (depth > 64)
            return false;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        return table switch {
            TableKind.TypeDef => true,
            TableKind.TypeRef => IsTrustedIntrinsicTypeRef(rid, null),
            TableKind.TypeSpec => IsTrustedTypeToken(_loader.GetTypeSpecDefinitionToken(rid), depth + 1),
            _ => false,
        };
    }

    private bool CanAttemptRuntimeBindingForTypeSpec(int typeSpecRid, VmType type) =>
        type is not VmIntrinsicType || IsTrustedTypeToken(_loader.GetTypeSpecDefinitionToken(typeSpecRid));

    private (TableKind Table, int Rid) TerminalResolutionScope(int typeRefRid) {
        return _loader.Image.GetTerminalTypeRefScope(typeRefRid);
    }

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
