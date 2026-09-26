using DotnetVM.Runtime.Types;
using DotnetVM.Runtime.Execution;

namespace DotnetVM.Runtime.Objects;

/// <summary>
/// Guest-visible handles are ordinary VM objects and can outlive a collectible
/// AssemblyLoadContext. Keep the lifetime check in one place so every call
/// boundary fails closed instead of reviving a loader/JIT cache.
/// </summary>
internal static class VmLifetime {
    public static void EnsureLive(in StackSlot slot) {
        if (slot.ObjectValue is { } value)
            EnsureLive(value);
    }

    public static void EnsureLive(object value) {
        switch (value) {
            case VmAssemblyLoadContext context:
                context.EnsureLive();
                break;
            case VmAssemblyObject assembly:
                assembly.Loader.EnsureLive();
                break;
            case VmClassInstance { AssemblyLoadContextHandle: { } context }:
                context.EnsureLive();
                break;
            case VmRuntimeObject runtimeType:
                EnsureLive(runtimeType.Target);
                break;
            case VmRuntimeMethod runtimeMethod:
                EnsureLive(runtimeMethod.Target);
                break;
            case VmRuntimeField runtimeField:
                EnsureLive(runtimeField.Target);
                break;
            case VmRuntimeProperty runtimeProperty:
                EnsureLive(runtimeProperty.Getter);
                break;
            case VmTypeHandle typeHandle:
                EnsureLive(typeHandle.Target);
                break;
            case VmMethodHandle methodHandle:
                EnsureLive(methodHandle.Target);
                break;
            case VmFieldHandle fieldHandle:
                EnsureLive(fieldHandle.Target);
                break;
            case VmFieldRvaData rva:
                rva.OwnerLoader?.EnsureLive();
                break;
            case VmMethodPointer methodPointer:
                EnsureLive(methodPointer.Target);
                break;
            case VmDelegate @delegate:
                foreach (var invocation in @delegate.Invocations) {
                    EnsureLive(invocation.Target);
                    EnsureLive(invocation.Method);
                }
                break;
            case VmType type:
                EnsureLive(type);
                break;
            case VmMethod method:
                method.Loader?.EnsureLive();
                break;
            case VmField field:
                EnsureLive(field.DeclaringType);
                break;
        }
    }

    public static void EnsureLive(VmType type) {
        switch (type) {
            case VmClassType classType:
                classType.Loader?.EnsureLive();
                break;
            case VmConstructedType constructed:
                EnsureLive(constructed.Definition);
                foreach (var argument in constructed.TypeArguments)
                    EnsureLive(argument);
                break;
            case VmArrayType array:
                EnsureLive(array.ElementType);
                break;
            case VmMultiDimArrayType array:
                EnsureLive(array.ElementType);
                break;
            case VmByRefType byRef:
                EnsureLive(byRef.ElementType);
                break;
        }
    }

    public static void EnsureLive(VmMethod method) => method.Loader?.EnsureLive();
}
