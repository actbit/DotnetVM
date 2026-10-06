# BCL と Span の初回性能測定（履歴）

最新の再測定は [PR #34の性能測定](performance-pr34.md) に保存しています。
以下は旧master `e42b3e1` との当時の比較です。「整数解析31.7倍」「Span確保量約40%削減」は現在のmasterに対するPR #34の効果ではありません。

基点は `origin/master` の `e42b3e1`（PR #31 まで）です。
改造 BCL とランタイムの両方を変更し、同じゲストメソッド・入力・期待値で比較しました。

## 変更内容

- `NumberFormatting.Int32ToString` / `Int64ToString` は、結果を使わない unsigned 書式設定を実行してから
  CultureSettings を呼んでいました。discard された処理を除き、指定カルチャの結果を直接返します。
- `ParseInt32` / `ParseInt64` も、結果を使わない ParseMagnitude と host parser の両方を実行していました。
  重複解析を除きます。先の固定 ASCII 符号検査がカスタム NegativeSign を拒否する問題も解消します。
- スロット列同士の Buffer.Memmove は一時 StackSlot 配列を確保して 2 回コピーしていました。
  Array.Copy に置き換え、重なりを保持します。構造体はコピー先で値を複製し、プリミティブは追加走査を省きます。
- raw struct layout を型ごとに weak cache へ保存します。構造体の packing / alignment / FieldLayout と
  型引数を扱い、Unsafe の BitCast / ReadUnaligned / WriteUnaligned で同じ表現を使います。
- VmString / #US heap は UTF-16 を直接保存・読み出し、不正サロゲートを Encoding.Unicode で置き換えないようにします。
  この変更は互換性の修正であり、文字列全般の高速化は主張しません。

命令数クォータ、readonly、バッファ境界、ホスト作業量、構造体の値コピー、GC のルートを維持します。
改造 BCL の公開面は引き続き managed IL としてトレースされ、カルチャの境界は既存の CultureSettings を通ります。

## 前後測定

Windows 10.0.26200、.NET 10.0.12、Release、DOTNET_TieredCompilation=0。
LoadHostCoreLib=true、VM JIT=true、昇格閾値 2、warmup 3、7 samples、入力サイズ 1,000 です。
master と変更後を順番に実行し、測定中はテストや別のベンチマークを同時に実行していません。
同じ workload を CLR の型付きデリゲートでも実行し、各 VM の戻り値と一致することを検査します。

| workload | master median ms | 変更後 median ms | master allocated bytes | 変更後 allocated bytes | master IL instructions | 変更後 IL instructions |
|---|---:|---:|---:|---:|---:|---:|
| IntegerFormatting | 9.8556 | 7.5835 | 3,972,840 | 2,833,744 | 38,007 | 24,010 |
| IntegerParsing | 169.3676 | 5.3492 | 70,046,528 | 2,313,752 | 524,010 | 17,010 |
| SpanCopies | 34.9127 | 33.4909 | 19,943,952 | 11,923,224 | 76,020 | 76,020 |

整数書式は時間約 23%・確保量約 29% 減、整数解析は時間約 31.7 倍・確保量約 97% 減です。
Span コピーは時間差約 4% にとどまりますが、確保量は約 40% 減っています。
これは特定 workload の測定で、アプリケーション全体の倍率や全 BCL API の改善を示すものではありません。
raw struct layout cache、UTF-8、JSON、Regex、Crypto、HTTP の性能をこの表から推定しません。

確保量は GC.GetTotalAllocatedBytes(true) の差分で、プロセス全体のホスト確保です。
VM のクォータ会計のバイト数とは別です。ロード・初回準備・JIT 昇格を時間から除きます。
測定前の強制 GC は CoreCLR／VM のどちらにも行わず、実行中の通常の GC は時間に含めます。
実行中の GC は時間に含めます。各 sample の時間、CLR 比較、設定は
[変更前 JSON](benchmarks/2026-10-04/bcl-before-jit.json) と
[変更後 JSON](benchmarks/2026-10-04/bcl-after-jit.json) に保存しています。

## 再現

今回追加した IntegerFormatting / IntegerParsing / StringCopies / SpanCopies を含む通常の runner は unified-22 です。
過去の unified-18 の記録は変更しません。StringCopies は文字列の確認用で、上の改善率には含めません。

```powershell
$benchmarkTieredCompilation = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet build DotnetVM.Benchmarks/DotnetVM.Benchmarks.csproj -c Release
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --jit --filter Integer --size 1000 --output integer.json
    dotnet DotnetVM.Benchmarks/bin/Release/net10.0/DotnetVM.Benchmarks.dll --jit --filter SpanCopies --size 1000 --output span.json
} finally {
    $env:DOTNET_TieredCompilation = $benchmarkTieredCompilation
}
```

変更前は `git archive e42b3e1` を別のディレクトリへ展開し、今回の runner の Program.cs と GuestWorkloads.cs だけをコピーして
同じ手順で独立してビルドします。DotnetVM / DotnetVM.CoreLib は master のままです。
`--jit` を省略するとインタプリタを測定でき、`--filter` を省略すると全 22 項目を実行します。
