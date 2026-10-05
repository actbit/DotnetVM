# IL 実行基盤の性能計測

最新の再測定は [PR #34の性能測定（2026-10-04）](performance-pr34.md) を参照してください。
この文書の数値と「最新」は2026-10-03時点の記録です。当時の改善率を現在のPRの効果としては扱いません。
現在の通常runnerは25項目（unified-25）で、下記の18項目の測定データは履歴として保持します。

`origin/master`（`f19f436`、PR #30 まで）を取り込み、その実行基盤に今回の最適化と互換性修正を統合しました。
最適化はインタプリタと VM JIT が共有する IL 実行経路に適用され、通常のゲストコード、
CoreLib、System.Linq、async の生成 IL に使われます。List / LINQ / Dictionary は managed IL と既存の表現境界を通ります。

## 共通の最適化と統合

- master の値型 instruction lease と、1,024 命令までの read lease のまとめ実行を保持。命令数クォータ、shutdown、GC、ByRef の所有権、IL 検証を継続します。
- ホストスレッドの実行状態はフレーム開始時に取得し、命令ループと JIT フレームで再利用。命令ごとの ThreadLocal 取得を減らします。
- ゲスト呼び出しの入口で GC ルートを登録し、終了時に解除。同じホストスレッドでの次回呼び出しでも再登録します。
- master のデコード済み IL、ローカル初期値、分岐先表、EH、準備キャッシュ予算、JIT の昇格・アンロード管理を再利用します。
- 型引数を含む `call`、フィールド、型のオペランド解決をキャッシュ。MemberRef / MethodSpec / TypeSpec の署名デコードも再利用し、Span の解析と深度制限を保持します。
- 不変の MethodDef / FieldDef は整数キーの既存経路で再利用。`ldfld` / `stfld` は一時 ByRef を生成せず、スロットの同期、readonly、値コピーを維持します。
- GC の評価スタック走査は未使用スロットを消去する元バッファを参照し、コピーを除去。ヒープ sweep は線形に圧縮します。

追加の共通経路の最適化も適用しています。

- 成功した TypeRef 解決を loader ごとに再利用し、同じ型のためのディレクトリ探索を削減。解決失敗は保存せず、登録・アンロードで解決世代を更新します。
- 解決キャッシュは世代ごとの状態を公開し、ヒット時は ConcurrentDictionary の読み取りを使います。追加・無効化時の同期と型引数のスナップショットを維持します。
- intrinsic レジストリは Seal の公開後、変更されない表をロックなしで読み取ります。登録・置換の禁止と caller domain の照合は継続します。
- loader ごとの engine は既存エントリを同期辞書から参照。新規作成は lifetime gate の下で行い、参照時にも loader の生存を確認します。
- 名前と引数数による仮想ディスパッチの成功結果を、実行時型の定義ごとに再利用。型引数は毎回レシーバから取得し、アンロード中の古い結果は再公開しません。
- 呼出前の lifetime 検査は評価スタックの有効範囲を ReadOnlySpan で走査し、スロット配列のコピーを除去します。
- 実型・ファサードの完全名を再利用。ネスト型では包含型の名前も照合し、遅延ロードで包含関係が確定した場合に名前を更新します。

追加のオペランド解決キャッシュは engine ごとに各 4,096 件までです。TypeRef 解決も loader ごとに最大 4,096 件、仮想ディスパッチも engine ごとに最大 4,096 件です。定義トークンと署名構文のキャッシュは画像のメタデータ行数で上限が決まります。
型引数は構造と型実体の同一性で区別し、同名でも別アセンブリ / ALC の型は混同しません。
登録・アンロード・retire で解決世代を更新し、アンロードでは他 loader の参照も消去します。
親コンテキストを持つ呼び出し先のバインドは毎回解決します。動的トークンと未解決の結果は追加キャッシュに保存しません。
仮想ディスパッチ、呼び出し元ドメイン、loader の生存確認は実行時にも検査します。

## 今回の呼び出し・Span の最適化

- JIT の同期 leaf メソッドは `ReadOnlySpan<StackSlot>` で引数を受け取ります。直接呼び出しでは評価スタックの有効範囲を借り、引数配列の確保を除去します。呼び出し終了まで元スロットを GC ルートに保ち、戻った後に消去します。
- 通常の呼び出しは Span の一括コピー・消去で引数を移します。引数数と範囲を検査し、値型のコピー規約を維持します。
- receiver を使わない、プリミティブ引数だけの instance メソッドも leaf の対象にします。キャプチャのないラムダなどの生成 IL に適用されます。
- interface・仮想・デリゲートの各経路でも、解決済みのコンパイル結果を再利用します。バインド、caller domain、仮想ディスパッチ、tail call の判定は先に行います。
- 抽象メソッドの宣言も解決済みの呼び出し先として保存します。実装の選択は毎回レシーバの宣言スロットから行います。
- 準備済みメソッドと JIT の公開済みエントリは同期辞書から読み取ります。準備・検証・昇格の処理、FIFO / バイト数・JIT 予算、アンロード時の公開判定は同期の下で行います。
- 型初期化と MethodDef 検索のラムダを遅い経路に移し、キャッシュヒットでも作られていた closure を除去します。構築型の初期化済み判定では型引数を借りて照合し、保存するキーだけにスナップショットを持たせます。cctor の待機・再入・失敗の扱いを維持します。
- ローカル署名のデコードも元の blob の Span を使い、一時配列を除去します。

命令数クォータと safepoint は各 IL 命令で維持します。今回の前後測定は、全18項目で入力・期待値・IL 命令数が一致しています。

## 再現手順

`DotnetVM.Benchmarks` の通常実行に全18項目を統合しました。
すべて `LoadHostCoreLib=true`、warmup 3 回、7 サンプル、VM JIT 昇格閾値2回です。
Release の同じゲストメソッドと入力を CoreCLR の型付きデリゲートと VM の公開呼び出しで実行し、戻り値を照合します。

短い CoreCLR 呼び出しは約50 msのバッチに調整し、1呼び出しあたりへ換算します（最大1,000,000回）。
VM は各サンプル1回です。ロード、初回準備・JIT 昇格は時間に含みません。
測定前の強制 GC は CoreCLR／VM のどちらにも行わず、各ランタイムの通常の GC 発生を時間に含めます。
CoreCLR の結果確認はバッチ後、VM の確認は各呼び出しで行います。
確保量は `GC.GetTotalAllocatedBytes(true)` の差分で、async worker を含むプロセス全体の値です。
VM のメモリクォータ会計とは別の指標で、実行中の GC は計測時間に含みます。

```powershell
$benchmarkTieredCompilation = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet build DotnetVM.slnx -c Release
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --output artifacts/performance/interpreter.json
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --jit --output artifacts/performance/jit.json
} finally {
    $env:DOTNET_TieredCompilation = $benchmarkTieredCompilation
}
```

`--filter Linq` などで項目を選び、`--size` で入力を変更できます。
`--warmups`、`--samples`、`--trace` も利用できます。
コンソールの **VM ms は時間**、**VM / CLR は時間の比率**です。不一致や例外は項目ごとに `ERROR` を記録し、終了コード1を返します。
保存済みの測定条件を再現する `--legacy` は、warmup 3 回、9 サンプル × 5 呼び出し、
昇格閾値1回、実在 CoreLib のロード無効です。この出力の時間は5呼び出しの合計で、現在の測定とは条件が異なります。

## 最新測定と追加前の比較

最新の全18項目と CoreCLR の比較は [README](../README.md#ベンチマーク) に掲載しています。
2026-10-03、AMD Ryzen 9 3900 / Windows x64（build 26200）、SDK 10.0.401 / runtime 10.0.12、
Release、host tiered compilation 無効です。インタプリタと VM JIT は別プロセスで順に測定しました。

追加修正の前にも **統一した18項目を同じ条件で測定**しました。
以下の時間は1呼び出しあたりの ms です。時間削減は `1 - 最新 / 追加前`、負数は増加を示します。
小さい差や単発の測定には host 負荷・クロック・GC の変動が含まれます。
待機を含む `AsyncWorkers` は速度改善の評価から外し、サンプルは両 JSON に保存しています。

| ワークロード | 追加前 interp ms | 最新 interp ms | 時間削減 | 追加前 JIT ms | 最新 JIT ms | 時間削減 |
|---|---:|---:|---:|---:|---:|---:|
| 算術・シフト (`Arithmetic`) | 4.662 | 4.894 | -5.0% | 3.213 | 3.107 | 3.3% |
| 算術・剰余 (`ArithmeticLoop`) | 111.349 | 111.588 | -0.2% | 78.877 | 77.326 | 2.0% |
| 条件分岐 (`BranchLoop`) | 104.273 | 110.385 | -5.9% | 75.559 | 76.358 | -1.1% |
| 配列の作成・走査 (`ArraySum`) | 18.110 | 18.322 | -1.2% | 13.530 | 15.421 | -14.0% |
| フィールド (`FieldAccess`) | 6.186 | 6.377 | -3.1% | 4.832 | 4.803 | 0.6% |
| ジェネリックフィールド (`GenericFieldAccess`) | 6.603 | 6.738 | -2.0% | 6.587 | 6.558 | 0.4% |
| メソッド呼び出し (`MethodCalls`) | 2.349 | 2.317 | 1.4% | 1.150 | 1.099 | 4.4% |
| メソッド呼び出しループ (`CallLoop`) | 221.900 | 207.202 | 6.6% | 86.371 | 81.542 | 5.6% |
| オブジェクト確保 (`ObjectLoop`) | 69.867 | 65.107 | 6.8% | 53.249 | 49.001 | 8.0% |
| List 更新・interface 列挙 (`List`) | 10.959 | 9.972 | 9.0% | 11.227 | 10.300 | 8.3% |
| List 拡張・通常列挙 (`ListGrowth`) | 6.752 | 6.512 | 3.5% | 6.789 | 6.612 | 2.6% |
| LINQ Where / Select / Sum (`Linq`) | 15.942 | 14.163 | 11.2% | 15.548 | 13.408 | 13.8% |
| 整数 Dictionary (`DictionaryInt`) | 22.308 | 21.503 | 3.6% | 21.264 | 22.975 | -8.0% |
| Dictionary 拡張 (`DictionaryGrowth`) | 19.108 | 18.240 | 4.5% | 20.017 | 17.019 | 15.0% |
| 文字列 Dictionary (`DictionaryString`) | 20.237 | 19.282 | 4.7% | 21.645 | 17.057 | 21.2% |
| 完了済み Task の await (`AsyncCompleted`) | 1.670 | 1.666 | 0.2% | 1.849 | 1.639 | 11.4% |
| 完了済み ValueTask の await (`ValueTaskCompleted`) | 1.834 | 1.896 | -3.4% | 1.802 | 1.813 | -0.6% |

ArraySum の JIT は全体測定で 13.530 → 15.421 ms、DictionaryInt は 21.264 → 22.975 msでした。
変動を確認するため、同じ入力で個別に21サンプルを追加しました。中央値はそれぞれ **12.862 ms / 20.357 ms**で、
全体測定の値も表と JSON にそのまま残しています。この2項目の増減を確実な効果とは扱いません。

以下は同じ測定範囲の host 確保量です。

| ワークロード | 追加前 interp bytes | 最新 interp bytes | 削減 | 追加前 JIT bytes | 最新 JIT bytes | 削減 |
|---|---:|---:|---:|---:|---:|---:|
| メソッド呼び出し (`MethodCalls`) | 1,014,568 | 974,528 | 3.9% | 430,568 | 302,528 | 29.7% |
| メソッド呼び出しループ (`CallLoop`) | 74,702,560 | 70,702,520 | 5.4% | 16,302,560 | 302,520 | 98.1% |
| オブジェクト確保 (`ObjectLoop`) | 22,382,560 | 21,982,520 | 1.8% | 19,502,560 | 13,902,520 | 28.7% |
| List 更新・interface 列挙 (`List`) | 3,683,032 | 3,213,976 | 12.7% | 3,682,744 | 3,213,104 | 12.8% |
| LINQ Where / Select / Sum (`Linq`) | 6,341,944 | 5,587,304 | 11.9% | 6,122,400 | 4,928,104 | 19.5% |
| 文字列 Dictionary (`DictionaryString`) | 6,265,144 | 6,191,952 | 1.2% | 5,235,056 | 5,161,376 | 1.4% |

## 過去の測定との比較

[前回の計測と比較](performance-round2.md) に、同一条件で再測定した `f19f436` と統合後の比較、
前回追加最適化の時間・確保量、PR #29 との参考比較を保存しました。前回公開値の生データも上書きせず保存しています。
[過去のベンチマーク記録](benchmark-history.md) には2026-09-25、PR #27、PR #29 の記録と比較対象コミットがあります。

前回までの13項目は、今回も同じ入力・CoreLib のロード設定・warmup・サンプル数・昇格閾値を使っています。
5つのループ項目は今回から CoreLib をロードする統一測定に変更したため、CoreLib をロードしない過去の時間と直接の速度比は付けません。
過去の記録では5呼び出しの合計だった値も、各比較表で1呼び出しあたりに換算しています。
host tiered compilation が不明な記録は参考値として区別しています。

## 計測データ

各 JSON に測定時刻、環境、入力、期待値、時間、確保量、IL 命令数、全サンプルを保存しています。
現在の JSON は `Protocol=unified-18` と測定条件も持ちます。

- 最新18項目: [interpreter.json](benchmarks/2026-10-03/interpreter.json)、[jit.json](benchmarks/2026-10-03/jit.json)
- 同じ18項目の追加前: [unified-before-interpreter.json](benchmarks/2026-10-03/unified-before-interpreter.json)、[unified-before-jit.json](benchmarks/2026-10-03/unified-before-jit.json)
- 変動確認の21サンプル: [array-verification.json](benchmarks/2026-10-03/array-verification.json)、[dictionary-verification.json](benchmarks/2026-10-03/dictionary-verification.json)
- 前回公開値: [round2/interpreter.json](benchmarks/2026-10-03/round2/interpreter.json)、[round2/jit.json](benchmarks/2026-10-03/round2/jit.json)、[round2/legacy.json](benchmarks/2026-10-03/round2/legacy.json)
- 比較用 master: [master-interpreter.json](benchmarks/2026-10-03/master-interpreter.json)、[master-jit.json](benchmarks/2026-10-03/master-jit.json)、[master-legacy.json](benchmarks/2026-10-03/master-legacy.json)

## 互換性と回帰検証

Debug / Release ゲスト × インタプリタ / JIT の4条件で、全18ワークロードを CoreCLR と照合します。
GC の要求間隔を小さくし、独自キー、構造体の重なった配列コピー、ジェネリックの box / unbox・フィールド、
static abstract、Task / ValueTask の suspension、キャッシュの再利用と昇格を確認します。

参照型の `box !T`、constrained のレシーバ、既定の等値比較子、UTF-16 終端と hash 用 padding、
Release async の builder / Task の共有、継続失敗の fault 伝播を修正しています。
依存ロード・アンロードによる型解決の更新、ネスト型名の遅延確定、異なる型引数を交互に使う仮想呼び出しも確認します。
今回の検証では、スタックの境界・参照消去、Span を使う入れ子呼び出しの引数順・例外後の復帰・命令数クォータを追加しました。
GC、readonly、並行実行、アンロード、動的コード、provenance、リソース制約、IL 検証を含む **全1,002件が成功**しています。

```powershell
dotnet test DotnetVM.Tests -c Release
```
