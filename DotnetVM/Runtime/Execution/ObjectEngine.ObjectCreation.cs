using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using DotnetVM.IL;
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
            VmMethodPointer pointer => new[] { new DelegateInvocation(targetSlot, pointer.Target, pointer.Context) },
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
    private StackSlot NewStringFromCtor(SigType[] parameterTypes, InterpreterFrame caller) {
        var paramCount = parameterTypes.Length;
        if (parameterTypes is [{ Kind: SigKind.SzArray, Inner.Kind: SigKind.Char }]) {
            var argument = caller.Stack.Pop();
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            var array = RequireCharArray(argument);
            var result = _intrinsicContext.Strings.Allocate(array.Length);
            CopyChars(result, 0, array, 0, array.Length);
            return StackSlot.OfObject(result);
        }
        var args = new StackSlot[paramCount];
        for (var i = paramCount; i >= 1; i--)
            args[i - 1] = caller.Stack.Pop();
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        // CoreCLR's InternalCall constructor enters the matching managed Ctor helper.
        if (parameterTypes is [{ Kind: SigKind.Pointer, Inner.Kind: SigKind.Char }, ..] &&
            _loader.TryResolveTrustedUnifiedType("System.String") is VmClassType stringType) {
            var helper = stringType.Methods.Single(method => method.Name == "Ctor" && method.IsStatic &&
                method.Signature.ParamTypes.SequenceEqual(parameterTypes));
            return invoker.Invoke(helper, args, null);
        }
        var strings = _intrinsicContext.Strings;
        switch (paramCount) {
            case 1: {
                if (args[0].ObjectValue is VmStructValue span &&
                    (span.StructType.FullName == "System.ReadOnlySpan`1<System.Char>" || span.StructType.FullName == "System.ReadOnlySpan`1" && span.TypeArguments.FirstOrDefault()?.FullName == "System.Char"))
                    return CoreLibBindings.MakeStringFromCharSpan(_intrinsicContext, args[0]);
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

    internal bool TryGetStringConstructorSignature(int token, out SigType[] parameterTypes) {
        parameterTypes = null!;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MethodDef) {
            if (!_methodDefConstructors.TryGetValue(token, out var ctor)) {
                ctor = _loader.GetMethodByToken((uint)token);
                if (ctor is null)
                    return false;
                _methodDefConstructors.TryAdd(token, ctor);
            }
            if (ctor.DeclaringType is not VmClassType {
                    FullName: "System.String", Loader.IsTrustedCoreLib: true } ||
                ctor.Name != ".ctor")
                return false;
            parameterTypes = ctor.Signature.ParamTypes;
        } else if (table == TableKind.MemberRef) {
            if (_stringConstructorSignatures.TryGetValue(token, out var cachedParameters)) {
                parameterTypes = cachedParameters;
            } else {
                var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0,
                    CodedIndexKind.MemberRefParent);
                if (parent.Table == TableKind.TypeSpec)
                    return false;
                var typeName = _loader.GetMemberRefParentTypeName(rid);
                if (typeName != "System.String" || _loader.GetMemberRefName(rid) != ".ctor")
                    return false;
                parameterTypes = _loader.DecodeMemberRefMethodSignature(rid).ParamTypes;
                _stringConstructorSignatures.TryAdd(token, parameterTypes);
            }
        } else {
            return false;
        }
        return true;
    }

    internal StackSlot NewStringFromCtorJit(SigType[] parameterTypes, InterpreterFrame caller) =>
        NewStringFromCtor(parameterTypes, caller);

    internal bool TryNewStringFromCtorJit(int token, InterpreterFrame caller, out StackSlot value) {
        value = default;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true ||
            !TryGetStringConstructorSignature(token, out var parameterTypes))
            return false;
        value = NewStringFromCtor(parameterTypes, caller);
        return true;
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

    /// <summary>
    /// Try the common, side-effect-free portion of <c>newobj</c> through a
    /// promoted constructor leaf while an outer JIT frame is already active.
    /// Only MethodDef constructors with a compatible leaf are accepted here;
    /// all dynamic tokens, value types, delegates and runtime-backed
    /// constructors continue through <see cref="NewObject"/>.
    /// </summary>
    internal bool TryNewObjectLeaf(int token, InterpreterFrame caller, Interpreter interpreter,
        out StackSlot value) {
        value = default;
        if (caller.Context is not null ||
            caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true ||
            (TableKind)(token >> 24) != TableKind.MethodDef)
            return false;

        if (!_methodDefConstructors.TryGetValue(token, out var ctor)) {
            ctor = _loader.GetMethodByToken(unchecked((uint)token));
            if (ctor is null)
                return false;
            ctor = _methodDefConstructors.GetOrAdd(token, ctor);
        }

        if (ctor.Body is null || ctor.Signature.GenericParamCount != 0 ||
            ctor.DeclaringType is not VmClassType owner || owner.IsValueType ||
            TypeChecks.IsDelegateType(owner) ||
            (owner.FullName == "System.Reflection.Emit.DynamicMethod" && ctor.Name == ".ctor") ||
            (owner.FullName == "System.String" && ctor.Name == ".ctor") ||
            (owner.FullName == "System.Guid" && ctor.Name == ".ctor") ||
            _loader.IsTrustedCoreLib && owner.FullName == "System.Threading.Tasks.ValueTask")
            return false;

        // The overwhelmingly common guest constructor shape is a field store
        // (optionally followed by the parameterless System.Object constructor).
        // It is safe to execute structurally: allocation, field initialization,
        // init-only validation and the original IL charge are all preserved,
        // while avoiding a nested frame for every tiny object in a hot loop.
        if (ctor.Loader?.IsTrustedCoreLib != true &&
            ctor.Loader?.IsTrustedVmCoreLib != true &&
            ctor.Loader?.IsTrustedBcl != true &&
            ctor.Signature.ParamTypes.Length == 1 &&
            caller.Stack.Count > 0 &&
            TryGetSimpleFieldConstructor(ctor, out var simple) &&
            ctor.CanWriteInitOnly(simple.Field)) {
            EnsureInitializedForAllocation(owner);
            var storage = simple.CanSkipDefaultStorage
                ? new StackSlot[1]
                : _objects.CreateInstanceStorage(owner, _loader);
            var simpleInstance = _heap.Allocate(new VmClassInstance(owner, storage));
            simpleInstance.Fields[simple.FieldIndex] =
                SlotOps.StoreCopyOfValue(caller.Stack.Peek());
            caller.Stack.DropArguments(1);
            interpreter.ConsumeJitInstruction(simple.InstructionCost);
            value = StackSlot.OfObject(simpleInstance);
            return true;
        }

        var leaf = interpreter.GetNestedConstructorLeaf(ctor);
        if (leaf is null)
            return false;

        EnsureInitializedForAllocation(owner);
        var instance = _heap.Allocate(new VmClassInstance(owner,
            _objects.CreateInstanceStorage(owner, _loader)));
        var receiver = StackSlot.OfObject(instance);
        using var arguments = caller.BorrowConstructorArguments(ctor.Signature.ParamTypes.Length, receiver);
        if (!interpreter.TryInvokeCompiledLeafNested(ctor, leaf, arguments.Arguments, out _))
            throw new InvalidOperationException("コンストラクターleaf JITの実行条件が途中で失われました。");
        value = receiver;
        return true;
    }

    /// <summary>
    /// Elide an immediately consumed object when the constructor and getter
    /// are both pure structural accessors.  This is a general escape-analysis
    /// fast path for <c>newobj; call get_Field</c>, not a type-specific route:
    /// the constructor must be a one-field store and the getter must load that
    /// same field.  No guest-visible object can escape between the two IL
    /// instructions, so retaining only the value is semantically equivalent.
    /// </summary>
    internal bool TryNewObjectAndGetField(int constructorToken, int getterToken,
        InterpreterFrame caller, Interpreter interpreter, out StackSlot value,
        out int instructionCost) {
        value = default;
        instructionCost = 0;
        if (caller.Context is not null || caller.Method.DynamicTokens is not null ||
            (TableKind)(constructorToken >> 24) is not (TableKind.MethodDef or TableKind.MemberRef) ||
            (TableKind)(getterToken >> 24) != TableKind.MethodDef || caller.Stack.Count == 0)
            return false;

        var constructor = ResolveConstructor(constructorToken, caller);
        var getter = ResolveMethodDef(getterToken);
        if (constructor is null || getter is null ||
            constructor.DeclaringType is not VmClassType owner || owner.IsValueType ||
            constructor.Loader?.IsTrustedCoreLib == true ||
            constructor.Loader?.IsTrustedVmCoreLib == true ||
            constructor.Loader?.IsTrustedBcl == true ||
            constructor.Signature.ParamTypes.Length != 1 ||
            getter.DeclaringType != owner || getter.IsStatic || getter.Signature.HasThis == false ||
            getter.Signature.ParamTypes.Length != 0 || getter.Signature.ReturnType.Kind == SigKind.Void)
            return false;
        if (!TryGetSimpleFieldConstructor(constructor, out var simple) ||
            !constructor.CanWriteInitOnly(simple.Field) ||
            !TryGetSimpleFieldGetter(getter, out var getterField) ||
            getterField != simple.Field)
            return false;

        EnsureInitializedForAllocation(owner);
        value = SlotOps.StoreCopyOfValue(caller.Stack.Peek());
        caller.Stack.DropArguments(1);
        var getterCost = _constructorInstructionCosts.GetOrAdd(getter,
            static method => method.DecodeIl().Sum(static instruction => instruction.InstructionCost));
        instructionCost = simple.InstructionCost + getterCost;
        interpreter.ConsumeJitInstruction(instructionCost);
        return true;
    }

    private VmMethod? ResolveMethodDef(int token) {
        if (_methodDefConstructors.TryGetValue(token, out var cached))
            return cached;
        var method = _loader.GetMethodByToken(unchecked((uint)token));
        return method is null ? null : _methodDefConstructors.GetOrAdd(token, method);
    }

    private VmMethod? ResolveConstructor(int token, InterpreterFrame caller) {
        if ((TableKind)(token >> 24) == TableKind.MethodDef)
            return ResolveMethodDef(token);
        var rid = token & 0xFFFFFF;
        var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0,
            CodedIndexKind.MemberRefParent);
        var signature = _loader.DecodeMemberRefMethodSignature(rid);
        var name = _loader.GetMemberRefName(rid);
        VmClassType? owner = parent.Table switch {
            TableKind.TypeRef => _loader.ResolveTypeRefType(parent.Rid) as VmClassType,
            TableKind.TypeSpec => ResolveConstructedParent(parent.Rid, caller.Context).Definition as VmClassType,
            _ => null,
        };
        return owner is null ? null : FindCtorThroughChain(owner, name, signature.ParamTypes.Length,
            signature.ParamTypes);
    }

    private bool TryGetSimpleFieldGetter(VmMethod getter, out VmField field) {
        if (_simpleFieldGetters.TryGetValue(getter, out field!))
            return true;
        if (_nonSimpleFieldGetters.ContainsKey(getter)) {
            field = null!;
            return false;
        }
        var code = getter.DecodeIl();
        if (code.Length != 3 || code[0].Op != ILOp.Ldarg_0 ||
            code[1].Op != ILOp.Ldfld || code[2].Op != ILOp.Ret)
            return RejectSimpleFieldGetter(getter, out field);
        field = ResolveFieldToken(code[1].IntOperand);
        if (field.IsStatic || !ReferenceEquals(field.DeclaringType, getter.DeclaringType))
            return RejectSimpleFieldGetter(getter, out field);
        _simpleFieldGetters.TryAdd(getter, field);
        return true;
    }

    private bool RejectSimpleFieldGetter(VmMethod getter, out VmField field) {
        _nonSimpleFieldGetters.TryAdd(getter, 0);
        field = null!;
        return false;
    }

    private bool TryGetSimpleFieldConstructor(VmMethod ctor, out SimpleFieldConstructor info) {
        if (_simpleFieldConstructors.TryGetValue(ctor, out info!))
            return true;
        if (_nonSimpleFieldConstructors.ContainsKey(ctor)) {
            info = null!;
            return false;
        }

        var code = ctor.DecodeIl();
        var hasOnlyFieldStore = code.Length == 4 &&
            code[0].Op == ILOp.Ldarg_0 &&
            code[1].Op == ILOp.Ldarg_1 &&
            code[2].Op == ILOp.Stfld &&
            code[3].Op == ILOp.Ret;
        var hasObjectBaseCall = code.Length is 6 or 7 &&
            code[0].Op == ILOp.Ldarg_0 &&
            code[1].Op == ILOp.Ldarg_1 &&
            code[2].Op == ILOp.Stfld &&
            code[3].Op == ILOp.Ldarg_0 &&
            code[4].Op == ILOp.Call &&
            code[^1].Op == ILOp.Ret &&
            (code.Length == 6 || code[5].Op == ILOp.Nop) &&
            IsObjectParameterlessConstructor(code[4].IntOperand, ctor.DeclaringType);
        if (!hasOnlyFieldStore && !hasObjectBaseCall)
            return RejectSimpleFieldConstructor(ctor, out info);

        var field = ResolveFieldToken(code[2].IntOperand);
        if (field.IsStatic)
            return RejectSimpleFieldConstructor(ctor, out info);
        var layout = _objects.GetLayout((VmClassType)ctor.DeclaringType);
        if (!layout.TryGetValue(field, out var fieldIndex))
            return RejectSimpleFieldConstructor(ctor, out info);

        info = new SimpleFieldConstructor(field, fieldIndex, layout.Count == 1,
            code.Sum(static instruction => instruction.InstructionCost));
        _simpleFieldConstructors.TryAdd(ctor, info);
        return true;
    }

    private bool RejectSimpleFieldConstructor(VmMethod ctor, out SimpleFieldConstructor info) {
        _nonSimpleFieldConstructors.TryAdd(ctor, 0);
        info = null!;
        return false;
    }

    private bool IsObjectParameterlessConstructor(int token, VmType declaringType) {
        if (declaringType is not VmClassType owner || owner.BaseType?.FullName != "System.Object")
            return false;
        var table = (TableKind)(token >> 24);
        var rid = token & 0xFFFFFF;
        if (table == TableKind.MemberRef)
            return _loader.GetMemberRefName(rid) == ".ctor" &&
                _loader.GetMemberRefParentTypeName(rid) == "System.Object" &&
                _loader.DecodeMemberRefMethodSignature(rid) is { HasThis: true, ParamTypes.Length: 0 };
        if (table == TableKind.MethodDef &&
            _loader.GetMethodByToken(unchecked((uint)token)) is { } method)
            return method.Name == ".ctor" && method.Signature.HasThis &&
                method.Signature.ParamTypes.Length == 0 &&
                method.DeclaringType.FullName == "System.Object";
        return false;
    }

    /// <summary>
    /// Construct the common array-backed value-type shape without entering a
    /// nested interpreter frame.  This is deliberately structural: it covers
    /// trusted CoreLib wrappers whose instance is exactly a managed reference
    /// plus a length, while leaving user-defined types and all other .ctors on
    /// the normal IL path.
    /// </summary>
    internal bool TryNewArrayBackedValueType(int token, InterpreterFrame caller,
        out StackSlot value, out int nestedInstructionCost) {
        value = default;
        nestedInstructionCost = 0;
        if ((TableKind)(token >> 24) != TableKind.MemberRef)
            return false;

        if (caller.Stack.Count == 0 || caller.Stack.Peek().ObjectValue is not VmArray array)
            return false;

        var cacheable = caller.Context is null &&
            caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) != true;
        if (!cacheable || !_arrayBackedValueTypes.TryGetValue(token, out var info)) {
            var memberRefRid = token & 0xFFFFFF;
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, memberRefRid, 0,
                CodedIndexKind.MemberRefParent);
            if (parent.Table != TableKind.TypeSpec)
                return false;

            var constructed = ResolveConstructedParent(parent.Rid, caller.Context);
            if (constructed.Definition is not VmClassType definition ||
                !definition.IsValueType ||
                definition.Loader?.IsTrustedCoreLib != true ||
                constructed.TypeArguments.Length != 1)
                return false;

            var signature = _loader.DecodeMemberRefMethodSignature(memberRefRid);
            if (signature.ParamTypes is not [{ Kind: SigKind.SzArray, Inner: { } }])
                return false;

            var elementType = constructed.TypeArguments[0];
            var layout = _objects.GetLayout(definition);
            if (layout.Count != 2)
                return false;
            VmField? referenceField = null;
            VmField? lengthField = null;
            var referenceIndex = -1;
            var lengthIndex = -1;
            foreach (var (field, index) in layout) {
                if (field.Name == "_reference") {
                    referenceField = field;
                    referenceIndex = index;
                } else if (field.Name == "_length") {
                    lengthField = field;
                    lengthIndex = index;
                }
            }
            if (referenceField is null || lengthField is null || referenceIndex < 0 || lengthIndex < 0 ||
                referenceField.FieldType is not VmByRefType ||
                !string.Equals(lengthField.FieldType?.FullName, "System.Int32", StringComparison.Ordinal))
                return false;

            var ctorName = _loader.GetMemberRefName(memberRefRid);
            var ctor = FindCtorThroughChain(definition, ctorName, 1, signature.ParamTypes);
            if (ctor?.Body is null)
                return false;

            var instructionCost = _constructorInstructionCosts.GetOrAdd(ctor,
                static method => method.DecodeIl().Sum(static instruction => instruction.InstructionCost));
            info = new ArrayBackedValueTypeInfo(constructed, definition, elementType, ctor,
                referenceIndex, lengthIndex, instructionCost);
            if (cacheable)
                info = _arrayBackedValueTypes.GetOrAdd(token, info);
        }

        var actualElementType = array.ArrayType.ElementType;
        if (!ReferenceEquals(actualElementType, info.ElementType) &&
            !string.Equals(actualElementType.FullName, info.ElementType.FullName,
                StringComparison.Ordinal))
            return false;

        EnsureInitializedForAllocation(info.Definition);
        _ = caller.Stack.Pop();
        var fields = new StackSlot[2];
        fields[info.ReferenceIndex] = StackSlot.OfByRef(VmByRef.ArrayElement(array, 0));
        fields[info.LengthIndex] = StackSlot.OfInt32(array.Length);
        value = StackSlot.OfValueType(new VmStructValue(info.Constructed, fields, info.Constructed.TypeArguments));
        nestedInstructionCost = info.InstructionCost;
        return true;
    }

    public StackSlot? NewObject(int token, InterpreterFrame caller) {
        if (caller.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var reflectedReference) == true && reflectedReference is VmRuntimeMethod reflectedCtor) {
            using var argumentLease = caller.BorrowCallArguments(reflectedCtor.Target.Signature.ParamTypes.Length);
            return StackSlot.OfObject(CreateInstanceByCtor(reflectedCtor.ReflectedType ?? reflectedCtor.Target.DeclaringType,
                reflectedCtor.Target, argumentLease.Arguments, null));
        }
        if (caller.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out var dynamicReference) == true &&
            dynamicReference is VmMethod dynamicCtor) {
            using var argumentLease = caller.BorrowCallArguments(dynamicCtor.Signature.ParamTypes.Length);
            return ConstructExpression(dynamicCtor, argumentLease.Arguments);
        }
        VmMethod ctor;
        var table = (TableKind)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        if (table == TableKind.MethodDef) {
            if (!_methodDefConstructors.TryGetValue(token, out ctor!)) {
                ctor = _loader.GetMethodByToken((uint)token)
                    ?? throw new BadImageFormatException($"newobj トークン 0x{token:X8} を解決できません。");
                ctor = _methodDefConstructors.GetOrAdd(token, ctor);
            }
            if (ctor.DeclaringType.FullName == "System.Reflection.Emit.DynamicMethod" && ctor.Name == ".ctor")
                return NewDynamicMethodInstance(ctor.Signature.ParamTypes.Length, caller);
            if (ctor.DeclaringType is VmClassType { FullName: "System.String", Loader.IsTrustedCoreLib: true } && ctor.Name == ".ctor")
                return NewStringFromCtor(ctor.Signature.ParamTypes, caller);
        } else if (table == TableKind.MemberRef) {
            var parent = _loader.Image.Tables.DecodeCoded(TableKind.MemberRef, rid, 0, CodedIndexKind.MemberRefParent);
            if (parent.Table == TableKind.TypeSpec)
                return NewConstructedObject(token, rid, parent.Rid, caller);
            var signature = _loader.DecodeMemberRefMethodSignature(rid);
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
                    return NewStringFromCtor(signature.ParamTypes, caller);
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
                    // Preserve overload identity for host-backed facade constructors.
                    // Only a framework AssemblyRef can select a normalized binding.
                    string[]? ctorParameterNames = null;
                    if (parent.Table == TableKind.TypeRef && (typeName is ("System.Globalization.CultureInfo" or "System.Text.UTF8Encoding" or "System.Globalization.NumberFormatInfo" or
                        "System.Text.RegularExpressions.Regex" or "System.IO.Compression.GZipStream" or "System.IO.Compression.DeflateStream" or "System.IO.Compression.BrotliStream" or "System.IO.Compression.ZLibStream") ||
                        CoreLibBindings.HttpBoundaryTypes.Concat(CoreLibBindings.CryptoBoundaryTypes).Any(t => t.FullName == typeName))) {
                        var scope = _loader.Image.GetTerminalTypeRefScope(parent.Rid);
                        if (scope.Table == TableKind.AssemblyRef && TypeLoader.IsKnownFrameworkContract(_loader.Image.GetAssemblyRefIdentity(scope.Rid))) {
                            ctorParameterNames = signature.ParamTypes.Select(t => _loader.ResolveToken(t, caller.Context).FullName).ToArray();
                            if (_intrinsics.TryGetBinding(BindingKey.Instance(typeName, name, ctorParameterNames), out var boundCtor, out _)) {
                                intrinsicCtor = boundCtor;
                                hasCtorIntrinsic = true;
                            }
                        }
                    }
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
                        var previousParameters = _intrinsicContext.ParameterTypeNames;
                        StackSlot? intrinsicResult;
                        try {
                            _intrinsicContext.ParameterTypeNames = ctorParameterNames ?? [];
                            intrinsicResult = intrinsicCtor(_intrinsicContext, ctorArgs);
                        } finally { _intrinsicContext.ParameterTypeNames = previousParameters; }
                        if (intrinsicResult is { Kind: StackKind.ValueType } valueTaskValue) {
                            return valueTaskValue;
                        }
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
                InvokeGuest(ctor, args);
            return thisStorage[0].Kind == StackKind.ValueType
                ? thisStorage[0]
                : StackSlot.OfValueType(thisStorage[0]);
        }

        var instance = _heap.Allocate(new VmClassInstance(owner, _objects.CreateInstanceStorage(owner, _loader)));
        args[0] = StackSlot.OfObject(instance);
        if (ctor.Body is not null)
            InvokeGuest(ctor, args);
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
            InvokeGuest(ctor, callArgs);
            return storage[0].Kind == StackKind.ValueType ? storage[0] : StackSlot.OfValueType(storage[0]);
        }
        var instance = _heap.Allocate(new VmClassInstance(owner, _objects.CreateInstanceStorage(owner, _loader)));
        var args = new StackSlot[values.Length + 1];
        args[0] = StackSlot.OfObject(instance);
        Array.Copy(values, 0, args, 1, values.Length);
        InvokeGuest(ctor, args);
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
        var signature = _loader.DecodeMemberRefMethodSignature(memberRefRid);
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
        if (constructed.Definition.FullName == "System.Threading.Tasks.ValueTask`1" &&
            (constructed.Definition is VmIntrinsicType ||
             constructed.Definition is VmClassType { Loader.IsTrustedCoreLib: true }) &&
            ctorName == ".ctor" && paramCount is 1 or 2) {
            var resultType = constructed.TypeArguments.FirstOrDefault()
                ?? _loader.FindIntrinsicType("System.Object")!;
            if (paramCount == 2) {
                var valueTaskToken = unchecked((short)caller.Stack.Pop().AsInt32);
                var sourceValue = caller.Stack.Pop();
                return ValueTaskRuntime.FromSource(_intrinsicContext, generic: true, resultType: resultType,
                    sourceSlot: sourceValue, token: valueTaskToken);
            }

            // ValueTask<T>(T) and ValueTask<T>(Task<T>) share the same arity but
            // have completely different representations.  The former is the
            // overwhelmingly common completed-value path.  Do not normalize a
            // primitive T into a newly allocated completed Task<T>; preserve the
            // direct ValueTask representation used by the intrinsic binding.
            if (signature.ParamTypes[0].Kind == SigKind.GenericVar) {
                var directResult = caller.Stack.Pop();
                return directResult.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt
                    or StackKind.IntPtr or StackKind.Float
                    ? StackSlot.OfInlineValue(constructed, directResult)
                    : StackSlot.OfValueType(new VmStructValue(constructed,
                        [directResult, default], [resultType]));
            }

            var sourceValueForTask = caller.Stack.Pop();
            VmTaskObject task;
            if (sourceValueForTask.ObjectValue is VmTaskObject existing) {
                task = existing;
            } else {
                var taskDefinition = _loader.FindIntrinsicType("System.Threading.Tasks.Task`1")!
                    ?? throw new InvalidOperationException("Task<T> intrinsic type が見つかりません。");
                var taskType = _intrinsicContext.ConstructedType(taskDefinition, resultType);
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
                InvokeGuest(ctor, args, context);
            return thisStorage[0].Kind == StackKind.ValueType
                ? thisStorage[0]
                : StackSlot.OfValueType(thisStorage[0]);
        }

        var instance = _heap.Allocate(new VmClassInstance(definition,
            _objects.CreateInstanceStorage(definition, _loader, context), constructed.TypeArguments));
        args[0] = StackSlot.OfObject(instance);
        if (ctor?.Body is not null)
            InvokeGuest(ctor, args, context);
        return StackSlot.OfObject(instance);
    }
}
