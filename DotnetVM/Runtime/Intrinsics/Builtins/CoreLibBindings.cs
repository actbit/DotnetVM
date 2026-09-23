using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// CoreLib 画像 (System.Private.CoreLib) の面に対するランタイムバインド。
/// fail-driven に発見した InternalCall 面 / IL 実行が表現境界で保留されている面を
/// 署名キー + BindingOrigin 付きで登録する。すべての実装は intrinsic 契約
/// (VM オブジェクトモデル正規化 / ヒープ計上 / デバイス・ゲートウェイ経由 I/O) に従う。
/// ジェネリック メソッドは開いたキー (!!n / !n) で 1 件登録し、実引数は
/// IntrinsicContext.ParameterTypeNames 経由で判別する。
/// </summary>
internal sealed class BuiltInCoreLibBindingProvider : ICoreLibBindingProvider {
    public void RegisterBindings(IntrinsicRegistry registry) => CoreLibBindings.RegisterAll(registry);
}

internal static partial class CoreLibBindings {
    private const string RuntimeHelpersType = "System.Runtime.CompilerServices.RuntimeHelpers";

    public static void RegisterAll(IntrinsicRegistry r) {
        RegisterString(r);
        RegisterStringInternals(r);
        RegisterRuntimeHelpers(r);
        RegisterVectorIntrinsics(r);
        RegisterEnvironmentAndMarshal(r);
        RegisterInterlockedBindings(r);
        RegisterObject(r);
        RegisterEnum(r);
        RegisterThreading(r);
        RegisterTaskBindings(r);
        RegisterComparableInterfaces(r);
        RegisterPrimitiveToString(r);
        RegisterDecimalBindings(r);
        RegisterTimeCultureFaces(r);
        RegisterMathBindings(r);
        RegisterSystemSr(r);
        RegisterThrowHelpers(r);
        RegisterArrayBindings(r);
        RegisterActivator(r);
        RegisterEqualityComparer(r);
        RegisterSpanFormattable(r);
        RegisterArrayPool(r);
        RegisterConvertBinary(r);
        RegisterGuid(r);
        RegisterAssembly(r);
        AssemblyLoadContextRuntime.RegisterBindings(r);
        RegisterExpressionTrees(r);
        RegisterReflectionEmit(r);
    }

    
    // ---- host 側 CPU コストの面共費 (タスク 2 hardening #7)。culture 比較 / TextInfo 書式面 /
    //      number formatting が intrinsic 内で走る際に、文字列の文字数近似での host work 予算
    //      (HostWorkBudget) を消費させる。budget 超過は MemoryQuotaExceededException (ゲスト外)。
    private static void ChargeHostWork(this IntrinsicContext ctx, long costUnits) =>
        ctx.Heap.ChargeHostWork(costUnits);

    // ---- host 側 CPU コストの計上 (タスク 2 hardening #7/#9)。culture / 書式面の
    // 重いホスト演算 (CompareInfo / TextInfo / Number.Formatting) を HostWorkBudget
    // (VmHeap.ChargeHostWork) に計上する (符号の作業量を文字数近似での計上)。
    // charge を 1 箇所で集中管理し、従来「intrinsic 毎に Sculptor」だった経路を統一。
    private static long HostWorkChars(string? a, string? b) => (long)a?.Length + b?.Length ?? 0;
}
