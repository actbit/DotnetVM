using DotnetVM.Host;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Intrinsics;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// C5.5「載っていない面をゼロにする」の監査闭合テスト。
/// VM に登録された全 intrinsic / ランタイムバインド面が CoreLibSurfaceAudit に
/// 分類 + 正当化付きで載っていることを双方向で検査し、未分類の委譲面の混入を構造的に防ぐ:
/// - 順方向: 登録済みの全キーが監査表に載っていること (新規登録者は監査表への記載を強制される)
/// - 逆方向: 監査表の全エントリに対応する登録キーが実在すること
///   (監査表だけ載って実体が無い = ④ fail-closed に落ちる面の検出)
/// - シャドウ禁止: 置換面 (b) (VmCoreLibSurfaces.Faces) に署名一致するバインドが
///   登録されていないこと (① が (b) 置換面を塞ぐ退行の防止)
/// - (a) RealCoreLibIl 分類の委譲キーは残置 (shadowed-legacy) であること
///   (実 IL が実際に実行される面に到達しうる委譲を置かない)
/// </summary>
public class CoreLibSurfaceAuditTests {
    /// <summary>監査対象の VM (起動時に既定の intrinsic / バインドを全登録した状態)。</summary>
    private static (VirtualMachine Vm, List<IntrinsicKey> Keys, List<(BindingKey Key, BindingOrigin Origin)> Bindings)
        CreateAudited() {
        var vm = new VirtualMachine(new VmHostOptions());
        return (vm, [.. vm.IntrinsicKeys], [.. vm.Bindings]);
    }

    /// <summary>IntrinsicKey の宣言パラメータ数 (this を除く)。</summary>
    private static int ParamCount(IntrinsicKey key) => key.HasThis ? key.Arity - 1 : key.Arity;

    /// <summary>BindingKey の宣言パラメータ数 (全引数一致面は null。無パラメータ面は 0)。</summary>
    private static int? ParamCount(BindingKey key) =>
        key.IsAnyParams ? null : key.ParamSignature.Length == 0 ? 0 : key.ParamSignature.Split(',').Length;

    [Fact]
    public void All_Registered_Intrinsics_Are_Classified() {
        var (vm, keys, _) = CreateAudited();
        try {
            var unclassified = keys
                .Where(k => CoreLibSurfaceAudit.Classify(k.TypeFullName, k.MethodName, k.HasThis, ParamCount(k)) is null)
                .Select(k => $"{k.TypeFullName}::{k.MethodName} (arity {k.Arity}, hasThis {k.HasThis})")
                .ToList();
            Assert.True(unclassified.Count == 0,
                "監査表に載っていない intrinsic があります (CoreLibSurfaceAudit.BuildDelegations に分類を追加してください):\n" +
                string.Join("\n", unclassified));
        } finally {
            vm.Dispose();
        }
    }

    [Fact]
    public void All_Registered_Bindings_Are_Classified() {
        var (vm, _, bindings) = CreateAudited();
        try {
            var unclassified = bindings
                .Where(b => CoreLibSurfaceAudit.Classify(b.Key.TypeFullName, b.Key.MethodName, b.Key.HasThis, ParamCount(b.Key)) is null)
                .Select(b => $"{b.Key} (origin {b.Origin})")
                .ToList();
            Assert.True(unclassified.Count == 0,
                "監査表に載っていないランタイムバインドがあります (CoreLibSurfaceAudit.BuildDelegations に分類を追加してください):\n" +
                string.Join("\n", unclassified));
        } finally {
            vm.Dispose();
        }
    }

    [Fact]
    public void Every_Audit_Entry_Has_A_Registered_Key() {
        var (vm, keys, bindings) = CreateAudited();
        try {
            var orphaned = CoreLibSurfaceAudit.Delegations
                .Where(e => !keys.Any(k => e.Matches(k.TypeFullName, k.MethodName, k.HasThis, ParamCount(k))) &&
                            !bindings.Any(b => e.Matches(b.Key.TypeFullName, b.Key.MethodName, b.Key.HasThis, ParamCount(b.Key))))
                .Select(e => $"{e.Type}::{e.Method} (hasThis {e.HasThis?.ToString() ?? "*"}, params {e.ParamCount?.ToString() ?? "*"})")
                .ToList();
            Assert.True(orphaned.Count == 0,
                "監査表に載っているが対応する登録が存在しない面があります (実体が無い = ④ fail-closed に落ちる面。監査表か登録を見直してください):\n" +
                string.Join("\n", orphaned));
        } finally {
            vm.Dispose();
        }
    }

    [Fact]
    public void No_Binding_Shadows_A_Substitute_Face() {
        var (vm, _, bindings) = CreateAudited();
        try {
            // 置換面 (b) と同一 (型, メソッド, 署名) のバインドは ① が ②' 置換面を
            // 塞ぐ退行。整数 4 型の ToString() をバインドしない規約の一般化。
            // (legacy intrinsic は署名キーを持たないため除外 — ②' コンサルトが ③ より先に解決する)
            var shadows = bindings
                .Where(b => !b.Key.IsAnyParams && VmCoreLibSurfaces.FacesForAudit.Any(f =>
                    f.Type == b.Key.TypeFullName &&
                    f.Method == b.Key.MethodName &&
                    string.Join(",", f.Params) == b.Key.ParamSignature))
                .Select(b => $"バインド {b.Key} が置換面を塞ぎます")
                .ToList();
            Assert.True(shadows.Count == 0,
                "置換面 (VmCoreLibSurfaces.Faces) と同一キーのバインドが登録されています (該当バインドを廃止してください):\n" +
                string.Join("\n", shadows));
        } finally {
            vm.Dispose();
        }
    }

    [Fact]
    public void RealCoreLibIl_Keys_Are_Shadowed_Residue() {
        // Kind (a) の委譲キーは「実 IL が実際には実行される面の残置」でなければならない
        // (到達しうる委譲が残っていれば実 IL 実行の退行)。正当化は shadowed- で始める規約。
        var violations = CoreLibSurfaceAudit.Delegations
            .Where(e => e.Kind == CoreLibSurfaceKind.RealCoreLibIl && !e.Justification.StartsWith("shadowed"))
            .Select(e => $"{e.Type}::{e.Method}")
            .ToList();
        Assert.True(violations.Count == 0,
            "RealCoreLibIl 分類の委譲キーが残置 (shadowed-legacy) として説明されていません:\n" +
            string.Join("\n", violations));
    }
}
