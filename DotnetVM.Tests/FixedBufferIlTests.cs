using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

public sealed class FixedBufferIlTests {
    private const string Source = """
        using System;
        using System.Runtime.InteropServices;
        public static unsafe class FixedChecks {
            public struct Buffer {
                public fixed uint Values[8];
            }
            public sealed class Holder { public Buffer Buffer; }
            public static long CopyPreservesAllElements() {
                Buffer original = default;
                for (var i = 0; i < 8; i++) original.Values[i] = (uint)(i * 31);
                var copy = original;
                copy.Values[7] = 999;
                return original.Values[7] * 1000L + copy.Values[7];
            }
            public static long HeapFieldAndRawRoundtrip() {
                var holder = new Holder();
                fixed (uint* values = holder.Buffer.Values) {
                    values[0] = 11;
                    values[7] = 101;
                }
                var bytes = new byte[sizeof(Buffer)];
                MemoryMarshal.Write(bytes, in holder.Buffer);
                var restored = MemoryMarshal.Read<Buffer>(bytes);
                return restored.Values[0] * 1000L + restored.Values[7];
            }
            public static int DefaultBufferIsZero() {
                Buffer value = default;
                return (int)(value.Values[0] | value.Values[7]);
            }
            public static void WriteOnePast() {
                Buffer value = default;
                uint* pointer = value.Values;
                pointer[8] = 1;
            }
        }
        """;
    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new(Source, "FixedBufferChecks", allowUnsafe: true));

    [Theory]
    [InlineData("CopyPreservesAllElements", false)]
    [InlineData("CopyPreservesAllElements", true)]
    [InlineData("HeapFieldAndRawRoundtrip", false)]
    [InlineData("HeapFieldAndRawRoundtrip", true)]
    [InlineData("DefaultBufferIsZero", false)]
    [InlineData("DefaultBufferIsZero", true)]
    public void FixedBufferManagedIlMatchesClr(string method, bool jit) {
        using var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = true, EnableJit = jit, JitPromotionThreshold = 1,
        });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(Source, "FixedBufferChecks", allowUnsafe: true));
        vm.LoadAssembly(stream);
        Assert.Equal(Compiled.Value.InvokeClr("FixedChecks", method), vm.Invoke("FixedChecks", method));
    }

    [Fact]
    public void FixedBufferStorageIsChargedBeforeAllocation() {
        using var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = true,
            Memory = new MemoryPolicy { HostTempAllocationByteLimit = 64 },
        });
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(Source, "FixedBufferChecks", allowUnsafe: true));
        vm.LoadAssembly(stream);
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("FixedChecks", "DefaultBufferIsZero"));
    }

    [Fact]
    public void FixedBufferPointerCannotWriteOnePastStorage() {
        using var vm = Compiled.Value.CreateVm();
        var error = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("FixedChecks", "WriteOnePast"));
        Assert.Equal("System.IndexOutOfRangeException", error.ExceptionTypeName);
    }
}
