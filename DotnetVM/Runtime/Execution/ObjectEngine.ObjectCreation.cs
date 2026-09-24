using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal sealed partial class ObjectEngine {
    // ---- オブジェクト生成 (newobj) ----

    /// <summary>デリゲート生成 (newobj instance void D::.ctor(object, native int))。
    /// 関数ポインタは ldftn/ldvirtftn の VmMethodPointer、既存デリゲートの複製 (マルチキャスト含む) も可。</summary>
    private VmDelegate NewDelegate(VmType delegateType, StackSlot targetSlot, StackSlot pointerSlot) {
        var invocations = pointerSlot.ObjectValue switch {
            VmMethodPointer pointer => new[] { new DelegateInvocation(targetSlot, pointer.Target) },
            VmDelegate source => source.CopyInvocations(),
            _ => throw new UnhandledGuestException("System.ArgumentException",
                "デリゲート生成の第 2 引数が関数ポインタ (ldftn/ldvirtftn の結果) ではありません。"),
        };
        var @delegate = _heap.Allocate(new VmDelegate { DeclaredType = delegateType });
        foreach (var invocation in invocations)
            @delegate.AddInvocation(invocation);
        return @delegate;
    }

    /// <summary>.ctor を基底連鎖 (ジェネリック定義へ解いて) から探す。署名精度 (スロットキー一致) を
    /// 優先し、キー解決不可の候補は従来どおり名前+引数個数の最初の一致にフォールバックする
    /// (ReadOnlySpan の (in T&amp;) と (T[]) 等の同引数個数オーバーロード誤解決の解消)。</summary>
    private VmMethod? FindCtorThroughChain(VmClassType type, string name, int paramCount, SigType[]? paramTypes) {
        var queryKey = paramTypes is not null && _loader.TryResolveSlotParams(paramTypes) is { } parameters
            ? VmSlotKeys.Of(name, parameters) : null;
        VmMethod? byParamCount = null;
        for (VmType? t = type; t is not null;) {
            if (t is VmConstructedType ct)
                t = ct.Definition;
            if (t is not VmClassType cls)
                break;
            foreach (var method in cls.Methods) {
                if (method.Name != name || method.IsStatic)
                    continue;
                if (queryKey is not null && method.SlotKey == queryKey)
                    return method; // 署名一致 (オーバーロード誤解決の解消)
                if (byParamCount is null && method.Signature.ParamTypes.Length == paramCount)
                    byParamCount = method;
            }
            t = cls.BaseType;
        }
        return byParamCount;
    }

    /// <summary>string::.ctor の構築面 (char[] / char[],int / char)。CLR と同じ確保点
    /// (FastAllocateString 相当の VmStringPool.Allocate) で確保し、char 列をバッファへ
    /// 書き込む。引数検査は CLR と同じ例外分類 (null 配列は ArgumentNullException、
    /// 範囲外は ArgumentOutOfRangeException)。</summary>
    private StackSlot NewStringFromCtor(int paramCount, InterpreterFrame caller) {
        var args = new StackSlot[paramCount];
        for (var i = paramCount; i >= 1; i--)
            args[i - 1] = caller.Stack.Pop();
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        var strings = _intrinsicContext.Strings;
        switch (paramCount) {
            case 1: {
                // string(char[] value)
                var array = RequireCharArray(args[0]);
                var result = strings.Allocate(array.Length);
                CopyChars(result, 0, array, 0, array.Length);
                return StackSlot.OfObject(result);
            }
            case 2: {
                // string(char c, int count)
                var c = (char)args[0].Int64Value;
                var count = (int)args[1].Int64Value;
                if (count < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var result = strings.Allocate(count);
                for (var i = 0; i < count; i++)
                    WriteChar(result, i, c);
                return StackSlot.OfObject(result);
            }
            case 3: {
                // string(char[] value, int startIndex, int length)
                var array = RequireCharArray(args[0]);
                var startIndex = (int)args[1].Int64Value;
                var length = (int)args[2].Int64Value;
                if ((uint)startIndex > (uint)array.Length || length < 0 || (uint)length > (uint)(array.Length - startIndex))
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                var result = strings.Allocate(length);
                CopyChars(result, 0, array, startIndex, length);
                return StackSlot.OfObject(result);
            }
            default:
                throw new NotSupportedException($"string::.ctor (引数 {paramCount} 個) は対応していません。");
        }
    }

    /// <summary>Guid::.ctor の構築面。該当 overload のみホスト解析 + Guid 構造体値で受け、
    /// 非該当は null を返して通常の実体解決フローへ流す。
    /// 本家 .ctor 実 IL は span 16 進解析の生ポインタ演算 (VM のスロット表現に落ちない)
    /// で構成されるため (string / byte[] / (int,short,short,byte[]) / 11 引数面)。</summary>
    private StackSlot? TryNewGuidFromCtor(MethodSignature signature, InterpreterFrame caller) {
        var kinds = signature.ParamTypes.Select(t => t.Kind).ToArray();
        Guid value;
        if (kinds is [SigKind.String]) {
            var args = new StackSlot[1];
            args[0] = caller.Stack.Pop();
            var s = (args[0].ObjectValue as VmString)?.Value;
            if (s is null)
                throw new UnhandledGuestException("System.ArgumentNullException", null);
            try {
                value = new Guid(s);
            } catch (FormatException) {
                throw new UnhandledGuestException("System.FormatException", null);
            } catch (OverflowException) {
                throw new UnhandledGuestException("System.OverflowException", null);
            }
        } else if (kinds is [SigKind.SzArray]) {
            var args = new StackSlot[1];
            args[0] = caller.Stack.Pop();
            if (args[0].ObjectValue is not VmArray array)
                throw new UnhandledGuestException("System.ArgumentNullException", null);
            try {
                value = new Guid(ReadBytes(array));
            } catch (ArgumentException ex) {
                throw new UnhandledGuestException("System." + ex.GetType().Name, null);
            }
        } else if (kinds is [SigKind.I4, SigKind.I2, SigKind.I2, SigKind.SzArray]) {
            var args = new StackSlot[4];
            for (var i = 4; i >= 1; i--)
                args[i - 1] = caller.Stack.Pop();
            var d = args[3].ObjectValue as VmArray
                ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
            try {
                value = new Guid(args[0].AsInt32, (short)args[1].AsInt32, (short)args[2].AsInt32, ReadBytes(d));
            } catch (ArgumentException ex) {
                throw new UnhandledGuestException("System." + ex.GetType().Name, null);
            }
        } else if (kinds.Length == 11 && kinds[0] == SigKind.I4) {
            var args = new StackSlot[11];
            for (var i = 11; i >= 1; i--)
                args[i - 1] = caller.Stack.Pop();
            try {
                value = new Guid(args[0].AsInt32, (short)args[1].AsInt32, (short)args[2].AsInt32,
                    (byte)args[3].AsInt32, (byte)args[4].AsInt32, (byte)args[5].AsInt32,
                    (byte)args[6].AsInt32, (byte)args[7].AsInt32, (byte)args[8].AsInt32,
                    (byte)args[9].AsInt32, (byte)args[10].AsInt32);
            } catch (ArgumentException ex) {
                throw new UnhandledGuestException("System." + ex.GetType().Name, null);
            }
        } else {
            return null;
        }
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        return BuildGuidStruct(value) is { } sv
            ? StackSlot.OfValueType(sv)
            : null;
    }

    private static byte[] ReadBytes(VmArray array) {
        var data = new byte[array.Length];
        for (var i = 0; i < array.Length; i++) {
            var element = array.Elements[i];
            if (element.Kind != StackKind.Int32 || element.Int64Value is < 0 or > 255)
                throw new InvalidOperationException($"byte 配列の要素 {i} が不正です (Kind={element.Kind})。");
            data[i] = (byte)element.Int64Value;
        }
        return data;
    }

    /// <summary>ホスト Guid から CoreLib Guid 構造体値を構築する (_a.._k の 11 フィールド、
    /// フィールド名で対応付け)。CoreLib 画像 (呼出元画像でなく) から型を引く。
    /// 非該当の面は通常フローへ流すため型解決できない場合は null。</summary>
    private VmStructValue? BuildGuidStruct(Guid value) {
        VmClassType? cls = null;
        foreach (var loader in _loader.Context?.Loaders ?? (IReadOnlyList<TypeLoader>)[_loader]) {
            if (loader.FindTypeByFullName("System.Guid") is not VmClassType candidate)
                continue;
            if (loader.Image.SourcePath?.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) == true) {
                cls = candidate;
                break;
            }
            cls ??= candidate;
        }
        if (cls is null)
            return null;
        var bytes = value.ToByteArray();
        var layout = _objects.GetLayout(cls);
        var fields = new StackSlot[layout.Count == 0 ? 0 : layout.Values.Max() + 1];
        foreach (var field in cls.Fields) {
            if (field.IsStatic || field.IsLiteral || !layout.TryGetValue(field, out var index))
                continue;
            fields[index] = field.Name switch {
                "_a" => StackSlot.OfInt32(BitConverter.ToInt32(bytes, 0)),
                "_b" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 4)),
                "_c" => StackSlot.OfInt32(BitConverter.ToInt16(bytes, 6)),
                "_d" => StackSlot.OfInt32(bytes[8]),
                "_e" => StackSlot.OfInt32(bytes[9]),
                "_f" => StackSlot.OfInt32(bytes[10]),
                "_g" => StackSlot.OfInt32(bytes[11]),
                "_h" => StackSlot.OfInt32(bytes[12]),
                "_i" => StackSlot.OfInt32(bytes[13]),
                "_j" => StackSlot.OfInt32(bytes[14]),
                "_k" => StackSlot.OfInt32(bytes[15]),
                _ => StackSlot.OfInt32(0),
            };
        }
        return new VmStructValue(cls, fields);
    }

    private static VmArray RequireCharArray(StackSlot slot) =>
        slot.ObjectValue as VmArray
        ?? throw new UnhandledGuestException("System.ArgumentNullException", null);

    private static void CopyChars(VmString target, int targetIndex, VmArray source, int sourceIndex, int count) {
        for (var i = 0; i < count; i++)
            WriteChar(target, targetIndex + i, (char)source.Elements[sourceIndex + i].Int64Value);
    }

    private static void WriteChar(VmString target, int charIndex, char value) {
        var offset = VmString.CharDataByteOffset + charIndex * 2;
        target.Bytes[offset] = (byte)value;
        target.Bytes[offset + 1] = (byte)((ushort)value >> 8);
    }

    public StackSlot? NewObject(int token, InterpreterFrame caller) {
        if (caller.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmMethod dynamicCtor) {
            var values = new StackSlot[dynamicCtor.Signature.ParamTypes.Length];
            for (var i = values.Length - 1; i >= 0; i--)
                values[i] = caller.Stack.Pop();
            return ConstructExpression(dynamicCtor, values);
        }
        VmMethod ctor;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MethodDef) {
            ctor = _loader.GetMethodByToken((uint)token)
                ?? throw new BadImageFormatException($"newobj トークン 0x{token:X8} を解決できません。");
            if (ctor.DeclaringType.FullName == "System.Reflection.Emit.DynamicMethod" && ctor.Name == ".ctor")
                return NewDynamicMethodInstance(ctor.Signature.ParamTypes.Length, caller);
        } else if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeSpec)
                return NewConstructedObject(token, rid, parent.Rid, caller);
            var signature = SignatureDecoder.DecodeMethodSignature(
                _loader.Image.GetMemberRefSignature(rid).ToArray(),
                _loader.Image.Limits?.MaxSignatureDepth ?? 64,
                _loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
            var facadeParamCount = signature.ParamTypes.Length;
            var name = _loader.GetMemberRefName(rid);
            var typeName = _loader.GetMemberRefParentTypeName(rid);
            if (typeName == "System.Reflection.Emit.DynamicMethod" && name == ".ctor")
                return NewDynamicMethodInstance(facadeParamCount, caller);
            if (typeName is not null) {
                if (typeName is ("System.Runtime.Loader.AssemblyLoadContext" or "System.Reflection.AssemblyName"
                    or "System.IO.MemoryStream") && name == ".ctor") {
                    var specialCtorArgs = new StackSlot[facadeParamCount + 1];
                    for (var i = facadeParamCount; i >= 1; i--)
                        specialCtorArgs[i] = caller.Stack.Pop();
                    return AssemblyLoadContextRuntime.Construct(_intrinsicContext, typeName, specialCtorArgs);
                }
                // string の構築面 (new string(char[]) / new string(char, int) 等):
                // FastAllocateString + char 列コピーと同じ確保点で VmString を生成する。
                // 置換面 (DotnetVM.CoreLib の NumberFormatting IL) が使うほか、ゲストの
                // 直接の new string(...) もここに着地する
                if (typeName == "System.String" && name == ".ctor")
                    return NewStringFromCtor(facadeParamCount, caller);
                // Guid の構築面 (new Guid(string) / (byte[]) 等):
                // 本家 .ctor 実 IL は span 16 進解析の生ポインタ演算 (単一スロットへの
                // バイト単位 Add 等、VM のスロット表現に落ちない) で構成されるため、
                // ホスト解析 + CoreLib Guid 構造体値の直接構築で受ける
                if (typeName == "System.Guid" && name == ".ctor" &&
                    TryNewGuidFromCtor(signature, caller) is { } guidSlot)
                    return guidSlot;
                var facadeType = _loader.FindIntrinsicType(typeName);
                if (facadeType is not null) {
                    // デリゲートファサード (Action/Func/Predicate 等) の newobj (object, native int)
                    if (TypeChecks.IsDelegateType(facadeType)) {
                        var pointerSlot = caller.Stack.Pop();
                        var targetSlot = caller.Stack.Pop();
                        return StackSlot.OfObject(NewDelegate(facadeType, targetSlot, pointerSlot));
                    }
                    var ctorArgs = new StackSlot[facadeParamCount + 1];
                    for (var i = facadeParamCount; i >= 1; i--)
                        ctorArgs[i] = caller.Stack.Pop();
                    // .ctor は基底ファサード連鎖からも解決する (Exception::.ctor を派生型で使う等)
                    var hasCtorIntrinsic = TryGetIntrinsicThroughHierarchy(
                        typeName, name, facadeParamCount + 1, hasThis: true, out var intrinsicCtor);
                    if (TypeChecks.IsExceptionFacade(facadeType)) {
                        // 例外ファサード型: VmExceptionObject として実体化 (throw 機構が依存)
                        var exception = _heap.Allocate(new VmExceptionObject(facadeType, null));
                        ctorArgs[0] = StackSlot.OfObject(exception);
                        if (hasCtorIntrinsic) {
                            gate.ConsumeInstruction();
                            gate.CheckSafepoint();
                            intrinsicCtor(_intrinsicContext, ctorArgs);
                        } else if (facadeParamCount != 0) {
                            throw new OperationNotAllowedException(
                                $"intrinsic {typeName}::{name} (引数 {facadeParamCount} 個) は未登録です。");
                        }
                        return StackSlot.OfObject(exception);
                    }
                    if (hasCtorIntrinsic && name == ".ctor") {
                        // 例外ファサード以外で .ctor intrinsic が登録された型 (例: System.Net.WebClient):
                        // VmIntrinsicInstance として実体化し、状態は intrinsic が State に保持する
                        var facadeInstance = _heap.Allocate(new VmIntrinsicInstance(facadeType));
                        ctorArgs[0] = StackSlot.OfObject(facadeInstance);
                        gate.ConsumeInstruction();
                        gate.CheckSafepoint();
                        intrinsicCtor(_intrinsicContext, ctorArgs);
                        return StackSlot.OfObject(facadeInstance);
                    }
                    if (typeName == "System.Threading.Tasks.ValueTask" && name == ".ctor") {
                        var source = ctorArgs[1];
                        if (facadeParamCount == 1) {
                            if (source.ObjectValue is not VmTaskObject task)
                                throw new OperationNotAllowedException("ValueTask(Task) の source は VM Task でなければなりません。");
                            return StackSlot.OfValueType(new VmStructValue(facadeType, [StackSlot.OfObject(task)]));
                        }
                        if (facadeParamCount == 2) {
                            var valueTaskToken = unchecked((short)ctorArgs[2].AsInt32);
                            return ValueTaskRuntime.FromSource(_intrinsicContext, generic: false,
                                resultType: null, sourceSlot: source, token: valueTaskToken);
                        }
                    }
                    // それ以外の intrinsic 型の実体化は BCL 不実装の面として拒否し続ける
                    throw new NotSupportedException(
                        $"intrinsic 型 {typeName} のインスタンス生成は未対応です (例外ファサード型または .ctor intrinsic 登録済み型のみ)。");
                }
            }
            // intrinsic ファサードでない TypeRef 親 (依存アセンブリの型 / ネスト型) は
            // 実 TypeDef の .ctor として解決し、MethodDef と共通の生成経路へ流す
            if (_loader.ResolveTypeRefType(parent.Rid) is VmClassType realClass) {
                ctor = FindCtorThroughChain(realClass, name, facadeParamCount, signature.ParamTypes)
                    ?? throw new BadImageFormatException(
                        $"newobj の MemberRef 0x{token:X8} の解決先 .ctor {realClass.FullName}::{name} (引数 {facadeParamCount} 個) が見つかりません。");
            } else {
                throw new NotSupportedException(
                    $"newobj の MemberRef 0x{token:X8} ({typeName ?? "?"}::{name}) を解決できません。");
            }
        } else {
            throw new BadImageFormatException($"newobj トークン 0x{token:X8} のテーブルが不正です。");
        }

        var owner = (VmClassType)ctor.DeclaringType;
        // ゲストのカスタム delegate 宣言の newobj (object target, native int method)
        if (TypeChecks.IsDelegateType(owner)) {
            var pointerSlot = caller.Stack.Pop();
            var targetSlot = caller.Stack.Pop();
            return StackSlot.OfObject(NewDelegate(owner, targetSlot, pointerSlot));
        }
        EnsureInitialized(owner);
        var paramCount = ctor.Signature.ParamTypes.Length;
        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (owner.IsValueType) {
            // 構造体の newobj: this (既定値) を作り、.ctor があればミューテートして this を返す。
            // this は書き込み可能スロット (VmByRef) で渡す — CoreLib の構造体 ctor は
            // this = default の IL (initobj this) を持つことがあり (ReadOnlySpan 等)、
            // this が値スロットだと initobj/ldobj/stobj のアドレス要求に落ちる。
            // ctor 完了後のスロット値を戻り値とする
            var thisStorage = new[] { StackSlot.OfValueType(_objects.DefaultStruct(owner, _loader)) };
            args[0] = StackSlot.OfByRef(new VmByRef(thisStorage, 0));
            if (ctor.Body is not null)
                invoker.Invoke(ctor, args, null);
            return thisStorage[0].Kind == StackKind.ValueType
                ? thisStorage[0]
                : StackSlot.OfValueType(thisStorage[0]);
        }

        var instance = _heap.Allocate(new VmClassInstance(owner, _objects.CreateInstanceStorage(owner, _loader)));
        args[0] = StackSlot.OfObject(instance);
        if (ctor.Body is not null)
            invoker.Invoke(ctor, args, null);
        return StackSlot.OfObject(instance);
    }

    /// <summary>式木/動的コードから MethodInfo として保持された VM .ctor を実行する生成経路。</summary>
    public StackSlot ConstructExpression(VmMethod ctor, StackSlot[] values) {
        if (ctor.DeclaringType is not VmClassType owner)
            throw new UnhandledGuestException("System.NotSupportedException", $"型 {ctor.DeclaringType.FullName} の構築は未対応です。");
        if (owner.IsValueType) {
            var storage = new[] { StackSlot.OfValueType(_objects.DefaultStruct(owner, _loader)) };
            var callArgs = new StackSlot[values.Length + 1];
            callArgs[0] = StackSlot.OfByRef(new VmByRef(storage, 0));
            Array.Copy(values, 0, callArgs, 1, values.Length);
            invoker.Invoke(ctor, callArgs, null);
            return storage[0].Kind == StackKind.ValueType ? storage[0] : StackSlot.OfValueType(storage[0]);
        }
        var instance = _heap.Allocate(new VmClassInstance(owner, _objects.CreateInstanceStorage(owner, _loader)));
        var args = new StackSlot[values.Length + 1];
        args[0] = StackSlot.OfObject(instance);
        Array.Copy(values, 0, args, 1, values.Length);
        invoker.Invoke(ctor, args, null);
        return StackSlot.OfObject(instance);
    }

    private StackSlot NewDynamicMethodInstance(int parameterCount, InterpreterFrame caller) {
        if (parameterCount is not (3 or 4 or 5 or 7))
            throw new NotSupportedException($"DynamicMethod .ctor の引数 {parameterCount} 個は未対応です。");
        var facade = _loader.FindIntrinsicType("System.Reflection.Emit.DynamicMethod")
            ?? throw new InvalidOperationException("DynamicMethod ファサードがありません。");
        var instance = _heap.Allocate(new VmIntrinsicInstance(facade));
        var args = new StackSlot[parameterCount + 1];
        for (var i = parameterCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();
        args[0] = StackSlot.OfObject(instance);
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        ReflectionEmitRuntime.ConstructDynamicMethod(_intrinsicContext, args);
        return StackSlot.OfObject(instance);
    }

    /// <summary>
    /// 構築ジェネリック型 (TypeSpec 親の MemberRef) の newobj。
    /// 例: newobj instance void class List`1&lt;int32&gt;::.ctor() — 実引数を VmClassInstance/VmStructValue に
    /// 記録し、.ctor はその型引数の GenericContext で実行する (フィールドの !0 等が正しく解決される)。
    /// </summary>
    private StackSlot NewConstructedObject(int token, int memberRefRid, int typeSpecRid, InterpreterFrame caller) {
        var constructed = ResolveConstructedParent(typeSpecRid, caller.Context);
        var signature = SignatureDecoder.DecodeMethodSignature(
            _loader.Image.GetMemberRefSignature(memberRefRid).ToArray(),
            _loader.Image.Limits?.MaxSignatureDepth ?? 64,
            _loader.Image.Limits?.MaxGenericNestingDepth ?? 64);
        var paramCount = signature.ParamTypes.Length;
        var ctorName = _loader.GetMemberRefName(memberRefRid);
        var context = new GenericContext { ClassArgs = constructed.TypeArguments };

        // 構築ジェネリック デリゲート (Func<int> 等) の newobj (object, native int)。
        // 定義は BCL ファサード (VmIntrinsicType) のこともあるため ClassType キャストより先に判定する
        if (TypeChecks.IsDelegateType(constructed.Definition)) {
            var pointerSlot = caller.Stack.Pop();
            var targetSlot = caller.Stack.Pop();
            return StackSlot.OfObject(NewDelegate(constructed, targetSlot, pointerSlot));
        }

        // ValueTask<T> は intrinsic 値型であり、guest TypeDef の .ctor を実行しない。
        // 値から作る ctor と Task<T> を包む ctor を同じ VM Task 表現へ正規化する。
        if (constructed.Definition is VmIntrinsicType { FullName: "System.Threading.Tasks.ValueTask`1" } &&
            ctorName == ".ctor" && paramCount is 1 or 2) {
            var resultType = constructed.TypeArguments.FirstOrDefault()
                ?? _loader.FindIntrinsicType("System.Object")!;
            if (paramCount == 2) {
                var valueTaskToken = unchecked((short)caller.Stack.Pop().AsInt32);
                var sourceValue = caller.Stack.Pop();
                return ValueTaskRuntime.FromSource(_intrinsicContext, generic: true, resultType: resultType,
                    sourceSlot: sourceValue, token: valueTaskToken);
            }
            var sourceValueForTask = caller.Stack.Pop();
            VmTaskObject task;
            if (sourceValueForTask.ObjectValue is VmTaskObject existing) {
                task = existing;
            } else {
                var taskDefinition = _loader.FindIntrinsicType("System.Threading.Tasks.Task`1")!
                    ?? throw new InvalidOperationException("Task<T> intrinsic type が見つかりません。");
                var taskType = new VmConstructedType { Definition = taskDefinition, TypeArguments = [resultType] };
                task = _heap.Allocate(_intrinsicContext.Shared.GuestTasks.Create(taskType, completed: true, result: sourceValueForTask));
            }
            return StackSlot.OfValueType(new VmStructValue(constructed, [StackSlot.OfObject(task)], [resultType]));
        }

        var definition = (VmClassType)constructed.Definition;

        // .ctor は宣言型 (継承チェーン上の基底ジェネリック定義も含む) から署名精度で探す
        // (MemberRef の !0 と定義側のスロットキーはどちらも VmGenericParameterType に正規化され照合可能)
        var ctor = FindCtorThroughChain(definition, ctorName, paramCount, signature.ParamTypes);
        EnsureInitialized(definition);
        if (ctor is null)
            throw new BadImageFormatException(
                $"構築型 {constructed.FullName} に引数 {paramCount} 個の .ctor ({ctorName}) が見つかりません。");

        var args = new StackSlot[paramCount + 1];
        for (var i = paramCount; i >= 1; i--)
            args[i] = caller.Stack.Pop();

        if (definition.IsValueType) {
            // 構造体 newobj も this を書き込み可能スロット (VmByRef) で渡す (上記非ジェネリック
            // パスと同一規約 — initobj this を持つ CoreLib 構造体 ctor を受けるため)
            var thisStorage = new[] { StackSlot.OfValueType(_objects.DefaultStruct(definition, _loader, context, constructed.TypeArguments)) };
            args[0] = StackSlot.OfByRef(new VmByRef(thisStorage, 0));
            if (ctor?.Body is not null)
                invoker.Invoke(ctor, args, context);
            return thisStorage[0].Kind == StackKind.ValueType
                ? thisStorage[0]
                : StackSlot.OfValueType(thisStorage[0]);
        }

        var instance = _heap.Allocate(new VmClassInstance(definition,
            _objects.CreateInstanceStorage(definition, _loader, context), constructed.TypeArguments));
        args[0] = StackSlot.OfObject(instance);
        if (ctor?.Body is not null)
            invoker.Invoke(ctor, args, context);
        return StackSlot.OfObject(instance);
    }
}
