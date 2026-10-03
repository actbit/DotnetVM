# 2026-10-03 の前回計測

このページは、全18項目を同じ条件の測定に統一する前に公開した比較の保存です。13項目と5項目には別々の測定条件があり、以下の比較は各条件内で行っています。現在の実装と統一測定の結果は [性能計測ガイド](performance.md) を参照してください。

前回公開の `origin/master`（`f19f436`、PR #30 まで）を取り込み、その実行基盤に今回の最適化と互換性修正を統合しました。
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

## 再現手順

`DotnetVM.Benchmarks` は外部ベンチマークライブラリに依存しません。
Release の同じゲストメソッドと入力を、CoreCLR の型付きデリゲートと VM の公開呼び出し経路で実行し、戻り値を照合します。

通常の13項目は warmup 3 回、測定 7 回の中央値です。短い CoreCLR 呼び出しは約50 ms のバッチに調整し、
1 呼び出しあたりに換算します（最大 1,000,000 回）。VM は各サンプル1回です。
作成、アセンブリロード、初回準備・JIT 昇格、サンプル前の host GC は時間に含みません。
CoreCLR の結果確認はバッチ後、VM の確認は呼び出しごとです。
host 確保量は `GC.GetTotalAllocatedBytes(true)` の差分で、async worker を含むプロセス全体の値です。
VM のメモリクォータ会計とは別の指標で、バッチ内に発生する GC は計測に含みます。

`--legacy` は保存済みの5項目と `0c419aa` の測定手順を維持します。
warmup 3 回、9 サンプル × 5 呼び出し、3方式の順番をローテーションし、実在 CoreLib はロードしません。
コンソール / JSON の legacy 時間は5呼び出しの合計で、README と以下の表では5で割っています。

PowerShell の実行例です。全方式で host tiered compilation を無効にし、CoreCLR 自体の JIT は有効のままです。

```powershell
$benchmarkTieredCompilation = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet run --project DotnetVM.Benchmarks -c Release -- --output artifacts/performance/interpreter.json
    dotnet run --project DotnetVM.Benchmarks -c Release -- --jit --output artifacts/performance/jit.json
    dotnet run --project DotnetVM.Benchmarks -c Release -- --legacy --output artifacts/performance/legacy.json
} finally {
    $env:DOTNET_TieredCompilation = $benchmarkTieredCompilation
}
```

通常実行の `--jit` は昇格閾値2回です。`--warmups 5 --samples 9`、`--filter GenericFieldAccess --size 10000` で条件を変更できます。
`--trace` は warmup の実行フレームを記録し、失敗時に末尾を表示します。
legacy の回数と昇格閾値1回は固定です。不一致・例外は失敗とし、通常実行では項目ごとに `ERROR` を記録して終了コード1を返します。

## 前回公開結果と比較対象

以下は前回公開した CoreCLR / VM インタプリタ / VM JIT の測定値です。
2026-10-03、AMD Ryzen 9 3900 / Windows x64（build 26200）、SDK 10.0.401 / runtime 10.0.12、
Release、host tiered compilation 無効の実測です。追加最適化後に全18項目を再測定しました。

比較用の `f19f436` は `git archive` から独立したディレクトリに展開し、同じワークロードと測定ハーネスを使って順に実行しました。
以下はすべて **1 呼び出しあたりの ms** です。時間削減率は `1 - 統合後 / master` で、負数は増加を示します。
小さい差には host 負荷やクロックの変動も含まれます。CoreCLR の各モードの測定値とサンプルも JSON に保存しています。

| ワークロード | master interp ms | 統合後 interp ms | 時間削減 | master JIT ms | 統合後 JIT ms | 時間削減 |
|---|---:|---:|---:|---:|---:|---:|
| 算術 IL | 5.713 | 4.694 | 17.8% | 4.367 | 3.248 | 25.6% |
| フィールド IL | 7.126 | 6.182 | 13.3% | 6.385 | 4.812 | 24.6% |
| ジェネリックフィールド IL | 13.315 | 6.781 | 49.1% | 13.342 | 6.593 | 50.6% |
| 通常のメソッド呼び出し | 2.793 | 2.375 | 15.0% | 1.374 | 1.104 | 19.6% |
| List 更新・interface 列挙 | 122.767 | 11.204 | 90.9% | 124.970 | 11.005 | 91.2% |
| 文字列 Dictionary | 111.545 | 20.704 | 81.4% | 81.046 | 17.780 | 78.1% |
| 完了済み Task の await | 8.627 | 1.730 | 79.9% | 9.143 | 1.648 | 82.0% |

ジェネリックフィールドの host 確保量は、インタプリタで 5,910,024 → 304,872 bytes、
VM JIT で 5,909,832 → 304,680 bytes です。算術・フィールド・List の IL 命令数は比較対象と同じです。
文字列 Dictionary は等値比較とハッシュ処理の互換性修正も含むため、IL 命令数も変わります。

master で失敗した項目には速度比を付けません。統合後は以下を含む全18項目の結果が CoreCLR と一致しました。

| ワークロード | master での問題 | 統合後 |
|---|---|---|
| List 拡張 | ネストした Enumerator の型参照 | CoreCLR と一致 |
| LINQ | CurrentManagedThreadId と static abstract の呼び出し | CoreCLR と一致 |
| 整数 Dictionary | CLR=59700、VM=796 | CoreCLR と一致 |
| Dictionary 拡張 | Array.Copy のレシーバ表現 / IL 検証 | CoreCLR と一致 |
| ValueTask の await | 値 / Task コンストラクターの表現 | CoreCLR と一致 |
| Task.Run / Delay / WhenAll | Task.Run の署名と debugger probe | CoreCLR と一致 |

保存済み5項目も同じ条件で再計測しました。

| ワークロード | master interp ms | 統合後 interp ms | 時間削減 | master JIT ms | 統合後 JIT ms | 時間削減 |
|---|---:|---:|---:|---:|---:|---:|
| Arithmetic | 133.000 | 118.129 | 11.2% | 100.662 | 77.458 | 23.1% |
| Branches | 129.919 | 112.471 | 13.4% | 96.147 | 74.145 | 22.9% |
| Array access | 20.739 | 18.104 | 12.7% | 15.860 | 12.190 | 23.1% |
| Method calls | 260.729 | 214.725 | 17.6% | 104.435 | 82.745 | 20.8% |
| Object allocation | 54.737 | 44.524 | 18.7% | 40.840 | 34.110 | 16.5% |

## 追加最適化前との比較

追加修正の直前にも通常13項目を同じ条件で再計測し、修正後と比べました。
以下の12項目は **同じ IL 命令数・入力・期待値** です。算術と単純フィールドは小さい変動も含めて掲載しています。
`AsyncWorkers` は待機時間・スケジューリングの影響が大きいため、速度改善の評価から外しています。
同項目を含む全サンプルは JSON に保存しています。

| ワークロード | 追加前 interp ms | 前回公開 interp ms | 時間削減 | 追加前 JIT ms | 前回公開 JIT ms | 時間削減 |
|---|---:|---:|---:|---:|---:|---:|
| 算術 IL | 4.610 | 4.694 | -1.8% | 3.227 | 3.248 | -0.7% |
| フィールド IL | 6.139 | 6.182 | -0.7% | 5.089 | 4.812 | 5.4% |
| ジェネリックフィールド IL | 6.832 | 6.781 | 0.8% | 6.778 | 6.593 | 2.7% |
| 通常のメソッド呼び出し | 2.692 | 2.375 | 11.8% | 1.222 | 1.104 | 9.7% |
| List 更新・interface 列挙 | 40.098 | 11.204 | 72.1% | 40.570 | 11.005 | 72.9% |
| List 拡張・通常列挙 | 7.600 | 6.694 | 11.9% | 7.649 | 6.595 | 13.8% |
| LINQ Where / Select / Sum | 38.574 | 16.673 | 56.8% | 37.386 | 15.621 | 58.2% |
| 整数 Dictionary | 27.360 | 22.482 | 17.8% | 25.865 | 21.116 | 18.4% |
| Dictionary 拡張 | 23.974 | 19.089 | 20.4% | 20.532 | 17.316 | 15.7% |
| 文字列 Dictionary | 24.704 | 20.704 | 16.2% | 21.392 | 17.780 | 16.9% |
| 完了済み Task の await | 2.040 | 1.730 | 15.2% | 2.014 | 1.648 | 18.2% |
| 完了済み ValueTask の await | 7.832 | 1.853 | 76.3% | 7.615 | 1.867 | 75.5% |

host 確保量も、同じ測定範囲で減少しました。以下はインタプリタでの1呼び出しあたりの中央値です。

| ワークロード | 追加前 bytes | 前回公開 bytes | 確保量削減 |
|---|---:|---:|---:|
| List 更新・interface 列挙 | 6,262,808 | 3,683,032 | 41.2% |
| List 拡張・通常列挙 | 2,996,088 | 1,846,656 | 38.4% |
| LINQ Where / Select / Sum | 39,077,496 | 6,341,944 | 83.8% |
| 整数 Dictionary | 10,266,032 | 6,500,224 | 36.7% |
| Dictionary 拡張 | 9,811,616 | 5,953,112 | 39.3% |
| 文字列 Dictionary | 9,461,464 | 6,265,144 | 33.8% |
| 完了済み ValueTask の await | 1,471,648 | 1,000,064 | 32.0% |

## 過去の記録との比較

[過去のベンチマーク記録](benchmark-history.md) に、2026-09-25 の実行経路、PR #27 のセーフポイント、
PR #29 のオブジェクト確保の表と比較対象コミットを保管しています。

直近の PR #29（`7211c60`）の記録と今回の値を、1呼び出しに換算して並べると以下です。
旧記録は host tiered compilation 条件が明記されておらず、今回とは条件が揃わないため参考値です。
今回の変更の速度比には上の `f19f436` 再計測を使ってください。

| ワークロード | PR #29 interp ms | 今回 interp ms | PR #29 JIT ms | 今回 JIT ms |
|---|---:|---:|---:|---:|
| Arithmetic | 109.263 | 118.129 | 74.922 | 77.458 |
| Branches | 103.744 | 112.471 | 69.514 | 74.145 |
| Array access | 16.977 | 18.104 | 11.759 | 12.190 |
| Method calls | 185.067 | 214.725 | 72.344 | 82.745 |
| Object allocation | 39.388 | 44.524 | 29.083 | 34.110 |

## 計測データ

`benchmarks/2026-10-03/` に測定時刻、実行環境、入力、期待値、時間、確保量、IL 命令数を保存しています
（legacy は時間と期待値のみで、時刻・環境は同様に保存）。

- 統合後: [interpreter.json](benchmarks/2026-10-03/round2/interpreter.json)、[jit.json](benchmarks/2026-10-03/round2/jit.json)、[legacy.json](benchmarks/2026-10-03/round2/legacy.json)
- 追加修正直前の再測定: [followup-before-interpreter.json](benchmarks/2026-10-03/followup-before-interpreter.json)、[followup-before-jit.json](benchmarks/2026-10-03/followup-before-jit.json)
- 前回公開値の保存: [initial/interpreter.json](benchmarks/2026-10-03/initial/interpreter.json)、[initial/jit.json](benchmarks/2026-10-03/initial/jit.json)、[initial/legacy.json](benchmarks/2026-10-03/initial/legacy.json)
- 比較用 master: [master-interpreter.json](benchmarks/2026-10-03/master-interpreter.json)、[master-jit.json](benchmarks/2026-10-03/master-jit.json)、[master-legacy.json](benchmarks/2026-10-03/master-legacy.json)

## 互換性と回帰検証

Debug / Release ゲスト × インタプリタ / JIT の4条件で CoreCLR と結果を照合します。
GC の要求間隔を小さくし、独自キー、構造体の重なった配列コピー、ジェネリックの box / unbox・フィールド、
static abstract、Task / ValueTask の実際の suspension、キャッシュの再利用と昇格を確認します。

参照型の `box !T`、constrained のレシーバ、既定の等値比較子、UTF-16 終端と hash 読み取り用 padding、
Release async の builder / Task の共有、継続失敗の fault 伝播を修正しました。
統合時に確認した GC ルートの再登録と readonly フィールド参照にも、両実行方式の回帰テストを追加しています。
追加の検証では、失敗後の依存ロード、登録・アンロードによる型解決の更新、ネスト型名の遅延確定、
異なる型とジェネリック引数を交互に使う仮想呼び出しを確認します。
GC、並行実行、アンロード、動的コード、provenance、リソース制約、IL 検証を含む **全991件が成功**しています。

```powershell
dotnet test DotnetVM.Tests -c Release
```
