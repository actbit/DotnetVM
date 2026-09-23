using DotnetVM.Host;
using DotnetVM.Runtime.Intrinsics;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// 項目5 (AnyParams binding 縮小) の回帰テスト集:
/// - Guest 到達可能な AnyParams ワイルドカードは Console デバイス統合面のみ
///   (Monitor / Unsafe / RuntimeHelpers / Buffer / String 内部 / MemoryMarshal /
///   IUtfChar 面は .NET 10 の既知署名で列挙され、未知署名は fail-closed)
/// - Monitor.Exit_FastPath は LeaveHelperAction.None (0) を返す
///   (void ではなく enum 値を積む。lock 後の評価スタックがずれないこと)
/// </summary>
public class AnyParamsHardeningTests {
    private static VirtualMachine CreateVm(bool loadCoreLib = true) =>
        new(new VmHostOptions { LoadHostCoreLib = loadCoreLib });

    /// <summary>Guest domain の AnyParams 残置は Console デバイス統合面のみ。</summary>
    [Fact]
    public void No_Guest_AnyParams_Bindings_Remain_Outside_Console_Device() {
        using var vm = CreateVm(loadCoreLib: false);
        var remaining = vm.Bindings
            .Where(b => b.Key.IsAnyParams)
            .Select(b => $"{b.Key.TypeFullName}::{b.Key.MethodName} (origin {b.Origin})")
            .OrderBy(s => s)
            .ToList();
        var allowed = new HashSet<string> {
            "System.Console::Write",
            "System.Console::WriteLine",
            "System.Console::ReadLine",
        };
        var violations = remaining
            .Where(s => !allowed.Any(a => s.StartsWith(a, StringComparison.Ordinal)))
            .ToList();
        Assert.True(violations.Count == 0,
            "AnyParams ワイルドカードが残置しています (既知署名への置換漏れ):\n" +
            string.Join("\n", violations));
    }

    /// <summary>hardening 対象面が既知署名の正確キーで登録されている。</summary>
    [Fact]
    public void Hardened_Faces_Are_Registered_With_Exact_Signatures() {
        using var vm = CreateVm(loadCoreLib: false);
        var keys = vm.Bindings.Select(b => b.Key).ToHashSet();
        static BindingKey S(string t, string m, params string[] p) => BindingKey.Static(t, m, p);
        BindingKey[] required = [
            S("System.Threading.Monitor", "TryEnter_FastPath", "System.Object"),
            S("System.Threading.Monitor", "TryEnter_FastPath_WithTimeout", "System.Object", "System.Int32"),
            S("System.Threading.Monitor", "Exit_FastPath", "System.Object"),
            S("System.Threading.Monitor", "IsEnteredNative", "System.Object"),
            S("System.String", "FastAllocateString", "System.IntPtr"),
            S("System.String", "FastAllocateString",
                "System.Runtime.CompilerServices.MethodTable*", "System.IntPtr"),
            S("System.Buffer", "Memmove", "!!0&", "!!0&", "System.UIntPtr"),
            S("System.Runtime.CompilerServices.Unsafe", "Add", "!!0&", "System.Int32"),
            S("System.Runtime.CompilerServices.Unsafe", "Add", "!!0&", "System.IntPtr"),
            S("System.Runtime.CompilerServices.Unsafe", "Add", "!!0&", "System.UIntPtr"),
            S("System.Runtime.CompilerServices.Unsafe", "Add", "System.Void*", "System.Int32"),
            S("System.Runtime.CompilerServices.Unsafe", "AddByteOffset", "!!0&", "System.IntPtr"),
            S("System.Runtime.CompilerServices.Unsafe", "AddByteOffset", "!!0&", "System.UIntPtr"),
            S("System.Runtime.CompilerServices.Unsafe", "As", "System.Object"),
            S("System.Runtime.CompilerServices.Unsafe", "As", "!!0&"),
            S("System.Runtime.CompilerServices.Unsafe", "AreSame", "!!0&", "!!0&"),
            S("System.Runtime.CompilerServices.Unsafe", "AsRef", "!!0&"),
            S("System.Runtime.CompilerServices.Unsafe", "AsRef", "System.Void*"),
            S("System.Runtime.CompilerServices.Unsafe", "SizeOf"),
            S("System.Runtime.CompilerServices.Unsafe", "BitCast", "!!0"),
            S("System.Runtime.CompilerServices.Unsafe", "CopyBlockUnaligned",
                "System.Byte&", "System.Byte&", "System.UInt32"),
            S("System.Runtime.CompilerServices.Unsafe", "CopyBlockUnaligned",
                "System.Void*", "System.Void*", "System.UInt32"),
            S("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "System.Array"),
            S("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference", "!!0[]"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsReferenceOrContainsReferences"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsBitwiseEquatable"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsKnownConstant", "System.Char"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsKnownConstant", "System.String"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsKnownConstant", "System.Type"),
            S("System.Runtime.CompilerServices.RuntimeHelpers", "IsKnownConstant", "!!0"),
            S("System.IUtfChar`1", "CastFrom", "System.Byte"),
            S("System.IUtfChar`1", "CastFrom", "System.Char"),
            S("System.IUtfChar`1", "CastFrom", "System.Int32"),
            S("System.IUtfChar`1", "CastFrom", "System.UInt32"),
            S("System.IUtfChar`1", "CastFrom", "System.UInt64"),
            S("System.IUtfChar`1", "CastToUInt32", "!0"),
        ];
        var missing = required.Where(k => !keys.Contains(k)).Select(k => k.ToString()).ToList();
        Assert.True(missing.Count == 0,
            "既知署名のバインドが登録されていません:\n" + string.Join("\n", missing));
    }

    /// <summary>lock (Monitor.Enter/Exit の CoreLib IL 経路) の前後で評価スタックが保たれる。
    /// Exit_FastPath が enum 値 (None = 0) を積まないと Exit IL の後続処理がスタックずれする。</summary>
    [Fact]
    public void Lock_Preserves_Evaluation_Stack() {
        using var vm = CreateVm();
        using var stream = new MemoryStream(TestAssemblyCompiler.CompileToBytes(
            """
            namespace Vm.Hardening {
                public static class LockOps {
                    public static int Run() {
                        var o = new object();
                        int x = 0;
                        lock (o) { x = 1; }
                        return x + 1;
                    }
                }
            }
            """, "HardLock"));
        vm.LoadAssembly(stream);
        Assert.Equal(2, vm.Invoke("Vm.Hardening.LockOps", "Run"));
    }
}
