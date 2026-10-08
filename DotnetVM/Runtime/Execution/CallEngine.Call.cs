using DotnetVM.Metadata;
using System.Threading;
using System.Runtime.CompilerServices;
using DotnetVM.Metadata.Signatures;
using DotnetVM.IL;
using DotnetVM.Policy;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Intrinsics.Builtins;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Execution;

internal enum JitCallRoute : byte {
    None,
    TypeFromHandle,
    ConstrainedIntrinsic,
    StaticIntrinsic,
    RuntimeMetadata,
    CachedVirtual,
    CompiledMethod,
    CachedBinding,
    InstanceIntrinsic,
    CoreLibSurface,
    CollectionFast,
    SpanFast,
    LinqFast,
    PrimitiveFast,
}

internal sealed partial class CallEngine {
    // ---- 呼出 (call / callvirt) ----

    /// <summary>
    /// Static intrinsic calls have no receiver dispatch or guest override to
    /// inspect. Generated code can therefore use the already-resolved target
    /// directly and avoid re-entering the full call compatibility ladder.
    /// Instance callvirt remains on the normal path because its runtime
    /// receiver may select a guest implementation.
    /// </summary>
    internal bool TryInvokeStaticIntrinsicJit(int token, InterpreterFrame caller,
        bool isCallvirt, int constrainedToken, out StackSlot? result) {
        return TryInvokeStaticIntrinsicJit(token, caller, isCallvirt, constrainedToken,
            resolvedTarget: null, out result);
    }

    private bool TryInvokeStaticIntrinsicJit(int token, InterpreterFrame caller,
        bool isCallvirt, int constrainedToken, CallTarget? resolvedTarget,
        out StackSlot? result) {
        result = null;
        if (isCallvirt || constrainedToken != 0 ||
            caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;

        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (target.Intrinsic is not { } intrinsic || target.HasThis)
            return false;

        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var arguments = argumentLease.Arguments;
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
        _intrinsicContext.MethodTypeArgumentNames = target.MethodTypeArgumentNames;
        _intrinsicContext.ClassTypeArgumentNames = target.ClassTypeArgumentNames;
        _intrinsicContext.MethodTypeArguments = target.MethodArgs ?? [];
        _intrinsicContext.ClassTypeArguments = target.ClassArgs ?? [];
        using var roots = RegisterTransientRootsIfNeeded(arguments);
        var intrinsicResult = intrinsic(_intrinsicContext, arguments);
        if (target.ReturnsValue == true)
            result = intrinsicResult;
        return true;
    }

    /// <summary>
    /// Generated callvirt sites for value-type CoreLib facades (ValueTask,
    /// awaiters, spans, and similar wrappers) have no possible guest override.
    /// Dispatching those calls through the general virtual/binding ladder is
    /// pure overhead: the target is already a sealed intrinsic surface and the
    /// receiver remains a VM slot/byref.  Keep reference receivers on the
    /// compatibility path, but invoke this common value-facade shape directly.
    /// </summary>
    internal bool TryInvokeInstanceIntrinsicJit(int token, InterpreterFrame caller,
        int constrainedToken, out StackSlot? result) {
        return TryInvokeInstanceIntrinsicJit(token, caller, constrainedToken,
            resolvedTarget: null, out result);
    }

    private bool TryInvokeInstanceIntrinsicJit(int token, InterpreterFrame caller,
        int constrainedToken, CallTarget? resolvedTarget, out StackSlot? result) {
        result = null;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;

        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (target.Intrinsic is not { } intrinsic || !target.HasThis ||
            target.Method?.DeclaringType is VmClassType { IsInterface: true } ||
            target.DeclaringType is "System.IEquatable`1" or "System.IComparable`1")
            return false;

        var stackArguments = caller.Stack.ArgumentSlots(target.Arity);
        if (stackArguments.Length == 0)
            return false;
        var receiver = stackArguments[0];
        if (receiver.Kind == StackKind.Object && receiver.ObjectValue is not VmStructValue)
            return false;
        if (constrainedToken != 0 && receiver.Kind is StackKind.Object)
            return false;

        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var arguments = argumentLease.Arguments;
        if (arguments[0].Kind == StackKind.ByRef && arguments[0].ObjectValue is VmByRef byRef &&
            !PreservesByRefReceiver(target.DeclaringType))
            arguments[0] = byRef.Read();

        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
        _intrinsicContext.MethodTypeArgumentNames = target.MethodTypeArgumentNames;
        _intrinsicContext.ClassTypeArgumentNames = target.ClassTypeArgumentNames;
        _intrinsicContext.MethodTypeArguments = target.MethodArgs ?? [];
        _intrinsicContext.ClassTypeArguments = target.ClassArgs ?? [];
        using var roots = RegisterTransientRootsIfNeeded(arguments);
        var intrinsicResult = intrinsic(_intrinsicContext, arguments);
        if (target.ReturnsValue == true)
            result = intrinsicResult;
        return true;
    }

    /// <summary>
    /// The managed CoreLib facade uses runtime bindings rather than an
    /// IntrinsicImpl on the resolved method.  Value-type receivers cannot
    /// carry a guest override, so a cached binding can be entered directly
    /// from generated code after the normal call-site target lookup.
    /// </summary>
    internal bool TryInvokeCachedBindingJit(int token, InterpreterFrame caller,
        int constrainedToken, out StackSlot? result) {
        return TryInvokeCachedBindingJit(token, caller, constrainedToken,
            resolvedTarget: null, out result);
    }

    private bool TryInvokeCachedBindingJit(int token, InterpreterFrame caller,
        int constrainedToken, CallTarget? resolvedTarget, out StackSlot? result) {
        result = null;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;
        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (target.Method is not { Body: not null } method ||
            !HasRuntimeBinding(method.DeclaringType))
            return false;

        var stackArguments = caller.Stack.ArgumentSlots(target.Arity);
        if (stackArguments.Length < target.Arity ||
            method.Signature.HasThis &&
            stackArguments.Length == 0 ||
            method.Signature.HasThis &&
            stackArguments[0].Kind is StackKind.Object or StackKind.Empty)
            return false;

        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var arguments = argumentLease.Arguments;
        var callerDomain = CallerDomainOf(caller);
        if (_intrinsics.IsSealed && (callerDomain == BindingDomain.Guest
                ? target.GuestBinding : target.TrustedBinding) is { } siteCached) {
            var bound = InvokeBindingResolution(method, target.MethodArgs, target.ClassArgs,
                siteCached, arguments, alreadyGated: true);
            if (method.Signature.ReturnType.Kind != SigKind.Void)
                result = bound;
            return true;
        }
        var cacheKey = new BindingCacheKey(method, target.MethodArgs, target.ClassArgs, callerDomain);
        if (_intrinsics.IsSealed && _bindingResolutions.TryGetValue(cacheKey, out var cached)) {
            if (callerDomain == BindingDomain.Guest)
                target.GuestBinding = cached;
            else
                target.TrustedBinding = cached;
            var bound = InvokeBindingResolution(method, target.MethodArgs, target.ClassArgs,
                cached, arguments, alreadyGated: true);
            if (method.Signature.ReturnType.Kind != SigKind.Void)
                result = bound;
            return true;
        }

        if (TryResolveBinding(method, target.MethodArgs, arguments, callerDomain,
                target.ClassArgs, out var resolution)) {
            if (_intrinsics.IsSealed && _bindingResolutions.Count < 4096)
                _bindingResolutions.TryAdd(cacheKey, resolution);
            if (_intrinsics.IsSealed) {
                if (callerDomain == BindingDomain.Guest)
                    target.GuestBinding = resolution;
                else
                    target.TrustedBinding = resolution;
            }
            var bound = InvokeBindingResolution(method, target.MethodArgs, target.ClassArgs,
                resolution, arguments, alreadyGated: true);
            if (method.Signature.ReturnType.Kind != SigKind.Void)
                result = bound;
            return true;
        }

        // The registry can contain bindings for only part of a facade's
        // surface. Restore the exact stack shape before falling back.
        for (var i = 0; i < arguments.Length; i++)
            caller.Stack.Push(arguments[i]);
        return false;
    }

    /// <summary>
    /// Type.GetTypeFromHandle is a CoreLib field-unwrapping shim.  A VM type
    /// token is already a VmTypeHandle, so entering that shim as a guest frame
    /// only repeats the same object-model lookup.  Preserve the handle's
    /// managed-field identity and bypass just this shape of call; all other
    /// RuntimeTypeHandle values continue through the normal IL path.
    /// </summary>
    internal bool TryInvokeTypeFromHandleJit(int token, InterpreterFrame caller,
        out StackSlot? result) {
        return TryInvokeTypeFromHandleJit(token, caller, resolvedTarget: null, out result);
    }

    private bool TryInvokeTypeFromHandleJit(int token, InterpreterFrame caller,
        CallTarget? resolvedTarget, out StackSlot? result) {
        result = null;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;
        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        var declaringType = DeclaringTypeOf(target);
        if (declaringType != "System.Type" || target.Name != "GetTypeFromHandle" ||
            target.HasThis || target.Arity != 1)
            return false;
        // Do not borrow until applicability is known. A negative probe must
        // preserve the caller stack for the next common call path.
        if (caller.Stack.Count < 1 ||
            caller.Stack.ArgumentSlots(1)[0].ObjectValue is not VmTypeHandle handle)
            return false;
        using var argumentLease = caller.BorrowCallArguments(1);
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        result = _objectEngine.RuntimeTypeFromHandle(handle);
        return true;
    }

    /// <summary>共通の型メタデータ/プリミティブ値面をスロットのまま実行する。</summary>
    internal bool TryInvokeCommonRuntimeMetadataJit(int token, InterpreterFrame caller,
        out StackSlot? result) {
        return TryInvokeCommonRuntimeMetadataJit(token, caller,
            resolvedTarget: null, out result);
    }

    private bool TryInvokeCommonRuntimeMetadataJit(int token, InterpreterFrame caller,
        CallTarget? resolvedTarget, out StackSlot? result) {
        result = null;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;
        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        var declaringType = DeclaringTypeOf(target);
        if (TryInvokePrimitiveEqualityComparerJit(target, caller, out result))
            return true;
        if (declaringType == "System.Type" && target.Name is "get_IsValueType" or "get_IsInterface" &&
            target.HasThis && target.Arity == 1) {
            if (caller.Stack.Count < 1 ||
                caller.Stack.ArgumentSlots(1)[0].ObjectValue is not VmRuntimeObject runtimeType)
                return false;
            using var lease = caller.BorrowCallArguments(1);
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = StackSlot.OfInt32(target.Name == "get_IsValueType"
                ? runtimeType.Target.IsValueType ? 1 : 0
                : runtimeType.Target.IsInterface ? 1 : 0);
            return true;
        }
        // String's non-randomized hash helper is an internal CoreLib method
        // whose IL uses a char* local. That representation is intentionally
        // outside the VM JIT, but the operation itself is pure over VmString
        // and is shared by every ordinal string-keyed collection. Keep the
        // exact CoreLib Marvin-independent algorithm at this representation
        // boundary instead of falling back to an interpreter frame per hash.
        if (declaringType == "System.String" &&
            target.Name == "GetNonRandomizedHashCode" && target.HasThis &&
            target.Arity == 1 && caller.Stack.Count >= 1 &&
            caller.Stack.ArgumentSlots(1)[0].ObjectValue is VmString stringValue) {
            using var stringHashLease = caller.BorrowCallArguments(1);
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = StackSlot.OfInt32(NonRandomizedStringHash(stringValue.Value));
            return true;
        }
        if (declaringType == "System.String" && target.Name == "Equals" &&
            ((target.HasThis && target.Arity == 2) || (!target.HasThis && target.Arity == 2)) &&
            caller.Stack.Count >= target.Arity) {
            var equalityArguments = caller.Stack.ArgumentSlots(target.Arity);
            var left = target.HasThis ? equalityArguments[0] : equalityArguments[0];
            var right = target.HasThis ? equalityArguments[1] : equalityArguments[1];
            var leftText = left.ObjectValue as VmString;
            var rightText = right.ObjectValue as VmString;
            if ((left.ObjectValue is null || leftText is not null) &&
                (right.ObjectValue is null || rightText is not null)) {
                using var stringEqualsLease = caller.BorrowCallArguments(target.Arity);
                gate.ConsumeInstruction();
                gate.CheckSafepoint();
                result = StackSlot.OfInt32(string.Equals(leftText?.Value, rightText?.Value,
                    StringComparison.Ordinal) ? 1 : 0);
                return true;
            }
        }
        if (target.Name != "GetHashCode" || !target.HasThis || target.Arity != 1)
            return false;
        if (caller.Stack.Count < 1)
            return false;
        var value = caller.Stack.ArgumentSlots(1)[0];
        if (value.ObjectValue is VmByRef byRef)
            value = byRef.Read();
        else if (value.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length != 0)
            value = boxed.Fields[0];
        var primitiveTarget = declaringType is ("System.Int32" or "System.UInt32" or "System.Int16" or "System.UInt16"
            or "System.Byte" or "System.SByte" or "System.Char" or "System.Boolean"
            or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double");
        var rawPrimitiveTarget = declaringType is "System.Object" or "System.ValueType" &&
            value.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt or StackKind.Float;
        if (!primitiveTarget && !rawPrimitiveTarget)
            return false;
        using var hashLease = caller.BorrowCallArguments(1);
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        result = declaringType is "System.Int64" or "System.UInt64" || rawPrimitiveTarget && value.Kind is StackKind.Int64 or StackKind.NativeInt
            ? StackSlot.OfInt32(unchecked((int)value.Int64Value))
            : declaringType is "System.Single" or "System.Double" || rawPrimitiveTarget && value.Kind == StackKind.Float
                ? StackSlot.OfInt32(value.DoubleValue.GetHashCode())
                : StackSlot.OfInt32(value.AsInt32);
        return true;
    }

    private static int NonRandomizedStringHash(string value) {
        unchecked {
            uint hash1 = (5381u << 16) + 5381u;
            uint hash2 = hash1;
            var index = 0;
            while (value.Length - index > 2) {
                var first = (uint)value[index] | ((uint)value[index + 1] << 16);
                hash1 = (RotateLeft5(hash1) + hash1) ^ first;
                index += 2;
                var second = (uint)value[index];
                if (index + 1 < value.Length)
                    second |= (uint)value[index + 1] << 16;
                hash2 = (RotateLeft5(hash2) + hash2) ^ second;
                index += 2;
            }
            if (value.Length - index > 0) {
                uint tail = value[index];
                if (value.Length - index > 1)
                    tail |= (uint)value[index + 1] << 16;
                hash2 = (RotateLeft5(hash2) + hash2) ^ tail;
            }
            return (int)(hash1 + hash2 * 1566083941u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft5(uint value) => (value << 5) | (value >> 27);

    private bool TryInvokeCommonRuntimeMetadata(CallTarget target, StackSlot[] arguments,
        out StackSlot? result) {
        result = null;
        var declaringType = DeclaringTypeOf(target);
        if (declaringType == "System.Type" && target.Name == "GetTypeFromHandle" &&
            !target.HasThis && target.Arity == 1 && arguments[0].ObjectValue is VmTypeHandle handle) {
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = _objectEngine.RuntimeTypeFromHandle(handle);
            return true;
        }
        if (declaringType == "System.Type" && target.Name is "get_IsValueType" or "get_IsInterface" &&
            target.HasThis && target.Arity == 1 && arguments[0].ObjectValue is VmRuntimeObject runtimeType) {
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = StackSlot.OfInt32(target.Name == "get_IsValueType"
                ? runtimeType.Target.IsValueType ? 1 : 0
                : runtimeType.Target.IsInterface ? 1 : 0);
            return true;
        }
        if (target.Name != "GetHashCode" || !target.HasThis || target.Arity != 1)
            return false;
        var value = arguments[0];
        if (value.ObjectValue is VmByRef byRef)
            value = byRef.Read();
        else if (value.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length != 0)
            value = boxed.Fields[0];
        var primitiveTarget = declaringType is ("System.Int32" or "System.UInt32" or "System.Int16" or "System.UInt16"
            or "System.Byte" or "System.SByte" or "System.Char" or "System.Boolean"
            or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double");
        var rawPrimitiveTarget = declaringType is "System.Object" or "System.ValueType" &&
            value.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt or StackKind.Float;
        if (!primitiveTarget && !rawPrimitiveTarget)
            return false;
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        result = declaringType is "System.Int64" or "System.UInt64" || rawPrimitiveTarget && value.Kind is StackKind.Int64 or StackKind.NativeInt
            ? StackSlot.OfInt32(unchecked((int)value.Int64Value))
            : declaringType is "System.Single" or "System.Double" || rawPrimitiveTarget && value.Kind == StackKind.Float
                ? StackSlot.OfInt32(value.DoubleValue.GetHashCode())
                : StackSlot.OfInt32(value.AsInt32);
        return true;
    }

    /// <summary>
    /// Fast path for a constrained callvirt whose resolved surface is a
    /// slot-level intrinsic.  The regular Call path must preserve all
    /// reflection/virtual/binding fallbacks; a cached constrained interface
    /// intrinsic has already crossed those resolution points and can invoke
    /// directly with the caller's exact argument buffer.
    /// </summary>
    internal bool TryInvokeConstrainedIntrinsicJit(int token, InterpreterFrame caller,
        int constrainedToken, out StackSlot? result) {
        return TryInvokeConstrainedIntrinsicJit(token, caller, constrainedToken,
            resolvedTarget: null, out result);
    }

    private bool TryInvokeConstrainedIntrinsicJit(int token, InterpreterFrame caller,
        int constrainedToken, CallTarget? resolvedTarget, out StackSlot? result) {
        result = null;
        if (constrainedToken == 0 ||
            caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;

        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (target.Intrinsic is not { } intrinsic || !target.HasThis ||
            !AcceptsRawConstrainedReceiver(target.DeclaringType))
            return false;

        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var arguments = argumentLease.Arguments;
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        _intrinsicContext.ParameterTypeNames = target.ParamTypeNames ?? [];
        _intrinsicContext.MethodTypeArgumentNames =
            target.MethodArgs?.Select(t => t.FullName).ToArray() ?? [];
        _intrinsicContext.ClassTypeArgumentNames =
            target.ClassArgs?.Select(t => t.FullName).ToArray() ?? [];
        _intrinsicContext.MethodTypeArguments = target.MethodArgs ?? [];
        _intrinsicContext.ClassTypeArguments = target.ClassArgs ?? [];
        var intrinsicResult = intrinsic(_intrinsicContext, arguments);
        if (target.ReturnsValue == true)
            result = intrinsicResult;
        return true;
    }

    /// <summary>
    /// Generated code uses this probe before entering the compatibility-heavy
    /// callvirt path.  It is deliberately expressed in terms of the resolved
    /// receiver type and method shape, so the same fast path applies to every
    /// concrete CoreLib or guest class rather than to a particular collection.
    /// The caller's argument slots are only inspected until applicability is
    /// known; they are borrowed (and consumed) only on the successful path.
    /// </summary>
    internal bool TryInvokeCachedVirtualJit(int token, InterpreterFrame caller,
        out StackSlot? result) {
        return TryInvokeCachedVirtualJit(token, caller, resolvedTarget: null,
            cachedVirtual: null, out result);
    }

    private bool TryInvokeCachedVirtualJit(int token, InterpreterFrame caller,
        CallTarget? resolvedTarget, VmMethod? cachedVirtual,
        out StackSlot? result) {
        result = null;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;

        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (target.Intrinsic is not null ||
            target.Method is not { Signature.HasThis: true } declaredVirtual ||
            HasRuntimeBinding(declaredVirtual.DeclaringType) ||
            DelegateContinuingSurfaces.Contains(declaredVirtual.DeclaringType.FullName))
            return false;

        var stackArguments = caller.Stack.ArgumentSlots(target.Arity);
        if (stackArguments.Length == 0 ||
            stackArguments[0].ObjectValue is not VmClassInstance receiver)
            return false;

        var receiverType = receiver.ClassType;
        VmMethod resolvedVirtual;
        if (cachedVirtual is not null)
            resolvedVirtual = cachedVirtual;
        else if (caller.TryGetCachedVirtualTarget(token, receiverType, out var frameVirtual))
            resolvedVirtual = frameVirtual;
        else {
            resolvedVirtual = DispatchVirtual(declaredVirtual, stackArguments[0]);
            caller.CacheVirtualTarget(token, receiverType, resolvedVirtual);
        }

        if (resolvedVirtual.Body is null ||
            HasRuntimeBinding(resolvedVirtual.DeclaringType) ||
            DelegateContinuingSurfaces.Contains(resolvedVirtual.DeclaringType.FullName))
            return false;

        var stackContext = BuildCallContext(caller, target, resolvedVirtual, stackArguments[0]);
        if (TryInvokeCompiledLeafFromStack(caller, resolvedVirtual, stackContext,
                target.Arity, out var leafResult)) {
            if (SlotOps.SignatureReturnsValue(resolvedVirtual.Signature))
                result = leafResult;
            return true;
        }

        if (TryInvokeCompiledMethodFromStack(caller, resolvedVirtual, stackContext,
                target.Arity, allowStatic: false, out var stackResult)) {
            if (SlotOps.SignatureReturnsValue(resolvedVirtual.Signature))
                result = stackResult;
            return true;
        }

        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var arguments = argumentLease.Arguments;
        var context = BuildCallContext(caller, target, resolvedVirtual, arguments[0]);
        var virtualResult = InvokeResolvedMethod(caller, resolvedVirtual, arguments, context);
        if (SlotOps.SignatureReturnsValue(resolvedVirtual.Signature))
            result = virtualResult;
        return true;
    }

    /// <summary>
    /// A generated call site has already validated the receiver type and
    /// selected the virtual target. Enter the compiled target directly instead
    /// of repeating the virtual-dispatch probes on every visit.
    /// </summary>
    internal bool TryInvokePublishedCompiledVirtualJit(InterpreterFrame caller,
        CallTarget target, VmMethod resolvedVirtual, out StackSlot? result) {
        result = null;
        if (resolvedVirtual.Body is null || caller.Stack.Count < target.Arity ||
            invoker is not Interpreter interpreter)
            return false;
        var receiver = caller.Stack.ArgumentSlots(target.Arity)[0];
        var context = BuildCallContext(caller, target, resolvedVirtual, receiver);
        if (TryInvokeCompiledLeafFromStack(caller, resolvedVirtual, context,
                target.Arity, out var leafResult)) {
            if (SlotOps.SignatureReturnsValue(resolvedVirtual.Signature))
                result = leafResult;
            return true;
        }
        if (TryInvokeCompiledMethodFromStack(caller, resolvedVirtual, context,
                target.Arity, allowStatic: false, out var stackResult)) {
            if (SlotOps.SignatureReturnsValue(resolvedVirtual.Signature))
                result = stackResult;
            return true;
        }
        _ = interpreter;
        return false;
    }

    private bool TryInvokeCachedVirtualJit(int token, InterpreterFrame caller,
        CallTarget target, out StackSlot? result) =>
        TryInvokeCachedVirtualJit(token, caller, target, cachedVirtual: null, out result);

    internal bool TryInvokeCachedVirtualResolvedJit(int token, InterpreterFrame caller,
        CallTarget target, VmMethod cachedVirtual, out StackSlot? result) =>
        TryInvokeCachedVirtualJit(token, caller, target, cachedVirtual, out result);

    /// <summary>
    /// Fast call-site dispatcher used by generated JIT frames. All compatible
    /// probes share one resolved CallTarget; this is important for managed
    /// CoreLib IL where one call site is visited millions of times and the
    /// individual probes otherwise repeat the same token/context lookup.
    /// </summary>
    internal bool TryInvokeJitCall(int token, InterpreterFrame caller, bool isCallvirt,
        int constrainedToken, out StackSlot? result) {
        return TryInvokeJitCall(token, caller, isCallvirt, constrainedToken,
            resolvedTarget: null, out result, out _);
    }

    internal bool TryInvokeCachedMetadataAttributes(int token, InterpreterFrame caller,
        out StackSlot? result) {
        result = null;
        if (!TryGetMetadataAttributeKey(token, caller, out var key) ||
            !_services.Shared.MetadataAttributeArrays.TryGetValue(key, out var array))
            return false;
        caller.Stack.DropArguments(3);
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        result = StackSlot.OfObject(array);
        return true;
    }

    internal bool TryGetMetadataAttributeKey(int token, InterpreterFrame caller,
        out MetadataAttributeArrayKey key) {
        key = default;
        var target = ResolveCallTargetForFrame(caller, token);
        if (target.Name != "GetCustomAttributes" || !target.HasThis || target.Arity != 3 ||
            caller.Stack.Count < 3)
            return false;
        var args = caller.Stack.ArgumentSlots(3);
        if (args[0].ObjectValue is not VmObject receiver ||
            args[1].ObjectValue is not VmObject attributeType)
            return false;
        key = new MetadataAttributeArrayKey(receiver, attributeType, args[2].AsInt32 != 0);
        return true;
    }

    internal void CacheMetadataAttributes(in MetadataAttributeArrayKey key, StackSlot? result) {
        if (result?.ObjectValue is VmArray array)
            _services.Shared.MetadataAttributeArrays.TryAdd(key, array);
    }

    internal bool TryInvokeJitCall(int token, InterpreterFrame caller, bool isCallvirt,
        int constrainedToken, CallTarget? resolvedTarget, out StackSlot? result,
        out JitCallRoute route) {
        result = null;
        route = JitCallRoute.None;
        if (caller.Method.DynamicTokens?.ContainsKey(unchecked((uint)token)) == true)
            return false;

        var target = resolvedTarget ?? ResolveCallTargetForFrame(caller, token);
        if (HasReferenceGetHashCodeReceiver(target, caller))
            return false;
        if (!isCallvirt && constrainedToken == 0 &&
            TryInvokeTypeFromHandleJit(token, caller, target, out result)) {
            route = JitCallRoute.TypeFromHandle;
            return true;
        }
        if (isCallvirt && constrainedToken != 0 &&
            TryInvokeConstrainedIntrinsicJit(token, caller, constrainedToken, target, out result)) {
            route = JitCallRoute.ConstrainedIntrinsic;
            return true;
        }
        if (!isCallvirt &&
            TryInvokeStaticIntrinsicJit(token, caller, isCallvirt, constrainedToken, target, out result)) {
            route = JitCallRoute.StaticIntrinsic;
            return true;
        }
        if (TryInvokeCommonRuntimeMetadataJit(token, caller, target, out result)) {
            route = JitCallRoute.RuntimeMetadata;
            return true;
        }
        if (TryInvokePrimitiveFastJit(caller, target, out result)) {
            route = JitCallRoute.PrimitiveFast;
            return true;
        }
        // A registered value-type intrinsic is already the VM's canonical
        // representation-level implementation. Prefer it before an audited
        // CoreLib IL substitution: entering the replacement IL would only
        // recreate a nested guest frame around the same primitive operation.
        // This remains a generic route for every sealed intrinsic surface;
        // non-intrinsic CoreLib methods continue through their IL substitute.
        if (TryInvokeInstanceIntrinsicJit(token, caller, constrainedToken, target, out result)) {
            route = JitCallRoute.InstanceIntrinsic;
            return true;
        }
        if (TryInvokeCoreLibSurfaceJit(caller, target, out result)) {
            route = JitCallRoute.CoreLibSurface;
            return true;
        }
        // A managed binding is the representation-level implementation of a
        // CoreLib method. Prefer it even after the guest method has reached
        // the promotion threshold; otherwise the first few calls use the
        // binding and later calls silently re-enter the much slower IL body.
        if (TryInvokeCachedBindingJit(token, caller, constrainedToken, target, out result)) {
            route = JitCallRoute.CachedBinding;
            return true;
        }
        // ECMA-335 callvirt performs a null check, but it does not perform
        // virtual dispatch when the referenced method itself is non-virtual.
        // CoreLib collection helpers contain many such callvirt sites.  Send
        // them straight to the compiled method path before the virtual cache
        // so they do not pay receiver-type lookup and DispatchVirtual on every
        // helper call.
        if (isCallvirt && constrainedToken == 0 &&
            target.Method is { IsVirtual: false } &&
            TryInvokeCompiledMethodJit(caller, target, isCallvirt, out result)) {
            route = JitCallRoute.CompiledMethod;
            return true;
        }
        if (isCallvirt && constrainedToken == 0 &&
            TryInvokeCachedVirtualJit(token, caller, target, out result)) {
            route = JitCallRoute.CachedVirtual;
            return true;
        }
        if (constrainedToken == 0 &&
            TryInvokeCompiledMethodJit(caller, target, isCallvirt, out result)) {
            route = JitCallRoute.CompiledMethod;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The default equality comparer for slot-representable values is still a
    /// normal guest object, but its generic implementation only forwards to
    /// the value's equality/hash operation.  Resolve that common semantic
    /// shape once at the call site instead of entering an abstract comparer
    /// method and redispatching through a short-lived frame on every lookup.
    /// Custom comparer types and non-slot values remain on the ordinary IL
    /// dispatch path.
    /// </summary>
    private bool TryInvokePrimitiveEqualityComparerJit(CallTarget target,
        InterpreterFrame caller, out StackSlot? result) {
        result = null;
        if (DeclaringTypeOf(target) is not { } declaringType ||
            !declaringType.StartsWith("System.Collections.Generic.EqualityComparer`1",
                StringComparison.Ordinal) || target.Name is not ("GetHashCode" or "Equals") ||
            !target.HasThis || caller.Stack.Count < target.Arity)
            return false;
        var arguments = caller.Stack.ArgumentSlots(target.Arity);
        if (arguments[0].ObjectValue is not VmClassInstance comparer ||
            !IsDefaultEqualityComparerType(comparer.ClassType.FullName))
            return false;
        if (target.Name == "GetHashCode" && target.Arity == 2 &&
            TryNormalizePrimitiveComparerValue(arguments[1], out var value)) {
            caller.Stack.DropArguments(target.Arity);
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = StackSlot.OfInt32(PrimitiveComparerHash(value));
            return true;
        }
        if (target.Name == "Equals" && target.Arity == 3 &&
            TryNormalizePrimitiveComparerValue(arguments[1], out var left) &&
            TryNormalizePrimitiveComparerValue(arguments[2], out var right)) {
            caller.Stack.DropArguments(target.Arity);
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            result = StackSlot.OfInt32(PrimitiveComparerEquals(left, right) ? 1 : 0);
            return true;
        }
        return false;
    }

    private static bool IsDefaultEqualityComparerType(string fullName) =>
        fullName.Contains("GenericEqualityComparer`1", StringComparison.Ordinal) ||
        fullName.Contains("ObjectEqualityComparer`1", StringComparison.Ordinal) ||
        fullName.Contains("NullableEqualityComparer`1", StringComparison.Ordinal) ||
        fullName.Contains("EnumEqualityComparer`1", StringComparison.Ordinal) ||
        fullName.Contains("StringEqualityComparer", StringComparison.Ordinal);

    private static string? DeclaringTypeOf(CallTarget target) =>
        string.IsNullOrEmpty(target.DeclaringType)
            ? target.Method?.DeclaringType.FullName
            : target.DeclaringType;

    private static bool TryNormalizePrimitiveComparerValue(StackSlot value, out StackSlot normalized) {
        if (value.ObjectValue is VmByRef byRef)
            value = byRef.Read();
        else if (value.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length != 0)
            value = boxed.Fields[0];
        normalized = value;
        return value.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt or StackKind.Float ||
            value.ObjectValue is VmString;
    }

    private static int PrimitiveComparerHash(in StackSlot value) => value.ObjectValue is VmString str
        ? str.Value.GetHashCode()
        : value.Kind == StackKind.Float
            ? value.DoubleValue.GetHashCode()
            : unchecked((int)value.Int64Value);

    private static bool PrimitiveComparerEquals(in StackSlot left, in StackSlot right) {
        if (left.ObjectValue is VmString leftString && right.ObjectValue is VmString rightString)
            return string.Equals(leftString.Value, rightString.Value, StringComparison.Ordinal);
        if (left.Kind == StackKind.Float || right.Kind == StackKind.Float)
            return left.Kind == right.Kind && left.DoubleValue.Equals(right.DoubleValue);
        return left.Kind == right.Kind && left.Int64Value == right.Int64Value;
    }

    internal bool TryInvokeCachedJitCall(JitCallRoute route, int token,
        InterpreterFrame caller, bool isCallvirt, int constrainedToken,
        CallTarget target, out StackSlot? result) {
        result = null;
        if (HasReferenceGetHashCodeReceiver(target, caller))
            return false;
        return route switch {
            JitCallRoute.TypeFromHandle => TryInvokeTypeFromHandleJit(token, caller, target, out result),
            JitCallRoute.ConstrainedIntrinsic => TryInvokeConstrainedIntrinsicJit(
                token, caller, constrainedToken, target, out result),
            JitCallRoute.StaticIntrinsic => TryInvokeStaticIntrinsicJit(
                token, caller, isCallvirt, constrainedToken, target, out result),
            JitCallRoute.RuntimeMetadata => TryInvokeCommonRuntimeMetadataJit(
                token, caller, target, out result),
            JitCallRoute.CachedVirtual => TryInvokeCachedVirtualJit(
                token, caller, target, out result),
            JitCallRoute.CompiledMethod => TryInvokeCompiledMethodJit(
                caller, target, isCallvirt, out result),
            JitCallRoute.CachedBinding => TryInvokeCachedBindingJit(
                token, caller, constrainedToken, target, out result),
            JitCallRoute.InstanceIntrinsic => TryInvokeInstanceIntrinsicJit(
                token, caller, constrainedToken, target, out result),
            JitCallRoute.CoreLibSurface => TryInvokeCoreLibSurfaceJit(caller, target, out result),
            JitCallRoute.PrimitiveFast => TryInvokePrimitiveFastJit(caller, target, out result),
            _ => false,
        };
    }

    internal bool TryInvokePrimitiveFastJit(InterpreterFrame caller, VmMethod method,
        out StackSlot? result) {
        return TryInvokePrimitiveFastCore(caller, method.DeclaringType.FullName, method.Name,
            method.Signature.HasThis,
            method.Signature.ParamTypes.Length + (method.Signature.HasThis ? 1 : 0), out result);
    }

    private bool TryInvokePrimitiveFastJit(InterpreterFrame caller, CallTarget target,
        out StackSlot? result) {
        return TryInvokePrimitiveFastCore(caller, DeclaringTypeOf(target), target.Name,
            target.HasThis, target.Arity, out result);
    }

    private bool TryInvokePrimitiveFastCore(InterpreterFrame caller, string? type, string? name,
        bool hasThis, int arity, out StackSlot? result) {
        result = null;
        if (type == "System.Int32" && name == "ToString" && hasThis && arity == 1) {
            using var lease = caller.BorrowCallArguments(1);
            var value = NormalizePrimitiveValue(lease.Arguments[0]);
            result = StackSlot.OfObject(_intrinsicContext.Strings.FormatInt32(value.AsInt32));
            return true;
        }
        if (type == "System.String" && name == "get_Length" && hasThis && arity == 1) {
            using var lease = caller.BorrowCallArguments(1);
            if (lease.Arguments[0].ObjectValue is not VmString text)
                throw new UnhandledGuestException("System.NullReferenceException", null);
            result = StackSlot.OfInt32(text.Value.Length);
            return true;
        }
        if (type == "System.Int32" && name == "Parse" && !hasThis &&
            arity == 1 && caller.Stack.Count >= arity) {
            using var lease = caller.BorrowCallArguments(arity);
            if (lease.Arguments[0].ObjectValue is not VmString text)
                throw new UnhandledGuestException("System.ArgumentNullException", null);
            if (text.TryParseInt32(out var parsed)) {
                result = StackSlot.OfInt32(parsed);
                return true;
            }
            try {
                result = StackSlot.OfInt32(int.Parse(text.Value, _intrinsicContext.Shared.CurrentCulture));
            } catch (FormatException) {
                throw new UnhandledGuestException("System.FormatException", null);
            } catch (OverflowException) {
                throw new UnhandledGuestException("System.OverflowException", null);
            }
            return true;
        }
        return false;
    }

    private static StackSlot NormalizePrimitiveValue(in StackSlot value) {
        if (value.ObjectValue is VmByRef byRef)
            return NormalizePrimitiveValue(byRef.Read());
        if (value.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length != 0)
            return boxed.Fields[0];
        if (value.ObjectValue is VmStructValue structure && structure.Fields.Length != 0)
            return structure.Fields[0];
        return value;
    }

    /// <summary>
    /// CoreLib surface substitutions are still ordinary managed IL. When a
    /// generated call site reaches one, enter the replacement method directly
    /// with the active stack instead of crossing the public invocation boundary
    /// only to normalize the receiver and apply the same substitution again.
    /// </summary>
    private bool TryInvokeCoreLibSurfaceJit(InterpreterFrame caller, CallTarget target,
        out StackSlot? result) {
        result = null;
        if (invoker is not Interpreter interpreter || target.Method is not { } source ||
            _services.CoreLibSurfaces?.Substitute(source) is not { } replacement ||
            !replacement.IsStatic || replacement.Body is null ||
            caller.Stack.Count < target.Arity)
            return false;

        var replacementArity = replacement.Signature.ParamTypes.Length +
            (replacement.Signature.HasThis ? 1 : 0);
        if (replacementArity != target.Arity)
            return false;

        var argumentOffset = caller.Stack.Count - target.Arity;
        var originalReceiver = target.Method.Signature.HasThis
            ? caller.Stack.RootSlots[argumentOffset]
            : default;
        if (target.Method.Signature.HasThis && !replacement.Signature.HasThis) {
            var receiver = originalReceiver;
            receiver = receiver.ObjectValue switch {
                VmByRef byRef => byRef.Slot,
                VmBoxedValue boxed when boxed.Fields.Length > 0 => boxed.Fields[0],
                _ => receiver,
            };
            caller.Stack.RootSlots[argumentOffset] = receiver;
        }

        var context = GenericContext.Of(target.ClassArgs, target.MethodArgs);
        if (TryInvokeCompiledMethodFromStack(caller, replacement, context, argumentOffset,
                replacementArity, target.Arity, allowStatic: true, out var invoked)) {
            if (SlotOps.SignatureReturnsValue(replacement.Signature))
                result = invoked;
            return true;
        }

        if (target.Method.Signature.HasThis && !replacement.Signature.HasThis)
            caller.Stack.RootSlots[argumentOffset] = originalReceiver;
        _ = interpreter;
        return false;
    }

    /// <summary>
    /// Enter a resolved guest IL method directly from a generated frame.  The
    /// normal interpreter call gate still performs the full binding and
    /// dispatch ladder; this path is only for a non-callvirt target that has
    /// already crossed resolution and provenance checks.  MemberRef and
    /// MethodSpec call sites use this just as MethodDef sites use CallDirect,
    /// so generic CoreLib helpers do not pay the compatibility ladder on every
    /// collection operation.
    /// </summary>
    private bool TryInvokeCompiledMethodJit(InterpreterFrame caller, CallTarget target,
        bool isCallvirt, out StackSlot? result) {
        result = null;
        if (invoker is not Interpreter interpreter || target.Intrinsic is not null ||
            target.Method is not { Body: not null } method ||
            isCallvirt && method.IsVirtual ||
            RequiresHostBridge(method) ||
            HasRuntimeBinding(method.DeclaringType) ||
            _services.CoreLibSurfaces?.Substitute(method) is not null ||
            DelegateContinuingSurfaces.Contains(method.DeclaringType.FullName))
            return false;

        if (caller.Stack.Count < target.Arity)
            return false;
        var stackArguments = caller.Stack.ArgumentSlots(target.Arity);
        var receiver = method.Signature.HasThis ? stackArguments[0] : default;
        // callvirt performs the null check even for a non-virtual instance
        // method.  Leave null and unusual receiver representations to the
        // normal gate so the exact guest exception path is preserved.
        if (isCallvirt && method.Signature.HasThis &&
            (receiver.Kind != StackKind.Object || receiver.ObjectValue is null))
            return false;
        var context = BuildCallContext(caller, target, method, receiver);
        if (caller.TryGetCachedCompiled(method, out var compiled)) {
            if (compiled is null)
                return false;
        } else {
            // The call engine is loader-local, so a published method delegate
            // can be shared across every short-lived nested frame. Reusing
            // this cache avoids a ConcurrentDictionary/JIT-entry probe for
            // every BCL helper frame while the caller frame cache still
            // handles the first visit at each call site.
            if (!_nestedCompiledMethods.TryGetValue(method, out compiled)) {
                compiled = interpreter.GetOrPromoteNestedCompiled(method);
                if (compiled is not null)
                    _nestedCompiledMethods.TryAdd(method, compiled);
            }
            caller.CacheCompiled(method, compiled);
            if (compiled is null)
                return false;
        }
        if (compiled.HasLeaf) {
            var leafArguments = caller.Stack.ArgumentSlots(target.Arity);
            if (!interpreter.TryInvokeCompiledLeafNested(method, compiled, context,
                    leafArguments, out var leafResult))
                return false;
            caller.Stack.DropArguments(target.Arity);
            if (SlotOps.SignatureReturnsValue(method.Signature))
                result = leafResult;
            return true;
        }

        StackSlot invokedResult;
        if (compiled.Prepared.LocalTypes.All(static local => local.Kind != SigKind.ByRef)) {
            if (!interpreter.TryInvokeCompiledNestedResolvedFromStack(method, compiled, caller,
                    target.Arity, context, allowStatic: method.IsStatic, out invokedResult))
                return false;
        } else {
            using var argumentLease = caller.BorrowCallArguments(target.Arity);
            var arguments = argumentLease.Arguments;
            if (!interpreter.TryInvokeCompiledNestedResolved(method, compiled, arguments, context,
                    allowStatic: method.IsStatic, out invokedResult)) {
                // The lease has already removed the operands. Restore the exact
                // stack shape before the general JIT call path retries it.
                for (var i = 0; i < arguments.Length; i++)
                    caller.Stack.Push(arguments[i]);
                return false;
            }
        }
        if (SlotOps.SignatureReturnsValue(method.Signature))
            result = invokedResult;
        return true;
    }

    private bool TryInvokeCompiledMethodFromStack(InterpreterFrame caller, VmMethod method,
        GenericContext? context, int arity, bool allowStatic, out StackSlot result) {
        return TryInvokeCompiledMethodFromStack(caller, method, context,
            caller.Stack.Count - arity, arity, arity, allowStatic, out result);
    }

    private bool TryInvokeCompiledMethodFromStack(InterpreterFrame caller, VmMethod method,
        GenericContext? context, int argumentOffset, int arity, int dropCount,
        bool allowStatic, out StackSlot result) {
        result = default;
        if (invoker is not Interpreter interpreter)
            return false;
        if (!caller.TryGetCachedCompiled(method, out var compiled)) {
            if (!_nestedCompiledMethods.TryGetValue(method, out compiled)) {
                compiled = interpreter.GetOrPromoteNestedCompiled(method);
                if (compiled is not null)
                    _nestedCompiledMethods.TryAdd(method, compiled);
            }
            caller.CacheCompiled(method, compiled);
        }
        if (compiled is null)
            return false;

        if (compiled.Prepared.LocalTypes.All(static local => local.Kind != SigKind.ByRef))
            return interpreter.TryInvokeCompiledNestedResolvedFromStack(method, compiled, caller,
                argumentOffset, arity, dropCount, context, allowStatic, out result);

        using var argumentLease = caller.BorrowCallArguments(arity);
        var arguments = argumentLease.Arguments;
        if (!interpreter.TryInvokeCompiledNestedResolved(method, compiled, arguments,
                context, allowStatic, out result)) {
            for (var i = 0; i < arguments.Length; i++)
                caller.Stack.Push(arguments[i]);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A normal predicate/selector is a single guest method. Its callvirt
    /// Invoke stack is already laid out as [delegate, arguments]. For static
    /// methods the argument suffix is directly aliased; for instance methods
    /// the dispatcher slot is replaced with the bound receiver before the
    /// same compiled nested-call path is entered. Other delegate shapes keep
    /// the general path, preserving multicast, closures and host callbacks.
    /// </summary>
    private bool TryInvokeDelegateFromStack(InterpreterFrame caller,
        VmDelegate @delegate, int invokeArity, out StackSlot? result) {
        result = null;
        if (invoker is not Interpreter interpreter ||
            @delegate.ExpressionLambda is not null ||
            @delegate.HostCallback is not null ||
            @delegate.Invocations.Count != 1 || invokeArity < 1 ||
            caller.Stack.Count < invokeArity)
            return false;

        var invocation = @delegate.Invocations[0];
        var method = invocation.Method;
        var argumentCount = invokeArity - 1;
        if (method.Signature.HasThis != !method.IsStatic ||
            method.Signature.ParamTypes.Length != argumentCount || method.Body is null)
            return false;

        var arguments = caller.Stack.ArgumentSlots(invokeArity);
        VmLifetime.EnsureLiveForGuest(method);
        if (method.DeclaringType is { } declaringType)
            VmLifetime.EnsureLiveForGuest(declaringType);
        VmLifetime.EnsureLiveForGuest(invocation.Target);
        for (var i = 1; i < arguments.Length; i++) {
            if (arguments[i].Kind is StackKind.Object or StackKind.ByRef)
                VmLifetime.EnsureLiveForGuest(arguments[i]);
        }

        var isStatic = method.IsStatic;
        var argumentOffset = caller.Stack.Count - invokeArity + (isStatic ? 1 : 0);
        var aliasedArity = isStatic ? argumentCount : invokeArity;
        var receiverSlot = caller.Stack.Count - invokeArity;
        if (!isStatic)
            caller.Stack.RootSlots[receiverSlot] = invocation.Target;
        var context = isStatic
            ? invocation.Context
            : BuildCallContext(new CallTarget {
                ClassArgs = invocation.Context?.ClassArgs,
                MethodArgs = invocation.Context?.MethodArgs,
            }, method, invocation.Target);
        if (!TryInvokeCompiledMethodFromStack(caller, method, context,
                argumentOffset, aliasedArity, invokeArity,
                allowStatic: isStatic, out var invoked)) {
            if (!isStatic)
                caller.Stack.RootSlots[receiverSlot] = StackSlot.OfObject(@delegate);
            return false;
        }
        if (SlotOps.SignatureReturnsValue(method.Signature))
            result = invoked;
        return true;
    }

    private static bool HasReferenceGetHashCodeReceiver(CallTarget target, InterpreterFrame caller) {
        // Object/ValueType.GetHashCode is virtual for reference receivers.
        // A guest class may override it, so do not consume a reference
        // receiver before DispatchVirtual selects the override. Primitive
        // receivers remain eligible for the common value path below.
        if (target.Name == "GetHashCode" && target.HasThis && caller.Stack.Count >= target.Arity) {
            var hashReceiver = caller.Stack.ArgumentSlots(target.Arity)[0];
            if (hashReceiver.ObjectValue is VmByRef hashByRef)
                hashReceiver = hashByRef.Read();
            if (hashReceiver.ObjectValue is VmClassInstance)
                return true;
        }
        return false;
    }

    public StackSlot? Call(int token, InterpreterFrame caller, bool isCallvirt, int constrainedToken,
        bool tailCallAllowed, out TailCallRequest? tailCallRequest, bool validateArguments = true,
        CallTarget? resolvedTarget = null) {
        tailCallRequest = null;
        // 未登録 intrinsic はこの時点では例外にしない (callvirt ならレシーバのゲスト実装を
        // 引数ポップ後に試すため。旧来の即時例外は最後のフォールバックで再現する)
        object? dynamicReference = null;
        var hasDynamic = caller.Method.DynamicTokens?.TryGetValue(unchecked((uint)token), out dynamicReference) == true;
        var reflected = hasDynamic ? dynamicReference as VmRuntimeMethod : null;
        var dynamicMethod = hasDynamic ? reflected?.Target ?? dynamicReference as VmMethod : null;
        var target = dynamicMethod is not null
            ? new CallTarget {
                Arity = dynamicMethod.Signature.ParamTypes.Length + (dynamicMethod.Signature.HasThis ? 1 : 0),
                Method = dynamicMethod,
                Name = dynamicMethod.Name,
                ParamCount = dynamicMethod.Signature.ParamTypes.Length,
                HasThis = dynamicMethod.Signature.HasThis,
                ClassArgs = (reflected?.ReflectedType as VmConstructedType)?.TypeArguments,
                MethodArgs = reflected?.MethodArguments,
                DeclaringType = dynamicMethod.DeclaringType.FullName,
                ReturnsValue = SlotOps.SignatureReturnsValue(dynamicMethod.Signature),
            }
            : resolvedTarget ?? ResolveCallTargetForFrame(caller, token);

        // Keep the common static delegate shape on the caller's active stack;
        // this avoids both the outer argument copy and a second argument array
        // for each predicate/selector invocation.
        if (isCallvirt && target.HasThis && target.Name is "Invoke" or "BeginInvoke" &&
            caller.Stack.Count >= target.Arity &&
            caller.Stack.ArgumentSlots(target.Arity)[0].ObjectValue is VmDelegate delegateValue &&
            TryInvokeDelegateFromStack(caller, delegateValue, target.Arity, out var delegateResult)) {
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            return delegateResult;
        }
        using var argumentLease = caller.BorrowCallArguments(target.Arity);
        var args = argumentLease.Arguments;
        // Only values crossing the call boundary need a lifetime check. The
        // caller's remaining evaluation-stack values stay in the same frame
        // and will be checked when they cross a later boundary. Scanning the
        // entire active stack here made every call pay for unrelated locals
        // and temporaries, which was especially costly in managed collection
        // IL with many small helper calls.
        if (validateArguments)
            foreach (ref readonly var argument in args.AsSpan())
                VmLifetime.EnsureLiveForGuest(argument);

        // MethodBase.Invoke is a general reflection boundary. Once its
        // receiver is already the VM's reflected-method object, replaying the
        // CoreLib pointer/handle plumbing for every call only adds frames and
        // allocations; the target method still uses the ordinary guest call
        // hook below. Keep tracing on the IL path so diagnostics preserve the
        // observable CoreLib surface.
        if (_services.Tracer?.CapturesInstructions != true &&
            isCallvirt && target.HasThis && target.Name == "Invoke" && args.Length == 3 &&
            args[0].ObjectValue is VmRuntimeMethod reflectedMethod &&
            args[2].ObjectValue is VmArray or null &&
            CoreLibBindings.TryInvokeReflectedMethod(_intrinsicContext, reflectedMethod,
                args[1], args[2].ObjectValue as VmArray, out var reflectedResult)) {
            gate.ConsumeInstruction();
            gate.CheckSafepoint();
            return reflectedResult;
        }

        // Value-type CoreLib facades (including ValueTask and its awaiters)
        // cannot have a guest reference-type override.  Their managed IL
        // surface is nevertheless represented by a runtime binding, so enter
        // the already cached binding before the general dispatch ladder.
        // This is also used by interpreter frames; the JIT probe above is only
        // available once the containing method has been promoted.
        // Most CoreLib collection calls are emitted as callvirt even though
        // the receiver is a concrete VM class. Once the receiver type has
        // been resolved at this call site, cache that vtable result in the
        // caller frame and skip the binding/interface compatibility ladder.
        // This is a receiver/type cache shared by every guest class, not a
        // collection-specific implementation.
        if (isCallvirt && constrainedToken == 0 && target.Intrinsic is null &&
            target.Method is { Body: not null, Signature.HasThis: true } declaredVirtual &&
            args[0].ObjectValue is VmClassInstance receiver &&
            !HasRuntimeBinding(declaredVirtual.DeclaringType) &&
            !DelegateContinuingSurfaces.Contains(declaredVirtual.DeclaringType.FullName)) {
            var receiverType = receiver.ClassType;
            VmMethod resolvedVirtual;
            if (caller.TryGetCachedVirtualTarget(token, receiverType, out var cachedVirtual))
                resolvedVirtual = cachedVirtual;
            else {
                resolvedVirtual = DispatchVirtual(declaredVirtual, args[0]);
                caller.CacheVirtualTarget(token, receiverType, resolvedVirtual);
            }
            if (resolvedVirtual.Body is not null &&
                !HasRuntimeBinding(resolvedVirtual.DeclaringType) &&
                !RequiresHostBridge(resolvedVirtual) &&
                !DelegateContinuingSurfaces.Contains(resolvedVirtual.DeclaringType.FullName)) {
                var virtualContext = BuildCallContext(caller, target, resolvedVirtual, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, resolvedVirtual, args,
                        virtualContext, out tailCallRequest))
                    return null;
                var virtualResult = InvokeResolvedMethod(caller, resolvedVirtual, args, virtualContext);
                return SlotOps.SignatureReturnsValue(resolvedVirtual.Signature) ? virtualResult : null;
            }
        }

        // A non-virtual call to a concrete IL method with no runtime binding
        // cannot change dispatch after token resolution. Keep the common
        // guest/BCL IL path out of the virtual/binding compatibility ladder;
        // InvokeResolvedMethod still performs preparation, static
        // initialization, lifetime checks, JIT promotion and frame creation.
        // This is a generic call-site fast path, not a type-specific surface.
        if (!isCallvirt && constrainedToken == 0 && target.Intrinsic is null &&
            target.Method is { Body: not null } directMethod &&
            !HasRuntimeBinding(directMethod.DeclaringType) &&
            !RequiresHostBridge(directMethod) &&
            !DelegateContinuingSurfaces.Contains(directMethod.DeclaringType.FullName)) {
            var directContext = BuildCallContext(caller, target, directMethod,
                directMethod.Signature.HasThis ? args[0] : default);
            if (tailCallAllowed && invoker.TryCreateTailCall(caller, directMethod, args,
                    directContext, out tailCallRequest))
                return null;
            var directResult = InvokeResolvedMethod(caller, directMethod, args, directContext);
            return SlotOps.SignatureReturnsValue(directMethod.Signature) ? directResult : null;
        }

        // 特権判定は callee ではなく実際の呼出元 loader 基準 (caller.Method.Loader)。
        var callerDomain = CallerDomainOf(caller);

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
        VmType? constrainedReceiverType = null;
        if (constrainedToken != 0 && isCallvirt && target.HasThis &&
            args[0].Kind is not StackKind.Object) {
            var constrainedType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context,
                caller.Method.DynamicTokens);
            if (constrainedType.IsValueType) {
                constrainedReceiverType = constrainedType;
                var valueSlot = args[0].Kind == StackKind.ByRef && args[0].ObjectValue is VmByRef receiverByRef
                    ? receiverByRef.Slot
                    : args[0];
                if (args[0].Kind != StackKind.ByRef || constrainedType.IsEnum) {
                    var fields = valueSlot.Kind == StackKind.ValueType
                        ? ((VmStructValue)valueSlot.ObjectValue!).Clone().Fields
                        : [valueSlot];
                    args[0] = StackSlot.OfObject(_heap.Allocate(new VmBoxedValue(constrainedType, fields)));
                }
            } else if (args[0].ObjectValue is VmByRef referenceReceiver) {
                // A reference type's constrained receiver is an address of the
                // object reference; dispatch on its actual (possibly derived) type.
                args[0] = referenceReceiver.Read();
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
                return InvokeDelegate(caller, @delegate, args);
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
                    if (TryInvokeBinding(guestOverride, target.MethodArgs, args, out var overrideBound, callerDomain,
                            callTarget: target))
                        return overrideBound;
                    var context = BuildCallContext(caller, target, guestOverride, args[0]);
                    if (tailCallAllowed && invoker.TryCreateTailCall(caller, guestOverride, args,
                            context, out tailCallRequest))
                        return null;
                    var guestRet = InvokeResolvedMethod(caller, guestOverride, args, context);
                    return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? guestRet : null;
                }
                // ファサード インターフェースの明示的実装 (EII) を実行時型の InterfaceMap で解決する
                // (明示的実装はメソッド名が規定名と異なるため名前照合では見つからない)。
                // EII 本体にも優先順位 ① (ランタイムバインド) を照合する (Enum の
                // IFormattable EII 等の culture/表現境界面の委譲のため)。EII 本体名は
                // ドット付きのため短名バインドへの誤ヒットは無い
                if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
                    if (TryInvokeBinding(explicitImpl, target.MethodArgs, args, out var explicitBound,
                            callerDomain, target.ClassArgs, target))
                        return explicitBound;
                    var context = BuildCallContext(caller, target, explicitImpl, args[0]);
                    if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args,
                            context, out tailCallRequest))
                        return null;
                    var guestRet = InvokeResolvedMethod(caller, explicitImpl, args, context);
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
                    var surfaceResult = surfaceImpl(_intrinsicContext, args);
                    return target.ReturnsValue == false ? null : surfaceResult;
                }
            }
            // constrained. 値型レシーバが intrinsic 宣言型 (System.Object 等) に着地した場合、
            // ECMA-335 規約に従い値をボックス化してから渡す (ゲスト実装は上の仮想ディスパッチで優先済み)
            if (constrainedToken != 0 && target.HasThis &&
                args[0].Kind is not (StackKind.Object or StackKind.ByRef) &&
                !AcceptsRawConstrainedReceiver(target.DeclaringType)) {
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
            using var roots = RegisterTransientRootsIfNeeded(args);
            var intrinsicResult = target.DeclaringType == "System.Threading.Interlocked"
                ? InvokeInterlocked(intrinsic, target.ParamTypeNames ?? [], args, alreadyGated: true)
                : intrinsic(_intrinsicContext, args);
            return target.ReturnsValue == false ? null : intrinsicResult;
        }

        // 解決未了 (未登録 intrinsic): レシーバへの仮想ディスパッチを最終試行してから拒否
        if (target.Method is null)
            return FailOrDispatchLate(target, caller, isCallvirt, args, callerDomain,
                tailCallAllowed, out tailCallRequest);

        // ゲスト呼出。callvirt はレシーバの実行時型で仮想解決 (VTable 相当)。
        // constrained. 値型レシーバは ByRef/ValueType スロットで来るためディスパッチがそのまま適用される
        var method = target.Method!;
        if (isCallvirt && method.Name == "ToString" && method.Signature.ParamTypes.Length == 0 &&
            args[0].ObjectValue is VmBclObject or VmIntrinsicInstance &&
            DotnetVM.Runtime.Intrinsics.Builtins.CoreLibBindings.BclToString(_intrinsicContext, args[0]) is { } bclText) {
            gate.ConsumeInstruction(); gate.CheckSafepoint(); return StackSlot.OfObject(bclText);
        }
        VmType[]? staticImplementationArgs = null;
        if (constrainedToken != 0 && method.IsStatic && method.DeclaringType is VmClassType { IsInterface: true }) {
            var implementingType = _objectEngine.ResolveTypeToken(constrainedToken, caller.Context,
                caller.Method.DynamicTokens);
            if (TryDispatchStaticInterface(method, implementingType, target.ClassArgs) is { } implementation) {
                method = implementation;
                staticImplementationArgs = implementingType is VmConstructedType constructed
                    ? constructed.TypeArguments : [];
            }
        }
        if (isCallvirt && method.Signature.HasThis) {
            if (SlotOps.IsNullReference(args[0]))
                throw new UnhandledGuestException("System.NullReferenceException",
                    $"null レシーバで {method.DeclaringType.FullName}::{method.Name} を呼び出しました。");
            method = DispatchVirtual(method, args[0], constrainedReceiverType);
        }

        // 優先順位 ①: ランタイムバインド (署名照合) を最優先で解決する。
        // 構築型の実引数 (ClassArgs) もキー化に使う (IComparable`1<uint> の !0 等)
        // callerDomain は呼出元フレーム基準 (特権面の callee 基準判定はしない)。
        if (TryInvokeBinding(method, target.MethodArgs, args, out var bound, callerDomain,
                staticImplementationArgs ?? target.ClassArgs, target))
            return bound;

        // HostBridge is an explicit ABI policy. A managed method body is not
        // an implicit fallback in this mode; the host must register a bridge
        // for the imported ABI surface.
        if (RequiresHostBridge(method) && method.Body is not null)
            throw new OperationNotAllowedException(
                $"ABI 面 {method.DeclaringType.FullName}::{method.Name} は HostBridge に指定されていますが、対応するブリッジが登録されていません。");

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
            var surfaceResult = runtimeSurfaceImpl(_intrinsicContext, args);
            return SlotOps.SignatureReturnsValue(method.Signature) ? surfaceResult : null;
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
                        callerDomain, target.ClassArgs, target))
                    return explicitBound;
                var implContext = BuildCallContext(caller, target, explicitImpl, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args,
                        implContext, out tailCallRequest))
                    return null;
                var implRet = InvokeResolvedMethod(caller, explicitImpl, args, implContext);
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

        var context2 = staticImplementationArgs is not null
            ? GenericContext.Of(staticImplementationArgs, target.MethodArgs)
            : BuildCallContext(caller, target, method, method.Signature.HasThis ? args[0] : default);
        if (tailCallAllowed && invoker.TryCreateTailCall(caller, method, args, context2, out tailCallRequest))
            return null;
        var ret = InvokeResolvedMethod(caller, method, args, context2);
        return SlotOps.SignatureReturnsValue(method.Signature) ? ret : null;
    }

    // Binding, virtual dispatch and tail-call checks precede this entry. Reuse
    // the promoted method on every route, including interface/delegate calls.
    private bool TryInvokeCompiledLeaf(InterpreterFrame caller, VmMethod method, GenericContext? context,
        StackSlot[] arguments, out StackSlot result) {
        result = default;
        if (invoker is not Interpreter interpreter)
            return false;

        if (!caller.TryGetCachedLeaf(method, out var cached)) {
            var compiled = interpreter.GetOrPromoteNestedCompiled(method);
            if (compiled is null)
                return false;
            cached = compiled.HasLeaf ? compiled : null;
            caller.CacheLeaf(method, cached);
        }
        if (cached is not { } leaf)
            return false;
        return interpreter.TryInvokeCompiledLeafNested(method, leaf, context,
            arguments.AsSpan(), out result);
    }

    /// <summary>
    /// Leaf calls can consume the caller's active stack span directly. Keeping
    /// that span live avoids copying every primitive argument into a temporary
    /// call array before the allocation-free compiled body is entered.
    /// </summary>
    private bool TryInvokeCompiledLeafFromStack(InterpreterFrame caller, VmMethod method,
        GenericContext? context, int arity, out StackSlot result) {
        result = default;
        if (invoker is not Interpreter interpreter)
            return false;

        if (!caller.TryGetCachedLeaf(method, out var cached)) {
            var compiled = interpreter.GetOrPromoteNestedCompiled(method);
            if (compiled is null)
                return false;
            cached = compiled.HasLeaf ? compiled : null;
            caller.CacheLeaf(method, cached);
        }
        if (cached is not { } leaf)
            return false;

        var arguments = caller.Stack.ArgumentSlots(arity);
        if (!interpreter.TryInvokeCompiledLeafNested(method, leaf, context,
                arguments, out result))
            return false;
        caller.Stack.DropArguments(arity);
        return true;
    }

    /// <summary>Intrinsic からの guest 呼出しも、通常の call-site と同じ loader-local JIT キャッシュを使う。</summary>
    internal StackSlot InvokeGuestMethod(VmMethod method, StackSlot[] arguments,
        GenericContext? context) {
        if (invoker is Interpreter interpreter && interpreter.IsInsideGuestInstruction) {
            if (_nestedCompiledMethods.TryGetValue(method, out var cachedCompiled) &&
                interpreter.TryInvokeCompiledNestedResolved(method, cachedCompiled, arguments, context,
                    allowStatic: true, out var cachedResult))
                return cachedResult;
            if (interpreter.TryInvokeCompiledNestedResolved(method, arguments, context,
                    allowStatic: true, out var nestedResult)) {
                if (interpreter.GetNestedCompiled(method) is { } compiled)
                    _nestedCompiledMethods.TryAdd(method, compiled);
                return nestedResult;
            }
            if (interpreter.TryInvokeCompiled(method, arguments, context,
                    argumentsAlreadyValidated: true, out var compiledResult))
                return compiledResult;
        }
        return invoker.Invoke(method, arguments, context);
    }

    private StackSlot InvokeResolvedMethod(InterpreterFrame caller, VmMethod method,
        StackSlot[] arguments, GenericContext? context) {
        if (TryInvokeCompiledLeaf(caller, method, context, arguments, out var leafResult))
            return leafResult;
        if (invoker is Interpreter interpreter) {
            if (_nestedCompiledMethods.TryGetValue(method, out var cachedCompiled) &&
                interpreter.TryInvokeCompiledNestedResolved(method, cachedCompiled, arguments, context,
                    allowStatic: true, out var cachedResult))
                return cachedResult;
            if (interpreter.TryInvokeCompiledNestedResolved(method, arguments, context,
                    allowStatic: true, out var nestedResult))
            {
                if (interpreter.GetNestedCompiled(method) is { } compiled)
                    _nestedCompiledMethods.TryAdd(method, compiled);
                return nestedResult;
            }
            if (interpreter.TryInvokeCompiled(method, arguments, context,
                    argumentsAlreadyValidated: true, out var compiledResult))
                return compiledResult;
        }
        return invoker.Invoke(method, arguments, context);
    }

    // Delegate invocation has no owning interpreter frame. Keep that boundary
    // on the same compiled path, while the normal IL call path can use the
    // caller-local leaf cache above.
    private StackSlot InvokeResolvedMethod(VmMethod method, StackSlot[] arguments,
        GenericContext? context) =>
        invoker is Interpreter interpreter &&
            interpreter.TryInvokeCompiled(method, arguments, context,
                argumentsAlreadyValidated: true, out var result)
            ? result : invoker.Invoke(method, arguments, context);

    private bool HasRuntimeBinding(VmType type) => type switch {
        VmConstructedType constructed => HasRuntimeBinding(constructed.Definition),
        _ => CanAttemptRuntimeBinding(type) && _intrinsics.HasBindingForType(type.FullName),
    };

    // These generic value-type interface surfaces are deliberately registered
    // as slot-level intrinsics. They already normalize ByRef/boxed/raw values,
    // so materializing a VmBoxedValue here would only serve the host call
    // boundary and would be immediately discarded.
    private static bool AcceptsRawConstrainedReceiver(string? declaringType) =>
        declaringType is "System.IEquatable`1" or "System.IComparable`1";

    internal CallTarget ResolveCallTargetForFrame(InterpreterFrame caller, int token) {
        if (caller.TryGetCachedCallTarget(token, out var cached))
            return cached;
        var target = ResolveCallTarget(token, caller.Context, throwOnMissingIntrinsic: false);
        caller.CacheCallTarget(token, target);
        return target;
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
                if (TryInvokeBinding(guestOverride, target.MethodArgs, args, out var lateBound, callerDomain,
                        callTarget: target))
                    return lateBound;
                var context = BuildCallContext(caller, target, guestOverride, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, guestOverride, args, context, out tailCallRequest))
                    return null;
                var ret = InvokeResolvedMethod(caller, guestOverride, args, context);
                return SlotOps.SignatureReturnsValue(guestOverride.Signature) ? ret : null;
            }
            // ファサード インターフェースの明示的実装 (EII) もここで救済する。
            // EII 本体にも ① を照合する (intrinsic 分岐と同一)
            if (TryDispatchInterfaceKey(target.DeclaringType, target.Name!, target.ParamTypeNames, args[0]) is { } explicitImpl) {
                if (TryInvokeBinding(explicitImpl, target.MethodArgs, args, out var explicitBound,
                        callerDomain, target.ClassArgs, target))
                    return explicitBound;
                var context = BuildCallContext(caller, target, explicitImpl, args[0]);
                if (tailCallAllowed && invoker.TryCreateTailCall(caller, explicitImpl, args, context, out tailCallRequest))
                    return null;
                var ret = InvokeResolvedMethod(caller, explicitImpl, args, context);
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
        if (declaringParamCount > 0) {
            var receiverType = receiver.ObjectValue is VmClassInstance instance ? instance.RuntimeType : ReceiverRuntimeType(receiver);
            var inherited = receiverType is null ? null : InheritanceTypeArguments(receiverType, method.DeclaringType);
            if (inherited is { Length: > 0 }) classArgs = inherited;
            else if (SlotOps.TryGetReceiverTypeArguments(receiver, declaringParamCount, out var receiverArgs)) classArgs = receiverArgs;
        }
        return GenericContext.Of(classArgs, target.MethodArgs);
    }

    private GenericContext? BuildCallContext(InterpreterFrame caller, CallTarget target,
        VmMethod method, in StackSlot receiver) {
        VmType? receiverType = null;
        if (method.DeclaringType.GenericParamCount > 0)
            receiverType = receiver.ObjectValue is VmClassInstance instance
                ? instance.RuntimeType : ReceiverRuntimeType(receiver);
        if (caller.TryGetCachedCallContext(target, method, receiverType, out var cached))
            return cached;
        if (receiverType is not null && _callContexts.TryGetValue(
                new(target, method, receiverType), out var sharedContext)) {
            caller.CacheCallContext(target, method, receiverType, sharedContext);
            return sharedContext;
        }
        var context = BuildCallContext(target, method, receiver);
        if (receiverType is not null && context is not null)
            _callContexts.TryAdd(new(target, method, receiverType), context);
        caller.CacheCallContext(target, method, receiverType, context);
        return context;
    }
}
