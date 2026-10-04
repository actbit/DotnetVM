using DotnetVM.Runtime.Execution;
using Xunit;

namespace DotnetVM.Tests;

public sealed class NativeReflectionExecutionTests {
    private const string Source = """
        using System;
        using System.Reflection;
        [assembly: Vm.NativeReflection.Marker(23)]
        namespace Vm.NativeReflection;
        [AttributeUsage(AttributeTargets.All)]
        public sealed class MarkerAttribute : Attribute {
            public MarkerAttribute(int value) { Value = value; }
            public int Value { get; }
            public string Label { get; set; }
        }
        [Marker(7, Label = "sample")]
        public class Model {
            [Marker(13)] public int Field;
            public Model() { Value = 11; }
            public Model(int value) { Value = value; }
            [Marker(9)] public int Value { get; set; }
            [Marker(17)] public static int Add([Marker(19)] int value) => value + 3;
            public int Read() => Value;
            public virtual int Virtual() => 1;
            public static int NoArgs() => 42;
            public static void Mutate(ref int value, out string text) { value += 2; text = "done"; }
            public static void Fail() => throw new InvalidOperationException("probe");
            public static int Many(int a, int b, int c, int d, int e) => a + b + c + d + e;
            public static string Optional(int count = -7, string text = "既定値") => text;
            [return: Marker(31)] public static bool TaggedReturn() => true;
        }
        public sealed class Derived : Model { public override int Virtual() => 2; }
        public static class Entry {
            public static string PropertyName() => typeof(MarkerAttribute).GetProperty("Label", BindingFlags.Public | BindingFlags.Instance, null, typeof(string), Type.EmptyTypes, null).Name;
            public static int NoArgs() => (int)typeof(Model).GetMethod("NoArgs").Invoke(null, null);
            public static int OneArg() => (int)typeof(Model).GetMethod("Add").Invoke(null, new object[] { 4 });
            public static int Repeated() {
                var method = typeof(Model).GetMethod("Add");
                object[] args = { 4 };
                var total = 0;
                for (int i = 0; i < 100; i++) total += (int)method.Invoke(null, args);
                return total;
            }
            public static bool InvocationSwitch() => AppContext.TryGetSwitch("Switch.System.Reflection.ForceInterpretedInvoke", out var enabled) && enabled;
            public static string MethodName() => typeof(Model).GetMethod("Add").Name;
            public static int Instance() => (int)typeof(Model).GetMethod("Read").Invoke(new Model(17), null);
            public static int Constructor() => ((Model)typeof(Model).GetConstructor(new[] { typeof(int) }).Invoke(new object[] { 19 })).Value;
            public static int Data() => (int)typeof(Model).GetCustomAttributesData()[0].ConstructorArguments[0].Value;
            public static int Attribute() { var a = (MarkerAttribute)typeof(Model).GetCustomAttributes(typeof(MarkerAttribute), false)[0]; return a.Value + a.Label.Length; }
            public static bool Defined() => typeof(Model).IsDefined(typeof(MarkerAttribute), false);
            public static string RefOut() { var args = new object[] { 1025, null }; typeof(Model).GetMethod("Mutate").Invoke(null, args); return args[0] + "|" + args[1]; }
            public static string Exception() { try { typeof(Model).GetMethod("Fail").Invoke(null, null); return "missing"; } catch (TargetInvocationException e) { return e.InnerException.GetType().FullName + "|" + e.InnerException.Message; } }
            public static int Many() => (int)typeof(Model).GetMethod("Many").Invoke(null, new object[] {1,2,3,4,5});
            public static int MemberAttributes() => ((MarkerAttribute)typeof(Model).GetProperty("Value").GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value
                + ((MarkerAttribute)typeof(Model).GetField("Field").GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value
                + ((MarkerAttribute)typeof(Model).GetMethod("Add").GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value
                + ((MarkerAttribute)typeof(Model).GetMethod("Add").GetParameters()[0].GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value;
            public static int AssemblyAttributes() => ((MarkerAttribute)typeof(Model).Assembly.GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value;
            public static int Virtual() => (int)typeof(Model).GetMethod("Virtual").Invoke(new Derived(), null);
            public static int Inherited() => ((MarkerAttribute)typeof(Derived).GetCustomAttributes(typeof(MarkerAttribute), true)[0]).Value;
            public static int Properties() => typeof(Model).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
            public static string Defaults() {
                var parameters = typeof(Model).GetMethod("Optional").GetParameters();
                return parameters[0].Name + "|" + parameters[0].Position + "|" + parameters[0].HasDefaultValue + "|"
                    + parameters[0].DefaultValue + "|" + parameters[1].RawDefaultValue;
            }
            public static int ReturnAttribute() => ((MarkerAttribute)typeof(Model).GetMethod("TaggedReturn").ReturnParameter.GetCustomAttributes(typeof(MarkerAttribute), false)[0]).Value;
            public static string NamedData() => (string)typeof(Model).GetCustomAttributesData()[0].NamedArguments[0].TypedValue.Value;
        }
        """;
    private static readonly Lazy<CompiledTestAssembly> Guest = new(() => new(Source, "NativeReflectionGuest"));

    [Fact]
    public void ManagedReflectionHasNoReplacementBindings() {
        using var vm = Guest.Value.CreateVm();
        Assert.DoesNotContain(vm.Bindings, binding => binding.Key.TypeFullName.StartsWith("System.Reflection.") &&
            binding.Key.MethodName is "GetCustomAttributes" or "GetCustomAttributesData" or "IsDefined" or "Invoke" or "GetParameters" or "get_ReturnParameter");
    }

    [Theory]
    [InlineData("PropertyName", "System.RuntimeType", "GetPropertyImpl")]
    [InlineData("Properties", "System.RuntimeType", "GetProperties")]
    [InlineData("Defaults", "System.Reflection.RuntimeParameterInfo", "get_DefaultValue")]
    [InlineData("ReturnAttribute", "System.Reflection.CustomAttribute", "GetCustomAttributes")]
    [InlineData("NamedData", "System.Reflection.RuntimeCustomAttributeData", "get_NamedArguments")]
    [InlineData("Virtual", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Inherited", "System.Reflection.CustomAttribute", "GetCustomAttributes")]
    [InlineData("MemberAttributes", "System.Reflection.CustomAttribute", "GetCustomAttributes")]
    [InlineData("AssemblyAttributes", "System.Reflection.CustomAttribute", "GetCustomAttributes")]
    [InlineData("RefOut", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Exception", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Many", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Data", "System.Reflection.RuntimeCustomAttributeData", "get_ConstructorArguments")]
    [InlineData("Attribute", "System.Reflection.CustomAttribute", "GetCustomAttributes")]
    [InlineData("Defined", "System.Reflection.CustomAttribute", "IsDefined")]
    [InlineData("NoArgs", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("OneArg", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Repeated", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("MethodName", "System.Reflection.RuntimeMethodInfo", "get_Name")]
    [InlineData("Instance", "System.Reflection.RuntimeMethodInfo", "Invoke")]
    [InlineData("Constructor", "System.Reflection.RuntimeConstructorInfo", "Invoke")]
    public void OriginalInvocationIlMatchesClr(string name, string owner, string operation) {
        using var vm = Guest.Value.CreateVm();
        vm.Tracer.Start();
        try { Assert.Equal(Guest.Value.InvokeClr("Vm.NativeReflection.Entry", name), vm.Invoke("Vm.NativeReflection.Entry", name)); }
        catch (Exception e) { throw new InvalidOperationException(string.Join("\n", vm.Tracer.Frames.Where(f => f.ToString().Contains("Reflection") || f.ToString().Contains("RuntimeType")).TakeLast(60)), e); }
        Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", owner, operation));
        if (name == "Defaults") Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.String", "Ctor"));
        if (name == "Repeated") {
            Assert.True(vm.Tracer.ContainsFrame("System.Private.CoreLib", "System.Reflection.MethodBaseInvoker", "InvokeWithOneArg"));
            Assert.True((bool)vm.Invoke("Vm.NativeReflection.Entry", "InvocationSwitch")!);
            Assert.DoesNotContain(vm.Tracer.Frames, frame => frame.ToString().Contains("InvokeStub_"));
        }
    }
}
