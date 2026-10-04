using DotnetVM.Runtime.Types;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C2 型ユニフィケーションのテスト: LoadHostCoreLib = true でゲストの BCL 型参照が
/// CoreLib 実 TypeDef に解決され (参照アセンブリ⇔実装アセンブリの統合)、typeof/box/unbox/
/// キャスト/例外 catch が intrinsic ファサード経由時と同一の結果になることを CLR 突合で確認する。
/// </summary>
public class CoreLibTypeTests {
    private const string Source = """
        namespace Vm.C2 {
            public enum Color { Red, Green, Blue }
            public sealed class Model {
                public int Value { get; set; }
                public static int Global { get; set; }
            }

            public static class Entry {
                // typeof (ldtoken TypeRef → 統合された実型への解決)
                public static string TypeOfInt() => typeof(int).ToString();
                public static string TypeOfString() => typeof(string).ToString();
                public static string TypeOfEnum() => typeof(Color).ToString();
                public static bool TypeEquality() => typeof(int) == typeof(int) && typeof(int) != typeof(long);
                public static bool DefaultTypeHandle() => System.Type.GetTypeFromHandle(default) is null;
                public static int PropertiesViaManagedWrapper() => typeof(Model).GetProperties().Length;
                public static int NativeAttributes() => (int)typeof(Model).Attributes;
                public static int NativeToken() => typeof(Model).MetadataToken;
                public static string NativeModule() => typeof(Model).Module.ScopeName;
                public static string NativeTypeNames() => typeof(Model).Name + "|" + typeof(Model).FullName;
                public static string NativeBaseType() => typeof(Model).BaseType.ToString();
                public static bool NativeCast() => typeof(object).IsAssignableFrom(typeof(Model)) && !typeof(string).IsAssignableFrom(typeof(Model));
                public static string NativeGenericNames() => typeof(System.Collections.Generic.List<int>).ToString() + "|" + typeof(System.Collections.Generic.List<int>).Name + "|" + typeof(System.Collections.Generic.List<int>).FullName + "|" + typeof(System.Collections.Generic.List<>).ToString() + "|" + typeof(System.Collections.Generic.List<>).FullName;
                public static string NativeArrayNames() => typeof(int[,]).ToString() + "|" + typeof(int[]).Name;
                public static string RuntimeTypeOfType() => typeof(Model).GetType().FullName;
                public static string RuntimeTypeOfString() => "value".GetType().Name;

                // box / unbox / isinst (実型同士の代入可能性)
                public static bool BoxedIsInt() { object o = 42; return o is int; }
                public static int UnboxRoundtrip() { object o = 42; return (int)o; }
                public static bool BoxedIsValueType() { object o = 42; return o is System.ValueType; }
                public static long UnboxLong() { object o = 5L; return (long)o; }
                public static bool BoxedEnumIsEnum() { object o = Color.Blue; return o is Color && o is System.Enum; }

                // 参照型キャスト (VmString/VmArray → 実型 System.String/System.Array)
                public static bool StringIsString() { object o = "abc"; return o is string; }
                public static bool ArrayIsArray() { object o = new int[3]; return o is System.Array; }

                // 例外 catch (送出面はファサード、catch 目標は実型に解決される混合経路)
                public static string CatchOverflow() {
                    try { int x = int.MaxValue; return (x + 1).ToString(); }
                    catch (System.OverflowException) { return "caught"; }
                }
            }
        }
        """;

    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new CompiledTestAssembly(Source, "C2Asm"));

    private static object? RunClr(string method) =>
        Compiled.Value.InvokeClr("Vm.C2.Entry", method);

    private static DotnetVM.Host.VirtualMachine CreateVm(bool loadCoreLib) =>
        Compiled.Value.CreateVm(loadCoreLib);

    /// <summary>CoreLib あり/なしの両 VM と CLR の 3 方突合 (実型化しても結果が変わらないこと)。</summary>
    private static void AssertSameEverywhere(string method) {
        var expected = RunClr(method);
        using (var withCoreLib = CreateVm(loadCoreLib: true))
            Assert.Equal(expected, withCoreLib.Invoke("Vm.C2.Entry", method));
        using (var withoutCoreLib = CreateVm(loadCoreLib: false))
            Assert.Equal(expected, withoutCoreLib.Invoke("Vm.C2.Entry", method));
    }

    [Fact]
    public void TypeFromHandleExecutesOriginalManagedIl() {
        using var vm = CreateVm(loadCoreLib: true);
        vm.Tracer.Start();
        try { Assert.Equal(RunClr("TypeOfInt"), vm.Invoke("Vm.C2.Entry", "TypeOfInt")); }
        catch (Exception exception) { throw new InvalidOperationException(string.Join("\n", vm.Tracer.Frames.TakeLast(60)), exception); }
        Assert.Equal(true, vm.Invoke("Vm.C2.Entry", "DefaultTypeHandle"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.Type", "GetTypeFromHandle"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.RuntimeType", "ToString"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.RuntimeType", "InitializeCache"));
        Assert.DoesNotContain(vm.Bindings, binding => binding.Key.TypeFullName == "System.Type" &&
            binding.Key.MethodName == "GetTypeFromHandle");
    }

    [Theory]
    [InlineData("NativeAttributes", "System.RuntimeType", "GetAttributeFlagsImpl")]
    [InlineData("NativeToken", "System.RuntimeType", "get_MetadataToken")]
    [InlineData("NativeModule", "System.Reflection.RuntimeModule", "get_ScopeName")]
    [InlineData("NativeTypeNames", "System.RuntimeType", "get_Name")]
    [InlineData("NativeBaseType", "System.RuntimeType", "GetBaseType")]
    [InlineData("NativeCast", "System.Runtime.CompilerServices.CastCache", "TryGet")]
    [InlineData("NativeGenericNames", "System.RuntimeType", "ToString")]
    [InlineData("NativeArrayNames", "System.RuntimeType", "ToString")]
    [InlineData("RuntimeTypeOfType", "System.RuntimeType", "get_FullName")]
    [InlineData("RuntimeTypeOfString", "System.RuntimeType", "get_Name")]
    public void NativeMetadataLeavesPreserveOriginalReflectionIl(string method, string type, string member) {
        using var vm = CreateVm(loadCoreLib: true);
        vm.Tracer.Start();
        try { Assert.Equal(RunClr(method), vm.Invoke("Vm.C2.Entry", method)); }
        catch (Exception exception) { throw new InvalidOperationException(string.Join("\n", vm.Tracer.Frames.TakeLast(60)), exception); }
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", type, member));
        Assert.DoesNotContain(vm.Bindings, binding => binding.Key.TypeFullName is "System.Type" or "System.RuntimeType" &&
            binding.Key.MethodName is "GetAttributeFlagsImpl" or "get_Module");
    }

    [Fact]
    public void ParameterlessGetPropertiesExecutesOriginalManagedIl() {
        using var vm = CreateVm(loadCoreLib: true);
        vm.Tracer.Start();
        Assert.Equal(RunClr("PropertiesViaManagedWrapper"), vm.Invoke("Vm.C2.Entry", "PropertiesViaManagedWrapper"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.Type", "GetProperties"));
        Assert.DoesNotContain(vm.Bindings, binding =>
            binding.Key.TypeFullName is "System.Type" or "System.RuntimeType" &&
            binding.Key.MethodName == "GetProperties" && binding.Key.ParamSignature.Length == 0);
    }

    [Fact]
    public void RuntimeTypeCacheCanBeRebuiltAfterVmCollection() {
        using var vm = CreateVm(loadCoreLib: true);
        var expected = RunClr("TypeOfInt");
        Assert.Equal(expected, vm.Invoke("Vm.C2.Entry", "TypeOfInt"));
        vm.CollectGarbage();
        vm.Tracer.Start();
        Assert.Equal(expected, vm.Invoke("Vm.C2.Entry", "TypeOfInt"));
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.RuntimeType", "InitializeCache"));
    }

    [Fact]
    public void NativeStructFieldsUseClrOffsetsAndEnforceBoundsAndReadonly() {
        using var vm = CreateVm(loadCoreLib: true);
        var type = vm.Context.FindBySimpleName("System.Private.CoreLib")!
            .FindTypeByFullName("System.Runtime.CompilerServices.CastCache+CastCacheEntry")!;
        var hostType = typeof(object).Assembly.GetType(type.FullName)!;
        var memory = new VmLocallocMemory { Bytes = new byte[MemoryOps.SizeOfRawType(type)] };
        var pointer = new VmNativePointer { Memory = memory, IsReadOnly = true };
        foreach (var field in type.Fields.Where(field => !field.IsStatic)) {
            var address = MemoryOps.RawFieldAddress(pointer, field);
            Assert.Equal((int)System.Runtime.InteropServices.Marshal.OffsetOf(hostType, field.Name), address.ByteOffset);
            Assert.Same(memory, address.Memory);
            Assert.Throws<DotnetVM.Policy.UnhandledGuestException>(() => address.EnsureWritable());
        }
        var outside = new VmNativePointer { Memory = memory, ByteOffset = memory.Bytes.Length };
        Assert.Throws<DotnetVM.Policy.UnhandledGuestException>(() => MemoryOps.RawFieldAddress(outside, type.Fields.First(field => !field.IsStatic)));
    }

    [Fact]
    public void RuntimeMetadataHandlesBelongToTheirVm() {
        using var first = CreateVm(loadCoreLib: true);
        using var second = CreateVm(loadCoreLib: true);
        first.Invoke("Vm.C2.Entry", "TypeOfInt"); second.Invoke("Vm.C2.Entry", "TypeOfInt");
        var firstType = first.Context.FindBySimpleName("System.Private.CoreLib")!.FindTypeByFullName("System.Int32")!;
        var secondType = second.Context.FindBySimpleName("System.Private.CoreLib")!.FindTypeByFullName("System.Int32")!;
        var firstMetadata = first.SharedState.RuntimeMetadata;
        var secondMetadata = second.SharedState.RuntimeMetadata;
        var firstAddress = firstMetadata.Get(first.SharedState.TypeFacades[firstType].ManagedInstance!, "m_handle").Int64Value;
        var secondAddress = secondMetadata.Get(second.SharedState.TypeFacades[secondType].ManagedInstance!, "m_handle").Int64Value;
        Assert.Same(firstType, firstMetadata.Resolve<VmType>(firstAddress));
        Assert.Same(secondType, secondMetadata.Resolve<VmType>(secondAddress));
        Assert.NotSame(firstType, secondType);
        Assert.Throws<DotnetVM.Policy.UnhandledGuestException>(() => firstMetadata.Resolve<VmType>(-1));
        Assert.Throws<DotnetVM.Policy.UnhandledGuestException>(() => firstMetadata.Resolve<TypeLoader>(firstAddress));
    }

    [Fact]
    public void TypeOf_Resolves_To_Real_Types_And_Matches_Clr() {
        AssertSameEverywhere("TypeOfInt");
        AssertSameEverywhere("TypeOfString");
        AssertSameEverywhere("TypeOfEnum");
        AssertSameEverywhere("TypeEquality");
    }

    [Fact]
    public void Box_Unbox_And_Casts_Match_Clr_With_Real_Types() {
        AssertSameEverywhere("BoxedIsInt");
        AssertSameEverywhere("UnboxRoundtrip");
        AssertSameEverywhere("BoxedIsValueType");
        AssertSameEverywhere("UnboxLong");
        AssertSameEverywhere("BoxedEnumIsEnum");
        AssertSameEverywhere("StringIsString");
        AssertSameEverywhere("ArrayIsArray");
    }

    [Fact]
    public void Guest_Catch_Matches_Clr_Across_Facade_And_Real_Types() {
        AssertSameEverywhere("CatchOverflow");
    }

    [Fact]
    public void Unified_Real_Types_Have_Clr_Consistent_Type_Properties() {
        using var vm = CreateVm(loadCoreLib: true);
        var coreLib = vm.Context.FindBySimpleName("System.Private.CoreLib");
        Assert.NotNull(coreLib);

        // 実型は統合辞書経由で解決され、基底チェーンが実 ValueType/Object で完結する
        var int32 = coreLib.FindTypeByFullName("System.Int32");
        Assert.NotNull(int32);
        Assert.True(int32.IsValueType);
        Assert.Equal("System.ValueType", int32.BaseType!.FullName);
        Assert.Equal("System.Object", int32.BaseType.BaseType!.FullName);
        Assert.Null(int32.BaseType.BaseType.BaseType); // Object の親は無い (循環を断っている)

        // 列挙型の実型判定
        var dayOfWeek = coreLib.FindTypeByFullName("System.DayOfWeek");
        Assert.NotNull(dayOfWeek);
        Assert.True(dayOfWeek.IsEnum);

        // ゲストから typeof(int) で見える型は CoreLib 実型そのもの (同一インスタンス)
        // (ldtoken 経由の解決が統合辞書に載ることを反映し、ファサードとは別インスタンス)
        Assert.Same(coreLib.FindTypeByFullName("System.String"), coreLib.FindTypeByFullName("System.String"));
    }
}
