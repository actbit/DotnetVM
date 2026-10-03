using System.Reflection;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

public class CollectionAsyncCompatibilityTests {
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public interface IFactory<TSelf> where TSelf : IFactory<TSelf> {
            static abstract int Size();
            static abstract T Identity<T>(T value);
        }
        public struct Factory<T> : IFactory<Factory<T>> {
            static int IFactory<Factory<T>>.Size() => typeof(T) == typeof(int) ? 4 : 8;
            public static U Identity<U>(U value) => value;
        }
        public struct Inner { public int Value; }
        public sealed class Holder<T> { public T Value; }
        public struct Item { public Inner Nested; public object Tag; }
        public sealed class Key {
            public int Value;
            public override int GetHashCode() => Value;
            public override bool Equals(object other) => other is Key key && key.Value == Value;
        }

        public static class CompatibilityGuest {
            private static int FromFactory<T>() where T : IFactory<T> => T.Size() + T.Identity(3);
            public static int StaticInterface() => FromFactory<Factory<int>>() * 10 + FromFactory<Factory<long>>();

            private static object BoxGeneric<T>(T value) => value;
            private static T RoundTrip<T>(T value) {
                var holder = new Holder<T> { Value = value };
                return (T)BoxGeneric(holder.Value);
            }
            public static int GenericOperands() {
                var key = new Key { Value = 7 };
                return RoundTrip(17) + RoundTrip("hello").Length +
                    (ReferenceEquals(key, RoundTrip(key)) ? 100 : 0) +
                    (RoundTrip<Key>(null) == null ? 1000 : 0);
            }

            public static int CustomEquality() {
                var comparer = EqualityComparer<Key>.Default;
                var first = new Key { Value = 7 };
                var second = new Key { Value = 7 };
                return comparer.GetHashCode(first) * 10 + (comparer.Equals(first, second) ? 1 : 0);
            }

            public static int CustomDictionary() {
                var values = new Dictionary<Key, int>();
                for (var i = 0; i < 64; i++) values.Add(new Key { Value = i }, i * 2);
                var sum = 0;
                for (var i = 0; i < 64; i++) sum += values[new Key { Value = i }];
                return sum + (ReferenceEquals(EqualityComparer<Key>.Default, EqualityComparer<Key>.Default) ? 1 : 0);
            }

            public static int StructCopy() {
                var tag = new object();
                var source = new[] {
                    new Item { Nested = new Inner { Value = 1 }, Tag = tag },
                    new Item { Nested = new Inner { Value = 2 }, Tag = tag },
                    new Item { Nested = new Inner { Value = 3 }, Tag = tag },
                };
                var copy = new Item[3];
                Array.Copy(source, copy, 3);
                source[0].Nested.Value = 9;
                Array.Copy(copy, 0, copy, 1, 2);
                copy[0].Nested.Value = 7;
                var values = new List<Item>();
                for (var i = 0; i < 10; i++) values.Add(copy[1]);
                var sum = 0;
                foreach (var value in values) sum += value.Nested.Value;
                return sum + copy[1].Nested.Value * 100 + copy[2].Nested.Value * 1000
                    + (ReferenceEquals(copy[1].Tag, tag) ? 10000 : 0);
            }

            private static ValueTask WrapDelay() => new ValueTask(Task.Delay(1));
            private static async ValueTask<int> DelayedValue() {
                await WrapDelay().ConfigureAwait(false);
                return await new ValueTask<int>(Task.FromResult(17));
            }
            public static int ValueTaskConstructors() => DelayedValue().GetAwaiter().GetResult();
        }
        """;

    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> DebugGuest = new(() => Compile(false));
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> ReleaseGuest = new(() => Compile(true));
    private static (byte[], Assembly) Compile(bool optimize) {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source,
            optimize ? "CompatibilityReleaseGuest" : "CompatibilityDebugGuest", optimize: optimize);
        return (bytes, Assembly.Load(bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopiesGenericDispatchAndValueTasksMatchClr(bool optimize, bool jit) {
        var guest = (optimize ? ReleaseGuest : DebugGuest).Value;
        using var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = true, EnableJit = jit, JitPromotionThreshold = 2,
            Memory = new MemoryPolicy { GcTriggerAllocationInterval = 1024 },
        });
        using var stream = new MemoryStream(guest.Bytes);
        vm.LoadAssembly(stream);
        foreach (var name in new[] { "GenericOperands", "StructCopy", "CustomEquality", "CustomDictionary", "StaticInterface", "ValueTaskConstructors" }) {
            var expected = guest.Clr.GetType("CompatibilityGuest")!.GetMethod(name)!.Invoke(null, null);
            for (var repeat = 0; repeat < 3; repeat++) {
                var actual = await Task.Run(() => vm.Invoke("CompatibilityGuest", name))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(Equals(expected, actual), $"{name}: CLR={expected}, VM={actual}, optimize={optimize}, jit={jit}");
            }
        }
    }
}
