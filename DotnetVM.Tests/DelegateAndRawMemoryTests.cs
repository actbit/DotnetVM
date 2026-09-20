using System.Reflection;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// デリゲート機構 (ldftn/ldvirtftn + newobj delegate .ctor + callvirt Invoke +
/// Delegate.Combine/Remove/op_Equality 面) と raw メモリ・特殊命令
/// (localloc=stackalloc / sizeof / mkrefany-refanyval-refanytype=__makeref 系) の
/// CLR 突合テスト。同一ソースを VM と CLR の両方で実行し結果を突き合わせる。
/// </summary>
public class DelegateAndRawMemoryTests {
    private const string DelegateSource = """
        using System;
        namespace Vm {
            public static class Delegates {
                public delegate int IntOp(int x);
                public delegate R Mapper<T, R>(T input);   // ジェネリック カスタム delegate

                public static int Add100(int x) => x + 100;

                public class Calc {
                    public virtual int Double(int x) => x * 2;
                    public int Triple(int x) => x * 3;
                }
                public sealed class BigCalc : Calc {
                    public override int Double(int x) => x * 20;
                }

                public static int StaticMethodGroup() {
                    IntOp d = Add100;              // ldftn + newobj
                    return d(1);                   // callvirt Invoke
                }
                public static int InstanceMethodGroup() {
                    var c = new Calc();
                    IntOp d = c.Triple;            // 非仮想 instance メソッド グループ
                    return d(7);
                }
                public static int VirtualMethodGroup() {
                    Calc c = new BigCalc();
                    IntOp d = c.Double;            // ldvirtftn → 最派生実装に確定
                    return d(3);
                }
                public static int LambdaClosure(int factor) {
                    Func<int, int> f = x => x * factor;   // クロージャ キャプチャ
                    return f(6);
                }
                public static int GenericFuncLambda() {
                    Func<int, int> f = x => x + 1;        // BCL ファサード構築型
                    return f(41);
                }
                public static int Apply(IntOp op, int x) => op(x);
                public static int CustomDelegateLambda() {
                    IntOp op = x => x - 5;
                    return Apply(op, 50);
                }
                public static string GenericMapper() {
                    Mapper<string, int> len = s => s.Length;
                    return len("hello") + ":" + ApplyGen(len, "ab");
                }
                private static string ApplyGen<T, R>(Mapper<T, R> m, T input) => "" + m(input);

                public static string Multicast() {
                    var log = "";
                    Action<string> a = s => log += "a:" + s + ";";
                    Action<string> b = s => log += "b:" + s + ";";
                    Action<string> all = null;
                    all += a;                       // Delegate.Combine
                    all += b;
                    all("x");                       // a:x;b:x;
                    all -= a;                       // Delegate.Remove (末尾側一致の除去)
                    all("y");                       // b:y;
                    var eq = all == b;              // 残り [b] 1 件 → CLR 規約で等価
                    return log + (eq ? "eq" : "ne");
                }
                public static int CombineOrder() {
                    Func<int, int> f = x => x + 1;
                    Func<int, int> g = x => x * 10;
                    var both = f + g;               // Combine → 戻り値は最後のエントリ (CLR 規約)
                    return both(5);                 // 50
                }
                public static string NullInvoke() {
                    try { Action a = null; a(); return "no-throw"; }
                    catch (NullReferenceException) { return "nre"; }
                }
                public static string RemoveToEmpty() {
                    Action a = delegate { };
                    a -= a;                         // 空になる → CLR 規約で null
                    return a == null ? "null" : "non-null";
                }
                public static string DelegateFieldEvent() {
                    var log = "";
                    var sink = new Sink();
                    sink.Fired += msg => log += "1:" + msg + ";";
                    sink.Fired += msg => log += "2:" + msg + ";";
                    sink.Fire("go");
                    return log;
                }
                private sealed class Sink {
                    public event Action<string> Fired;      // フィールド風イベント = Combine/Remove + stfld
                    public void Fire(string msg) {
                        var handler = Fired;
                        if (handler != null) handler(msg);
                    }
                }
                public static string StaticDelegateEquality() {
                    // 同一 static メソッドから作った 2 つのデリゲートは等価 (CLR 規約)
                    Func<int, int> a = Add100;
                    Func<int, int> b = Add100;
                    return a == b ? "eq" : "ne";
                }
            }
        }
        """;

    private const string RawMemorySource = """
        using System;
        namespace Vm {
            public struct Point { public int X; public short Y; public byte Z; }
            public enum Color : byte { Red, Green, Blue }

            public static unsafe class Raw {
                public static int SizeOfPrims() =>
                    sizeof(byte) + sizeof(short) + sizeof(int) + sizeof(long) +
                    sizeof(char) + sizeof(bool) + sizeof(float) + sizeof(double);
                public static int SizeOfStruct() => sizeof(Point);
                public static int SizeOfEnum() => sizeof(Color);

                public static long StackAllocSum(int n) {
                    int* p = stackalloc int[n];        // localloc
                    for (var i = 0; i < n; i++) p[i] = i * i;
                    long sum = 0;
                    for (var i = 0; i < n; i++) sum += p[i];
                    return sum;
                }
                public static int PointerWalk() {
                    byte* p = stackalloc byte[8];
                    for (var i = 0; i < 8; i++) p[i] = (byte)(i * 3);
                    var s = 0;
                    byte* q = p;
                    for (var i = 0; i < 8; i++) { s += *q; q++; }   // ポインタ漸進 (ptr + 1 バイト)
                    return s * 100 + (int)(q - p);                  // ptr - ptr = 8 バイト
                }
                public static int StackAllocInitSum() {
                    // 初期化子なし stackalloc の読み出し (実 CLR は不定値のため CLR 突合不可)
                    int* p = stackalloc int[4];
                    return p[0] + p[1] + p[2] + p[3];
                }
                public static string TypedRefRoundtrip() {
                    var x = 42;
                    TypedReference tr = __makeref(x);   // mkrefany
                    var name = __reftype(tr).Name;      // refanytype + Type.GetTypeFromHandle
                    return __refvalue(tr, int) + ":" + name;   // refanyval + ldind.i4
                }
            }
        }
        """;

    private static readonly byte[] DelegateBytes =
        TestAssemblyCompiler.CompileToBytes(DelegateSource, "DelegateAsm");
    private static readonly Assembly DelegateClrAssembly = Assembly.Load(DelegateBytes);

    private static readonly byte[] RawBytes =
        TestAssemblyCompiler.CompileToBytes(RawMemorySource, "RawAsm", allowUnsafe: true);
    private static readonly Assembly RawClrAssembly = Assembly.Load(RawBytes);

    private static VirtualMachine CreateVm(byte[] bytes) {
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static void AssertDelegateMatchesClr(string method, params object?[] args) {
        var expected = DelegateClrAssembly.GetType("Vm.Delegates")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
        using var vm = CreateVm(DelegateBytes);
        Assert.Equal(expected, vm.Invoke("Vm.Delegates", method, args));
    }

    private static void AssertRawMatchesClr(string method, params object?[] args) {
        var expected = RawClrAssembly.GetType("Vm.Raw")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
        using var vm = CreateVm(RawBytes);
        Assert.Equal(expected, vm.Invoke("Vm.Raw", method, args));
    }

    // ---- メソッド グループ (ldftn / ldvirtftn) ----

    [Fact]
    public void StaticMethodGroup_MatchesClr() => AssertDelegateMatchesClr("StaticMethodGroup");

    [Fact]
    public void InstanceMethodGroup_MatchesClr() => AssertDelegateMatchesClr("InstanceMethodGroup");

    [Fact]
    public void VirtualMethodGroup_BindsMostDerived_MatchesClr() =>
        AssertDelegateMatchesClr("VirtualMethodGroup");

    // ---- ラムダ / クロージャ / ジェネリック ファサード ----

    [Fact]
    public void LambdaClosure_MatchesClr() => AssertDelegateMatchesClr("LambdaClosure", 3);

    [Fact]
    public void GenericFuncFacade_MatchesClr() => AssertDelegateMatchesClr("GenericFuncLambda");

    [Fact]
    public void CustomDelegateAsParameter_MatchesClr() =>
        AssertDelegateMatchesClr("CustomDelegateLambda");

    [Fact]
    public void GenericCustomDelegate_MatchesClr() => AssertDelegateMatchesClr("GenericMapper");

    // ---- マルチキャスト (Combine / Remove / 等価) ----

    [Fact]
    public void MulticastCombineRemoveEquality_MatchesClr() =>
        AssertDelegateMatchesClr("Multicast");

    [Fact]
    public void CombineReturnsLastResult_MatchesClr() => AssertDelegateMatchesClr("CombineOrder");

    [Fact]
    public void NullInvoke_ThrowsNre_MatchesClr() => AssertDelegateMatchesClr("NullInvoke");

    [Fact]
    public void RemoveToEmpty_BecomesNull_MatchesClr() =>
        AssertDelegateMatchesClr("RemoveToEmpty");

    [Fact]
    public void FieldLikeEvent_MatchesClr() => AssertDelegateMatchesClr("DelegateFieldEvent");

    [Fact]
    public void StaticDelegateEquality_MatchesClr() =>
        AssertDelegateMatchesClr("StaticDelegateEquality");

    // ---- sizeof ----

    [Fact]
    public void SizeOfPrimitives_MatchesClr() => AssertRawMatchesClr("SizeOfPrims");

    [Fact]
    public void SizeOfStruct_MatchesClr() => AssertRawMatchesClr("SizeOfStruct");

    [Fact]
    public void SizeOfEnum_MatchesClr() => AssertRawMatchesClr("SizeOfEnum");

    // ---- stackalloc (localloc + unmanaged ポインタ) ----

    [Fact]
    public void StackAllocSum_MatchesClr() => AssertRawMatchesClr("StackAllocSum", 10);

    [Fact]
    public void PointerArithmetic_MatchesClr() => AssertRawMatchesClr("PointerWalk");

    [Fact]
    public void StackAllocIsZeroInitialized() {
        // 実 CLR は不定値 (突合不能)。VM は安全側の上限動作として 0 初期化する
        using var vm = CreateVm(RawBytes);
        Assert.Equal(0, vm.Invoke("Vm.Raw", "StackAllocInitSum"));
    }

    // ---- TypedReference (__makeref / __refvalue / __reftype) ----

    [Fact]
    public void TypedReferenceRoundtrip_MatchesClr() =>
        AssertRawMatchesClr("TypedRefRoundtrip");
}
