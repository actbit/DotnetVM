using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Math (拡張 overload / JIT intrinsic 面) ----

    /// <summary>Math の拡張 overload 面。
    /// 本家 IL は double 丸め機構 (ModF InternalCall / fixed バッファ) と JIT intrinsic
    /// (BigMul / FusedMultiplyAdd 等) で構成されるため VM の表現境界。ホストの同一意味論
    /// へ委譲し、結果は VM スロットに正規化する。基本算術面 (Abs / Sqrt 等) は既存の
    /// 一般経路 (② IL 実行 / ③ legacy) のまま。</summary>
    private static void RegisterMathBindings(IntrinsicRegistry r) {
        const string T = "System.Math";
        const string Midpoint = "System.MidpointRounding";

        // ModF(double, out double&): 丸め核 (RoundNumber 内部 IL が呼ぶ InternalCall 面)。
        // 戻り = 整数部、out 参照先 = 小数部 (本家と同一の呼び出し規約)。
        // 本家筐体は QCall 相当の double* 署名で呼ぶため両形状を登録する
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ModF", "System.Double", ["System.Double", "System.Double&"]),
            static (_, a) => ModFImpl(a), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "ModF", "System.Double", ["System.Double", "System.Double*"]),
            static (_, a) => ModFImpl(a), BindingOrigin.InternalCall);

        // Round 系 overload (4 形状)。Math.Round(double, int) の既定中点規約 = AwayFromZero。
        // 第 2 引数が MidpointRounding か digits かはバインドキーのパラメータ型名 (IntrinsicContext) で判別
        static StackSlot RoundFaces(IntrinsicContext ctx, StackSlot[] a) => a.Length switch {
            1 => StackSlot.OfFloat(Math.Round(a[0].DoubleValue)),
            2 when ctx.ParamAt(1) == "System.MidpointRounding" =>
                StackSlot.OfFloat(Math.Round(a[0].DoubleValue, (System.MidpointRounding)a[1].AsInt32)),
            2 => StackSlot.OfFloat(Math.Round(a[0].DoubleValue, a[1].AsInt32)),
            _ => StackSlot.OfFloat(Math.Round(a[0].DoubleValue, a[1].AsInt32,
                (System.MidpointRounding)a[2].AsInt32)),
        };
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double"]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", "System.Int32"]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", Midpoint]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Round", "System.Double", ["System.Double", "System.Int32", Midpoint]),
            static (ctx, a) => RoundFaces(ctx, a), BindingOrigin.Managed);
        // Truncate は managed IL が TruncateNative (InternalCall) を辿るため直接ホスト面で受ける
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "Truncate", "System.Double", ["System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.Truncate(a[0].DoubleValue)), BindingOrigin.Managed);

        // JIT intrinsic / 内部面の残り (ホスト同一意味論)
        r.RegisterBinding(BindingKey.Static(T, "BigMul", ["System.Int32", "System.Int32"]),
            static (_, a) => StackSlot.OfInt64(Math.BigMul(a[0].AsInt32, a[1].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "ILogB", ["System.Double"]),
            static (_, a) => StackSlot.OfInt32(Math.ILogB(a[0].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "ScaleB", ["System.Double", "System.Int32"]),
            static (_, a) => StackSlot.OfFloat(Math.ScaleB(a[0].DoubleValue, a[1].AsInt32)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Sign", ["System.Double"]),
            static (_, a) => StackSlot.OfInt32(Math.Sign(a[0].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "CopySign", ["System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.CopySign(a[0].DoubleValue, a[1].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "MaxMagnitude", ["System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.MaxMagnitude(a[0].DoubleValue, a[1].DoubleValue)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "FusedMultiplyAdd", ["System.Double", "System.Double", "System.Double"]),
            static (_, a) => StackSlot.OfFloat(Math.FusedMultiplyAdd(a[0].DoubleValue, a[1].DoubleValue, a[2].DoubleValue)), BindingOrigin.Managed);
        // DivRem (左, 右) → (商, 余り) ValueTuple`2 面 (戻り = VM 構造体値)
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "DivRem", "System.ValueTuple`2", ["System.Int32", "System.Int32"]),
            static (ctx, a) => Tuple2Int32(ctx, a[0].AsInt32, a[1].AsInt32), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticWithReturn(T, "DivRem", "System.ValueTuple`2", ["System.Int64", "System.Int64"]),
            static (ctx, a) => Tuple2Int64(ctx, a[0].Int64Value, a[1].Int64Value), BindingOrigin.Managed);
    }

    private static StackSlot ModFImpl(StackSlot[] a) {
        var intPart = Math.Truncate(a[0].DoubleValue);
        if (a[1].ObjectValue is VmByRef byref)
            byref.Write(StackSlot.OfFloat(a[0].DoubleValue - intPart));
        return StackSlot.OfFloat(intPart);
    }

    /// <summary>Math.DivRem の (商, 余り) 戻り面。System.ValueTuple`2&lt;System.Int32,System.Int32&gt; の
    /// 構造体値 (Fields = Item1 / Item2 スロット) を構築して返す。</summary>
    private static StackSlot Tuple2Int32(IntrinsicContext ctx, int item1, int item2) {
        var (def, args) = Tuple2Type(ctx, "System.Int32")
            ?? throw new InvalidOperationException("System.ValueTuple`2 (CoreLib 実型) がロードされていません。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [args, args] };
        var fields = new StackSlot[2];
        fields[0] = StackSlot.OfInt32(item1);
        fields[1] = StackSlot.OfInt32(item2);
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }

    /// <summary>Math.DivRem(long,long) の (商, 余り) 戻り面 (i8 スロット)。</summary>
    private static StackSlot Tuple2Int64(IntrinsicContext ctx, long item1, long item2) {
        var (def, args) = Tuple2Type(ctx, "System.Int64")
            ?? throw new InvalidOperationException("System.ValueTuple`2 (CoreLib 実型) がロードされていません。");
        var constructed = new VmConstructedType { Definition = def, TypeArguments = [args, args] };
        var fields = new StackSlot[2];
        fields[0] = StackSlot.OfInt64(item1);
        fields[1] = StackSlot.OfInt64(item2);
        return StackSlot.OfValueType(new VmStructValue(constructed, fields));
    }

    /// <summary>ValueTuple`2 の定義型と実引数型 (確認。値型統合済み型) を解決する。</summary>
    private static (VmClassType Def, VmType Arg)? Tuple2Type(IntrinsicContext ctx, string argTypeName) {
        if (ctx.Types.FindTypeByFullName("System.ValueTuple`2") is not VmClassType def)
            return null;
        var arg = ctx.Types.FindTypeByFullName(argTypeName)
            ?? ctx.Types.ResolveWellKnownType(argTypeName)
            ?? throw new InvalidOperationException($"{argTypeName} が解決できません。");
        return (def, arg);
    }
}
