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
    // ---- デリゲート呼出 ----

    /// <summary>デリゲート呼出 (callvirt Invoke/BeginInvoke のデリゲート実体ディスパッチ)。
    /// マルチキャストは全エントリを順に実行し、最後の戻り値を返す (CLR 規約)。
    /// 各呼出は通常の Invoke ゲート経由 (クォータ/セーフポイント/EH 機構を共有)。</summary>
    public StackSlot? InvokeDelegate(VmDelegate @delegate, StackSlot[] args) {
        VmLifetime.EnsureLiveForGuest(@delegate);
        foreach (var argument in args)
            VmLifetime.EnsureLiveForGuest(argument);
        if (@delegate.HostCallback is { } hostCallback)
            return hostCallback(args);

        if (@delegate.ExpressionLambda is { } expressionLambda) {
            if (args.Length != expressionLambda.Parameters.Length + 1)
                throw new UnhandledGuestException("System.ArgumentException", "式木 delegate の引数個数が一致しません。");
            var previousScope = _expressionScope.Value;
            _expressionScope.Value = new Dictionary<VmExpressionObject, StackSlot>();
            try {
                var value = EvaluateExpression(expressionLambda.Body
                    ?? throw new InvalidOperationException("式木 Lambda に本体がありません。"), expressionLambda, args);
                var definition = expressionLambda.DelegateType is VmConstructedType constructed
                    ? constructed.Definition : expressionLambda.DelegateType;
                return definition?.Name.StartsWith("Action", StringComparison.Ordinal) == true ? null : value;
            } finally {
                _expressionScope.Value = previousScope;
            }
        }
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

    private StackSlot EvaluateExpression(VmExpressionObject expression, VmExpressionObject lambda, StackSlot[] args) {
        gate.ConsumeInstruction();
        gate.CheckSafepoint();
        var scope = _expressionScope.Value ??= new Dictionary<VmExpressionObject, StackSlot>();
        switch (expression.Kind) {
            case VmExpressionKind.Constant:
                return expression.Constant;
            case VmExpressionKind.Parameter: {
                if (scope.TryGetValue(expression, out var assigned))
                    return assigned;
                var index = Array.FindIndex(lambda.Parameters, p => ReferenceEquals(p, expression));
                if (index < 0)
                    if (expression.IsVariable)
                        return _services.Objects.DefaultForType(expression.ResultType, _loader);
                if (index < 0)
                    throw new UnhandledGuestException("System.InvalidOperationException", "式木 parameter が lambda に束縛されていません。");
                return args[index + 1];
            }
            case VmExpressionKind.Assign: {
                var target = expression.Left
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "式木 Assign の左辺がありません。");
                var value = EvaluateExpression(expression.Right!, lambda, args);
                scope[target] = value;
                return value;
            }
            case VmExpressionKind.Block: {
                StackSlot value = default;
                foreach (var child in expression.Expressions)
                    value = EvaluateExpression(child, lambda, args);
                return value;
            }
            case VmExpressionKind.Default:
                return expression.NewType is { } defaultType
                    ? _services.Objects.DefaultForType(defaultType, _loader)
                    : default;
            case VmExpressionKind.Quote:
                return expression.Object is { } quoted
                    ? StackSlot.OfObject(quoted)
                    : StackSlot.Null;
            case VmExpressionKind.Add:
            case VmExpressionKind.Subtract:
            case VmExpressionKind.Multiply:
            case VmExpressionKind.Divide: {
                var left = EvaluateExpression(expression.Left!, lambda, args);
                var right = EvaluateExpression(expression.Right!, lambda, args);
                if (expression.Kind == VmExpressionKind.Add && left.ObjectValue is VmString ls && right.ObjectValue is VmString rs)
                    return StackSlot.OfObject(_intrinsicContext.MakeString(ls.Value + rs.Value));
                var op = expression.Kind switch {
                    VmExpressionKind.Add => ILOp.Add,
                    VmExpressionKind.Subtract => ILOp.Sub,
                    VmExpressionKind.Multiply => ILOp.Mul,
                    _ => ILOp.Div,
                };
                return SlotOps.BinaryArithmetic(op, left, right);
            }
            case VmExpressionKind.Modulo:
                return SlotOps.BinaryArithmetic(ILOp.Rem,
                    EvaluateExpression(expression.Left!, lambda, args),
                    EvaluateExpression(expression.Right!, lambda, args));
            case VmExpressionKind.Negate:
                return SlotOps.UnaryArithmetic(ILOp.Neg, EvaluateExpression(expression.Left!, lambda, args));
            case VmExpressionKind.Not:
                return SlotOps.UnaryArithmetic(ILOp.Not, EvaluateExpression(expression.Left!, lambda, args));
            case VmExpressionKind.Equal:
                return StackSlot.OfInt32(SlotOps.Compare(ILOp.Ceq,
                    EvaluateExpression(expression.Left!, lambda, args),
                    EvaluateExpression(expression.Right!, lambda, args)) ? 1 : 0);
            case VmExpressionKind.NotEqual:
                return StackSlot.OfInt32(!SlotOps.Compare(ILOp.Ceq,
                    EvaluateExpression(expression.Left!, lambda, args),
                    EvaluateExpression(expression.Right!, lambda, args)) ? 1 : 0);
            case VmExpressionKind.GreaterThan:
            case VmExpressionKind.GreaterThanOrEqual:
            case VmExpressionKind.LessThan:
            case VmExpressionKind.LessThanOrEqual: {
                var left = EvaluateExpression(expression.Left!, lambda, args);
                var right = EvaluateExpression(expression.Right!, lambda, args);
                var op = expression.Kind switch {
                    VmExpressionKind.GreaterThan => ILOp.Cgt,
                    VmExpressionKind.GreaterThanOrEqual => SlotOps.CgeShim,
                    VmExpressionKind.LessThan => ILOp.Clt,
                    _ => SlotOps.CleShim,
                };
                return StackSlot.OfInt32(SlotOps.Compare(op, left, right) ? 1 : 0);
            }
            case VmExpressionKind.And:
            case VmExpressionKind.Or:
            case VmExpressionKind.ExclusiveOr:
                return SlotOps.BinaryArithmetic(expression.Kind switch {
                    VmExpressionKind.And => ILOp.And,
                    VmExpressionKind.Or => ILOp.Or,
                    _ => ILOp.Xor,
                }, EvaluateExpression(expression.Left!, lambda, args),
                    EvaluateExpression(expression.Right!, lambda, args));
            case VmExpressionKind.AndAlso: {
                var left = EvaluateExpression(expression.Left!, lambda, args);
                return SlotOps.IsTrue(left) ? EvaluateExpression(expression.Right!, lambda, args) : StackSlot.OfInt32(0);
            }
            case VmExpressionKind.OrElse: {
                var left = EvaluateExpression(expression.Left!, lambda, args);
                return SlotOps.IsTrue(left) ? StackSlot.OfInt32(1) : EvaluateExpression(expression.Right!, lambda, args);
            }
            case VmExpressionKind.Convert:
                return ConvertExpressionValue(EvaluateExpression(expression.Left!, lambda, args), expression.ResultType);
            case VmExpressionKind.TypeIs: {
                var value = EvaluateExpression(expression.Left!, lambda, args);
                var matches = expression.NewType is { } target &&
                    (value.ObjectValue is not null
                        ? TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType)
                        : IsPrimitiveExpressionType(value, target));
                return StackSlot.OfInt32(matches ? 1 : 0);
            }
            case VmExpressionKind.TypeAs: {
                var value = EvaluateExpression(expression.Left!, lambda, args);
                if (value.ObjectValue is null || expression.NewType is not { } target ||
                    !TypeChecks.IsAssignableToType(value.ObjectValue, target, _services.StringType))
                    return StackSlot.Null;
                return value;
            }
            case VmExpressionKind.Conditional:
                return SlotOps.IsTrue(EvaluateExpression(expression.Left!, lambda, args))
                    ? EvaluateExpression(expression.IfTrue!, lambda, args)
                    : EvaluateExpression(expression.IfFalse!, lambda, args);
            case VmExpressionKind.NewArray: {
                var elementType = expression.NewType
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "式木 NewArray に要素型がありません。");
                StackSlot[] elements;
                if (expression.NewArrayBounds) {
                    if (expression.Arguments.Length != 1)
                        throw new UnhandledGuestException("System.NotSupportedException", "式木の多次元配列は未対応です。");
                    var length = EvaluateExpression(expression.Arguments[0], lambda, args).AsInt32;
                    if (length < 0)
                        throw new UnhandledGuestException("System.OverflowException", null);
                    elements = new StackSlot[length];
                    for (var i = 0; i < elements.Length; i++)
                        elements[i] = _services.Objects.DefaultForType(elementType, _loader);
                } else {
                    elements = expression.Arguments.Select(argument =>
                        EvaluateExpression(argument, lambda, args)).ToArray();
                }
                var arrayType = new VmArrayType { ElementType = elementType };
                return StackSlot.OfObject(_services.Heap.Allocate(new VmArray(arrayType, elements)));
            }
            case VmExpressionKind.Call: {
                var method = expression.Method
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "式木 Call に MethodInfo がありません。");
                var callArgs = new StackSlot[expression.Arguments.Length + (expression.Object is null ? 0 : 1)];
                var offset = 0;
                if (expression.Object is not null)
                    callArgs[offset++] = EvaluateExpression(expression.Object, lambda, args);
                foreach (var argument in expression.Arguments)
                    callArgs[offset++] = EvaluateExpression(argument, lambda, args);
                return invoker.Invoke(method, callArgs, null);
            }
            case VmExpressionKind.Invoke: {
                var target = EvaluateExpression(expression.Object!, lambda, args);
                if (target.ObjectValue is not VmDelegate delegateValue)
                    throw new UnhandledGuestException("System.ArgumentException", "式木 Invoke の対象が delegate ではありません。");
                var invokeArgs = new StackSlot[expression.Arguments.Length + 1];
                invokeArgs[0] = target;
                for (var i = 0; i < expression.Arguments.Length; i++)
                    invokeArgs[i + 1] = EvaluateExpression(expression.Arguments[i], lambda, args);
                return InvokeDelegate(delegateValue, invokeArgs) ?? default;
            }
            case VmExpressionKind.New: {
                var ctor = expression.Method
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "式木 New に ConstructorInfo がありません。");
                var constructorArgs = expression.Arguments.Select(argument => EvaluateExpression(argument, lambda, args)).ToArray();
                return _objectEngine.ConstructExpression(ctor, constructorArgs);
            }
            case VmExpressionKind.MemberAccess: {
                var receiver = expression.Object is null ? StackSlot.Null
                    : EvaluateExpression(expression.Object, lambda, args);
                if (expression.Property is { } property)
                    return property.Getter.Signature.HasThis
                        ? invoker.Invoke(property.Getter, [receiver], null)
                        : invoker.Invoke(property.Getter, [], null);
                var field = expression.Field
                    ?? throw new UnhandledGuestException("System.InvalidOperationException", "式木 MemberAccess に FieldInfo がありません。");
                if (field.IsStatic)
                    return _objectEngine.StaticFieldLocation((int)Token.From(TableKind.Field, field.FieldRid).Value).Read();
                return _objectEngine.FieldLocation(receiver, field).Read();
            }
            case VmExpressionKind.ArrayIndex: {
                var arraySlot = EvaluateExpression(expression.Left!, lambda, args);
                var index = EvaluateExpression(expression.Right!, lambda, args).AsInt32;
                if (arraySlot.ObjectValue is not VmArray array || (uint)index >= (uint)array.Length)
                    throw new UnhandledGuestException("System.IndexOutOfRangeException", null);
                return array.Elements[index];
            }
            case VmExpressionKind.ArrayLength: {
                var arraySlot = EvaluateExpression(expression.Left!, lambda, args);
                if (arraySlot.ObjectValue is not VmArray array)
                    throw new UnhandledGuestException("System.ArgumentException", "式木 ArrayLength の対象が配列ではありません。");
                return StackSlot.OfInt32(array.Length);
            }
            default:
                throw new UnhandledGuestException("System.NotSupportedException", $"式木ノード {expression.Kind} は評価できません。");
        }
    }

    private static StackSlot ConvertExpressionValue(in StackSlot value, VmType? target) {
        var name = target?.FullName;
        var op = name switch {
            "System.SByte" => ILOp.Conv_I1,
            "System.Byte" => ILOp.Conv_U1,
            "System.Int16" => ILOp.Conv_I2,
            "System.UInt16" => ILOp.Conv_U2,
            "System.Int32" => ILOp.Conv_I4,
            "System.UInt32" => ILOp.Conv_U4,
            "System.Int64" => ILOp.Conv_I8,
            "System.UInt64" => ILOp.Conv_U8,
            "System.Single" => ILOp.Conv_R4,
            "System.Double" => ILOp.Conv_R8,
            _ => (ILOp?)null,
        };
        return op is { } conversion ? SlotOps.ConvertValue(conversion, value) : value;
    }

    private static bool IsPrimitiveExpressionType(in StackSlot value, VmType target) =>
        target.FullName switch {
            "System.Object" or "System.ValueType" => value.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.Float or StackKind.NativeInt,
            "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or
            "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => value.Kind == StackKind.Int32,
            "System.Int64" or "System.UInt64" => value.Kind == StackKind.Int64,
            "System.Single" or "System.Double" => value.Kind == StackKind.Float,
            "System.IntPtr" or "System.UIntPtr" => value.Kind is StackKind.NativeInt or StackKind.IntPtr,
            _ => false,
        };
}
