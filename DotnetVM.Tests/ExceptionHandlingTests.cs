using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M4 例外処理の突合テスト。try/catch/finally/filter/rethrow、VM 内部例外 (ゼロ除算/境界外/
/// null 参照/ovf/キャスト) のゲスト捕捉、finally 順序を VM 実行と CLR (反射) 実行で比べる。
/// </summary>
public class ExceptionHandlingTests {
    private const string Source = """
        using System;
        namespace Vm {
            public class MyException : Exception {
                public MyException(string message) : base(message) { }
            }
            public class DerivedException : MyException {
                public DerivedException(string message) : base(message) { }
            }
            public static class Logger {
                public static string Log = "";
            }
            public static class Eh {
                // ---- 基本の catch ----

                public static string CatchFacade() {
                    try { throw new InvalidOperationException("boom"); }
                    catch (InvalidOperationException ex) { return "caught:" + ex.Message; }
                }
                public static string CatchGuest() {
                    try { throw new MyException("mine"); }
                    catch (MyException ex) { return "mine:" + ex.Message; }
                }
                // 派生例外を基底型で catch
                public static string CatchAsBase() {
                    try { throw new DerivedException("sub"); }
                    catch (MyException ex) { return "base:" + ex.Message; }
                }
                // ゲスト例外を System.Exception で catch
                public static string CatchExceptionRoot() {
                    try { throw new MyException("root"); }
                    catch (Exception ex) { return "root:" + ex.Message; }
                }
                // 複数 catch 句は派生側から一致させる
                public static string MultipleCatches() {
                    try { throw new MyException("pick"); }
                    catch (DerivedException) { return "derived"; }
                    catch (MyException) { return "my"; }
                    catch (Exception) { return "ex"; }
                }
                // 型不一致の catch はスキップして外側へ
                public static string WrongTypeSkipped() {
                    try {
                        try { throw new MyException("skip"); }
                        catch (InvalidOperationException) { return "wrong"; }
                    } catch (MyException) { return "right"; }
                }

                // ---- finally ----

                public static string FinallyOrder() {
                    var log = "";
                    try {
                        try { log += "try;"; throw new MyException("x"); }
                        finally { log += "fin;"; }
                    } catch (MyException) { log += "catch;"; }
                    return log;
                }
                // 入れ子 finally は内側から順に通過してから catch へ
                public static string NestedFinallys() {
                    var log = "";
                    try {
                        try {
                            try { log += "t;"; throw new MyException("x"); }
                            finally { log += "f1;"; }
                        } finally { log += "f2;"; }
                    } catch (Exception) { log += "c;"; }
                    return log;
                }
                // 通常 return でも finally は実行される
                public static int FinallyOnReturn() {
                    Logger.Log = "";
                    try { return 42; }
                    finally { Logger.Log += "fin"; }
                }
                // leave (ループ脱出) でも finally は実行される
                public static string FinallyOnBreak() {
                    Logger.Log = "";
                    for (var i = 0; i < 3; i++) {
                        try {
                            if (i == 1) break;
                            Logger.Log += "i" + i + ";";
                        } finally { Logger.Log += "f" + i + ";"; }
                    }
                    return Logger.Log;
                }
                // finally 内の例外は元の例外を置き換えて伝播する
                public static string FinallyThrows() {
                    try {
                        try { throw new MyException("first"); }
                        finally { throw new InvalidOperationException("second"); }
                    } catch (InvalidOperationException ex) { return "fin:" + ex.Message; }
                }

                // ---- フレームをまたぐ伝播 ----

                public static string CrossFrame() {
                    try { Thrower("cross"); return "no"; }
                    catch (MyException ex) { return "cross:" + ex.Message; }
                }
                private static void Thrower(string m) { throw new MyException(m); }
                // 深いフレームから finally を通過して catch へ
                public static string DeepUnwind() {
                    Logger.Log = "";
                    try { Level1("deep"); return "no"; }
                    catch (MyException ex) { return Logger.Log + "c:" + ex.Message; }
                }
                private static void Level1(string m) {
                    try { Level2(m); } finally { Logger.Log += "L1;"; }
                }
                private static void Level2(string m) {
                    try { Thrower(m); } finally { Logger.Log += "L2;"; }
                }
                // rethrow (catch 内の裸 throw) は元の例外情報を保持する
                public static string Rethrow() {
                    try {
                        try { Thrower("orig"); }
                        catch (MyException) { throw; }
                    } catch (MyException ex) { return "re:" + ex.Message; }
                    return "no";
                }
                // 未処理のまま VM 外へ出る (ホストは UnhandledGuestException を受ける)
                public static string Uncaught() { Thrower("uncaught"); return "no"; }
                // 静的ログの読み取り (finally 実行検査用)
                public static string GetLog() => Logger.Log;
                // quota 超過サイズのアロケーション (管理例外の検査用)
                public static string BigAllocCaught() {
                    try { var a = new int[100000]; return "alloc:" + a.Length; }
                    catch (Exception) { return "caught"; }
                }

                // ---- filter (catch when) ----

                public static string FilterTaken() {
                    try { throw new MyException("hello"); }
                    catch (MyException ex) when (ex.Message == "hello") { return "filtered"; }
                    catch (MyException) { return "plain"; }
                }
                public static string FilterNotTaken() {
                    try { throw new MyException("other"); }
                    catch (MyException ex) when (ex.Message == "hello") { return "filtered"; }
                    catch (MyException) { return "plain"; }
                }

                // ---- VM 内部例外のゲスト捕捉 ----

                public static string DivideByZeroCaught() {
                    try { var x = 0; return "bad" + (10 / x); }
                    catch (DivideByZeroException) { return "dz"; }
                }
                public static string BoundsCaught() {
                    try { var a = new int[2]; return "bad" + a[9]; }
                    catch (IndexOutOfRangeException) { return "oob"; }
                }
                public static string NullCaught() {
                    try { string s = null; return "bad" + s.Length; }
                    catch (NullReferenceException) { return "nre"; }
                }
                public static string OverflowCaught() {
                    try { var x = int.MaxValue; return "bad" + checked(x + 1); }
                    catch (OverflowException) { return "ovf"; }
                }
                public static string CastCaught(object o) {
                    try { return "bad" + (int)o; }
                    catch (InvalidCastException) { return "cast"; }
                }
                // Exception で全部まとめて捕捉できる
                public static string InternalCaughtAsException() {
                    try { var a = new int[1]; return "bad" + a[5]; }
                    catch (Exception) { return "ex"; }
                }
                // catch 後も実行は継続する
                public static string ContinueAfterCatch(int a, int b) {
                    var sum = 0;
                    for (var i = 0; i < 3; i++) {
                        try { sum += a / (i == 1 ? 0 : b); }
                        catch (DivideByZeroException) { sum += 100; }
                    }
                    return "sum=" + sum;
                }

                // ---- 例外オブジェクトの面 ----

                // 既定メッセージ (clr: Exception of type 'X' was thrown.)
                public static string DefaultMessage() {
                    try { throw new MyException("x"); }
                    catch (Exception ex) { return ex.Message; }
                }
                // 既定 ToString (clr: Vm.MyException: x)
                public static string ExceptionToString() {
                    try { throw new MyException("x"); }
                    catch (Exception ex) { return ex.ToString(); }
                }
                public static string FacadeToString() {
                    try { throw new InvalidOperationException("ops"); }
                    catch (Exception ex) { return ex.ToString(); }
                }
                // 型検査 (isinst)
                public static string CatchAndCheckType(bool mine) {
                    try { throw mine ? (Exception)new MyException("x") : (Exception)new InvalidOperationException("y"); }
                    catch (Exception ex) { return ex is MyException ? "my" : "other"; }
                }
            }
        }
        """;

    private static (Assembly Clr, byte[] Bytes) Compile() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source);
        return (Assembly.Load(bytes), bytes);
    }

    private static VirtualMachine CreateVm(long? allocationLimit = null) {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = new MemoryPolicy {
                InstructionQuota = 100_000_000,
                TotalAllocationByteLimit = allocationLimit ?? long.MaxValue,
            },
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    private static object? RunClr(string typeName, string method, params object?[] args) {
        var (clr, _) = Compile();
        var type = clr.GetType("Vm." + typeName)!;
        return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);
    }

    private static void AssertMatchesClr(string typeName, string method, params object?[] args) {
        Assert.Equal(RunClr(typeName, method, args), CreateVm().Invoke("Vm." + typeName, method, args));
    }

    // ---- 基本 ----

    [Fact]
    public void Catch_FacadeException_MatchesClr() {
        AssertMatchesClr("Eh", "CatchFacade");
        Assert.Equal("caught:boom", CreateVm().Invoke("Vm.Eh", "CatchFacade"));
    }

    [Fact]
    public void Catch_GuestException_MatchesClr() => AssertMatchesClr("Eh", "CatchGuest");

    [Fact]
    public void Catch_AsBaseType_MatchesClr() => AssertMatchesClr("Eh", "CatchAsBase");

    [Fact]
    public void Catch_ExceptionRoot_MatchesClr() => AssertMatchesClr("Eh", "CatchExceptionRoot");

    [Fact]
    public void MultipleCatches_MatchesClr() => AssertMatchesClr("Eh", "MultipleCatches");

    [Fact]
    public void WrongTypeSkipped_MatchesClr() => AssertMatchesClr("Eh", "WrongTypeSkipped");

    // ---- finally ----

    [Fact]
    public void FinallyOrder_MatchesClr() {
        AssertMatchesClr("Eh", "FinallyOrder");
        Assert.Equal("try;fin;catch;", CreateVm().Invoke("Vm.Eh", "FinallyOrder"));
    }

    [Fact]
    public void NestedFinallys_MatchesClr() {
        AssertMatchesClr("Eh", "NestedFinallys");
        Assert.Equal("t;f1;f2;c;", CreateVm().Invoke("Vm.Eh", "NestedFinallys"));
    }

    [Fact]
    public void FinallyOnReturn_MatchesClr() {
        Assert.Equal(RunClr("Eh", "FinallyOnReturn"), CreateVm().Invoke("Vm.Eh", "FinallyOnReturn"));
        using var vm = CreateVm();
        Assert.Equal(42, vm.Invoke("Vm.Eh", "FinallyOnReturn"));
        // return 後も finally 本体が実行されている (静的ログで検査)
        Assert.Equal("fin", vm.Invoke("Vm.Eh", "GetLog"));
    }

    [Fact]
    public void FinallyOnBreak_MatchesClr() {
        AssertMatchesClr("Eh", "FinallyOnBreak");
        Assert.Equal("i0;f0;f1;", CreateVm().Invoke("Vm.Eh", "FinallyOnBreak"));
    }

    [Fact]
    public void FinallyThrows_MatchesClr() => AssertMatchesClr("Eh", "FinallyThrows");

    // ---- フレームをまたぐ伝播 ----

    [Fact]
    public void CrossFrame_MatchesClr() {
        AssertMatchesClr("Eh", "CrossFrame");
        Assert.Equal("cross:cross", CreateVm().Invoke("Vm.Eh", "CrossFrame"));
    }

    [Fact]
    public void DeepUnwind_MatchesClr() {
        AssertMatchesClr("Eh", "DeepUnwind");
        Assert.Equal("L2;L1;c:deep", CreateVm().Invoke("Vm.Eh", "DeepUnwind"));
    }

    [Fact]
    public void Rethrow_MatchesClr() {
        AssertMatchesClr("Eh", "Rethrow");
        Assert.Equal("re:orig", CreateVm().Invoke("Vm.Eh", "Rethrow"));
    }

    // ---- filter ----

    [Fact]
    public void FilterTaken_MatchesClr() {
        AssertMatchesClr("Eh", "FilterTaken");
        Assert.Equal("filtered", CreateVm().Invoke("Vm.Eh", "FilterTaken"));
    }

    [Fact]
    public void FilterNotTaken_MatchesClr() {
        AssertMatchesClr("Eh", "FilterNotTaken");
        Assert.Equal("plain", CreateVm().Invoke("Vm.Eh", "FilterNotTaken"));
    }

    // ---- VM 内部例外 ----

    [Fact]
    public void DivideByZeroCaught_MatchesClr() {
        AssertMatchesClr("Eh", "DivideByZeroCaught");
        Assert.Equal("dz", CreateVm().Invoke("Vm.Eh", "DivideByZeroCaught"));
    }

    [Fact]
    public void BoundsCaught_MatchesClr() => AssertMatchesClr("Eh", "BoundsCaught");

    [Fact]
    public void NullCaught_MatchesClr() => AssertMatchesClr("Eh", "NullCaught");

    [Fact]
    public void OverflowCaught_MatchesClr() => AssertMatchesClr("Eh", "OverflowCaught");

    [Fact]
    public void CastCaught_MatchesClr() => AssertMatchesClr("Eh", "CastCaught", "ab");

    [Fact]
    public void InternalCaughtAsException_MatchesClr() => AssertMatchesClr("Eh", "InternalCaughtAsException");

    [Fact]
    public void ContinueAfterCatch_MatchesClr() {
        AssertMatchesClr("Eh", "ContinueAfterCatch", 7, 2);
        // 7 + 100 (ゼロ除算を補正) + 7
        Assert.Equal("sum=106", CreateVm().Invoke("Vm.Eh", "ContinueAfterCatch", 7, 2));
    }

    // ---- 例外オブジェクトの面 ----

    [Fact]
    public void DefaultMessage_MatchesClr() => AssertMatchesClr("Eh", "DefaultMessage");

    // 例外 ToString は CLR ではスタックトレースも含むため VM 単独で検査する
    [Fact]
    public void ExceptionToString_MatchesClr() {
        using var vm = CreateVm();
        Assert.Equal("Vm.MyException: x", vm.Invoke("Vm.Eh", "ExceptionToString"));
        Assert.Equal("System.InvalidOperationException: ops", vm.Invoke("Vm.Eh", "FacadeToString"));
    }

    [Fact]
    public void CatchAndCheckType_MatchesClr() {
        AssertMatchesClr("Eh", "CatchAndCheckType", true);
        AssertMatchesClr("Eh", "CatchAndCheckType", false);
    }

    // ---- 未処理/管理例外 ----

    [Fact]
    public void UncaughtGuestException_ReportsTypeNameAndMessage() {
        using var vm = CreateVm();
        var ex = Assert.Throws<UnhandledGuestException>(() => vm.Invoke("Vm.Eh", "Uncaught"));
        Assert.Equal("Vm.MyException", ex.ExceptionTypeName);
        Assert.Equal("uncaught", ex.GuestMessage);
    }

    [Fact]
    public void QuotaException_NotCaughtByGuest() {
        using var vm = CreateVm(allocationLimit: 1_000);
        // ゲストが catch (Exception) してもメモリ quota は管理例外として最上位まで伝播する
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("Vm.Eh", "BigAllocCaught"));
    }
}
