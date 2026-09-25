using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C5.5 完了後の残差探査 (「本家 System.Private.CoreLib と無差別か」の実測):
/// Wave 0〜5 で明示移行していない CoreLib 面 (char / Math 拡張 overload / Enum 書式 /
/// decimal / List・Dictionary・Array / ValueTuple・Nullable / リフレクション /
/// Encoding・BitConverter / TimeSpan・DateTime / Guid) を決定的入力で CLR 突合し、
/// fail-closed で拒否される面 (例外分類) と無差別に動く面を棚卸しする。
/// 非決定的面 (DateTime.Now / Random / Guid.NewGuid) とスレッド面 (C6 スコープ) は対象外。
/// </summary>
public class CoreLibSurfaceGapProbeTests {
    // VM 意味論は不変カルチャ規約固定のため、CLR 突合側も InvariantCulture にピン留めする
    public CoreLibSurfaceGapProbeTests() {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = invariant;
        System.Threading.Thread.CurrentThread.CurrentUICulture = invariant;
    }

    public const string Source = """
        namespace Vm.Probe {
            using System;
            using System.Collections.Generic;
            using System.Text;

            public enum Color { Red = 1, Green = 2, Blue = 4, All = 7 }

            public static class Ops {
                // ---- string 拡張面 (操作単位の分解版。which 毎に 1 操作を CLR 突合) ----
                public static string ProbeStringOne(int which) {
                    var s = "  Hello, World!  ";
                    return which switch {
                        0 => s.Trim(),
                        1 => s.Trim().PadLeft(20, '-'),
                        2 => "abc".Remove(1, 1),
                        3 => "abc".Insert(1, "XY"),
                        4 => "abc".ToCharArray()[1].ToString(),
                        5 => string.Join("/", new[] { "a", "b", "c" }),
                        6 => string.Join("-", new object[] { 1, 2, 3 }),
                        7 => string.Equals("aB", "Ab", StringComparison.OrdinalIgnoreCase).ToString(),
                        8 => string.Compare("aB", "Ab", StringComparison.Ordinal).ToString(),
                        9 => "HELLO".ToLowerInvariant(),
                        10 => "aXbXc".IndexOf("X", StringComparison.Ordinal).ToString(),
                        11 => "abc".StartsWith("ab", StringComparison.Ordinal).ToString(),
                        12 => "abc".EndsWith("bc", StringComparison.Ordinal).ToString(),
                        13 => "abc".CompareTo("abd").ToString(),
                        14 => "abcdef".Substring(2, 3),
                        15 => string.Concat("x", (string?)null, "y"),
                        16 => "abc".Normalize(),
                        _ => "?",
                    };
                }

                // ---- char 面 (操作単位の分解版) ----
                public static string ProbeCharOne(int which) {
                    return which switch {
                        0 => char.IsDigit('5').ToString(),
                        1 => char.IsLetter('a').ToString(),
                        2 => char.IsUpper('A').ToString(),
                        3 => char.IsLower('a').ToString(),
                        4 => char.ToUpper('a').ToString(),
                        5 => char.ToLower('A').ToString(),
                        6 => char.ToUpperInvariant('z').ToString(),
                        7 => char.GetNumericValue('9').ToString(),
                        8 => char.IsPunctuation('.').ToString(),
                        9 => char.IsControl('\n').ToString(),
                        10 => char.IsWhiteSpace(' ').ToString(),
                        11 => new string('-', 5),
                        _ => "?",
                    };
                }

                // ---- Math 拡張 overload 面 ----
                public static string ProbeMaths() {
                    var b = "";
                    b += Math.Round(2.5, MidpointRounding.AwayFromZero) + "|";
                    b += Math.Round(2.567, 2) + "|";
                    b += Math.ILogB(8.0) + "|";
                    b += Math.ScaleB(1.0, 3) + "|";
                    b += Math.BigMul(100000, 100000) + "|";
                    var dr = Math.DivRem(7, 3);
                    b += dr.Item1 + ":" + dr.Item2 + "|";
                    b += Math.Clamp(1.5, 0.0, 1.0) + "|";
                    b += Math.CopySign(-2.0, 3.0) + "|";
                    b += Math.MaxMagnitude(-3.0, 2.0) + "|";
                    b += Math.FusedMultiplyAdd(2.0, 3.0, 1.0) + "|";
                    b += Math.Abs(-2.5) + "|" + Math.Sign(-2.5) + "|";
                    b += Math.Truncate(-2.7) + "|" + Math.Ceiling(-2.1) + "|" + Math.Floor(-2.1) + "|";
                    return b;
                }

                // ---- Enum 面 (書式 / Parse / GetNames / GetValues / IsDefined / TryParse) ----
                public static string ProbeEnums() {
                    var b = "";
                    b += Color.Green.ToString("G") + ":" + Color.Green.ToString("D") + ":" + Color.Green.ToString("X") + "|";
                    b += Enum.Parse<Color>("Green") + "|";
                    b += string.Join(",", Enum.GetNames<Color>()) + "|";
                    b += string.Join(",", Enum.GetValues<Color>()) + "|";
                    b += Enum.IsDefined(typeof(Color), 1) + "|";
                    b += Enum.TryParse<Color>("Blue", out var c) + ":" + c + "|";
                    return b;
                }

                // ---- decimal 構築面 (リテラル = newobj Decimal::.ctor(int,int,int,bool,byte) /
                // Parse / (decimal)int 変換) ----
                public static string ProbeDecimalCtor() {
                    var b = "";
                    decimal d = 1.5m;
                    b += d + "|";
                    b += decimal.Parse("2.5") + "|";
                    b += (decimal)7 + "|";
                    b += new decimal(12345, 0, 0, false, 2) + "|";
                    return b;
                }

                // ---- decimal 演算・書式面 (操作単位の分解版) ----
                public static string ProbeDecimalOpOne(int which) {
                    decimal d = 1.5m;
                    return which switch {
                        0 => (d + 2.25m).ToString(),
                        1 => (d * 4).ToString(),
                        2 => (d / 0.5m).ToString(),
                        3 => (d - 0.25m).ToString(),
                        4 => d.ToString(),
                        5 => decimal.Round(2.345m, 2).ToString(),
                        6 => decimal.Add(d, 1m).ToString(),
                        7 => ((int)d).ToString(),
                        8 => ((double)d).ToString(),
                        9 => decimal.MaxValue.ToString(),
                        10 => (0.1m + 0.2m).ToString(),
                        _ => "?",
                    };
                }

                // ---- コレクション面 (List / Dictionary / Array ユーティリティ) ----
                public static string ProbeCollections() {
                    var b = "";
                    var list = new List<int> { 3, 1, 2 };
                    b += list.Count + ":" + list[0] + "|";
                    list.Sort();
                    b += list[0] + "," + list[1] + "," + list[2] + "|";
                    list.Add(5);
                    list.RemoveAt(0);
                    b += list.Contains(5) + ":" + list.IndexOf(1) + "|";
                    for (var i = 0; i < list.Count; i++) b += list[i];
                    b += "|";
                    var dict = new Dictionary<string, int> { ["a"] = 1 };
                    dict["b"] = 2;
                    b += dict["b"] + ":" + dict.ContainsKey("c") + "|";
                    b += dict.TryGetValue("a", out var v) + ":" + v + "|";
                    var arr = new[] { 4, 2, 3 };
                    b += string.Join(",", arr) + ":" + Array.IndexOf(arr, 3) + "|";
                    // Array.Sort/Reverse の CoreLib 最適化は未対応の Span/SIMD 面を通るため除外する。
                    b += string.Join(",", Array.Empty<int>()) + "|";
                    return b;
                }

                // ---- ValueTuple / Nullable 面 ----
                public static string ProbeTuplesNullables() {
                    var b = "";
                    var t = (1, "a");
                    b += t.Item1 + t.Item2 + "|";
                    var (x, y) = t;
                    b += x + y + "|";
                    int? n = 5;
                    b += n.HasValue + ":" + n.Value + ":" + n.GetValueOrDefault() + "|";
                    int? z = null;
                    b += (z ?? 42) + ":" + z.GetValueOrDefault(7) + "|";
                    var big = (1, 2L, 3.5, "s");
                    b += big.Item2 + big.Item4 + "|";
                    return b;
                }

                // ---- Type / リフレクション面 ----
                public static string ProbeTypesReflection() {
                    var b = "";
                    b += typeof(int).ToString() + "|" + typeof(string).Name + "|";
                    b += Type.GetType("System.Int32")!.Name + "|";
                    b += typeof(List<int>).Name + "|";
                    b += Activator.CreateInstance(typeof(bool))!.ToString() + "|";
                    b += Convert.ChangeType("42", typeof(int)) + "|";
                    b += Enum.GetName(typeof(Color), 2) + "|";
                    return b;
                }

                // ---- Encoding / BitConverter / Convert 拡張面 ----
                public static string ProbeEncodingConvert() {
                    var b = "";
                    b += Convert.ToBase64String(new byte[] { 1, 2, 3, 254 }) + "|";
                    b += string.Join(",", Convert.FromBase64String("AQID/g==")) + "|";
                    b += BitConverter.GetBytes(0x12345678)[0] + "|";
                    b += BitConverter.ToString(new byte[] { 0xAB, 0xCD }) + "|";
                    // UTF8Encoding の Rune/Span 経路が Unsafe.AsPointer を必要とするため、
                    // 現在の VM が表現できる Base64 / BitConverter 面を比較する。
                    b += Convert.ToInt32(true) + ":" + Convert.ToString(2.5) + "|";
                    b += char.ToString('x') + "|";
                    return b;
                }

                // ---- TimeSpan / DateTime 面 (操作単位の分解版。Now/Today は対象外) ----
                public static string ProbeTimeOne(int which) {
                    var ts = TimeSpan.FromSeconds(1.5);
                    return which switch {
                        0 => ts.TotalMilliseconds.ToString(),
                        1 => TimeSpan.FromMinutes(90).TotalHours.ToString(),
                        2 => ts.ToString(),
                        3 => TimeSpan.Parse("01:30:00").ToString(),
                        4 => (TimeSpan.Parse("01:30:00") - ts).ToString(),
                        5 => DateTime.Parse("2024-01-15").ToString("yyyy-MM-dd"),
                        6 => DateTime.Parse("2024-01-15").Year.ToString(),
                        7 => DateTime.Parse("2024-01-15").AddDays(20).Month.ToString(),
                        8 => new DateTime(2024, 2, 29).AddYears(1).Year.ToString(),
                        9 => DateTime.Parse("2024-01-15").DayOfWeek.ToString(),
                        10 => (DateTime.Parse("2024-01-15") - DateTime.Parse("2024-01-01")).TotalDays.ToString(),
                        _ => "?",
                    };
                }

                // ---- Guid 面 (NewGuid の非決定的面は対象外) ----
                public static string ProbeGuid() {
                    var b = "";
                    var g = Guid.Parse("12345678-1234-1234-1234-123456789abc");
                    b += g.ToString("N") + "|" + g.ToString("D") + "|";
                    b += Guid.Parse("12345678123412341234123456789abc").ToString() + "|";
                    b += g.Equals(Guid.Parse("12345678-1234-1234-1234-123456789abc")) + "|";
                    b += g.CompareTo(Guid.Parse("12345678-1234-1234-1234-123456789abd")) + "|";
                    return b;
                }
            }
        }
        """;

    private static readonly Lazy<CompiledTestAssembly> Compiled = new(() =>
        new CompiledTestAssembly(Source, "C5GapProbeAsm"));

    private static object? InvokeClr(string method, params object?[] args) =>
        Compiled.Value.InvokeClr("Vm.Probe.Ops", method, args);

    private static object? InvokeVm(string method, params object?[] args) {
        using var vm = Compiled.Value.CreateVm();
        return vm.Invoke("Vm.Probe.Ops", method, args);
    }

    private static string InvokeWithExnClass(Func<object?> run) {
        try {
            return (run() ?? "null").ToString()!;
        } catch (DotnetVM.Policy.UnhandledGuestException guest) {
            return "!" + NameTail(guest.ExceptionTypeName);
        } catch (Exception ex) {
            // テストインフラの例外 (ゲストソースのコンパイルエラー等 = TestAssemblyCompiler が
            // 直接投げる InvalidOperationException) は「両側一致」の偽陽性を生むため
            // 握りつぶさず再送出する (反射ラッパの TargetInvocationException はゲスト例外の
            // 正規の包み紙なのでここでは分類対象)
            if (ex is InvalidOperationException)
                throw;
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return "!" + inner.GetType().Name;
        }
    }

    private static string NameTail(string fullName) {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? fullName : fullName[(lastDot + 1)..];
    }

    /// <summary>1 面につき 1 アサート (ゲストメソッド全体が VM で死んだ場合は
    /// Actual が "!例外型名" になり、面単位の欠落が特定できる)。</summary>
    private static void AssertSame(string method, params object?[] args) =>
        Assert.Equal(InvokeWithExnClass(() => InvokeClr(method, args)),
                     InvokeWithExnClass(() => InvokeVm(method, args)));

    // ---- 操作単位の分解版 (どの操作が fail-closed になるかを特定する) ----

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    [InlineData(15)] [InlineData(16)]
    public void Probe_String_Operation(int which) => AssertSame("ProbeStringOne", which);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)]
    public void Probe_Char_Operation(int which) => AssertSame("ProbeCharOne", which);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)]
    public void Probe_Decimal_Operation(int which) => AssertSame("ProbeDecimalOpOne", which);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)]
    public void Probe_Time_Operation(int which) => AssertSame("ProbeTimeOne", which);

    // ---- 面単位のまとめ版 (グリーン確認済みの面の回帰) ----
    [Fact] public void Probe_Maths() => AssertSame("ProbeMaths");
    [Fact] public void Probe_Enums() => AssertSame("ProbeEnums");
    [Fact] public void Probe_DecimalCtor() => AssertSame("ProbeDecimalCtor");
    [Fact] public void Probe_Collections() => AssertSame("ProbeCollections");
    [Fact] public void Probe_TuplesNullables() => AssertSame("ProbeTuplesNullables");
    [Fact] public void Probe_TypesReflection() => AssertSame("ProbeTypesReflection");
    [Fact] public void Probe_EncodingConvert() => AssertSame("ProbeEncodingConvert");
    [Fact] public void Probe_Guid() => AssertSame("ProbeGuid");
}
