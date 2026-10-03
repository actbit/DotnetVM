using System.Reflection;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

public sealed class VirtualDispatchReuseTests {
    private static readonly Lazy<byte[]> Guest = new(() => TestAssemblyCompiler.CompileToBytes("""
        public static class PolymorphicGuest {
            private class Holder<T> {
                private T _value;
                public Holder(T value) { _value = value; }
                public override int GetHashCode() => _value is int number
                    ? number + 10 : ((string)(object)_value).Length + 20;
            }
            private sealed class Derived<T> : Holder<T> {
                public Derived(T value) : base(value) { }
                public override int GetHashCode() => base.GetHashCode() + 100;
            }
            public static int Run(int count) {
                object[] values = {
                    new Holder<int>(4), new Holder<string>("abc"),
                    new Derived<int>(7), new Derived<string>("abcdef")
                };
                var sum = 0;
                for (var i = 0; i < count; i++) sum += values[i & 3].GetHashCode();
                return sum;
            }
        }
        """, "PolymorphicDispatchGuest", optimize: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedDispatchKeepsReceiverDefinitionAndGenericArguments(bool enableJit) {
        var bytes = Guest.Value;
        var coreClr = Assembly.Load(bytes).GetType("PolymorphicGuest")!.GetMethod("Run")!;
        using var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = true,
            EnableJit = enableJit,
            JitPromotionThreshold = 1,
        });
        vm.LoadAssembly(new MemoryStream(bytes));
        foreach (var count in new[] { 40, 400, 400 })
            Assert.Equal(coreClr.Invoke(null, [count]), vm.Invoke("PolymorphicGuest", "Run", count));
    }
}
