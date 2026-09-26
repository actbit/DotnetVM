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
    // ---- 呼出 (call / callvirt) ----

    public StackSlot? Call(int token, InterpreterFrame caller, bool isCallvirt, int constrainedToken,
        bool tailCallAllowed, out TailCallRequest? tailCallRequest) {
        tailCallRequest = null;
        foreach (var argument in caller.Stack.CopySlots())
            VmLifetime.EnsureLive(argument);
        // 未登録 intrinsic はこの時点では例外にしない (callvirt ならレシーバのゲスト実装を
        // 引数ポップ後に試すため。旧来の即時例外は最後のフォールバックで再現する)
        var target = caller.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmMethod dynamicMethod
            ? new CallTarget {
                Arity = dynamicMethod.Signature.ParamTypes.Length + (dynamicMethod.Signature.HasThis ? 1 : 0),
                Method = dynamicMethod,
                Name = dynamicMethod.Name,
                ParamCount = dynamicMethod.Signature.ParamTypes.Length,
                HasThis = dynamicMethod.Signature.HasThis,
            }
            : ResolveCallTarget(token, caller.Context, throwOnMissingIntrinsic: false);
        // 特権判定は callee ではなく実際の呼出元 loader 基準 (caller.Method.Loader)。
        var callerDomain = CallerDomainOf(caller);

        // 引数はスタック上では逆順
        var args = new StackSlot[target.Arity];
        for (var i = target.Arity - 1; i >= 0; i--)
            args[i] = caller.Stack.Pop();

        // constrained. 付けた値型レシーバの前処理 (ECMA III.2.2): 値型レシーバを同値型で
        // ボックス化しておく。仮想ディスパッチ (レシーバ実行時型) と constrained. 多重面の
        // intrinsic 受けの両方で同じ形状を用いる
        // (例: constrained. DayOfWeek + callvirt Object::ToString → enum box の ToString 面へ
        // 仮想ディスパッチが実行時型 (DayOfWeek) で解決される)。
        // ldloca.s 経由の ByRef レシーバは参照先スロットを見て、enum の場合のみボックス化する:
        // enum は Int32 バックのため仮想ディスパッチが実行時型を解決できず、Enum.ToString() 面が
        // VmBoxedValue を要求するため。それ以外の値型 (decimal/TimeSpan やプリミティブ) は
        // ByRef のまま渡し、dispatch (ReceiverRuntimeType が参照先を読む) と binding
        // (NormalizeByRefReceiver) に委ねる (既存規約を維持し、回帰を避ける)
        if (constrainedToken != 0 && isCallvirt && target.HasThis &&
            args[0].Kind is not StackKind.Object) {
            var constrainedType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context,
                caller.Method.DynamicTokens);
            if (constrainedType.IsValueType) {
                var valueSlot = args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef receiverByRef
                    ? receiverByRef.Slot
                    : args[0];
                if (args[0].Kind != StackKind.ByRef || constrainedType.IsEnum) {
                    var fields = valueSlot.Kind == StackKind.ValueType
                        ? ((VmStructValue)valueSlot.ObjectValue!).Clone().Fields
                        : [valueSlot];
                    args[0] = StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(constrainedType, fields)));
                }
            }
        }

        // デリゲート実体の callvirt Invoke (カスタム delegate 宣言の abstract Invoke / Action・Func
        // ファサードの未登録面の両方をここで引き受ける)。null レシーバは NRE
        if (isCallvirt && target.HasThis && target.Name is "Invoke" or "BeginInvoke") {
            if (SlotOps.IsNullReference(args[0]))
                throw new UnhandledGuestException("System.NullReferenceException", null);
            if (args[0].ObjectValue is VmDelegate @delegate) {
                gate.ConsumeInstruction(); // 呼出ゲート: クォータ + セーフポイント
                gate.CheckSafepoint();
                return InvokeDelegate(@delegate, args);
            }
        }

        if (target.Intrinsic is { } intrinsic) {
            // プリミティブの instance メソッド (int.ToString() 等) は ldloca 経由の
            // ByRef レシーバで来るため、値に読み替えてから渡す (constrained. 値型レシーバも同様)。
            // ただし可変状態をローカルスロットに保持する構造体ファサード (補間ハンドラ等) は
            // ByRef のまま渡す (状態の読み書きが参照先スロットに対して行われる必要がある)。
            if (target.HasThis && args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef thisByRef &&
                !PreservesByRefReceiver(target.DeclaringType))
                args[0] = thisByRef.Read();
            // callvirt で intrinsic 宣言型 (System.Object 等) をターゲットにする場合、
            // レシーバの実行時型にゲスト側 override があればそちらを優先する (仮想ディスパッチ)
            if (isCallvirt && target.HasThis) {
                if (SlotOps.IsNullReference(args[0]))
                    throw new UnhandledGuestException("System.NullReferenceException",
                        $"null レシーバで {target.DeclaringType}::{target.Name} を呼び出しました。");
                if (TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
                    // 優先順位 ①: override が実型 (CoreLib TypeDef) の場合、IL 実行の前に
                    // ランタイムバインド (署名キー) を試す (culture / 表現境界に依存する面の委譲)
                    if (TryInvokeBinding(guestOverride, target.MethodArgs, args, out var overrideBound, callerDomain))
                        return overrideBound;
                    var context = BuildCallContext(target, guestOverride, args[0]);
                    if (tailCallAllowed && invoker.TryCreateTailCall(caller, guestOverride, args,
                            context, out tailCallRequest))
                        return null;
                    var guestRet = invoker.Invoke(guestOverride, args, context);
                    return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? guestRet : null;
                }
                // ファサード インターフェースの明示的実装 (EII) を実行時型の InterfaceMap で解決する
                // (明示的実装はメソッド名が規定名と異なるため名前照合では見つからない)。
                // EII 本体にも優先順位 ① (ランタイムバインド) を照合する (Enum の
                // IFormattable EII 等の culture/表現境界面の委譲のため)。EII 本体名は
                // ドット付きのため短名バインドへの誤ヒットは無い
                if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
                    if (TryInvokeBinding(explicitImpl, target.MethodArgs, args, out var explicitBound,
                            callerDomain, target.ClassArgs))
                        return explicitBound;
                    var context = BuildCallContext(target, explicitImpl, args[0]);
                    if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args,
                            context, out tailCallRequest))
                        return null;
                    var guestRet = invoker.Invoke(explicitImpl, args, context);
                    return SlotOps.SignatureReturnsValue(explicitImpl.Signature) ? guestRet : null;
                }
                // レシーバが VM ランタイムオブジェクト (typeof() 結果等) の場合、その実面
                // (System.Type / MethodBase) に登録された intrinsic を宣言型より優先する
                // (例: callvirt Object::ToString → System.Type::ToString)
                if (RuntimeReceiverSurfaceType(args[0].ObjectValue) is { } surfaceType &&
                    _objectEngine.TryGetIntrinsicThroughHierarchy(surfaceType, target.Name!, target.Arity,
                        target.HasThis, out var surfaceImpl)) {
                    gate.ConsumeInstruction();
                    gate.CheckSafepoint();
                    _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
                    return surfaceImpl(_intrinsicContext, args);
                }
            }
            // constrained. 値型レシーバが intrinsic 宣言型 (System.Object 等) に着地した場合、
            // ECMA-335 規約に従い値をボックス化してから渡す (ゲスト実装は上の仮想ディスパッチで優先済み)
            if (constrainedToken != 0 && target.HasThis &&
                args[0].Kind is not (StackKind.Object or StackKind.ByRef)) {
                var constrainedType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context,
                    caller.Method.DynamicTokens);
                if (constrainedType.IsValueType) {
                    var fields = args[0].Kind == StackKind.ValueType
                        ? ((VmStructValue)args[0].ObjectValue!).Clone().Fields
                        : [args[0]];
                    args[0] = StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(constrainedType, fields)));
                }
            }
            // intrinsic 呼出ゲート: ① 追加クォータ消費 ② セーフポイント検査
            // ③ I/O はデバイス経由・値は VM オブジェクトモデル正規化 (実装側契約)
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            // 宣言上のパラメータ型名を渡す (char/bool/int 等、i4 統合面のオーバーロード判別用)
            _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
            // メソッド型実引数も渡す (IsBitwiseEquatable<T>() 等の値パラメータ 0 個の面が
            // T を判別するため。MethodSpec 経由の解決では CallTarget が保持している)。
            // クラス型実引数も同様 (構築型経由の面のため)。
            // 無い場合は空にして前回呼出の残留 (stale) を残さない)
            _intrinsicContext.MethodTypeArgumentNames =
                target.MethodArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.ClassTypeArgumentNames =
                target.ClassArgs?.Select(t => t.FullName).ToArray() ?? [];
            _intrinsicContext.MethodTypeArguments = target.MethodArgs ?? [];
            _intrinsicContext.ClassTypeArguments = target.ClassArgs ?? [];
            using var roots = _intrinsicContext.RegisterTransientRoots?.Invoke(args);
            return target.DeclaringType == "System.Threading.Interlocked"
                ? InvokeInterlocked(intrinsic, target.ParamTypeNames ?? [], args, alreadyGated: true)
                : intrinsic(_intrinsicContext, args);
        }

        // 解決未了 (未登録 intrinsic): レシーバへの仮想ディスパッチを最終試行してから拒否
        if (target.Method is null)
            return FailOrDispatchLate(target, caller, isCallvirt, args, callerDomain,
                tailCallAllowed, out tailCallRequest);

        // ゲスト呼出。callvirt はレシーバの実行時型で仮想解決 (VTable 相当)。
        // constrained. 値型レシーバは ByRef/ValueType スロットで来るためディスパッチがそのまま適用される
        var method = target.Method!;
        if (isCallvirt && method.Signature.HasThis) {
            if (SlotOps.IsNullReference(args[0]))
                throw new UnhandledGuestException("System.NullReferenceException",
                    $"null レシーバで {method.DeclaringType.FullName}::{method.Name} を呼び出しました。");
            method = DispatchVirtual(method, args[0]);
        }

        // 優先順位 ①: ランタイムバインド (署名照合) を最優先で解決する。
        // 構築型の実引数 (ClassArgs) もキー化に使う (IComparable`1<uint> の !0 等)
        // callerDomain は呼出元フレーム基準 (特権面の callee 基準判定はしない)。
        if (TryInvokeBinding(method, target.MethodArgs, args, out var bound, callerDomain, target.ClassArgs))
            return bound;

        // callvirt で宣言どおりに着地した (実行時型で override が見つからなかった) 場合、
        // レシーバが VM ランタイムオブジェクト (typeof() 結果等) なら実行時型は intrinsic 面
        // (System.Type / MethodBase) に相当するため、実面に登録された intrinsic を IL 本体より
        // 先に実行する (C5.5 Wave 4: Object を IlPreferred に上げると Object::ToString IL 内の
        // callvirt Object::ToString (レシーバ = Type ファサード) が自己再帰するため。
        // intrinsic ターゲット経路 (CallEngine.Call の intrinsic 分岐) と同一の救済)
        if (isCallvirt && method.Signature.HasThis && ReferenceEquals(method, target.Method) &&
            RuntimeReceiverSurfaceType(args[0].ObjectValue) is { } runtimeSurface &&
            _objectEngine.TryGetIntrinsicThroughHierarchy(runtimeSurface, method.Name,
                method.Signature.ParamTypes.Length + 1, method.Signature.HasThis, out var runtimeSurfaceImpl)) {
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
            return runtimeSurfaceImpl(_intrinsicContext, args);
        }

        if (method.Body is null) {
            // 本体の無い面 (InternalCall / P/Invoke / 抽象宣言) はバインドが無い限り IL 実行できない。
            // 配列レシーバのインターフェース面 (IEnumerable<T> 等) は SZArrayHelper 経由で
            // 先に合成する (CLR のコンパイラ支援と同一)
            if (isCallvirt && method.Signature.HasThis &&
                TryInvokeArrayInterface(method.DeclaringType.FullName, method.Name,
                    method.Signature.ParamTypes.Length, target.ClassArgs, target.MethodArgs, args) is { } arrayResult)
                return arrayResult;
            // callvirt の場合のみレシーバ実行時型への最終救済 (EII) を試してから legacy intrinsic へ。
            // EII 本体にも ① を照合する (intrinsic 分岐と同一)
            if (isCallvirt && method.Signature.HasThis &&
                TryDispatchInterfaceKey(method.DeclaringType.FullName, method.Name,
                    ParamTypeNamesOf(method, target.MethodArgs), args[0]) is { } explicitImpl) {
                if (TryInvokeBinding(explicitImpl, target.MethodArgs, args, out var explicitBound,
                        callerDomain, target.ClassArgs))
                    return explicitBound;
                var implContext = BuildCallContext(target, explicitImpl, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args,
                        implContext, out tailCallRequest))
                    return null;
                var implRet = invoker.Invoke(explicitImpl, args, implContext);
                return SlotOps.SignatureReturnsValue(explicitImpl.Signature) ? implRet : null;
            }
            // 優先順位 ③: legacy intrinsic (名前 + 引数個数) の救済
            if (TryInvokeLegacyIntrinsic(method, args, out var legacy))
                return legacy;
            Interpreter.ThrowNoBody(method); // P/Invoke は OperationNotAllowed、抽象宣言は NotSupportedException (監査性)
        }

        // 表現境界 (設計原則 3): 委譲継続面の IL は実行しない (① バインド → ③ legacy → ④ 拒否 のみ)
        if (DelegateContinuingSurfaces.Contains(method.DeclaringType.FullName)) {
            if (TryInvokeLegacyIntrinsic(method, args, out var delegated))
                return delegated;
            throw new OperationNotAllowedException(
                $"面 {method.DeclaringType.FullName}::{method.Name} は表現境界 (DelegateContinuingSurfaces) により IL 実行が禁止されており、登録済みのランタイムバインド / intrinsic もありません。");
        }

        var context2 = BuildCallContext(target, method, method.Signature.HasThis ? args[0] : default);
        if (tailCallAllowed && invoker.TryCreateTailCall(caller, method, args, context2, out tailCallRequest))
            return null;
        if (invoker is Interpreter interpreter &&
            interpreter.TryInvokeCompiled(method, args, context2, out var compiledResult))
            return SlotOps.SignatureReturnsValue(method.Signature) ? compiledResult : null;
        var ret = invoker.Invoke(method, args, context2);
        return SlotOps.SignatureReturnsValue(method.Signature) ? ret : null;
    }

    /// <summary>ByRef レシーバを値に読み替えずにそのまま渡す intrinsic 宣言型
    /// (ローカルスロットに可変状態を保持する構造体ファサード)。</summary>
    private static bool PreservesByRefReceiver(string? declaringType) =>
        declaringType is "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler"
            or "System.Runtime.CompilerServices.AsyncTaskMethodBuilder"
            or "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"
            or "System.Runtime.CompilerServices.TaskAwaiter"
            or "System.Runtime.CompilerServices.TaskAwaiter`1"
            or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"
            or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"
            or "System.Threading.Tasks.ValueTask"
            or "System.Threading.Tasks.ValueTask`1"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter`1"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter";

    /// <summary>VM ランタイムオブジェクトのレシーバが属する intrinsic 面 (仮想ディスパッチの
    /// 実行時型相当)。ランタイムオブジェクトは対応する intrinsic ファサード型の実体として振る舞う。</summary>
    private static string? RuntimeReceiverSurfaceType(object? receiver) => receiver switch {
        VmRuntimeObject => "System.Type",
        VmRuntimeMethod => "System.Reflection.MethodBase",
        VmRuntimeField => "System.Reflection.FieldInfo",
        VmRuntimeProperty => "System.Reflection.PropertyInfo",
        VmAssemblyObject => "System.Reflection.Assembly",
        VmAssemblyLoadContext => "System.Runtime.Loader.AssemblyLoadContext",
        VmClassInstance { AssemblyLoadContextHandle: not null } => "System.Runtime.Loader.AssemblyLoadContext",
        VmAssemblyNameObject => "System.Reflection.AssemblyName",
        VmMemoryStreamObject => "System.IO.MemoryStream",
        VmExpressionObject { Kind: VmExpressionKind.Lambda } => "System.Linq.Expressions.LambdaExpression",
        VmExpressionObject => "System.Linq.Expressions.Expression",
        VmIntrinsicInstance { InstanceType.FullName: "System.Reflection.Emit.DynamicMethod" } => "System.Reflection.Emit.DynamicMethod",
        VmIntrinsicInstance { InstanceType.FullName: "System.Reflection.Emit.ILGenerator" } => "System.Reflection.Emit.ILGenerator",
        _ => null,
    };

    /// <summary>解決未了の呼出 (未登録 intrinsic) の最終処理。callvirt ならレシーバの実行時型に
    /// ゲスト実装があればそれを呼び (constrained callvirt による構造体の interface 実装呼出等)、
    /// 無ければ未登録 intrinsic として拒否する。callerDomain は呼出元フレーム基準。</summary>
    private StackSlot? FailOrDispatchLate(CallTarget target, InterpreterFrame caller, bool isCallvirt,
        StackSlot[] args, BindingDomain callerDomain, bool tailCallAllowed,
        out TailCallRequest? tailCallRequest) {
        tailCallRequest = null;
        // 配列レシーバのインターフェース面 (IEnumerable<T>.GetEnumerator 等) は
        // SZArrayHelper 経由で合成する (CLR と同一のコンパイラー支援面)
        if (isCallvirt && target.HasThis && TryInvokeArrayInterface(
                target.DeclaringType, target.Name, target.ParamCount,
                target.ClassArgs, target.MethodArgs, args) is { } arrayResult)
            return arrayResult;
        if (isCallvirt && target.HasThis) {
            if (TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
                // 優先順位 ①: IL 実行の前にランタイムバインドを試す (上の intrinsic 経路と同じ)
                if (TryInvokeBinding(guestOverride, target.MethodArgs, args, out var lateBound, callerDomain))
                    return lateBound;
                var context = BuildCallContext(target, guestOverride, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, guestOverride, args, context, out tailCallRequest))
                    return null;
                var ret = invoker.Invoke(guestOverride, args, context);
                return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? ret : null;
            }
            // ファサード インターフェースの明示的実装 (EII) もここで救済する。
            // EII 本体にも ① を照合する (intrinsic 分岐と同一)
            if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
                if (TryInvokeBinding(explicitImpl, target.MethodArgs, args, out var explicitBound,
                        callerDomain, target.ClassArgs))
                    return explicitBound;
                var context = BuildCallContext(target, explicitImpl, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args, context, out tailCallRequest))
                    return null;
                var ret = invoker.Invoke(explicitImpl, args, context);
                return SlotOps.SignatureReturnsValue(explicitImpl.Signature) ? ret : null;
            }
        }
        throw new OperationNotAllowedException(
            $"intrinsic {target.DeclaringType}::{target.Name} (引数 {target.Arity} 個) は未登録です。BCL 面は VM 起動時に登録されたランタイムバインド / intrinsic のみ提供されます。" +
            (target.ParamTypeNames is { } names ? $" 署名: {string.Join(",", names)}" : ""));
    }

    /// <summary>
    /// 呼出先メソッド用の GenericContext を構築する。クラス型引数は MemberRef/TypeSpec 親の構築型引数だが、
    /// レシーバの実行時型が実引数を持つ場合はそちらを優先する (仮想ディスパッチで派生/実装側の
    /// ジェネリック定義に着地した場合、その !0 はレシーバ自身の実引数を指すため)。
    /// </summary>
    internal GenericContext? BuildCallContext(CallTarget target, VmMethod method, in StackSlot receiver) {
        var classArgs = target.ClassArgs;
        var declaringParamCount = method.DeclaringType.GenericParamCount;
        if (declaringParamCount > 0 &&
            SlotOps.TryGetReceiverTypeArguments(receiver, declaringParamCount, out var receiverArgs))
            classArgs = receiverArgs;
        return GenericContext.Of(classArgs, target.MethodArgs);
    }
}
