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
    public CallTarget ResolveCallTarget(int token, GenericContext? context, bool throwOnMissingIntrinsic = true,
        IReadOnlyDictionary<uint, object>? dynamicTokens = null) {
        if (dynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmMethod dynamicMethod)
            return new CallTarget {
                Arity = dynamicMethod.Signature.ParamTypes.Length + (dynamicMethod.Signature.HasThis ? 1 : 0),
                Method = dynamicMethod,
                Name = dynamicMethod.Name,
                ParamCount = dynamicMethod.Signature.ParamTypes.Length,
                HasThis = dynamicMethod.Signature.HasThis,
            };
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
                    var bindingType = TryResolveTypeRefForBinding(parent.Rid);
                    if (CanAttemptRuntimeBinding(typeName, bindingType) &&
                        TryGetResolvedBinding(typeName, name, signature.HasThis, paramNames, callerDomain, out var bound)) {
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
        if (CanAttemptRuntimeBinding(definition)) {
            var realParamNames = signature.ParamTypes.Select(t => ParamTypeName(t, context)).ToArray();
            if (TryGetResolvedBinding(definition.FullName, name, signature.HasThis, realParamNames,
                    CallerDomainOfLoader(), out var realBound))
                return new CallTarget {
                    Arity = arity,
                    Intrinsic = realBound,
                    DeclaringType = definition.FullName,
                    Name = name,
                    ParamCount = signature.ParamTypes.Length,
                    HasThis = signature.HasThis,
                    ParamTypeNames = realParamNames,
                    ClassArgs = constructed.TypeArguments,
                };
        }
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
                var methodSpecBindingType = TryResolveTypeRefForBinding(parent.Rid);
                if (CanAttemptRuntimeBinding(typeName, methodSpecBindingType) &&
                    (TryGetResolvedBinding(typeName, name, signature.HasThis, concreteParams, methodSpecCaller, out var boundImpl) ||
                     TryGetResolvedBinding(typeName, name, signature.HasThis, openParams, methodSpecCaller, out boundImpl))) {
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
