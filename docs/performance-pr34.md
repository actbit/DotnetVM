# PR #34 の性能再測定（2026-10-04、反復Invoke修正後）

最新master `2950044` とPR `8455575` を同じ環境・ゲストコードで再測定しました。
25項目すべてがPRのInterpreter／JITで成功し、戻り値はCoreCLRと一致しました。masterはJSON往復のみ失敗し、両版で比較できたのは24項目です。
全体の高速化を示す結果はありません。属性取得などには元のCoreLib ILを実行するための時間・確保量の増加があります。

## 反復Invokeの修正と測定範囲

以前の測定ではPRのReflectionInvokeが未登録のGetUtf8NameInternalで失敗しました。
追跡すると、2回目のInvokeが生成IL経路に進み、Spanのインデクサー検索やCoreCLRのネイティブ戻りアドレスを前提にしたNextCallReturnAddressで失敗することが分かりました。[生成スタブのCoreLib実装](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Reflection/InvokerEmitUtil.cs)も参照してください。
VM起動時に元のAppContext.SetSwitch ILで `Switch.System.Reflection.ForceInterpretedInvoke=true` を設定し、[CoreLib標準のネイティブ呼び出し経路](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Reflection/MethodInvokerCommon.cs)を選択します。
Invoke・引数検証・例外ラップ・ref/outのILを置き換えていません。このスイッチはCoreLibのInvoke戦略に対する設定で、VM JITは両モードで通常どおり動作します。
生成Invokeスタブの完全対応は今回の修正に含みません。ゲストがこの設定を初回Invoke前に無効化すると、未対応の生成経路に入る場合があります。
診断用のメソッド名には実InternalCallのGetUtf8NameInternalを追加しました。

ReflectionInvokeはメソッド検索後、同じboxed intの引数配列で100回Invokeします。
PRはInterpreter **21.145 ms**／JIT **17.972 ms**、確保量7,460,984／7,284,880 bytesでした。
専用Invokeだったmasterから元のBCL ILへ実行経路が変わったため、この差は互換性対応の性能コストとして記録します。
単発のInvokeに加え、同じMethodInfoと引数配列で100回反復する回帰テスト、実際のベンチマークソースのDebug／Release×Interpreter／JIT・GC下の反復テストを追加しました。

## 条件

- AMD Ryzen 9 3900、Windows x64 10.0.26200、SDK 10.0.401／runtime 10.0.12、Release。
- DOTNET_TieredCompilation=0、LoadHostCoreLib=true、VM JIT昇格閾値2、warmup 3回、7 samples、Protocol unified-25。
- master Interpreter → master JIT → PR Interpreter → PR JITの順に別プロセスで直列実行。ビルドとテストを並行実行していません。
- 表は入力全体を処理するゲストメソッド1呼び出しの中央値。属性取得は50回、JSONは10回の往復を含みます。
- ロード、VM起動時のAppContext設定、初回準備、JIT昇格、サンプル前のホストGC、初回JSONメタデータ構築を時間から除外。実行中のGC、VMの公開Invoke入口、結果照合は含みます。
- 確保量はGC.GetTotalAllocatedBytes(true)の差分によるプロセス全体のホスト確保量。VMメモリクォータや常駐メモリの値ではありません。
- 7サンプルの1回の前後測定です。小さな差の再現性・統計的有意性は未確認。AsyncWorkersは待機・スケジューリングを含むため高速化の評価から除外します。

## 実測時間

PR/masterは時間比で、1より大きいほどPRが遅くなります。ERRORは失敗で、ゼロ時間ではありません。

| workload | 入力 | master interp ms | PR interp ms | PR/master | master JIT ms | PR JIT ms | PR/master |
|---|---:|---:|---:|---:|---:|---:|---:|
| SpanCopies | 1,000 | 33.652 | 38.085 | 1.13× | 34.691 | 37.736 | 1.09× |
| IntegerFormatting | 1,000 | 8.276 | 9.693 | 1.17× | 7.788 | 8.540 | 1.10× |
| IntegerParsing | 1,000 | 5.819 | 6.976 | 1.20× | 6.136 | 5.700 | 0.93× |
| StringCopies | 1,000 | 4.889 | 5.088 | 1.04× | 4.340 | 4.380 | 1.01× |
| ReflectionInvoke | 100 | 1.071 | 21.145 | 19.75× | 1.039 | 17.972 | 17.30× |
| ReflectionAttributes | 50 | 1.752 | 88.644 | 50.60× | 1.378 | 85.419 | 61.98× |
| JsonRoundTrip | 10 | ERROR | 50.680 | — | ERROR | 55.874 | — |
| Arithmetic | 5,000 | 5.596 | 5.265 | 0.94× | 3.835 | 3.955 | 1.03× |
| ArithmeticLoop | 100,000 | 128.849 | 121.068 | 0.94× | 85.350 | 88.663 | 1.04× |
| BranchLoop | 100,000 | 123.862 | 116.104 | 0.94× | 81.319 | 84.277 | 1.04× |
| ArraySum | 10,000 | 21.291 | 19.613 | 0.92× | 14.572 | 14.153 | 0.97× |
| FieldAccess | 5,000 | 7.226 | 6.905 | 0.96× | 5.638 | 5.501 | 0.98× |
| GenericFieldAccess | 5,000 | 7.362 | 7.062 | 0.96× | 7.872 | 7.464 | 0.95× |
| MethodCalls | 1,000 | 3.187 | 2.634 | 0.83× | 1.385 | 1.400 | 1.01× |
| CallLoop | 100,000 | 225.943 | 219.183 | 0.97× | 85.663 | 89.319 | 1.04× |
| ObjectLoop | 10,000 | 68.708 | 71.454 | 1.04× | 51.867 | 53.497 | 1.03× |
| List | 500 | 11.538 | 11.693 | 1.01× | 11.894 | 11.924 | 1.00× |
| ListGrowth | 500 | 7.294 | 7.182 | 0.98× | 7.721 | 8.223 | 1.07× |
| Linq | 500 | 16.727 | 23.316 | 1.39× | 15.387 | 16.262 | 1.06× |
| DictionaryInt | 200 | 25.053 | 29.127 | 1.16× | 23.335 | 25.092 | 1.08× |
| DictionaryGrowth | 200 | 19.916 | 21.086 | 1.06× | 18.869 | 20.770 | 1.10× |
| DictionaryString | 200 | 19.871 | 21.416 | 1.08× | 19.114 | 20.159 | 1.05× |
| AsyncCompleted | 200 | 2.143 | 2.124 | 0.99× | 2.250 | 2.475 | 1.10× |
| ValueTaskCompleted | 200 | 2.113 | 2.228 | 1.05× | 2.859 | 2.308 | 0.81× |
| AsyncWorkers | 8 | 12.273 | 11.073 | 0.90× | 12.316 | 10.873 | 0.88× |

属性取得はInterpreter 50.60×／JIT 61.98×の時間がかかりました。
SpanやDictionaryにもコストの増加があります。各内部変更の寄与は未分離です。
JSONはPRで成功しましたが、masterはIList<T>.get_Itemの未対応で失敗したため速度比はありません。
過去の整数解析31.7倍・Span確保量40%削減は旧masterとの当時の比較であり、今回のPRの効果ではありません。

## ホスト確保量

| workload | master interp bytes | PR interp bytes | master JIT bytes | PR JIT bytes |
|---|---:|---:|---:|---:|
| SpanCopies | 12,182,440 | 15,646,520 | 12,086,440 | 15,358,520 |
| IntegerFormatting | 2,892,960 | 3,260,960 | 2,884,960 | 3,252,960 |
| IntegerParsing | 2,276,968 | 2,636,968 | 2,364,968 | 2,724,968 |
| StringCopies | 2,149,656 | 2,293,672 | 2,053,656 | 2,197,672 |
| ReflectionInvoke | 650,184 | 7,460,984 | 650,184 | 7,284,880 |
| ReflectionAttributes | 1,952,168 | 34,456,168 | 1,901,768 | 30,435,768 |
| JsonRoundTrip | — | 24,588,672 | — | 24,268,488 |
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
| Linq | 5,876,896 | 6,811,328 | 5,217,696 | 6,104,704 |
| DictionaryInt | 6,938,808 | 8,442,016 | 6,470,312 | 7,858,224 |
| DictionaryGrowth | 6,347,064 | 7,399,768 | 5,783,336 | 6,758,568 |
| DictionaryString | 6,423,832 | 7,344,728 | 5,393,256 | 6,255,496 |
| AsyncCompleted | 1,081,880 | 1,088,872 | 1,081,880 | 1,088,872 |
| ValueTaskCompleted | 1,227,528 | 1,249,056 | 1,227,528 | 1,249,056 |
| AsyncWorkers | 703,288 | 715,160 | 698,440 | 708,088 |

## IL命令数

JITとInterpreterは元のIL命令で会計します。モードによってBCL実行経路が異なる場合があります。

| workload | master interp IL | PR interp IL | master JIT IL | PR JIT IL |
|---|---:|---:|---:|---:|
| SpanCopies | 76,020 | 80,020 | 76,020 | 80,020 |
| IntegerFormatting | 24,010 | 24,010 | 24,010 | 24,010 |
| IntegerParsing | 17,010 | 17,010 | 17,010 | 17,010 |
| StringCopies | 17,017 | 17,017 | 17,017 | 17,017 |
| ReflectionInvoke | 2,025 | 40,584 | 2,025 | 40,584 |
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

各JSONに測定日時、環境、revision、入力、期待値、全時間サンプル、中央値、確保量、命令数、失敗内容を保存しています。
時間・確保量・命令数の中央値は独立に計算され、同じサンプル由来とは限りません。

- [master Interpreter](benchmarks/2026-10-04/pr34/invoke-fix/master-interpreter.json)
- [master JIT](benchmarks/2026-10-04/pr34/invoke-fix/master-jit.json)
- [PR Interpreter](benchmarks/2026-10-04/pr34/invoke-fix/current-interpreter.json)
- [PR JIT](benchmarks/2026-10-04/pr34/invoke-fix/current-jit.json)
- 修正前の失敗記録: [Interpreter](benchmarks/2026-10-04/pr34/current-interpreter.json)、[JIT](benchmarks/2026-10-04/pr34/current-jit.json)

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet build DotnetVM.Benchmarks -c Release
dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --revision 8455575 --output artifacts/performance/pr-interpreter.json
dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --jit --revision 8455575 --output artifacts/performance/pr-jit.json
```

masterはgit archive 2950044を別ディレクトリへ展開し、PRのDotnetVM.Benchmarks/Program.csとGuestWorkloads.csだけをコピーして独立してビルドします。
ランタイムと改造BCLはmasterのままです。PRの全体実行は終了コード0、masterはJSONの未対応を含むため終了コード1でした。
失敗項目を成功やゼロ時間として平均しないでください。

過去の結果: [BCLの初回測定](performance-bcl.md)、[2026-10-03の測定](performance.md)、[ベンチマーク履歴](benchmark-history.md)。
