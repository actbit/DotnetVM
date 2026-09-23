using DotnetVM.CoreLib;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// DecimalCalc (本家 System.Decimal+DecCalc の DotnetVM.CoreLib 移植) の CLR 差分テスト。
/// このライブラリは普通の .NET クラスライブラリなので、ホスト CLR 上の decimal 演算と
/// 直接突合できる (「外でも使える」ことの証明と、VM 配線前の正当性担保)。
/// 境界値グリッド + シード固定のランダム 96 ビット mantissa × scale 0..28 の大量突合で、
/// スケール統一 / 丸め (half-even / OverflowUnscale) / 桁落ち / オーバーフロー分類まで一致させる。
/// VM 内実行経路の証明は CoreLibSurfaceGapProbeTests / CoreLibFacesIlTests 側で行う。
/// </summary>
public class VmCoreLibClrDecimalCalcTests {
    /// <summary>境界値グリッド (scale 0..28 / 桁落ち / 溢れ境界を含む)。</summary>
    public static IEnumerable<object[]> DecimalBoundaries() {
        var list = new List<decimal> {
            0m, 1m, -1m, 0.1m, -0.1m, 0.5m, 2.25m, 1.5m, 3.1415926535897932384626433833m,
            79228162514264337593543950335m, // decimal.MaxValue
            decimal.MaxValue / 2m, decimal.MaxValue / 3m, decimal.MinValue,
            0.0000000000000000000000000001m, // scale 28 の最小正数
            -0.0000000000000000000000000001m,
            1.000000000m, 123.4500m, 100.5m, 99.95m, 2.345m, 2.335m, 0.125m, 0.375m,
            7m, 0.5m, 1m / 3m, 2m / 3m, 1000000000m, 1000000000000000000m,
        };
        return list.Select(v => new object[] { v });
    }

    /// <summary>シード固定のランダム decimal (96 ビット mantissa 全範囲 × scale 0..28)。</summary>
    private static IEnumerable<decimal> RandomDecimals(int count, int seed) {
        var rnd = new Random(seed);
        for (var i = 0; i < count; i++) {
            var lo = rnd.Next();
            var mid = rnd.Next();
            var hi = rnd.Next();
            var scale = (byte)rnd.Next(29);
            yield return new decimal(lo, mid, hi, false, scale);
            var lo2 = rnd.Next();
            var mid2 = rnd.Next();
            var hi2 = rnd.Next();
            yield return new decimal(lo2, mid2, hi2, true, scale);
        }
    }

    /// <summary>決定的実行例外の型名 (双方一致突合用)。</summary>
    private static string ExnClass(Action run) {
        try {
            run();
            return "ok";
        } catch (Exception ex) {
            return ex.GetType().Name;
        }
    }

    // ---- 加減算 ----

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Add_Matches_Clr(decimal d) {
        foreach (var other in new[] { 0.5m, 2.25m, -1.25m, 79228162514264337593543950335m, 0.0000001m, 1000.0000005m }) {
            var clr = ExnClass(() => { _ = d + other; });
            var vm = ExnClass(() => { _ = DecimalCalc.Add(d, other); });
            if (clr == "ok") {
                Assert.Equal(d + other, DecimalCalc.Add(d, other));
            } else {
                Assert.Equal(clr, vm);
            }
        }
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Subtract_Matches_Clr(decimal d) {
        foreach (var other in new[] { 0.5m, 2.25m, -1.25m, decimal.MaxValue, 0.0000001m, 1000.0000005m }) {
            var clr = ExnClass(() => { _ = d - other; });
            var vm = ExnClass(() => { _ = DecimalCalc.Subtract(d, other); });
            if (clr == "ok")
                Assert.Equal(d - other, DecimalCalc.Subtract(d, other));
            else
                Assert.Equal(clr, vm);
        }
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Multiply_Matches_Clr(decimal d) {
        foreach (var other in new[] { 0.5m, 2m, -3m, 1m / 3m, 123.456789m, decimal.MaxValue / 2m }) {
            var clr = ExnClass(() => { _ = d * other; });
            var vm = ExnClass(() => { _ = DecimalCalc.Multiply(d, other); });
            if (clr == "ok")
                Assert.Equal(d * other, DecimalCalc.Multiply(d, other));
            else
                Assert.Equal(clr, vm);
        }
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Divide_Matches_Clr(decimal d) {
        foreach (var other in new[] { 0.5m, 2m, -3m, 7m, 1m / 3m, 123.456789m, 0.0000003m }) {
            var clr = ExnClass(() => { _ = d / other; });
            var vm = ExnClass(() => { _ = DecimalCalc.Divide(d, other); });
            if (clr == "ok")
                Assert.Equal(d / other, DecimalCalc.Divide(d, other));
            else
                Assert.Equal(clr, vm);
        }
        Assert.Equal("DivideByZeroException", ExnClass(() => { _ = DecimalCalc.Divide(d, 0m); }));
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Remainder_Matches_Clr(decimal d) {
        foreach (var other in new[] { 0.5m, 2m, -3m, 7m, 0.1m, 123.456789m }) {
            var clr = ExnClass(() => { _ = d % other; });
            var vm = ExnClass(() => { _ = DecimalCalc.Remainder(d, other); });
            if (clr == "ok")
                Assert.Equal(d % other, DecimalCalc.Remainder(d, other));
            else
                Assert.Equal(clr, vm);
        }
        Assert.Equal("DivideByZeroException", ExnClass(() => { _ = DecimalCalc.Remainder(d, 0m); }));
    }

    // ---- 丸め / 切り捨て ----

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Round_Matches_Clr(decimal d) {
        for (var decimals = 0; decimals <= 6; decimals++) {
            Assert.Equal(decimal.Round(d, decimals), DecimalCalc.Round(d, decimals));
        }
        foreach (var mode in Enum.GetValues<MidpointRounding>()) {
            Assert.Equal(decimal.Round(d, mode), DecimalCalc.Round(d, mode));
            Assert.Equal(decimal.Round(d, 2, mode), DecimalCalc.Round(d, 2, mode));
        }
        Assert.Equal("ArgumentOutOfRangeException", ExnClass(() => { _ = DecimalCalc.Round(d, 29); }));
        Assert.Equal("ArgumentOutOfRangeException", ExnClass(() => { _ = DecimalCalc.Round(d, -1); }));
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void Floor_Ceiling_Truncate_Match_Clr(decimal d) {
        Assert.Equal(decimal.Floor(d), DecimalCalc.Floor(d));
        Assert.Equal(decimal.Ceiling(d), DecimalCalc.Ceiling(d));
        Assert.Equal(decimal.Truncate(d), DecimalCalc.Truncate(d));
    }

    // ---- 変換 ----

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void ToInteger_Matches_Clr(decimal d) {
        var clr32 = ExnClass(() => { _ = (int)d; });
        var vm32 = ExnClass(() => { _ = DecimalCalc.ToInt32(d); });
        if (clr32 == "ok")
            Assert.Equal((int)d, DecimalCalc.ToInt32(d));
        else
            Assert.Equal("OverflowException", vm32);

        var clr64 = ExnClass(() => { _ = (long)d; });
        var vm64 = ExnClass(() => { _ = DecimalCalc.ToInt64(d); });
        if (clr64 == "ok")
            Assert.Equal((long)d, DecimalCalc.ToInt64(d));
        else
            Assert.Equal("OverflowException", vm64);

        var clru32 = ExnClass(() => { _ = (uint)d; });
        var vmu32 = ExnClass(() => { _ = DecimalCalc.ToUInt32(d); });
        if (clru32 == "ok")
            Assert.Equal((uint)d, DecimalCalc.ToUInt32(d));
        else
            Assert.Equal("OverflowException", vmu32);

        var clru64 = ExnClass(() => { _ = (ulong)d; });
        var vmu64 = ExnClass(() => { _ = DecimalCalc.ToUInt64(d); });
        if (clru64 == "ok")
            Assert.Equal((ulong)d, DecimalCalc.ToUInt64(d));
        else
            Assert.Equal("OverflowException", vmu64);
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void ToFloatingPoint_Matches_Clr(decimal d) {
        Assert.Equal((float)d, DecimalCalc.ToSingle(d));
        Assert.Equal((double)d, DecimalCalc.ToDouble(d));
    }

    // ---- 基本動作の診断 (最小ケース) ----

    [Fact]
    public void Diag_FindFirstMismatch() {
        var boundaries = new List<decimal> {
            0m, 1m, -1m, 0.1m, -0.1m, 0.5m, 2.25m, 1.5m, 3.1415926535897932384626433833m,
            79228162514264337593543950335m,
            decimal.MaxValue / 2m, decimal.MaxValue / 3m, decimal.MinValue,
            0.0000000000000000000000000001m, -0.0000000000000000000000000001m,
            1.000000000m, 123.4500m, 100.5m, 99.95m, 2.345m, 2.335m, 0.125m, 0.375m,
            7m, 0.5m, 1m / 3m, 2m / 3m, 1000000000m, 1000000000000000000m,
        };
        var others = new[] { 0.5m, 2.25m, -1.25m, decimal.MaxValue, 0.0000001m, 1000.0000005m };
        foreach (var d in boundaries) {
            foreach (var o in others) {
                var clrAdd = ExnClass(() => { _ = d + o; });
                var vmAdd = ExnClass(() => { _ = DecimalCalc.Add(d, o); });
                if (clrAdd != vmAdd || (clrAdd == "ok" && d + o != DecimalCalc.Add(d, o)))
                    Assert.Fail($"Add({d}, {o}) clr={clrAdd}/{(clrAdd == "ok" ? (d + o).ToString() : "")} vm={vmAdd}/{(vmAdd == "ok" ? DecimalCalc.Add(d, o).ToString() : "")}");
            }
        }
        foreach (var d in boundaries) {
            foreach (var o in new[] { 0.5m, 2m, -3m, 7m, 1m / 3m, 123.456789m, 0.0000003m }) {
                var clrDiv = ExnClass(() => { _ = d / o; });
                var vmDiv = ExnClass(() => { _ = DecimalCalc.Divide(d, o); });
                if (clrDiv != vmDiv || (clrDiv == "ok" && d / o != DecimalCalc.Divide(d, o)))
                    Assert.Fail($"Divide({d}, {o}) clr={clrDiv}/{(clrDiv == "ok" ? (d / o).ToString() : "")} vm={vmDiv}/{(vmDiv == "ok" ? DecimalCalc.Divide(d, o).ToString() : "")}");
            }
        }
        // 不一致が無ければこの診断テストは成功する (Add/Divide 全境界一致)。
        // かつては診断のため無条件 Assert.Fail していたが、移植バグ修正後に
        // 実クロスチェックとして機能するよう修正した
    }

    [Fact]
    public void Diag_Mul64x64() {
        // 5e18 × 1e9 = 5e27 の 128 ビット積 (本家 Math.BigMul との突合。BigMul の戻り = 上位 64、out = 下位 64)
        DecimalCalc.Mul64x64(5000000000000000000ul, 1000000000ul, out var low, out var high);
        var expectHigh = System.Math.BigMul(5000000000000000000ul, 1000000000ul, out var expectLow);
        if (expectLow != low || expectHigh != high)
            Assert.Fail($"Mul64x64(5e18, 1e9) lo {low} != {expectLow} / hi {high} != {expectHigh}");

        // ランダムな 64 ビット × 64 ビットの突合 (決定的シード。BigMul の戻り = 上位 64、out = 下位 64)
        var rnd = new Random(99);
        for (var i = 0; i < 1000; i++) {
            var a = ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next();
            var b = ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next();
            DecimalCalc.Mul64x64(a, b, out var lo2, out var hi2);
            var eHigh = System.Math.BigMul(a, b, out var eLow);
            if (eLow != lo2 || eHigh != hi2)
                Assert.Fail($"Mul64x64({a}, {b}) lo {lo2} != {eLow} / hi {hi2} != {eHigh}");
        }
    }

    // ---- 基本動作の診断 (最小ケース) ----

    [Fact]
    public void Diag_Basic() {
        Assert.Equal(3.75m, DecimalCalc.Add(1.5m, 2.25m));
        Assert.Equal(6m, DecimalCalc.Multiply(1.5m, 4m));
        Assert.Equal(3m, DecimalCalc.Divide(1.5m, 0.5m));
        Assert.Equal(1.25m, DecimalCalc.Subtract(1.5m, 0.25m));
        // ToEven (既定): 2.345 は 2.34/2.35 の正確な中点 → 偶数側 2.34
        Assert.Equal(decimal.Round(2.345m, 2), DecimalCalc.Round(2.345m, 2));
        Assert.Equal(1, DecimalCalc.ToInt32(1.5m));
        Assert.Equal(1.5, DecimalCalc.ToDouble(1.5m));
        Assert.Equal(-0.5m, DecimalCalc.Subtract(1.5m, 2m));
    }

    // ---- ランダム大量突合 (決定的シード) ----

    [Fact]
    public void Random_AddSubtract_Matches_Clr() {
        var values = RandomDecimals(300, 20260921).ToArray();
        foreach (var a in values) {
            var b = values[Array.IndexOf(values, a) ^ 1];
            foreach (var (x, y) in new[] { (a, b), (b, a), (a, a), (b, b) }) {
                var clrAdd = ExnClass(() => { _ = x + y; });
                var vmAdd = ExnClass(() => { _ = DecimalCalc.Add(x, y); });
                if (clrAdd == "ok")
                    Assert.Equal(x + y, DecimalCalc.Add(x, y));
                else
                    Assert.Equal(clrAdd, vmAdd);

                var clrSub = ExnClass(() => { _ = x - y; });
                var vmSub = ExnClass(() => { _ = DecimalCalc.Subtract(x, y); });
                if (clrSub == "ok")
                    Assert.Equal(x - y, DecimalCalc.Subtract(x, y));
                else
                    Assert.Equal(clrSub, vmSub);
            }
        }
    }

    [Fact]
    public void Random_MultiplyDivide_Matches_Clr() {
        var values = RandomDecimals(300, 921).ToArray();
        foreach (var a in values) {
            var b = values[Array.IndexOf(values, a) ^ 1];
            foreach (var (x, y) in new[] { (a, b), (b, a) }) {
                var clrMul = ExnClass(() => { _ = x * y; });
                var vmMul = ExnClass(() => { _ = DecimalCalc.Multiply(x, y); });
                if (clrMul == "ok" && x * y != DecimalCalc.Multiply(x, y))
                    Assert.Fail($"Multiply({x}, {y}) clr={x * y} vm={DecimalCalc.Multiply(x, y)}");
                if (clrMul != "ok" && clrMul != vmMul)
                    Assert.Fail($"Multiply({x}, {y}) clr={clrMul} vm={vmMul}");

                if (y != 0m) {
                    var clrDiv = ExnClass(() => { _ = x / y; });
                    var vmDiv = ExnClass(() => { _ = DecimalCalc.Divide(x, y); });
                    if (clrDiv == "ok" && x / y != DecimalCalc.Divide(x, y))
                        Assert.Fail($"Divide({x}, {y}) clr={x / y} vm={DecimalCalc.Divide(x, y)}");
                    if (clrDiv != "ok" && clrDiv != vmDiv)
                        Assert.Fail($"Divide({x}, {y}) clr={clrDiv} vm={vmDiv}");

                    var clrMod = ExnClass(() => { _ = x % y; });
                    var vmMod = ExnClass(() => { _ = DecimalCalc.Remainder(x, y); });
                    if (clrMod == "ok" && x % y != DecimalCalc.Remainder(x, y))
                        Assert.Fail($"Remainder({x}, {y}) clr={x % y} vm={DecimalCalc.Remainder(x, y)}");
                    if (clrMod != "ok" && clrMod != vmMod)
                        Assert.Fail($"Remainder({x}, {y}) clr={clrMod} vm={vmMod}");
                }
            }
        }
    }

    [Fact]
    public void Random_RoundMatches_Clr() {
        foreach (var d in RandomDecimals(500, 777)) {
            Assert.Equal(decimal.Round(d), DecimalCalc.Round(d));
            for (var decimals = 0; decimals <= 28; decimals += 5) {
                var clr = ExnClass(() => { _ = decimal.Round(d, decimals); });
                var vm = ExnClass(() => { _ = DecimalCalc.Round(d, decimals); });
                if (clr == "ok")
                    Assert.Equal(decimal.Round(d, decimals), DecimalCalc.Round(d, decimals));
                else
                    Assert.Equal(clr, vm);
            }
            Assert.Equal(decimal.Truncate(d), DecimalCalc.Truncate(d));
            Assert.Equal(decimal.Floor(d), DecimalCalc.Floor(d));
            Assert.Equal(decimal.Ceiling(d), DecimalCalc.Ceiling(d));
        }
    }

    [Fact]
    public void Random_Convert_Matches_Clr() {
        foreach (var d in RandomDecimals(500, 555)) {
            Assert.Equal((double)d, DecimalCalc.ToDouble(d));
            Assert.Equal((float)d, DecimalCalc.ToSingle(d));

            var clr32 = ExnClass(() => { _ = (int)d; });
            var vm32 = ExnClass(() => { _ = DecimalCalc.ToInt32(d); });
            if (clr32 == "ok")
                Assert.Equal((int)d, DecimalCalc.ToInt32(d));
            else
                Assert.Equal("OverflowException", vm32);

            var clr64 = ExnClass(() => { _ = (long)d; });
            var vm64 = ExnClass(() => { _ = DecimalCalc.ToInt64(d); });
            if (clr64 == "ok")
                Assert.Equal((long)d, DecimalCalc.ToInt64(d));
            else
                Assert.Equal("OverflowException", vm64);
        }
    }
}
