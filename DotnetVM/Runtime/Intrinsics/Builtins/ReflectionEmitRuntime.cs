using System.Reflection;
using System.Reflection.Emit;
using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>Reflection.Emit の DynamicMethod/ILGenerator 面。生成 IL は必ず VM interpreter で実行する。</summary>
internal static class ReflectionEmitRuntime {
    private const string DynamicMethodType = "System.Reflection.Emit.DynamicMethod";
    private const string GeneratorType = "System.Reflection.Emit.ILGenerator";

    public static StackSlot ConstructDynamicMethod(IntrinsicContext context, StackSlot[] args) {
        var name = (args[1].ObjectValue as VmString)?.Value
            ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
        var parameterSlot = args.Skip(2).FirstOrDefault(slot => slot.ObjectValue is VmArray);
        var parameterTypes = parameterSlot.ObjectValue switch {
            VmArray array => array.Elements.Select(slot => RequireType(slot)).ToArray(),
            null => [],
            _ => throw new UnhandledGuestException("System.ArgumentException", "parameterTypes は Type[] である必要があります。"),
        };
        var parameterIndex = Array.FindIndex(args, slot => slot.ObjectValue is VmArray);
        var returnSlot = parameterIndex > 2
            ? args.Take(parameterIndex).Last(slot => slot.ObjectValue is VmRuntimeObject)
            : args.Skip(2).First(slot => slot.ObjectValue is VmRuntimeObject);
        var returnType = RequireType(returnSlot);
        var builder = new VmDynamicMethodBuilder(context.Types, context.Heap, name,
            VmDynamicMethodBuilder.SignatureType(returnType),
            parameterTypes.Select(VmDynamicMethodBuilder.SignatureType).ToArray(),
            context.MemoryPolicy?.MaxMethodBodyBytes ?? 512 * 1024);
        SetBuilder(context, args[0], builder);
        return default;
    }

    public static StackSlot GetILGenerator(IntrinsicContext context, StackSlot[] args) {
        var builder = GetBuilder(args[0]);
        var facade = context.Types.FindIntrinsicType(GeneratorType)
            ?? throw new InvalidOperationException($"{GeneratorType} ファサードがありません。");
        var generator = context.Heap.Allocate(new VmIntrinsicInstance(facade));
        SetBuilder(context, StackSlot.OfObject(generator), builder);
        return StackSlot.OfObject(generator);
    }

    public static StackSlot DefineLabel(IntrinsicContext context, StackSlot[] args) {
        var builder = GetBuilder(args[0]);
        var label = context.Heap.Allocate(new VmEmitLabel { Id = builder.DefineLabel() });
        return StackSlot.OfObject(label);
    }

    public static StackSlot MarkLabel(IntrinsicContext context, StackSlot[] args) {
        GetBuilder(args[0]).MarkLabel(ReadLabel(args[1]));
        return default;
    }

    public static StackSlot DeclareLocal(IntrinsicContext context, StackSlot[] args) {
        var type = RequireType(args[1]);
        var local = context.Heap.Allocate(new VmLocalBuilder {
            Index = GetBuilder(args[0]).DeclareLocal(VmDynamicMethodBuilder.SignatureType(type)),
            LocalType = type,
        });
        return StackSlot.OfObject(local);
    }

    public static StackSlot Emit(IntrinsicContext context, StackSlot[] args) {
        var builder = GetBuilder(args[0]);
        var opcode = ReadOpcode(args[1]);
        if (args.Length == 2) {
            builder.EmitOpcode(opcode);
            return default;
        }
        switch (context.ParamAt(1)) {
            case "System.Byte":
                builder.EmitByte(opcode, args[2].AsInt32);
                break;
            case "System.SByte":
                builder.EmitSByte(opcode, args[2].AsInt32);
                break;
            case "System.Int16":
                builder.EmitInt16(opcode, args[2].AsInt32);
                break;
            case "System.Int32":
                builder.EmitInt32(opcode, args[2].AsInt32);
                break;
            case "System.Int64":
                builder.EmitInt64(opcode, args[2].Int64Value);
                break;
            case "System.Single":
                builder.EmitSingle(opcode, (float)args[2].DoubleValue);
                break;
            case "System.Double":
                builder.EmitDouble(opcode, args[2].DoubleValue);
                break;
            case "System.Reflection.Emit.Label":
                builder.EmitLabel(opcode, ReadLabel(args[2]));
                break;
            case "System.Reflection.Emit.Label[]":
                builder.EmitLabels(opcode, ReadLabels(args[2]));
                break;
            case "System.Reflection.Emit.LocalBuilder":
                builder.EmitInt32(opcode, ReadLocal(args[2]));
                break;
            case "System.Reflection.MethodInfo":
            case "System.Reflection.ConstructorInfo":
                builder.EmitReference(opcode, ReadMethod(args[2]));
                break;
            case "System.Reflection.FieldInfo":
                builder.EmitReference(opcode, ReadRuntimeField(args[2]));
                break;
            case "System.Type":
                builder.EmitReference(opcode, RequireType(args[2]));
                break;
            case "System.String":
                builder.EmitString(opcode, (args[2].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null));
                break;
            default:
                var info = IlOpcodeTable.Get((ILOp)opcode);
                throw new UnhandledGuestException("System.NotSupportedException",
                    $"ILGenerator.Emit(OpCode, {context.ParamAt(1)}) は未対応です" +
                    (info is null ? "。" : $" ({info.Name}/{info.Operand})。"));
        }
        return default;
    }

    public static StackSlot EmitCall(IntrinsicContext context, StackSlot[] args) {
        var builder = GetBuilder(args[0]);
        builder.EmitReference(ReadOpcode(args[1]), ReadMethod(args[2]));
        return default;
    }

    public static StackSlot CreateDelegate(IntrinsicContext context, StackSlot[] args) {
        var builder = GetBuilder(args[0]);
        var delegateType = RequireType(args[1]);
        if (!TypeChecks.IsDelegateType(delegateType))
            throw new UnhandledGuestException("System.ArgumentException", "CreateDelegate の型は delegate である必要があります。");
        var method = builder.CreateMethod();
        var result = context.Heap.Allocate(new VmDelegate { DeclaredType = delegateType });
        result.AddInvocation(new DelegateInvocation(StackSlot.Null, method));
        return StackSlot.OfObject(result);
    }

    public static void RegisterOpCodeFields(IntrinsicRegistry registry) {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (var field in typeof(OpCodes).GetFields(flags)) {
            if (field.GetValue(null) is not OpCode opcode)
                continue;
            var value = unchecked((ushort)opcode.Value);
            registry.RegisterStaticField("System.Reflection.Emit.OpCodes", field.Name,
                context => StackSlot.OfObject(context.Heap.Allocate(new VmIntrinsicCarrier { Payload = value })));
        }
    }

    private static VmType RequireType(in StackSlot slot) => slot.ObjectValue is VmRuntimeObject runtimeType
        ? runtimeType.Target
        : throw new UnhandledGuestException("System.ArgumentException", "Type 値が必要です。");

    private static int ReadLabel(in StackSlot slot) => slot.ObjectValue is VmEmitLabel label
        ? label.Id
        : throw new UnhandledGuestException("System.ArgumentException", "Label 値が必要です。");

    private static int[] ReadLabels(in StackSlot slot) => slot.ObjectValue switch {
        VmArray array => array.Elements.Select(slot => ReadLabel(slot)).ToArray(),
        _ => throw new UnhandledGuestException("System.ArgumentException", "Label[] 値が必要です."),
    };

    private static int ReadLocal(in StackSlot slot) => slot.ObjectValue is VmLocalBuilder local
        ? local.Index
        : throw new UnhandledGuestException("System.ArgumentException", "LocalBuilder 値が必要です。");

    private static VmMethod ReadMethod(in StackSlot slot) => slot.ObjectValue is VmRuntimeMethod method
        ? method.Target
        : throw new UnhandledGuestException("System.ArgumentException", "MethodInfo 値が必要です。");

    private static VmField ReadRuntimeField(in StackSlot slot) {
        if (slot.ObjectValue is not VmRuntimeField field)
            throw new UnhandledGuestException("System.ArgumentException", "FieldInfo 値が必要です。");
        return field.Target;
    }

    private static void SetBuilder(IntrinsicContext context, in StackSlot receiver, VmDynamicMethodBuilder builder) {
        var carrier = context.Heap.Allocate(new VmIntrinsicCarrier { Payload = builder });
        switch (receiver.ObjectValue) {
            case VmIntrinsicInstance intrinsic:
                intrinsic.State[0] = StackSlot.OfObject(carrier);
                break;
            case VmClassInstance instance when instance.Fields.Length > 0:
                instance.Fields[0] = StackSlot.OfObject(carrier);
                break;
            default:
                throw new InvalidOperationException("DynamicMethod/ILGenerator receiver は VM intrinsic instance である必要があります。");
        }
    }

    private static VmDynamicMethodBuilder GetBuilder(in StackSlot receiver) {
        var carrier = receiver.ObjectValue switch {
            VmIntrinsicInstance intrinsic when intrinsic.State.Length > 0 => intrinsic.State[0].ObjectValue as VmIntrinsicCarrier,
            VmClassInstance instance when instance.Fields.Length > 0 => instance.Fields[0].ObjectValue as VmIntrinsicCarrier,
            _ => null,
        };
        return carrier?.Payload as VmDynamicMethodBuilder
            ?? throw new UnhandledGuestException("System.InvalidOperationException", "DynamicMethod が初期化されていません。");
    }

    private static ushort ReadOpcode(in StackSlot slot) {
        if (slot.ObjectValue is VmIntrinsicCarrier { Payload: ushort value })
            return value;
        if (slot.Kind == StackKind.ValueType && slot.ObjectValue is VmStructValue { StructType: VmClassType type } valueType) {
            var fields = type.Fields.Where(field => !field.IsStatic && !field.IsLiteral).ToArray();
            var index = Array.FindIndex(fields, field => field.Name.Contains("value", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < valueType.Fields.Length)
                return unchecked((ushort)valueType.Fields[index].Int64Value);
        }
        throw new UnhandledGuestException("System.ArgumentException", "OpCode 値を読み取れません。");
    }
}
