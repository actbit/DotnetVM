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
        var context2 = BuildCallContext(target, method, method.Signature.HasThis ? args[0] : default);
        var ret = invoker.Invoke(method, args, context2);
        return SlotOps.SignatureReturnsValue(method.Signature) ? ret : null;
    }

    /// <summary>ByRef レシーバを値に読み替えずにそのまま渡す intrinsic 宣言型
    /// (ローカルスロットに可変状態を保持する構造体ファサード)。</summary>
    private static bool PreservesByRefReceiver(string? declaringType) =>
        declaringType == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";

    /// <summary>解決未了の呼出 (未登録 intrinsic) の最終処理。callvirt ならレシーバの実行時型に
    /// ゲスト実装があればそれを呼び (constrained callvirt による構造体の interface 実装呼出等)、
    /// 無ければ未登録 intrinsic として拒否する。</summary>
    private StackSlot? FailOrDispatchLate(CallTarget target, bool isCallvirt, StackSlot[] args) {
        if (isCallvirt && target.HasThis &&
            TryDispatchVirtual(target.Name!, target.ParamCount, args[0]) is { } guestOverride) {
            var context = BuildCallContext(target, guestOverride, args[0]);
            var ret = invoker.Invoke(guestOverride, args, context);
            return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? ret : null;
        }
        throw new OperationNotAllowedException(
            $"intrinsic {target.DeclaringType}::{target.Name} (引数 {target.Arity} 個) は未登録です。BCL 面は VM 起動時に登録された intrinsic のみ提供されます。");
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

    /// <summary>callvirt の実行時型ディスパッチ。名前+引数個数+実装本体で基底連鎖を辿る (VTable 相当)。</summary>
    public VmMethod DispatchVirtual(VmMethod declared, in StackSlot receiver) =>
        TryDispatchVirtual(declared.Name, declared.Signature.ParamTypes.Length, receiver) ?? declared;

    /// <summary>実行時型から最派生のゲスト実装を探す。見つからなければ null (intrinsic 宣装にフォールバック)。
    /// 構築ジェネリック型のインスタンス (VmBoxedValue の VmConstructedType 型 等) も定義側に解いて探索する。</summary>
    public VmMethod? TryDispatchVirtual(string name, int paramCount, in StackSlot receiver) {
        // 構造体の instance メソッドは ByRef レシーバで来ることがある
        var receiverValue = receiver.Kind == StackKind.ByRef && receiver.ObjectValue is VmByRef byRef
            ? byRef.Slot
            : receiver;
        var runtimeType = receiverValue.Kind switch {
            StackKind.ValueType => receiverValue.ObjectValue is VmStructValue sv ? (VmType)sv.StructType : null,
            StackKind.Object => receiverValue.ObjectValue switch {
                VmClassInstance ci => (VmType)ci.ClassType,
                VmBoxedValue bv => bv.Type,
                _ => null,
            },
            _ => null,
        };

        for (VmType? t = runtimeType; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition; // 構築型 → ジェネリック定義に解いて探索を続ける
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
                    var method = owner.Methods.FirstOrDefault(m => m.Name == name)
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

        // 構築ファサード型 (BCL 汎用インターフェース等) → intrinsic 面のみ
        if (constructed.Definition is VmIntrinsicType facade) {
            var facadeParamNames = signature.ParamTypes.Select(t => ParamTypeName(t, context)).ToArray();
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
        var method = FindMethodThroughChain(definition, name, signature.ParamTypes.Length)
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
                var method = owner.Methods.FirstOrDefault(m =>
                        m.Name == name && m.Signature.ParamTypes.Length == signature.ParamTypes.Length)
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
                var method = FindMethodThroughChain(definition, name, signature.ParamTypes.Length)
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

    /// <summary>名前+パラメータ数でメソッドを探す (継承チェーンを辿る。抽象宣言も解決対象)。</summary>
    private static VmMethod? FindMethodThroughChain(VmClassType type, string name, int paramCount) {
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType constructed)
                t = constructed.Definition;
            if (t is not VmClassType cls)
                break;
            var found = cls.Methods.FirstOrDefault(m =>
                m.Name == name && m.Signature.ParamTypes.Length == paramCount);
            if (found is not null)
                return found;
            t = cls.BaseType;
        }
        return null;
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

    /// <summary>トークン型の名前解決 (未対応のアセンブリ外参照は型名不要のため空文字列にフォールバック)。</summary>
    private string TryResolveTypeName(uint token, GenericContext? context) {
        try {
            return _objectEngine.ResolveTypeToken((int)token, context)?.FullName ?? "";
        } catch (NotSupportedException) {
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
