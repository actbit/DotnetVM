using System.Globalization;
using System.Runtime.CompilerServices;

namespace DotnetVM.Tests;

/// <summary>
/// C5.5 Wave 5: テスト アセンブリ全面の culture ピン留め。
/// VM の culture 規約は不変カルチャ固定のため、CLR 突合側 (ホスト BCL の CurrentCulture
/// 依存面 = string.Compare / IndexOf(string) / StartsWith / EndsWith / Format 等) も
/// すべてのテストで InvariantCulture に固定して初めて同一意味論で突合できる。
/// ModuleInitializer でアセンブリ ロード時に一度だけ適用する (xUnit はテストごとに
/// 別インスタンスを生成するがスレッドの culture は継承されるため二重適用は不要。
/// 念のためテスト失敗時に原因を切分けやすいよう null チェックつき)。
/// </summary>
internal static class TestCulture {
    [ModuleInitializer]
    internal static void Pin() {
        var invariant = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = invariant;
        CultureInfo.CurrentUICulture = invariant;
    }
}
