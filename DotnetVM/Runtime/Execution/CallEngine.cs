using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

/// <summary>呼出面の実行サービス: 呼出トークンの解決 (MethodDef/MemberRef/MethodSpec)、
/// intrinsic 呼出ゲートの適用、callvirt の実行時型ディスパッチ (VTable 相当)、デリゲート呼出、
/// constrained. レシーバのボックス化。全ての呼出は <see cref="IGuestInvoker"/> と
/// <see cref="IExecutionGate"/> を通るため、IL 実行と intrinsic 実行で制約が等価になる。</summary>
internal sealed class CallEngine(
    InterpreterServices services,
    IExecutionGate gate,
    IGuestInvoker invoker,
    ObjectEngine objectEngine) {
    private readonly TypeLoader _loader = services.Loader;
    private readonly IntrinsicRegistry _intrinsics = services.Intrinsics;
    private readonly VmHeap _heap = services.Heap;
    private readonly IntrinsicContext _intrinsicContext = services.IntrinsicContext;
    private readonly ObjectEngine _objectEngine = objectEngine;
    private readonly VmCoreLibSurfaces? _coreLibSurfaces = services.CoreLibSurfaces;
    private readonly InterpreterServices _services = services;

    // ---- 呼出 (call / callvirt) ----

    public StackSlot? Call(int token, InterpreterFrame caller, bool isCallvirt, int constrainedToken) {
        // 未登録 intrinsic はこの時点では例外にしない (callvirt ならレシーバのゲスト実装を
        // 引数ポップ後に試すため。旧来の即時例外は最後のフォールバックで再現する)
        var target = ResolveCallTarget(token, caller.Context, throwOnMissingIntrinsic: false);
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
            var constrainedType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context);
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
                var constrainedType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context);
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
            return FailOrDispatchLate(target, isCallvirt, args, callerDomain);

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
            or "System.Runtime.CompilerServices.TaskAwaiter`1";

    /// <summary>VM ランタイムオブジェクトのレシーバが属する intrinsic 面 (仮想ディスパッチの
    /// 実行時型相当)。ランタイムオブジェクトは対応する intrinsic ファサード型の実体として振る舞う。</summary>
    private static string? RuntimeReceiverSurfaceType(object? receiver) => receiver switch {
        VmRuntimeObject => "System.Type",
        VmRuntimeMethod => "System.Reflection.MethodBase",
        _ => null,
    };

    /// <summary>解決未了の呼出 (未登録 intrinsic) の最終処理。callvirt ならレシーバの実行時型に
    /// ゲスト実装があればそれを呼び (constrained callvirt による構造体の interface 実装呼出等)、
    /// 無ければ未登録 intrinsic として拒否する。callerDomain は呼出元フレーム基準。</summary>
    private StackSlot? FailOrDispatchLate(CallTarget target, bool isCallvirt, StackSlot[] args, BindingDomain callerDomain) {
        // 配列レシーバのインターフェース面 (IEnumerable<T>.GetEnumerator 等) は
        // SZArrayHelper 経由で合成する (CLR と同一のコンパイラ支援面)
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

    // ---- デリゲート呼出 ----

    /// <summary>デリゲート呼出 (callvirt Invoke/BeginInvoke のデリゲート実体ディスパッチ)。
    /// マルチキャストは全エントリを順に実行し、最後の戻り値を返す (CLR 規約)。
    /// 各呼出は通常の Invoke ゲート経由 (クォータ/セーフポイント/EH 機構を共有)。</summary>
    public StackSlot? InvokeDelegate(VmDelegate @delegate, StackSlot[] args) {
        var invocations = @delegate.Invocations;
        if (invocations.Count == 0)
            throw new UnhandledGuestException("System.ArgumentException",
                "呼出エントリのないマルチキャスト デリゲートは呼び出せません。");
        var argCount = args.Length - 1;
        StackSlot last = default;
        VmMethod lastMethod = invocations[^1].Method;
        foreach (var invocation in invocations) {
            var method = invocation.Method;
            if (method.Signature.ParamTypes.Length != argCount)
                throw new UnhandledGuestException("System.ArgumentException",
                    $"デリゲート {@delegate.DeclaredType.FullName} の呼出 ({method.DeclaringType.FullName}::{method.Name}) に引数個数が一致しません (期待 {method.Signature.ParamTypes.Length}, 実際 {argCount})。");
            GenericContext? context = null;
            if (method.Signature.HasThis &&
                SlotOps.TryGetReceiverTypeArguments(invocation.Target, method.DeclaringType.GenericParamCount, out var classArgs))
                context = GenericContext.Of(classArgs, null);
            if (method.Signature.HasThis) {
                var callArgs = new StackSlot[argCount + 1];
                callArgs[0] = invocation.Target;
                for (var i = 0; i < argCount; i++)
                    callArgs[i + 1] = args[i + 1];
                last = invoker.Invoke(method, callArgs, context);
            } else {
                var callArgs = new StackSlot[argCount];
                for (var i = 0; i < argCount; i++)
                    callArgs[i] = args[i + 1];
                last = invoker.Invoke(method, callArgs, context);
            }
        }
        return SlotOps.SignatureReturnsValue(lastMethod.Signature) ? last : null;
    }

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

    // ---- 呼出トークン解決 ----

    /// <summary>calli のオペランド (StandAloneSig トークン) から呼出規約 + 署名をデコードする。</summary>
    public MethodSignature DecodeStandAloneSignature(int token) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table != TableKind.StandAloneSig)
            throw new BadImageFormatException($"calli のオペランド 0x{token:X8} は StandAloneSig ではありません。");
        return SignatureDecoder.DecodeMethodSignature(
            _loader.Image.GetBlob(_loader.Image.Tables.GetRowIndex(TableKind.StandAloneSig, rid, 0)).ToArray(),
            _loader.Image.Limits?.MaxSignatureDepth ?? 64,
            _loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
    }

    /// <summary>呼出トークンを解決する (Arity = 引数個数、インスタンスは this 込み)。
    /// context は呼出元メソッドのジェネリック実引数 (MemberRef の TypeSpec 親が !0 を含む場合の置換に使う)。
    /// throwOnMissingIntrinsic = false の場合、TypeRef 親の未登録 intrinsic は即例外にせず
    /// Intrinsic = null の CallTarget を返す (Call 側でレシーバの仮想ディスパッチを試してから判定する)。</summary>
    public CallTarget ResolveCallTarget(int token, GenericContext? context, bool throwOnMissingIntrinsic = true) {
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        switch (table) {
            case TableKind.MethodDef: {
                var method = _loader.GetMethodByToken((uint)token)
                    ?? throw new BadImageFormatException($"MethodDef トークン 0x{token:X8} を解決できません。");
                return new CallTarget {
                    Arity = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                };
            }
            case TableKind.MemberRef: {
                // MemberRef 署名から hasThis/引数個数を得る
                var signature = SignatureDecoder.DecodeMethodSignature(
                    _loader.Image.GetMemberRefSignature(rid).ToArray(),
                    _loader.Image.Limits?.MaxSignatureDepth ?? 64,
                    _loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
                var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                var name = _loader.GetMemberRefName(rid);

                var parent = _loader.Image.Tables.DecodeCoded(
                    TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                var paramNames = signature.ParamTypes.Select(t => ParamTypeName(t, context)).ToArray();
                if (parent.Table == TableKind.TypeRef) {
                    var typeName = _loader.GetMemberRefParentTypeName(rid)!;
                    // 優先順位 ①: ランタイムバインド (署名照合。callerDomain は呼出元 loader 基準)
                    var callerDomain = CallerDomainOfLoader();
                    if (TryGetResolvedBinding(typeName, name, signature.HasThis, paramNames, callerDomain, out var bound)) {
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = bound,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ParamTypeNames = paramNames,
                        };
                    }
                    // 優先順位 ②: IL 優先面は legacy intrinsic より先に実型 IL へ解決する
                    // (CoreLib IL 実行の全面化。内部面バインドが揃った型から IlPreferred へ移す。
                    //  legacy の名前 + 引数個数照合は CoreLib の新しいため (ReadOnlySpan を取る
                    //  Concat 合成等) 誤経由する恐れがあり、IL 優先面では ② を先に見る。
                    //  IlPreferredFaces は型単位でなく面単位 (パラメータ型名一致) の IL 優先)
                    if ((DelegateContinuingSurfaces.PrefersIl(typeName) ||
                         DelegateContinuingSurfaces.PrefersIlFace(typeName, name, paramNames)) &&
                        _loader.ResolveTypeRefType(parent.Rid) is VmClassType preferred) {
                        var ilFirst = FindMethodThroughChain(preferred, name, signature.ParamTypes);
                        if (ilFirst is { Body: not null })
                            return new CallTarget {
                                Arity = arity,
                                Method = ilFirst,
                                Name = ilFirst.Name,
                                ParamCount = ilFirst.Signature.ParamTypes.Length,
                                HasThis = ilFirst.Signature.HasThis,
                            };
                    }
                    // 置換面 (C5): IlPreferred に載っていない型でも VM CoreLib (DotnetVM.CoreLib)
                    // の managed IL が面を置換する場合 (System.Convert 等) は実型 IL へ解決させる
                    // (実際の差し替えは Interpreter.Invoke の choke point)。対応表に載っていない
                    // 面 (Convert.ToInt32(object) 等のボックス化経由面) は従来どおり ③ で処理される
                    if (_coreLibSurfaces?.HasFace(typeName, name, paramNames) == true &&
                        _loader.ResolveTypeRefType(parent.Rid) is VmClassType faceOwner) {
                        var faceMethod = FindMethodThroughChain(faceOwner, name, signature.ParamTypes);
                        if (faceMethod is { Body: not null })
                            return new CallTarget {
                                Arity = arity,
                                Method = faceMethod,
                                Name = faceMethod.Name,
                                ParamCount = faceMethod.Signature.ParamTypes.Length,
                                HasThis = faceMethod.Signature.HasThis,
                            };
                    }
                    if (_intrinsics.TryGet(new IntrinsicKey(typeName, name, arity, signature.HasThis), out var impl))
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = impl,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ParamTypeNames = paramNames,
                        };
                    // 継承面のフォールバック: 派生ファサード型から基底連鎖を辿って解決する
                    // (例: InvalidOperationException::get_Message → System.Exception に登録された面)
                    if (_objectEngine.TryGetIntrinsicThroughHierarchy(typeName, name, arity, signature.HasThis, out var inherited)) {
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = inherited,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ParamTypeNames = paramNames,
                        };
                    }
                    // 実 TypeDef に解決できる TypeRef 親 (依存アセンブリの型 / ネスト型) は
                    // そのメソッドを実体として解決する。callvirt でも Call 側でレシーバの
                    // 実行時型による仮想ディスパッチが効くため、宣言解決の直接化は安全。
                    // ただし表現境界 (委譲継続面) の実型 IL には落ちない (① → ③ → ④ のみ)
                    if (_loader.ResolveTypeRefType(parent.Rid) is VmClassType realClass &&
                        !DelegateContinuingSurfaces.Contains(realClass.FullName)) {
                        var resolved = FindMethodThroughChain(realClass, name, signature.ParamTypes, signature.ReturnType);
                        if (resolved is { Body: not null })
                            return new CallTarget {
                                Arity = arity,
                                Method = resolved,
                                Name = resolved.Name,
                                ParamCount = resolved.Signature.ParamTypes.Length,
                                HasThis = resolved.Signature.HasThis,
                            };
                    }
                    // 未登録 intrinsic: 即例外にせず解決未了の CallTarget を返す
                    // (constrained callvirt ではレシーバの実行時型にゲスト実装があるため。
                    //  Call 側で最終ディスパッチが失敗した時点で改めて例外にする)
                    if (!throwOnMissingIntrinsic)
                        return new CallTarget {
                            Arity = arity,
                            DeclaringType = typeName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ParamTypeNames = paramNames,
                        };
                    throw new OperationNotAllowedException(
                        $"intrinsic {typeName}::{name} (引数 {arity} 個) は未登録です。BCL 面は VM 起動時に登録された intrinsic のみ提供されます。");
                }
                if (parent.Table == TableKind.TypeDef) {
                    var owner = _loader.GetTypeDef(parent.Rid);
                    var method = FindMethodThroughChain(owner, name, signature.ParamTypes)
                        ?? throw new BadImageFormatException($"MemberRef 0x{token:X8} の解決先メソッド {owner.FullName}::{name} が見つかりません。");
                    return new CallTarget {
                        Arity = arity,
                        Method = method,
                        Name = method.Name,
                        ParamCount = method.Signature.ParamTypes.Length,
                        HasThis = method.Signature.HasThis,
                    };
                }
                if (parent.Table == TableKind.TypeSpec)
                    return ResolveConstructedMethodTarget(token, rid, parent.Rid, signature, name, context, throwOnMissingIntrinsic);
                throw new NotSupportedException($"MemberRef 親テーブル {parent.Table} は未対応です。");
            }
            case TableKind.MethodSpec:
                return ResolveMethodSpecTarget(token, rid, context, throwOnMissingIntrinsic);
            default:
                throw new BadImageFormatException($"呼出トークン 0x{token:X8} のテーブル 0x{(int)table:X2} が不正です。");
        }
    }

    /// <summary>TypeSpec 親 (構築型) の MemberRef を解決する。例: callvirt int32 class List`1&lt;int32&gt;::get_Item(int32)。
    /// 定義がファサード型 (IEnumerator`1&lt;int&gt; 等の BCL インターフェース) なら intrinsic 面を解決し、
    /// 実呼出は Call でレシーバの実行時型に仮想ディスパッチされる。</summary>
    private CallTarget ResolveConstructedMethodTarget(int token, int memberRefRid, int typeSpecRid,
        MethodSignature signature, string name, GenericContext? context, bool throwOnMissingIntrinsic = true) {
        var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
        var constructed = _objectEngine.ResolveConstructedParent(typeSpecRid, context);

        // 構築ファサード型 (BCL 汎用インターフェース等) → ランタイムバインド / intrinsic 面
        if (constructed.Definition is VmIntrinsicType facade) {
            var facadeParamNames = signature.ParamTypes.Select(t => ParamTypeName(t, context)).ToArray();
            // 優先順位 ①: ランタイムバインド (署名照合。callerDomain は呼出元 loader 基準)
            if (TryGetResolvedBinding(facade.FullName, name, signature.HasThis, facadeParamNames, CallerDomainOfLoader(), out var boundImpl)) {
                return new CallTarget {
                    Arity = arity,
                    Intrinsic = boundImpl,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ParamTypeNames = facadeParamNames,
                    ClassArgs = constructed.TypeArguments,
                };
            }
            if (_intrinsics.TryGet(new IntrinsicKey(facade.FullName, name, arity, signature.HasThis), out var impl) ||
                _objectEngine.TryGetIntrinsicThroughHierarchy(facade.FullName, name, arity, signature.HasThis, out impl)) {
                return new CallTarget {
                    Arity = arity,
                    Intrinsic = impl,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ParamTypeNames = facadeParamNames,
                    ClassArgs = constructed.TypeArguments,
                };
            }
            // 未登録 intrinsic: 即例外にせず解決未了の CallTarget を返す
            // (constrained callvirt ではレシーバの実行時型にゲスト実装があるため)
            if (!throwOnMissingIntrinsic)
                return new CallTarget {
                    Arity = arity,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ParamTypeNames = facadeParamNames,
                    ClassArgs = constructed.TypeArguments,
                };
            throw new OperationNotAllowedException(
                $"intrinsic {facade.FullName}::{name} (引数 {arity} 個) は未登録です。" +
                "構築ファサード型のメソッドは intrinsic に登録された面のみ解決できます。");
        }

        var definition = (VmClassType)constructed.Definition;
        var method = FindMethodThroughChain(definition, name, signature.ParamTypes)
            ?? throw new BadImageFormatException(
                $"MemberRef 0x{token:X8} の解決先メソッド {definition.FullName}::{name} が見つかりません。");
        return new CallTarget {
            Arity = arity,
            Method = method,
            Name = method.Name,
            ParamCount = method.Signature.ParamTypes.Length,
            HasThis = method.Signature.HasThis,
            ClassArgs = constructed.TypeArguments,
        };
    }

    /// <summary>ジェネリックメソッド (MethodSpec) を解決する。Instantiation blob からメソッド型引数を取り出す。
    /// 例: call !!0 class Generics::First&lt;!!0&gt;(!!0[])</summary>
    private CallTarget ResolveMethodSpecTarget(int token, int methodSpecRid, GenericContext? context, bool throwOnMissingIntrinsic = true) {
        var underlying = _loader.Image.Tables.DecodeCoded(
            TableKind.MethodSpec, methodSpecRid, 0, CodedIndexKind.MethodDefOrRef);
        var instantiationBlobIndex = _loader.Image.Tables.GetRowIndex(TableKind.MethodSpec, methodSpecRid, 1);
        var methodArgs = SignatureDecoder.DecodeMethodSpecInstantiation(
            _loader.Image.GetBlob(instantiationBlobIndex).ToArray(),
            _loader.Image.Limits?.MaxSignatureDepth ?? 64,
            _loader.Image.Limits?.MaxGenericNestingDepth ?? 64)
            .Select(t => _loader.ResolveToken(t, context))
            .ToArray();

        if (underlying.Table == TableKind.MethodDef) {
            var method = _loader.GetMethodByToken(Token.From(underlying.Table, underlying.Rid).Value)
                ?? throw new BadImageFormatException($"MethodSpec 0x{token:X8} の解決先メソッドが見つかりません。");
            if (method.Signature.GenericParamCount != methodArgs.Length)
                throw new BadImageFormatException(
                    $"MethodSpec 0x{token:X8} の型引数は {methodArgs.Length} 個ですが、{method} は {method.Signature.GenericParamCount} 個を要求します。");
            return new CallTarget {
                Arity = method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0),
                Method = method,
                Name = method.Name,
                ParamCount = method.Signature.ParamTypes.Length,
                HasThis = method.Signature.HasThis,
                MethodArgs = methodArgs,
            };
        }
        if (underlying.Table == TableKind.MemberRef) {
            var memberRefRid = underlying.Rid;
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(memberRefRid).ToArray(),
                _loader.Image.Limits?.MaxSignatureDepth ?? 64,
                _loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
            var name = _loader.GetMemberRefName(memberRefRid);
            var parent = _loader.Image.Tables.DecodeCoded(
                TableKind.MemberRef, memberRefRid, 0, CodedIndexKind.MemberRefParent);
            if (signature.GenericParamCount != methodArgs.Length)
                throw new BadImageFormatException(
                    $"MethodSpec 0x{token:X8} の型引数は {methodArgs.Length} 個ですが、{name} は {signature.GenericParamCount} 個を要求します。");
            if (parent.Table == TableKind.TypeRef) {
                // intrinsic ジェネリックメソッド (例: DefaultInterpolatedStringHandler::AppendFormatted<T>)。
                // intrinsic キーはジェネリック引数を含まない (CLR の実体化も本体を共有するため)。
                var typeName = _loader.GetMemberRefParentTypeName(memberRefRid)!;
                var specArity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                // 優先順位 ①: ランタイムバインド (署名照合)。実引数を置換したキー → 開いたキー (!!n / !n)
                // の順に照合する (登録側は開いたキーで 1 件、実引数は実行時の宣言型名で判別)
                // callerDomain は呼出元 loader 基準で明示する。
                var concreteParams = signature.ParamTypes
                    .Select(t => SubstitutedParamTypeName(t, methodArgs)).ToArray();
                var openParams = signature.ParamTypes
                    .Select(t => DescribeBindingType(t, null, null, _loader) ?? "").ToArray();
                var methodSpecCaller = CallerDomainOfLoader();
                if (TryGetResolvedBinding(typeName, name, signature.HasThis, concreteParams, methodSpecCaller, out var boundImpl) ||
                    TryGetResolvedBinding(typeName, name, signature.HasThis, openParams, methodSpecCaller, out boundImpl)) {
                    return new CallTarget {
                        Arity = specArity,
                        Intrinsic = boundImpl,
                        DeclaringType = typeName,
                        Name = name,
                        ParamCount = signature.ParamTypes.Length,
                        HasThis = signature.HasThis,
                        // !!n を MethodSpec の実引数で置換した宣言型名 (char/bool 等 i4 統合面の判別に必要)
                        ParamTypeNames = concreteParams,
                        // メソッド型実引数も保持する (IsBitwiseEquatable<T>() 等の値パラメータ
                        // 0 個の面が T を判別するため。Call 側で IntrinsicContext へ設定する)
                        MethodArgs = methodArgs,
                    };
                }
                // 実ジェネリックメソッド (CoreLib の managed IL) を legacy より先に解決する
                // (優先順位 ② > ③。Join<T> 等の実体を持つ面が名前+引数個数の legacy 救済に
                // 誤経由するのを防ぐ。バインド (①) は上で優先済み。ファサード型には実体が
                // 無いため対象外。表現境界の型・可変状態をローカルに持つ構造体ファサード
                // (DefaultInterpolatedStringHandler 等) は legacy を維持する)。
                // legacy (③) は実体の無い面の救済として残す
                VmMethod? realMethodEarly = null;
                try {
                    if (_loader.ResolveTypeRefType(parent.Rid) is VmClassType realClassEarly &&
                        !DelegateContinuingSurfaces.Contains(realClassEarly.FullName) &&
                        !PreservesByRefReceiver(realClassEarly.FullName))
                        realMethodEarly = FindMethodThroughChain(realClassEarly, name, signature.ParamTypes);
                } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException
                    or InvalidOperationException or KeyNotFoundException or AssemblyDependencyNotFoundException) {
                    // 依存欠落等の解決不能は legacy 救済・拒否へ流す (ここで落とさない)
                }
                if (realMethodEarly is { Body: not null }) {
                    if (realMethodEarly.Signature.GenericParamCount != methodArgs.Length)
                        throw new BadImageFormatException(
                            $"MethodSpec 0x{token:X8} の型引数は {methodArgs.Length} 個ですが、{realMethodEarly} は {realMethodEarly.Signature.GenericParamCount} 個を要求します。");
                    return new CallTarget {
                        Arity = specArity,
                        Method = realMethodEarly,
                        Name = realMethodEarly.Name,
                        ParamCount = realMethodEarly.Signature.ParamTypes.Length,
                        HasThis = realMethodEarly.Signature.HasThis,
                        MethodArgs = methodArgs,
                    };
                }
                if (_intrinsics.TryGet(new IntrinsicKey(typeName, name, specArity, signature.HasThis), out var impl) ||
                    _objectEngine.TryGetIntrinsicThroughHierarchy(typeName, name, specArity, signature.HasThis, out impl)) {
                    return new CallTarget {
                        Arity = specArity,
                        Intrinsic = impl,
                        DeclaringType = typeName,
                        Name = name,
                        ParamCount = signature.ParamTypes.Length,
                        HasThis = signature.HasThis,
                        // !!n を MethodSpec の実引数で置換した宣言型名 (char/bool 等 i4 統合面の判別に必要)
                        ParamTypeNames = signature.ParamTypes
                            .Select(t => SubstitutedParamTypeName(t, methodArgs)).ToArray(),
                    };
                }
                // 実体の無い面の legacy 救済の後に実 IL フォールバックは不要 (上で解決済み)。
                // 未登録 intrinsic: 即例外にせず解決未了の CallTarget を返すこともある
                // (constrained callvirt ではレシーバの実行時型にゲスト実装があるため。
                // Call 側で最終ディスパッチが失敗した時点で改めて例外にする)
                if (!throwOnMissingIntrinsic)
                    return new CallTarget {
                        Arity = specArity,
                        DeclaringType = typeName,
                        Name = name,
                        ParamCount = signature.ParamTypes.Length,
                        HasThis = signature.HasThis,
                        ParamTypeNames = signature.ParamTypes
                            .Select(t => SubstitutedParamTypeName(t, methodArgs)).ToArray(),
                        MethodArgs = methodArgs,
                    };
                throw new OperationNotAllowedException(
                    $"intrinsic {typeName}::{name} (引数 {specArity} 個) は未登録です (MethodSpec 経由)。");
            }
            if (parent.Table == TableKind.TypeDef) {
                // 同アセンブリのジェネリックメソッド (Roslyn は MethodDef でも MemberRef 形式で出す)
                var owner = _loader.GetTypeDef(parent.Rid);
                var method = FindMethodThroughChain(owner, name, signature.ParamTypes)
                    ?? throw new BadImageFormatException(
                        $"MethodSpec 0x{token:X8} の解決先メソッド {owner.FullName}::{name} が見つかりません。");
                return new CallTarget {
                    Arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                    MethodArgs = methodArgs,
                };
            }
            if (parent.Table == TableKind.TypeSpec) {
                var constructed = _objectEngine.ResolveConstructedParent(parent.Rid, context);
                if (constructed.Definition is VmIntrinsicType facade) {
                    var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                    var concreteParams = signature.ParamTypes
                        .Select(t => SubstitutedParamTypeName(t, methodArgs)).ToArray();
                    var openParams = signature.ParamTypes
                        .Select(t => DescribeBindingType(t, null, null, _loader) ?? "").ToArray();
                    if (TryGetResolvedBinding(facade.FullName, name, signature.HasThis, concreteParams,
                            CallerDomainOfLoader(), out var facadeImpl) ||
                        TryGetResolvedBinding(facade.FullName, name, signature.HasThis, openParams,
                            CallerDomainOfLoader(), out facadeImpl)) {
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = facadeImpl,
                            DeclaringType = facade.FullName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ClassArgs = constructed.TypeArguments,
                            MethodArgs = methodArgs,
                            ParamTypeNames = concreteParams,
                        };
                    }
                    if (_intrinsics.TryGet(new IntrinsicKey(facade.FullName, name, arity, signature.HasThis), out var facadeLegacy)) {
                        return new CallTarget {
                            Arity = arity,
                            Intrinsic = facadeLegacy,
                            DeclaringType = facade.FullName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ClassArgs = constructed.TypeArguments,
                            MethodArgs = methodArgs,
                            ParamTypeNames = concreteParams,
                        };
                    }
                    if (!throwOnMissingIntrinsic)
                        return new CallTarget {
                            Arity = arity,
                            DeclaringType = facade.FullName,
                            Name = name,
                            ParamCount = signature.ParamTypes.Length,
                            HasThis = signature.HasThis,
                            ParamTypeNames = concreteParams,
                            ClassArgs = constructed.TypeArguments,
                            MethodArgs = methodArgs,
                        };
                    throw new OperationNotAllowedException($"intrinsic {facade.FullName}::{name} (引数 {arity} 個) は未登録です。");
                }
                var definition = (VmClassType)constructed.Definition;
                var method = FindMethodThroughChain(definition, name, signature.ParamTypes)
                    ?? throw new BadImageFormatException(
                        $"MethodSpec 0x{token:X8} の解決先メソッド {definition.FullName}::{name} が見つかりません。");
                return new CallTarget {
                    Arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0),
                    Method = method,
                    Name = method.Name,
                    ParamCount = method.Signature.ParamTypes.Length,
                    HasThis = method.Signature.HasThis,
                    ClassArgs = constructed.TypeArguments,
                    MethodArgs = methodArgs,
                };
            }
            throw new NotSupportedException(
                $"MethodSpec の解決先 MemberRef の親テーブル {parent.Table} は未対応です (intrinsic ジェネリックメソッドは今後のフェーズ)。");
        }
        throw new BadImageFormatException($"MethodSpec 0x{token:X8} の解決先テーブル {underlying.Table} が不正です。");
    }

    /// <summary>宣言署名 (名前 + パラメータ型) でメソッドを探す (継承チェーンを辿る。抽象宣言も解決対象)。
    /// スロットキーが一致する候補を署名精度で優先し、無い場合は従来どおり名前+パラメータ数の
    /// 最初の一致にフォールバックする (ジェネリック変数の文脈差等でキー照合できない呼出の救済)。</summary>
    private VmMethod? FindMethodThroughChain(VmClassType type, string name, SigType[] paramTypes, SigType? returnType = null) {
        var queryKey = _loader.TryResolveSlotParams(paramTypes) is { } parameters
            ? VmSlotKeys.Of(name, parameters) : null;
        // 戻り型で区別される面 (decimal の op_Implicit / op_Explicit 群) は戻り型名も照合する。
        // 戻り型名は候補メソッドの宣言ローダで解決する (トークンは自画像の TypeDef rid を指すため)。
        // どちらかが解決できない場合はワイルドカード (従来動作) に倒す
        var returnName = SafeDescribeReturn(returnType);
        VmMethod? byParamCount = null;
        VmMethod? byParamCountAndReturn = null;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (t is not VmClassType cls)
                break;
            foreach (var method in cls.Methods) {
                if (method.Name != name)
                    continue;
                var matchedReturn = true;
                if (returnName is not null)
                    matchedReturn = string.Equals(SafeDescribeReturn(method.Signature.ReturnType, method.Loader), returnName, StringComparison.Ordinal);
                if (matchedReturn) {
                    if (queryKey is not null && method.SlotKey == queryKey)
                        return method; // 署名一致 (オーバーロード誤解決の解消)
                    if (byParamCountAndReturn is null && method.Signature.ParamTypes.Length == paramTypes.Length)
                        byParamCountAndReturn = method;
                }
                if (byParamCount is null && method.Signature.ParamTypes.Length == paramTypes.Length)
                    byParamCount = method;
            }
            t = cls.BaseType;
        }
        return byParamCountAndReturn ?? byParamCount;
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

/// <summary>解決済みの呼出先 (ゲスト メソッド / intrinsic / 解決未了)。</summary>
internal sealed class CallTarget {
    public int Arity;
    public VmMethod? Method;
    public IntrinsicImpl? Intrinsic;
    /// <summary>intrinsic ターゲットの宣言型名/メソッド名 (callvirt の仮想ディスパッチ用)。</summary>
    public string? DeclaringType;
    public string? Name;
    public int ParamCount;
    public bool HasThis;
    /// <summary>構築型経由 (TypeSpec 親) で解決された場合の型引数 (MemberRef の !0 置換に使う)。</summary>
    public VmType[]? ClassArgs;
    /// <summary>MethodSpec の Instantiation (ジェネリックメソッドの !!0 置換に使う)。</summary>
    public VmType[]? MethodArgs;
    /// <summary>宣言上のパラメータ型名 (i4 統合面のオーバーロード判別用。intrinsic 経路のみ)。</summary>
    public string[]? ParamTypeNames;
}
