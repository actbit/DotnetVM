using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    /// <summary>指定の intrinsic が未実装であることを示すエラー (未実装面の明示)。</summary>
    private static StackSlot? NotImplemented(string name) =>
        throw new NotSupportedException($"intrinsic '{name}' は実装されていません (未実装面の明示)。");

    // ---- System.Object ----
    // C5.5 Wave 4 確定: ToString() / Equals ×2 の本家本体は managed IL のみ
    // (ToString は GetType() 呼び / Equals は参照比較 + 仮想呼び) で Object は IlPreferred のため
    // CoreLib ロード時は ② IL 本体が常に先に実行される。以下の intrinsic は CoreLib 未ロード時の
    // 代替経路 (VM ランタイム オブジェクト向けの特殊化を含む)

    private static void RegisterObject(IntrinsicRegistry r) {
        const string T = "System.Object";
        r.Register(IntrinsicKey.Instance(T, ".ctor", 0), static (_, _) => null);
        r.Register(IntrinsicKey.Instance(T, "ToString", 0), static (ctx, a) => {
            var s = new Args(a);
            return s[0].ObjectValue switch {
                VmString str => StackSlot.OfObject(str),
                VmBoxedValue boxed => StackSlot.OfObject(ctx.MakeString(FormatBoxed(boxed))),
                // CLR の既定 ToString は型の完全名を返す
                VmClassInstance ci => StackSlot.OfObject(ctx.MakeString(DefaultToString(ctx, ci))),
                VmExceptionObject e => StackSlot.OfObject(ctx.MakeString(FormatExceptionText(e.Type.FullName, e.Message))),
                VmArray arr => StackSlot.OfObject(ctx.MakeString(arr.ArrayType.FullName)),
                _ => NotImplemented("System.Object::ToString"),
            };
        });
        r.Register(IntrinsicKey.Instance(T, "Equals", 1), static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(ReferenceEquals(s[0].ObjectValue, s[1].ObjectValue) ? 1 : 0);
        });
        r.Register(IntrinsicKey.Static(T, "Equals", 2), static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(ReferenceEquals(s[0].ObjectValue, s[1].ObjectValue) ? 1 : 0);
        });
        r.Register(IntrinsicKey.Instance(T, "GetHashCode", 0), static (ctx, a) =>
            StackSlot.OfInt32(ctx.IdentityHash(a[0].ObjectValue)));
        // Object.GetType() は C5.5 Wave 4 で legacy intrinsic を廃止:
        // ① バインド (CoreLibBindings.RegisterObject) がロード / 未ロードを問わず常に先に
        // 解決するため、この intrinsic は到達しない死キーだった
    }
}
