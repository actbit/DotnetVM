# BCL 互換性と HTTP 制限

`origin/master` の `e42b3e1` を基点に、カルチャ・UTF-8・Span と追加 BCL の互換性を拡張しています。
以下は今回登録した API の範囲です。BCL 全体の互換性を保証するものではありません。

## 対応範囲

| 分野 | 対応・検証内容 |
|---|---|
| CultureInfo | 名前 / LCID からの構築、GetCultureInfo、InvariantCulture、CurrentCulture / CurrentUICulture、Clone / ReadOnly、基本プロパティ、NumberFormat、TextInfo の大文字小文字変換 |
| provider | CultureInfo / NumberFormatInfo を数値 ToString、Parse、Convert、String.Format、TryFormat、DateTime / TimeSpan の既存 provider 面へ渡す。null はゲストの現在のカルチャ |
| UTF8Encoding | BOM / strict フラグ、preamble、配列・文字列・Span の変換、要素数と容量検査、Encoder / Decoder の GetBytes / GetChars / GetByteCount / GetCharCount / Convert / Reset。分割された UTF-8 とサロゲートの状態を保持 |
| Span / Memory | CopyTo の重なりと構造体の値コピー、Clear / Fill、Memory / ReadOnlyMemory の Span、MemoryMarshal.CreateSpan / GetReference、Read / Write の構造体表現 |
| Unsafe | SizeOf、同一サイズの参照を含まない値の BitCast、Read / WriteUnaligned、NullRef、Subtract、ByteOffset、アドレス比較。ポインタは VM の管理バッファ内で範囲と readonly を検査 |
| SIMD | Vector64 / 128 / 256 / 512 の software 値表現、broadcast Create、Count / Zero / IsSupported、GetElement / WithElement、加減乗算。byte の桁あふれ、float / double、各幅を CLR と比較 |
| Regex | instance / static の IsMatch / Match / Replace / Split、Match / Group / Capture の基本プロパティ、Groups の名前 / 番号アクセス |
| Compression | GZip / Deflate / Brotli / ZLib とゲスト MemoryStream、CompressionMode / CompressionLevel、leaveOpen、Read / Write / CopyTo / Flush / Dispose |
| Crypto | SHA256 / 384 / 512.HashData と TryHashData、HMACSHA256.HashData、FixedTimeEquals、ZeroMemory、RandomNumberGenerator.GetBytes / Fill、AES の Key / IV と CBC / ECB の基本暗号化・復号 |
| HttpClient | string URL の GetStringAsync / GetByteArrayAsync / GetAsync / PostAsync、CancellationToken、StringContent / ByteArrayContent、response status / content / EnsureSuccessStatusCode。結果は VM の Task と値へ正規化 |
| System.Text.Json | 実在 DLL の JsonDocument / JsonElement と Utf8JsonWriter の managed IL を実行。日本語・絵文字・エスケープ・数値・bool・配列を CLR の結果と比較し、System.Text.Json の IL フレームを確認 |

ホスト BCL の状態は VM オブジェクトをキーにした内部の weak table に保持します。
ゲストオブジェクトをホストの任意の IFormatProvider やネイティブポインタとして渡しません。
`LoadHostCoreLib=true` では、ホストが選んだ Regex / HttpClient / Compression / Crypto / TextEncoder の
実在アセンブリをロードして、登録した境界を利用します。ゲストが同名 DLL を持ち込んでも
trusted BCL の印は付きません。CoreLib 専用 caller domain の権限はそのままです。

ゲストの現在のカルチャは VM ごとの AsyncLocal に保持し、Task へ引き継ぎます。
外側のゲスト呼び出しが終わるとホストの CurrentCulture / CurrentUICulture を復元します。
VM の文字列は UTF-16 コード単位を保持するため、不正サロゲートも UTF8Encoding の strict 検査に届きます。

## HTTP の設定

HTTP は `NetworkPolicy.Http` と `IHttpNetworkBridge` の両方が必要です。
null の policy、空の origin リスト、bridge 未設定では通信を拒否します。

```csharp
using DotnetVM.Host;
using DotnetVM.Policy;

using var transport = new HttpNetworkBridge();
using var vm = new VirtualMachine(new VmHostOptions {
    LoadHostCoreLib = true,
    NetworkBridge = transport,
    Network = new NetworkPolicy {
        MaxBytesPerRequest = 2 * 1024 * 1024,
        TotalTransferByteLimit = 10 * 1024 * 1024,
        Http = new HttpPolicy {
            AllowedOrigins = new[] { new Uri("https://api.example.test") },
            AllowedMethods = new[] { "GET", "POST" },
            MaxRequests = 20,
            RequestTimeout = TimeSpan.FromSeconds(5),
            MaxRequestBodyBytes = 64 * 1024,
            MaxResponseBodyBytes = 1024 * 1024,
            MaxRequestHeaderBytes = 4096,
            MaxResponseHeaderBytes = 8192,
        },
    },
});
```

origin は scheme・host・port を一致させます。別 scheme / port、host の接尾辞、user-info、fragment を拒否します。
AllowedOrigins に URL の path を付けても path の許可条件にはなりません。
origin / method / header の許可リストは gateway の構築時にコピーします。
ヘッダーは `AllowedRequestHeaders` にある名前だけを通し、Host / Content-Length と CR / LF を拒否します。
現在のゲスト HttpClient 面は独自ヘッダーを公開していませんが、gateway のヘッダー制限は
ホストの TransferHttp / 将来追加するゲスト面にも適用します。

要求数、送信・受信 body の 1 要求上限と累計上限、各ヘッダー上限を検査します。
通信を開始した送信 body は transport が失敗しても計上します。
要求には VM shutdown、client Dispose、ゲスト CancellationToken、policy timeout を連結した token を渡します。
gateway は 1 VM の HTTP 要求を直列化し、通信中にも同じ gateway のクォータを共有します。
既存の WebClient が IHttpNetworkBridge を使う場合もこの HTTP 検査を通します。
従来の INetworkBridge だけを設定する構成では、従来どおりホストのプロキシが宛先を判断します。

標準 HttpNetworkBridge は response headers を先に取得し、Content-Length が上限を超えれば body を読みません。
長さ不明の応答もチャンクで読み、上限を超える 1 バイトを検出して拒否します。
自動リダイレクト・Cookie・自動展開は無効です。3xx は応答として返すため、転送先へ自動接続しません。
標準 handler の応答ヘッダー上限は 16 KiB で、policy に小さい値を設定するとさらに制限します。
独自 IHttpNetworkBridge / HttpMessageHandler は MaxResponseBytes / MaxResponseHeaderBytes、token と
リダイレクト無効化を守る信頼済みホスト実装として扱います。ホストが transport を所有し、破棄します。

## 計上と制限

Regex は既定で pattern 16 Ki 文字、input 1 Mi 文字、match timeout 250 ms までです。
`MemoryPolicy.MaxRegexPatternLength` / `MaxRegexInputLength` / `MaxRegexMatchTimeoutMilliseconds` で変更できます。
Compiled フラグは host bridge で除去します。Replace の `$'` / `$&` 等による展開と Split の capture を含む
出力は構築前に保守的な上限で host allocation / work を計上します。このため、実際の出力が小さくても
複雑な replacement / capture を持つ大きい入力がクォータで拒否される場合があります。

圧縮の CopyTo は最大 8 KiB のチャンクを使い、展開するたびに shutdown と host work / allocation を検査します。
展開後の MemoryStream の増大を HostTempAllocationByteLimit で拒否でき、圧縮データの小ささだけでは上限を迂回できません。
Crypto の乱数は `VmHostOptions.RandomFill` を通します。HTTP worker は既存の VM worker 数の上限を消費します。

ハードウェア ISA の IsSupported / IsHardwareAccelerated は false を維持します。
参照を含む構造体の raw memory 解釈、任意のホストアドレス、独自 ICustomFormatter のゲストコールバック、
Encoding の pointer overload / 独自 fallback、Regex の evaluator / Matches collection、CryptoStream / RSA は追加していません。
HttpClient の SendAsync / HttpRequestMessage、BaseAddress、guest handler / headers も今回の範囲外です。
JSON の検証は reader / document / writer に絞っています。reflection を使う JsonSerializer の全面互換性は未対応です。

## テストと性能

`BclCompatibilityTests` / `AdditionalBclTests` は同じゲスト DLL を CLR と VM で実行して比較します。
`HttpPolicyTests` は拒否する要求が transport に届かないこと、要求数・通信量・ヘッダー・timeout、
長さ不明の body と 3xx を検証します。ネットワークのテストは fake transport で外部通信を行いません。
全体テストでは既存の IL verifier / caller domain / quota / GC / concurrency の検証も実行します。

master の CI で失敗していた Task worker の shutdown は、ゲスト実行から先に停止を観測しても
host ObjectDisposedException を保持します。Delay は完了を公開する前に timer quota を解放します。

改造 BCL の重複処理とランタイムのコピー処理の最適化、再現手順、測定値は
[BCL と Span の性能測定](performance-bcl.md) を参照してください。
