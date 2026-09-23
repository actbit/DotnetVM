using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.SR (CoreLib 内部リソース文字列: 例外既定文言の culture インフラ) ----

    // CoreLib の実 IL は例外生成時に SR.Overflow_Int32 等のリソース getter を辿る
    // (GetResourceString → ResourceManager → culture 機構)。VM では例外文言の culture 機構は
    // スコープ外 (不変カルチャ規約) のため、ホストが動いている同一 CoreLib から同一キーの
    // 資源文字列を取得して委譲する (ゲストに文字列として渡すだけ = I/O なし)

    private static void RegisterSystemSr(IntrinsicRegistry r) {
        const string T = "System.SR";
        r.RegisterBinding(BindingKey.Static(T, "GetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "InternalGetResourceString", "System.String"),
            static (ctx, a) => ResourceString(ctx, a),
            BindingOrigin.Managed);
        // リソースキー直返しモードのスイッチ値 (AppContext 機構には依存しない = false 固定)
        r.RegisterBinding(BindingKey.Static(T, "UsingResourceKeys"),
            static (_, _) => StackSlot.OfInt32(0),
            BindingOrigin.Managed);
    }

    private static readonly Func<string, string?> HostCoreLibResource = CreateHostResourceLookup();

    private static Func<string, string?> CreateHostResourceLookup() {
        var sr = typeof(object).Assembly.GetType("System.SR", throwOnError: false);
        var method = sr?.GetMethod("GetResourceString",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (method is null)
            return _ => null; // CoreLib 未配置環境ではキー直返し (UsingResourceKeys 相当) にフォールバック
        return key => {
            try {
                return method.Invoke(null, [key]) as string;
            } catch {
                return null;
            }
        };
    }

    private static StackSlot ResourceString(IntrinsicContext ctx, StackSlot[] a) {
        var key = a[0].ObjectValue as VmString ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
        var text = HostCoreLibResource(key.Value) ?? key.Value;
        return StackSlot.OfObject(ctx.MakeString(text));
    }


    // ---- System.ArgumentOutOfRangeException.ThrowIfNegative (ジェネリック検証面) ----

    /// <summary>検証ヘルパー ThrowIfNegative&lt;T&gt;(T[, string?]) の同等意味論。
    /// 本家 IL は constrained T + INumberBase&lt;T&gt;.IsNegative (static abstract) を辿るが、
    /// VM には static abstract ディスパッチ機構がなく、抽象宣言に着地して fail-closed になる。
    /// 検証の観測意味論 (負なら ArgumentOutOfRangeException) を直接提供する。
    /// -0.0 の符号ビットも CLR どおり負と判定する (単なる &lt; 0 比較では再現できない)。
    /// .NET 10 の既知署名は (T, string?) の 1 形状 (1 引数版は存在しない)。</summary>
    private static void RegisterThrowHelpers(IntrinsicRegistry r) {
        const string T = "System.ArgumentOutOfRangeException";
        r.RegisterBinding(BindingKey.Static(T, "ThrowIfNegative", "!!0", "System.String"),
            static (_, a) => {
                if (IsNegativeValue(a[0]))
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                return null;
            },
            BindingOrigin.InternalCall);
    }

    private static bool IsNegativeValue(in StackSlot slot) => slot.Kind switch {
        StackKind.Int32 => slot.Int64Value < 0,
        StackKind.Int64 => slot.Int64Value < 0,
        StackKind.NativeInt => slot.Int64Value < 0,
        StackKind.Float => BitConverter.DoubleToInt64Bits(slot.DoubleValue) < 0,
        _ => throw new InvalidOperationException(
            $"ThrowIfNegative の引数型 ({slot.Kind}) は数値検証に対応していません。"),
    };
}
