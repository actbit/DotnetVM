# PR #34 の性能再測定（2026-10-04）

最新のmaster `2950044f87d867171d2c14d2a6df6b09caa7aa23` と、PR側 `e277241aafb13fe1f20489fb2b2c9c7b9e65daec` を同じ環境・ゲストコードで再測定しました。
PR側のランタイム／改造BCLは `92304de` と同じで、`e277241` は測定項目とrevision記録の追加です。
古い `e42b3e1` や `f19f436` からの改善率を、このPRの最新masterに対する効果としては扱いません。

今回のPRに全体の高速化を示す結果はありません。属性取得は約51倍の時間がかかり、SpanとDictionaryにも時間・確保量の増加がありました。
25項目中、両版で測定できたのは23項目です。InvokeはPR側、JSON往復はmaster側で失敗し、速度比を計算していません。

## 条件と測定範囲

- AMD Ryzen 9 3900、Windows x64 10.0.26200、SDK 10.0.401／runtime 10.0.12、Release。
- `DOTNET_TieredCompilation=0`、`LoadHostCoreLib=true`、VM JIT昇格閾値2、warmup 3回、7 samples、Protocol `unified-25`。
- master interpreter → master JIT → PR interpreter → PR JITの順に、別プロセスで直列実行。テストや他のベンチマークを同時には実行していません。
- 各行は表の入力を処理する**ゲストメソッド1呼び出し全体**の中央値です。例えば属性取得は50回、JSONは10回の往復を含みます。
- 同一ゲストをCoreCLRの型付きデリゲートでも実行し、成功したVMサンプルの戻り値を照合しています。
- ロード、初回準備、JIT昇格、サンプル前のホストGCは時間から除外。実行中のGCとVMの公開Invoke入口、結果照合は含みます。初回JSONメタデータ構築の時間はwarmupに含まれ、表には含みません。
- 確保量は `GC.GetTotalAllocatedBytes(true)` の差分によるプロセス全体のホスト確保量です。VMのメモリクォータや常駐メモリの値ではありません。
- 7サンプルの1回の前後測定であり、小さな時間差の再現性や統計的有意性を保証しません。`AsyncWorkers` は待機・スケジューリングを含むため、速度改善の評価から除外します。

## 実測結果

`PR/master` は時間の比です。1より大きいほどPR側が遅く、1より小さいほど短時間です。
`ERROR` は実行失敗で、ゼロ時間ではありません。

| workload | 入力 | master interp ms | PR interp ms | PR/master | master JIT ms | PR JIT ms | PR/master |
|---|---:|---:|---:|---:|---:|---:|---:|
| SpanCopies | 1,000 | 33.860 | 38.087 | 1.12× | 34.272 | 37.227 | 1.09× |
| IntegerFormatting | 1,000 | 8.305 | 8.389 | 1.01× | 8.445 | 7.877 | 0.93× |
| IntegerParsing | 1,000 | 5.590 | 5.598 | 1.00× | 5.658 | 5.534 | 0.98× |
| StringCopies | 1,000 | 4.855 | 4.715 | 0.97× | 4.237 | 4.314 | 1.02× |
| ReflectionInvoke | 100 | 0.971 | ERROR | — | 0.960 | ERROR | — |
| ReflectionAttributes | 50 | 1.589 | 81.739 | 51.45× | 1.457 | 74.064 | 50.83× |
| JsonRoundTrip | 10 | ERROR | 46.270 | — | ERROR | 48.238 | — |
| Arithmetic | 5,000 | 5.231 | 4.981 | 0.95× | 3.502 | 3.555 | 1.01× |
| ArithmeticLoop | 100,000 | 121.878 | 116.378 | 0.95× | 78.717 | 79.534 | 1.01× |
| BranchLoop | 100,000 | 115.293 | 109.991 | 0.95× | 74.596 | 75.691 | 1.01× |
| ArraySum | 10,000 | 19.617 | 18.796 | 0.96× | 13.318 | 13.170 | 0.99× |
| FieldAccess | 5,000 | 6.790 | 6.775 | 1.00× | 5.364 | 5.332 | 0.99× |
| GenericFieldAccess | 5,000 | 7.225 | 7.071 | 0.98× | 7.633 | 7.268 | 0.95× |
| MethodCalls | 1,000 | 2.570 | 2.887 | 1.12× | 1.505 | 1.486 | 0.99× |
| CallLoop | 100,000 | 214.219 | 211.475 | 0.99× | 81.475 | 81.840 | 1.00× |
| ObjectLoop | 10,000 | 67.108 | 67.762 | 1.01× | 49.383 | 49.403 | 1.00× |
| List | 500 | 11.160 | 11.018 | 0.99× | 11.542 | 11.406 | 0.99× |
| ListGrowth | 500 | 7.194 | 7.077 | 0.98× | 7.560 | 7.336 | 0.97× |
| Linq | 500 | 15.369 | 16.165 | 1.05× | 14.559 | 15.283 | 1.05× |
| DictionaryInt | 200 | 22.178 | 26.689 | 1.20× | 21.112 | 23.392 | 1.11× |
| DictionaryGrowth | 200 | 18.736 | 19.933 | 1.06× | 17.607 | 19.332 | 1.10× |
| DictionaryString | 200 | 19.291 | 20.350 | 1.05× | 17.519 | 18.378 | 1.05× |
| AsyncCompleted | 200 | 1.975 | 1.997 | 1.01× | 2.336 | 1.993 | 0.85× |
| ValueTaskCompleted | 200 | 2.129 | 2.058 | 0.97× | 2.171 | 2.280 | 1.05× |
| AsyncWorkers | 8 | 12.373 | 12.174 | 0.98× | 11.983 | 11.249 | 0.94× |

## 結果の読み方

- **ReflectionAttributes:** interpreter 1.589 → 81.739 ms（51.45倍）、JIT 1.457 → 74.064 ms（50.83倍）。
  確保量も1,952,168 → 34,456,168 bytes／1,901,768 → 30,435,768 bytesへ増加しました。
  専用属性処理から元のCoreLib ILへ実行経路が変わっており、同じILを高速化した比較ではありません。互換性の改善に対する性能コストとして記録します。
- **SpanCopies:** interpreterは時間12.5%・確保量28.4%増、JITは時間8.6%・確保量27.1%増でした。
  IL命令数も76,020 → 80,020に増えています。以前の「確保量約40%削減」は古いmasterに対する当時の結果であり、このPRの前後比較ではありません。
- **DictionaryInt:** interpreterは時間20.3%増、JITは10.8%増。確保量はそれぞれ21.7%／21.5%増でした。
  DictionaryGrowth、DictionaryString、Linqにも増加があります。特定の内部変更が原因という切り分けは行っていません。
- **IntegerParsing:** interpreter 5.590 → 5.598 ms、JIT 5.658 → 5.534 ms。今回のmaster比較から31.7倍の高速化は主張しません。
  IntegerFormattingもinterpreter 8.305 → 8.389 ms、JIT 8.445 → 7.877 msで、全体の改善率へ一般化しません。
- **JsonRoundTrip:** 属性付きDTOのSerialize／Deserializeを10回繰り返し、PRはinterpreter 46.270 ms／JIT 48.238 ms、確保量24,588,336／24,268,152 bytesでした。
  masterは `IList<T>.get_Item` の未対応で失敗したため、改善率はありません。
- **ReflectionInvoke:** メソッド検索後、boxed int引数でInvokeを100回行う項目です。
  PRでは `System.RuntimeMethodHandle::GetUtf8NameInternal` に実行可能な本体・登録がないという例外で失敗しました。
  masterは0.971／0.960 msですがPRの時間・確保量・命令数は取得できていません。
  既存のInvoke互換性テストが成功していても、この追加ワークロードの未対応経路が解消されたとは扱いません。

## ホスト確保量

| workload | master interp bytes | PR interp bytes | master JIT bytes | PR JIT bytes |
|---|---:|---:|---:|---:|
| SpanCopies | 12,182,440 | 15,646,520 | 12,086,440 | 15,358,520 |
| IntegerFormatting | 2,892,960 | 3,260,960 | 2,884,960 | 3,252,960 |
| IntegerParsing | 2,276,968 | 2,636,968 | 2,364,968 | 2,724,968 |
| StringCopies | 2,149,656 | 2,293,672 | 2,053,656 | 2,197,672 |
| ReflectionInvoke | 650,184 | — | 650,184 | — |
| ReflectionAttributes | 1,952,168 | 34,456,168 | 1,901,768 | 30,435,768 |
| JsonRoundTrip | — | 24,588,336 | — | 24,268,152 |
| Arithmetic | 516,968 | 516,968 | 516,968 | 516,968 |
| ArithmeticLoop | 516,960 | 516,960 | 516,960 | 516,960 |
| BranchLoop | 516,960 | 516,960 | 516,960 | 516,960 |
| ArraySum | 837,192 | 837,200 | 837,192 | 837,200 |
| FieldAccess | 518,472 | 518,632 | 517,792 | 517,952 |
| GenericFieldAccess | 519,296 | 519,552 | 518,616 | 518,872 |
| MethodCalls | 1,188,968 | 1,188,968 | 516,968 | 516,968 |
| CallLoop | 70,916,960 | 70,916,960 | 516,960 | 516,960 |
| ObjectLoop | 22,596,960 | 23,876,960 | 14,516,960 | 15,796,960 |
| List | 3,653,368 | 3,750,040 | 3,652,496 | 3,749,168 |
| ListGrowth | 2,225,264 | 2,283,480 | 2,222,952 | 2,281,168 |
| Linq | 5,876,896 | 6,806,168 | 5,217,696 | 6,104,224 |
| DictionaryInt | 6,938,808 | 8,442,016 | 6,470,312 | 7,858,224 |
| DictionaryGrowth | 6,347,064 | 7,399,768 | 5,783,336 | 6,758,568 |
| DictionaryString | 6,423,832 | 7,344,728 | 5,393,256 | 6,255,496 |
| AsyncCompleted | 1,081,880 | 1,088,872 | 1,081,880 | 1,088,872 |
| ValueTaskCompleted | 1,227,528 | 1,249,056 | 1,227,528 | 1,249,056 |
| AsyncWorkers | 703,776 | 714,552 | 699,104 | 708,696 |

## IL命令数

JITとinterpreterで元のIL命令の会計を記録します。属性取得の両モードでは実行経路も異なり、同じ命令数ではありません。

| workload | master interp IL | PR interp IL | master JIT IL | PR JIT IL |
|---|---:|---:|---:|---:|
| SpanCopies | 76,020 | 80,020 | 76,020 | 80,020 |
| IntegerFormatting | 24,010 | 24,010 | 24,010 | 24,010 |
| IntegerParsing | 17,010 | 17,010 | 17,010 | 17,010 |
| StringCopies | 17,017 | 17,017 | 17,017 | 17,017 |
| ReflectionInvoke | 2,025 | — | 2,025 | — |
| ReflectionAttributes | 1,810 | 158,410 | 1,810 | 159,560 |
| JsonRoundTrip | — | 113,006 | — | 113,006 |
| Arithmetic | 85,010 | 85,010 | 85,010 | 85,010 |
| ArithmeticLoop | 2,300,010 | 2,300,010 | 2,300,010 | 2,300,010 |
| BranchLoop | 2,150,056 | 2,150,056 | 2,150,056 | 2,150,056 |
| ArraySum | 340,023 | 340,023 | 340,023 | 340,023 |
| FieldAccess | 95,015 | 95,015 | 95,015 | 95,015 |
| GenericFieldAccess | 95,015 | 95,015 | 95,015 | 95,015 |
| MethodCalls | 16,010 | 16,010 | 16,010 | 16,010 |
| CallLoop | 1,900,010 | 1,900,010 | 1,900,010 | 1,900,010 |
| ObjectLoop | 250,010 | 250,010 | 250,010 | 250,010 |
| List | 61,597 | 61,597 | 61,597 | 61,597 |
| ListGrowth | 40,134 | 40,134 | 40,134 | 40,134 |
| Linq | 43,296 | 44,300 | 43,296 | 44,300 |
| DictionaryInt | 90,578 | 92,980 | 90,578 | 92,980 |
| DictionaryGrowth | 84,174 | 85,788 | 84,174 | 85,788 |
| DictionaryString | 98,283 | 99,505 | 98,283 | 99,505 |
| AsyncCompleted | 8,257 | 8,257 | 8,257 | 8,257 |
| ValueTaskCompleted | 8,459 | 8,459 | 8,459 | 8,459 |
| AsyncWorkers | 1,111 | 1,111 | 1,111 | 1,111 |

## 生データと再現

各JSONに環境、revision、入力、期待値、全時間サンプル、中央値、確保量、命令数、失敗内容を保存しています。
時間・確保量・命令数の中央値は独立に計算され、同じサンプルに由来するとは限りません。

- [master interpreter](benchmarks/2026-10-04/pr34/master-interpreter.json)
- [master JIT](benchmarks/2026-10-04/pr34/master-jit.json)
- [PR interpreter](benchmarks/2026-10-04/pr34/current-interpreter.json)
- [PR JIT](benchmarks/2026-10-04/pr34/current-jit.json)

```powershell
$benchmarkTieredCompilation = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet build DotnetVM.Benchmarks -c Release
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --revision e277241 --output artifacts/performance/pr-interpreter.json
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --jit --revision e277241 --output artifacts/performance/pr-jit.json
} finally {
    $env:DOTNET_TieredCompilation = $benchmarkTieredCompilation
}
```

masterは `git archive 2950044` を別ディレクトリへ展開し、PRの `DotnetVM.Benchmarks/Program.cs` と `GuestWorkloads.cs` をコピーして独立してビルドします。
ランタイムと改造BCLはmasterのままです。測定後の明示的な `using System` の追加はテストの単体コンパイル用で、SDKのimplicit usingと同じ参照です。
この記録の全体実行は上記の未対応項目を含むため**終了コード1**を返します。成功項目のJSONも保存されます。
再現時もエラーを成功扱いしたり、失敗項目をゼロとして平均したりしないでください。

過去の結果は [BCLの初回測定](performance-bcl.md)、[2026-10-03の計測ガイドと記録](performance.md)、[ベンチマーク履歴](benchmark-history.md) に保存しています。
