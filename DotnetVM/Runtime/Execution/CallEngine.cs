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

    // ---- 呼出 (call / callvirt) ----

    public StackSlot? Call(int token, InterpreterFrame caller, bool isCallvirt, int constrainedToken) {
        // 未登録 intrinsic はこの時点では例外にしない (callvirt ならレシーバのゲスト実装を
        // 引数ポップ後に試すため。旧来の即時例外は最後のフォールバックで再現する)
        var target = ResolveCallTarget(token, caller.Context, throwOnMissingIntrinsic: false);

        // 引数はスタック上では逆順
        var args = new StackSlot[target.Arity];
        for (var i = target.Arity - 1; i >= 0; i--)
            args[i] = caller.Stack.Pop();

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
                args[0] = thisByRef.Slot;
            // callvirt で intrinsic 宣言型 (System.Object 等) をターゲットにする場合、
            // レシーバの実行時型にゲスト側 override があればそちらを優先する (仮想ディスパッチ)
            if (isCallvirt && target.HasThis) {
                if (SlotOps.IsNullReference(args[0]))
                    throw new UnhandledGuestException("System.NullReferenceException",
                        $"null レシーバで {target.DeclaringType}::{target.Name} を呼び出しました。");
                if (TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
                    var context = BuildCallContext(target, guestOverride, args[0]);
                    var guestRet = invoker.Invoke(guestOverride, args, context);
                    return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? guestRet : null;
                }
                // ファサード インターフェースの明示的実装 (EII) を実行時型の InterfaceMap で解決する
                // (明示的実装はメソッド名が規定名と異なるため名前照合では見つからない)
                if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
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
            return intrinsic(_intrinsicContext, args);
        }

        // 解決未了 (未登録 intrinsic): レシーバへの仮想ディスパッチを最終試行してから拒否
        if (target.Method is null)
            return FailOrDispatchLate(target, isCallvirt, args);

        // ゲスト呼出。callvirt はレシーバの実行時型で仮想解決 (VTable 相当)。
        // constrained. 値型レシーバは ByRef/ValueType スロットで来るためディスパッチがそのまま適用される
        var method = target.Method!;
        if (isCallvirt && method.Signature.HasThis) {
            if (SlotOps.IsNullReference(args[0]))
                throw new UnhandledGuestException("System.NullReferenceException",
                    $"null レシーバで {method.DeclaringType.FullName}::{method.Name} を呼び出しました。");
            method = DispatchVirtual(method, args[0]);
        }

        // 優先順位 ①: ランタイムバインド (署名照合) を最優先で解決する
        if (TryInvokeBinding(method, target.MethodArgs, args, out var bound))
            return bound;

        if (method.Body is null) {
            // 本体の無い面 (InternalCall / P/Invoke / 抽象宣言) はバインドが無い限り IL 実行できない。
            // callvirt の場合のみレシーバ実行時型への最終救済 (EII) を試してから legacy intrinsic へ
            if (isCallvirt && method.Signature.HasThis &&
                TryDispatchInterfaceKey(method.DeclaringType.FullName, method.Name,
                    ParamTypeNamesOf(method, target.MethodArgs), args[0]) is { } explicitImpl) {
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
        declaringType == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";

    /// <summary>VM ランタイムオブジェクトのレシーバが属する intrinsic 面 (仮想ディスパッチの
    /// 実行時型相当)。ランタイムオブジェクトは対応する intrinsic ファサード型の実体として振る舞う。</summary>
    private static string? RuntimeReceiverSurfaceType(object? receiver) => receiver switch {
        VmRuntimeObject => "System.Type",
        VmRuntimeMethod => "System.Reflection.MethodBase",
        _ => null,
    };

    /// <summary>解決未了の呼出 (未登録 intrinsic) の最終処理。callvirt ならレシーバの実行時型に
    /// ゲスト実装があればそれを呼び (constrained callvirt による構造体の interface 実装呼出等)、
    /// 無ければ未登録 intrinsic として拒否する。</summary>
    private StackSlot? FailOrDispatchLate(CallTarget target, bool isCallvirt, StackSlot[] args) {
        if (isCallvirt && target.HasThis) {
            if (TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
                var context = BuildCallContext(target, guestOverride, args[0]);
                var ret = invoker.Invoke(guestOverride, args, context);
                return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? ret : null;
            }
            // ファサード インターフェースの明示的実装 (EII) もここで救済する
            if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
                var context = BuildCallContext(target, explicitImpl, args[0]);
                var ret = invoker.Invoke(explicitImpl, args, context);
                return SlotOps.SignatureReturnsValue(explicitImpl.Signature) ? ret : null;
            }
        }
        throw new OperationNotAllowedException(
            $"intrinsic {target.DeclaringType}::{target.Name} (引数 {target.Arity} 個) は未登録です。BCL 面は VM 起動時に登録されたランタイムバインド / intrinsic のみ提供されます。");
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
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>宣言メソッド (仮想 / インターフェース) をレシーバの実行時型のディスパッチ表で解決する。
    /// 宣言型がレシーバの継承チェーンに属さない (ファサード宣言等) 場合は null。</summary>
    private VmMethod? TryDispatchDeclared(VmMethod declared, in StackSlot receiver) {
        var receiverType = ReceiverRuntimeType(receiver);
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

    /// <summary>ファサード インターフェースの明示的実装 (EII) 用: 宣言型名 + パラメータ型名から
    /// インターフェーススロットキーを組み、レシーバの InterfaceMap で解決する。
    /// 型名が解決できていないパラメータが混ざる場合は照合を諦める (null)。</summary>
    private VmMethod? TryDispatchInterfaceKey(string? declaringTypeName, string name, string[]? paramTypeNames, in StackSlot receiver) {
        if (declaringTypeName is null || paramTypeNames is null || paramTypeNames.Any(string.IsNullOrEmpty))
            return null;
        var receiverType = ReceiverRuntimeType(receiver);
        if (receiverType is null)
            return null;
        var definition = receiverType is VmConstructedType constructed ? constructed.Definition : receiverType;
        if (definition is not VmClassType receiverClass)
            return null;
        var maps = (receiverClass.Loader ?? _loader).EnsureDispatchMaps(receiverClass);
        return maps.InterfaceMap.GetValueOrDefault(
            declaringTypeName + "::" + name + "(" + string.Join(",", paramTypeNames) + ")");
    }

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

    /// <summary>呼出解決地点 (TypeRef / 構築ファサード親) でのランタイムバインド照合。
    /// 型名 + 宣言パラメータ型名から署名キーを組み、完全一致 → 全引数一致面の順で解決する。
    /// 型名が解決できていないパラメータ (空文字列) が混ざる場合は照合しない。</summary>
    private bool TryGetResolvedBinding(string typeName, string name, bool hasThis, string[] paramTypeNames, out IntrinsicImpl impl) {
        impl = null!;
        if (paramTypeNames.Any(string.IsNullOrEmpty))
            return false;
        var key = hasThis ? BindingKey.Instance(typeName, name, paramTypeNames)
                          : BindingKey.Static(typeName, name, paramTypeNames);
        return _intrinsics.TryGetBinding(key, out impl, out _);
    }

    /// <summary>解決済みメソッドをランタイムバインド (署名キー) で呼び出す (優先順位 ①)。
    /// パラメータ型名は「そのメソッドを定義したローダ」で解決する (TypeToken は自画像の
    /// TypeDef rid を指すため)。ジェネリック変数は MethodSpec の実引数で置換し、実引数が無い
    /// 場合は開いた名 (!n / !!n) のままキー化する (登録側の開いたキーと一致)。</summary>
    private bool TryInvokeBinding(VmMethod method, VmType[]? methodArgs, StackSlot[] args, out StackSlot? result) {
        result = null;
        var names = ParamTypeNamesOf(method, methodArgs);
        if (names is null || names.Any(string.IsNullOrEmpty))
            return false;
        var declaringName = method.DeclaringType.FullName;
        var key = method.Signature.HasThis
            ? BindingKey.Instance(declaringName, method.Name, names)
            : BindingKey.Static(declaringName, method.Name, names);
        if (!_intrinsics.TryGetBinding(key, out var impl, out _))
            return false;
        NormalizeByRefReceiver(method, args);
        result = InvokeDelegated(impl, names, args);
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
        result = InvokeDelegated(impl, ParamTypeNamesOf(method, null) ?? [], args);
        return true;
    }

    /// <summary>委譲実装 (ランタイムバインド / legacy intrinsic) を intrinsic 呼出ゲート経由で実行する。
    /// IL 実行と完全に等価な制約 (① 追加クォータ消費 ② セーフポイント検査 ③ 値は VM オブジェクト
    /// モデルに正規化) を受ける。</summary>
    private StackSlot? InvokeDelegated(IntrinsicImpl impl, string[] paramNames, StackSlot[] args) {
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        _intrinsicContext.ParameterTypeNames = paramNames;
        return impl(_intrinsicContext, args);
    }

    /// <summary>プリミティブ等の instance 面 (ByRef レシーバ) を値に読み替える。可変状態を
    /// ローカルスロットに保持する構造体ファサード (補間ハンドラ等) は除く。既存 intrinsic
    /// 経路と同じ規約。実行が確定した後でのみ呼ぶ (失敗経路で引数を壊さない)。</summary>
    private void NormalizeByRefReceiver(VmMethod method, StackSlot[] args) {
        if (method.Signature.HasThis && args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef thisByRef &&
            !PreservesByRefReceiver(method.DeclaringType.FullName))
            args[0] = thisByRef.Slot;
    }

    /// <summary>解決済みメソッドの宣言パラメータ型名 (i4 統合面の判別 / バインドキー構築用)。
    /// 呼出トークン解決で確定済みならそれを使い、無い場合は定義ローダで署名を解決する。</summary>
    private string[]? ParamTypeNamesOf(VmMethod method, VmType[]? methodArgs) {
        var loader = method.Loader;
        if (loader is null)
            return null;
        var names = new string[method.Signature.ParamTypes.Length];
        for (var i = 0; i < names.Length; i++) {
            var name = DescribeBindingType(method.Signature.ParamTypes[i], methodArgs, loader);
            if (name is null)
                return null;
            names[i] = name;
        }
        return names;
    }

    /// <summary>バインドキー用のパラメータ型名 (完全名)。ジェネリック変数は実引数 (MethodSpec) で
    /// 置換し、実引数が無い場合は開いた名 (!!n / !n) のまま返す。トークンは「そのメソッドを定義した
    /// ローダ」で解決する (署名の解決は定義ローダの原則)。</summary>
    private static string? DescribeBindingType(SigType type, VmType[]? methodArgs, TypeLoader loader) => type.Kind switch {
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
        SigKind.SzArray => DescribeBindingType(type.Inner!, methodArgs, loader) is { } inner ? inner + "[]" : null,
        SigKind.ByRef => DescribeBindingType(type.Inner!, methodArgs, loader) is { } element ? element + "&" : null,
        SigKind.Pointer => DescribeBindingType(type.Inner!, methodArgs, loader) is { } pointee ? pointee + "*" : null,
        SigKind.Array => DescribeBindingType(type.Inner!, methodArgs, loader) is { } multi ? $"{multi}[{type.Rank}]" : null,
        SigKind.GenericMethodVar => type.VarNumber < (methodArgs?.Length ?? 0)
            ? methodArgs![type.VarNumber].FullName
            : $"!!{type.VarNumber}",
        SigKind.GenericVar => $"!{type.VarNumber}",
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
            _loader.Image.GetBlob(_loader.Image.Tables.GetRowIndex(TableKind.StandAloneSig, rid, 0)).ToArray());
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
                    _loader.Image.GetMemberRefSignature(rid).ToArray());
                var arity = signature.ParamTypes.Length + (signature.HasThis ? 1 : 0);
                var name = _loader.GetMemberRefName(rid);

                var parent = _loader.Image.Tables.DecodeCoded(
                    TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
                var paramNames = signature.ParamTypes.Select(t => ParamTypeName(t, context)).ToArray();
                if (parent.Table == TableKind.TypeRef) {
                    var typeName = _loader.GetMemberRefParentTypeName(rid)!;
                    // 優先順位 ①: ランタイムバインド (署名照合)
                    if (TryGetResolvedBinding(typeName, name, signature.HasThis, paramNames, out var bound)) {
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
                        var resolved = FindMethodThroughChain(realClass, name, signature.ParamTypes);
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
                return ResolveMethodSpecTarget(token, rid, context);
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
            // 優先順位 ①: ランタイムバインド (署名照合)
            if (TryGetResolvedBinding(facade.FullName, name, signature.HasThis, facadeParamNames, out var boundImpl)) {
                return new CallTarget {
                    Arity = arity,
                    Intrinsic = boundImpl,
                    DeclaringType = facade.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ParamTypeNames = facadeParamNames,
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
    private CallTarget ResolveMethodSpecTarget(int token, int methodSpecRid, GenericContext? context) {
        var underlying = _loader.Image.Tables.DecodeCoded(
            TableKind.MethodSpec, methodSpecRid, 0, CodedIndexKind.MethodDefOrRef);
        var instantiationBlobIndex = _loader.Image.Tables.GetRowIndex(TableKind.MethodSpec, methodSpecRid, 1);
        var methodArgs = SignatureDecoder.DecodeMethodSpecInstantiation(
            _loader.Image.GetBlob(instantiationBlobIndex).ToArray())
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
                _loader.Image.GetMemberRefSignature(memberRefRid).ToArray());
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
                var concreteParams = signature.ParamTypes
                    .Select(t => SubstitutedParamTypeName(t, methodArgs)).ToArray();
                var openParams = signature.ParamTypes
                    .Select(t => DescribeBindingType(t, null, _loader) ?? "").ToArray();
                if (TryGetResolvedBinding(typeName, name, signature.HasThis, concreteParams, out var boundImpl) ||
                    TryGetResolvedBinding(typeName, name, signature.HasThis, openParams, out boundImpl)) {
                    return new CallTarget {
                        Arity = specArity,
                        Intrinsic = boundImpl,
                        DeclaringType = typeName,
                        Name = name,
                        ParamCount = signature.ParamTypes.Length,
                        HasThis = signature.HasThis,
                        // !!n を MethodSpec の実引数で置換した宣言型名 (char/bool 等 i4 統合面の判別に必要)
                        ParamTypeNames = concreteParams,
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
    private VmMethod? FindMethodThroughChain(VmClassType type, string name, SigType[] paramTypes) {
        var queryKey = _loader.TryResolveSlotParams(paramTypes) is { } parameters
            ? VmSlotKeys.Of(name, parameters) : null;
        VmMethod? byParamCount = null;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (t is not VmClassType cls)
                break;
            foreach (var method in cls.Methods) {
                if (method.Name != name)
                    continue;
                if (queryKey is not null && method.SlotKey == queryKey)
                    return method; // 署名一致 (オーバーロード誤解決の解消)
                if (byParamCount is null && method.Signature.ParamTypes.Length == paramTypes.Length)
                    byParamCount = method;
            }
            t = cls.BaseType;
        }
        return byParamCount;
    }

    // ---- 宣言上のパラメータ型名 (i4 統合面のオーバーロード判別) ----

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
        SigKind.TypeToken => TryResolveTypeName(type.Token, context),
        _ => "",
    };

    /// <summary>MethodSpec の宣言パラメータ型名を、メソッド型引数 (!!n) を実引数で置換してから求める
    /// (AppendFormatted&lt;char&gt; と AppendFormatted&lt;int&gt; 等 i4 統合面の intrinsic 判別に使う)。</summary>
    private string SubstitutedParamTypeName(SigType type, VmType[] methodArgs) => type.Kind switch {
        SigKind.GenericMethodVar when type.VarNumber < methodArgs.Length => methodArgs[type.VarNumber].FullName,
        SigKind.SzArray => SubstitutedParamTypeName(type.Inner!, methodArgs) + "[]",
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
