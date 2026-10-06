# DotnetVM

C# / .NET 10 で実装した、.NET アセンブリの IL を実行する仮想マシンです。
ホストアプリケーションに組み込み、ゲストコードの命令数・メモリ・CPU 作業・I/O・並行実行に上限を設定できます。

| 機能 | 概要 |
|---|---|
| IL 実行 | インタプリタを標準で使用。任意で簡易 JIT を有効化 |
| オブジェクトと GC | 独自のオブジェクトモデルとマーク＆スイープ GC |
| リソース制御 | 命令数、確保量、生存メモリ、ホスト作業量、ワーカー数などを制限 |
| ホストとの接続 | 仮想コンソールと、ホストが実装するネットワーク・ストレージブリッジ |
| .NET の機能 | 例外、ジェネリック、Thread、Task / ValueTask、async / await、VM 内での動的コード生成 |
| 診断 | IL 逆アセンブル、実行トレース、ブレークポイントとステップ実行 |

## 目次

- [クイックスタート](#クイックスタート)
- [実行モデル](#実行モデル)
- [リソース制御と入出力](#リソース制御と入出力)
- [CoreLib とランタイムバインド](#corelib-とランタイムバインド)
- [互換性と制約](#互換性と制約)
- [トレースとデバッグ](#トレースとデバッグ)
- [ベンチマーク](#ベンチマーク)
- [プロジェクト構成](#プロジェクト構成)
- [テストと開発状況](#テストと開発状況)

## クイックスタート

### 前提環境

- **ホスト**: .NET SDK 10.0
- **ゲスト**: .NET 5〜.NET 10 の DLL アセンブリ。実行できる API と IL の範囲は[互換性と制約](#互換性と制約)を参照してください。
- **依存関係**: 本体と `DotnetVM.CoreLib` に外部 NuGet 依存はありません。Roslyn と xUnit はテストプロジェクトで使用します。

### ビルドと参照

リポジトリのルートで実行します。

```bash
dotnet build DotnetVM.slnx --configuration Release
dotnet test DotnetVM.Tests/DotnetVM.Tests.csproj --configuration Release --no-build --no-restore
```

ホストアプリケーションからプロジェクト参照を追加します。

```bash
dotnet add path/to/HostApp.csproj reference DotnetVM/DotnetVM.csproj
```

### 最小の利用例

次のクラスを含むゲスト DLL を `MyGuest.dll` としてビルドします。

```csharp
namespace MyApp;

public static class Program {
    public static int Compute(int value) => value * 2;
}
```

ホスト側では VM を構築し、DLL をロードして静的メソッドを呼び出します。

```csharp
using DotnetVM.Host;

using var vm = new VirtualMachine(new VmHostOptions {
    Memory = new MemoryPolicy {
        InstructionQuota = 100_000_000,
        TotalAllocationByteLimit = 512L << 20, // 累計確保量: 512 MiB
        LiveObjectByteLimit = 128L << 20,      // 生存オブジェクト: 128 MiB
        MaxRecursionDepth = 512,
    },
});

vm.LoadAssembly("MyGuest.dll");
var result = vm.Invoke("MyApp.Program", "Compute", 42);
Console.WriteLine(result); // 84
```

EXE のエントリポイントは探索せず、ホストが型とメソッドを指定します。
インスタンスメソッドには `CreateInstance` と `CallInstance`、GC をまたぐホスト保持参照には `GcHandleTable` を使用します。

組み込み手順、ブリッジ実装、ポリシー設計、例外処理、運用上の詳細は[ホスト統合ガイド](docs/host-integration.md)を参照してください。

## 実行モデル

### インタプリタとオブジェクトモデル

インタプリタはメソッドごとにデコードした IL を実行し、命令境界でクォータとセーフポイントを検査します。
実行前には評価スタックを検証し、スタックの不足・超過、分岐合流時の高さの不一致、不正な引数・ローカル参照、
`ret` / `call` の形状やプレフィックスの配置を `BadImageFormatException` として拒否します。

ゲストの値とオブジェクトは `StackSlot`、`VmClassInstance`、`VmStructValue`、`VmArray`、`VmBoxedValue` などで表現します。
VM ヒープの確保量は `VmHeap` で計上し、実行中の参照を GC ルートとして管理します。

### 簡易 JIT

`VmHostOptions.EnableJit = true` で有効になります。既定は無効です。
`JitPromotionThreshold` 回呼び出されたメソッドを式ツリーから VM 内部デリゲートへコンパイルします。
キャッシュは VM とローダーごとに分離し、アンロード時に破棄します。

| 項目 | 対応範囲 |
|---|---|
| 基本操作 | 引数・ローカル、算術、比較、分岐、変換、文字列リテラル |
| 呼び出しとデータ | `call` / `callvirt`、配列、フィールド、`newobj`、box、cast、`ldtoken` |
| マネージ参照 | `ldarga` / `ldloca` / `ldelema` / `ldflda` / `ldsflda`、間接アクセス、値型コピー |
| インタプリタで実行 | EH、アンマネージポインタ、typed reference、`tail.` / `constrained.` など、JIT が未対応の IL |

生成コードも VM のスロットとフレームを使い、命令クォータ・セーフポイント・GC ルート登録を継続します。
マネージ参照（ByRef）は参照先の所有オブジェクトを保持し、読み取り専用の制約もフィールド参照へ伝播します。
コンパイルとアンロードが競合した場合は、生成したデリゲートを破棄します。

整数スロットだけで閉じるメソッドには、IL の基本ブロックを直接実行するスカラー JIT を使用できます。
純粋な `int32` static メソッド呼出しは検証後に式木へインライン化し、ローカル値の算術更新は IL 融合でまとめます。
トップレベルのスカラー呼出しではフレームを省略できますが、命令課金、クォータ、セーフポイント、例外時の後始末は維持します。

コンパイルには `JitCompilationBudget`、`HostWorkBudget`、`HostTempAllocationByteLimit` を適用します。
保持数と複雑度は `MaxJitCompiledMethods`、`MaxJitCacheEntries`、`MaxJitExpressionNodes`、`MaxJitMethodBodyBytes` で制限し、
上限を超えたメソッドはインタプリタへフォールバックします。
式ツリーからのデリゲート生成はホスト側で行うため、JIT 実装は VM の信頼基盤に含まれます。

### 実行経路の最適化

デコード済み IL、分岐オフセット、ローカル初期値、不変の呼び出し先をメソッド・ローダー単位で再利用します。
実行用の read lease と JIT フレームを値型にし、命令ごとのオブジェクト確保を減らしています。
メソッド準備キャッシュには `MaxPreparedMethods` と `MaxPreparedMethodBytes` の上限があります。

署名解析や仮想メモリ操作には `ReadOnlySpan<byte>`、`Span<byte>`、`stackalloc`、`BinaryPrimitives` を使用します。
GC 要求がない命令境界では停止処理を省き、要求がある場合は全ゲストスレッドを停止して回収します。
これらの経路でも、クォータ、境界検査、GC ルート、ByRef の所有権、ローダーの生存確認を継続します。

## リソース制御と入出力

クォータの超過は `ResourceExhaustedException` 系の管理例外としてホストへ伝播し、ゲストの `catch` では握りつぶせません。

| 対象 | 主な設定・制御 |
|---|---|
| 命令数・再帰 | `InstructionQuota`、`MaxRecursionDepth`。intrinsic 呼び出しも命令数を追加消費 |
| VM ヒープ | `TotalAllocationByteLimit`、`LiveObjectByteLimit`。`newarr` / `localloc` はホストでの実確保前に上限を検査 |
| ホストの処理 | `HostWorkBudget`、`HostTempAllocationByteLimit`。重い intrinsic の作業量と一時バッファを計上 |
| アセンブリ・解析 | `MaxAssemblyBytes`、`LoadedAssemblyHostByteLimit`、メタデータ行数・メソッドサイズ・署名深度の上限 |
| JIT・キャッシュ | コンパイル予算、生成数、式ツリー規模、準備済みメソッドの保持数・サイズを制限 |
| ネットワーク | `NetworkGateway` と `NetworkPolicy` で要求ごと・累計の転送量を計上 |
| ストレージ | `StorageGateway` と `StoragePolicy` で操作ごと・累計の転送量を計上 |
| 並行実行 | `MaxGuestThreads`、`MaxTaskWorkers`、`MaxGuestWorkers`、`MaxPendingTaskTimers`、`MaxTaskCombinatorInputs` |

信頼済みコードの性能測定などで命令数課金を外す場合は、`MemoryPolicy.InstructionChargingEnabled = false` を指定します。
この場合、`InstructionCount` は増加せず `InstructionQuota` も判定されませんが、Dispose・キャンセル・GC のセーフポイントと、
ヒープ・ホスト作業・再帰など他の制限は有効です。停止性も必要な場合は、例えば次のように実時間上限を併用します。

```csharp
Memory = new MemoryPolicy {
    InstructionChargingEnabled = false,
    ExecutionTimeout = TimeSpan.FromSeconds(2),
};
```

上限超過時は `ExecutionTimeoutException` がホストへ伝播します。タイムアウトは安全な命令境界で検出されるため、
信頼しないゲストでは既定の命令課金 (`InstructionChargingEnabled = true`) も併用してください。

課金処理は既定で `InstructionChargeBatchSize = 256` 命令ずつ予約します。未使用分は実行終了時に返却され、
完了後の `InstructionCount` は実際に実行した命令数です。厳密な命令単位の予約が必要な場合は `1` を指定してください。

### GC

既定はマーク＆スイープです。`IGcStrategy` を差し替えて別の回収戦略を実装でき、オブジェクトには世代情報を保持します。
回収はセーフポイントで行い、並行実行中は全ゲストスレッドを停止します。

GC ルートには、実行フレームの引数・ローカル・評価スタック・送出中の例外、静的フィールド、
`GcHandleTable` によるホスト保持参照、intrinsic の静的状態などを含みます。
循環参照を回収でき、ByRef から参照先の所有オブジェクトも走査します。

### ネットワークとストレージ

ホストが `INetworkBridge` / `IStorageBridge` を実装し、`VmHostOptions` に設定します。
従来のブリッジは接続先やパスの許可を判断し、VM が転送量を計上します。
`HttpClient` には `IHttpNetworkBridge` と `NetworkPolicy.Http` を設定します。
VM が origin・メソッド・ヘッダーと要求数・転送量・タイムアウトを検査します。
設定例と対応 API は [BCL 互換性と HTTP 制限](docs/bcl-compatibility.md) を参照してください。

ブリッジが未設定の場合、対応する `System.Net.WebClient` / `System.IO.File` のファサード型は合成しません。
ゲストの I/O はゲートウェイを通じてホストへ委譲されます。ホスト自身による `LoadAssembly(path)` は、明示した DLL を直接ロードする API です。

### 仮想コンソール

ゲストの `Console` 入出力は `VmConsole` に集約します。出力イベントの購読、入力の供給、`IVirtualConsoleBinding` による実装全体の差し替えができます。

```csharp
var input = new Queue<string>(["hello"]);
vm.Console.OutputWritten += output => Console.Write(output.Text);
vm.Console.BindInput(() => input.Count > 0 ? input.Dequeue() : null);
```

## CoreLib とランタイムバインド

### BCL の呼び出しを解決する仕組み

`System.String`、`Math`、`Console`、`Convert`、例外などの API は、managed IL、intrinsic、ランタイムバインドで提供します。
intrinsic は VM が登録した実装、ランタイムバインドはメソッド署名と実装を対応させる仕組みです。
登録は実行開始前に行い、レジストリの `Seal` 後は追加できません。
ホスト独自の契約アセンブリと実装も `VirtualMachine.RegisterIntrinsic` で登録できます。

呼び出しは VM のゲートを通り、命令数の計上、セーフポイント検査、メモリの計上、I/O の仲介、VM 値への正規化を行います。
解決には次の経路を使います。

| 経路 | 照合・処理 |
|---|---|
| ランタイムバインド | `BindingKey` の型名、メソッド名、パラメータ型名、`this` の有無で照合 |
| managed IL | ゲストや CoreLib の IL 本体を実行。`IlPreferred` 指定や置換対象の API はレガシー intrinsic より優先 |
| レガシー intrinsic | 名前と引数個数で照合する互換経路 |
| 未対応の API | 未登録の InternalCall は `NotSupportedException`、P/Invoke は `OperationNotAllowedException` で拒否 |

バインドには `BindingOrigin`（`Managed` / `InternalCall` / `PInvokeReplacement` / `Device`）が付き、`vm.Bindings` で監査できます。
ネイティブコードは実行せず、登録済みの `PInvokeReplacement` に限って VM 内の代替実装へ委譲します。

### 実在 CoreLib と VM CoreLib

`VmHostOptions.LoadHostCoreLib = true` で、ホストの `System.Private.CoreLib.dll` をロードして managed IL を実行します。
`System.Runtime` などの参照アセンブリとの型統合、署名に基づく仮想・インターフェース呼び出し（明示的インターフェース実装を含む）、
参照元 DLL と同じディレクトリからの依存解決に対応します。
ストリームをロードする際に `sourcePath` を省略すると、ディレクトリの自動探索は行いません。

実在 CoreLib の一部には、VM で表現できないポインタ操作や内部キャッシュへの依存があります。
その API は `VmCoreLibSurfaces` の対応表に従い、**`DotnetVM.CoreLib` の managed IL** へ置換します。
置換は `Interpreter.Invoke` で行うため、直接呼び出しと仮想呼び出しに同じ対応を適用します。

`DotnetVM.CoreLib` は外部依存のないクラスライブラリで、単体でも CLR 上で動作します。
CLR との差分テストで数値書式・解析・変換や文字列操作を検証し、VM では設定したカルチャを内部ブリッジで渡します。
`LoadHostCoreLib` を使う配布では、`DotnetVM.CoreLib.dll` を `DotnetVM.dll` と同じディレクトリに配置してください。

### 委譲先の監査

`CoreLibSurfaceAudit` は、CoreLib API の実行経路と委譲理由を分類する監査表です。

| 分類 | 実行先・役割 |
|---|---|
| `RealCoreLibIl` | 実在 CoreLib の managed IL。CoreLib 未ロード時の代替経路も監査 |
| `VmCoreLibSubstitute` | `DotnetVM.CoreLib` の置換 IL |
| `RuntimeInternal` | JIT intrinsic、InternalCall、ランタイム表現、カルチャ処理などの VM 側実装・ホスト委譲 |
| `Device` | 仮想コンソール、File / WebClient、その他のデバイス実装 |

`RuntimeInternal` の理由は `jit-intrinsic`、`internal-call`、`runtime-representation`、`culture-out-of-scope` で区別します。
`CoreLibSurfaceAuditTests` は登録と監査表の対応を双方向で確認し、置換 IL をバインドが隠す変更も検出します。

### カルチャと文字列

`VmHostOptions.Culture` に `CultureInfo` を指定します。既定は `InvariantCulture` です。
VM は外側のゲスト呼び出し中に `CurrentCulture` / `CurrentUICulture` を設定し、終了時にホストスレッドの値を復元します。
文字列比較・大文字小文字変換、数値と decimal の書式・解析、DateTime / TimeSpan の書式・解析で使用します。
ゲストの `CultureInfo.CurrentCulture` / `CurrentUICulture` の変更は実行コンテキストに保持し、Task に引き継ぎます。
`CultureInfo` / `NumberFormatInfo` の明示的な provider も数値の書式・解析に渡します。
UTF8Encoding、Span / Unsafe / software vector、Regex、Compression、Crypto の対応範囲と
実在 `System.Text.Json` の検証内容は [互換性ドキュメント](docs/bcl-compatibility.md) に記載しています。
`CultureInfo` オブジェクト自体を扱うゲスト API は未実装です。

序数比較に基づく `CompareOrdinal`、`IndexOf(char)`、`Contains`、`Replace`、`Split` の置換は `StringOrdinalOps` が担当します。
登録済みの文字列比較バインドは、null と不正な `StringComparison` を CLR と同じ順序で検証します。
例えば、`"abc".CompareTo((string)null)` は正の値、`"abc".Equals(null, StringComparison.Ordinal)` は `false` を返します。
null の検索値や不正な比較種別による `ArgumentException` 系の例外は、ゲストの `catch` で捕捉できます。

## 互換性と制約

### .NET の機能

| 項目 | 対応範囲・制約 |
|---|---|
| アセンブリ | DLL をロードし、ホストが実行メソッドを指定。EXE のエントリポイント探索は非対応 |
| 例外 | `try` / `catch` / `finally` / `fault` / `filter`。未処理のゲスト例外は `UnhandledGuestException` としてホストへ伝播 |
| ジェネリック | TypeSpec / MethodSpec、継承・ネスト型、変性付きキャスト、`constrained.` |
| スレッド | ゲストの Thread、Monitor の競合・待機、並列ホスト呼び出し、スレッドごとの実行フレーム |
| 非同期 | Task / ValueTask、async / await、Delay / Run / FromResult、WhenAll / WhenAny / WaitAll、キャンセル、独自 awaiter、IValueTaskSource |
| 実行コンテキスト | SynchronizationContext の捕捉、`ConfigureAwait(false)` |
| 動的コード | `Assembly.Load`、式ツリーの `Compile`、`Reflection.Emit` を VM 内の表現へ変換して実行 |
| ロードコンテキスト | `AssemblyLoadContext` の Default、名前付きコンテキスト、各ロード API、Assemblies、Unload。ゲストのパスロードは StorageBridge 経由 |
| ネイティブ実行 | P/Invoke、Win32 API、任意のネイティブ依存は実行せず、対応する代替実装がなければ拒否 |
| native int | `I` / `U`、IntPtr / UIntPtr はホスト OS によらず **64 bit 固定**。32 bit のゲスト ABI は非対応 |

Thread と Task のワーカー上限は既定でそれぞれ 64、VM 全体のワーカー上限も 64 です。
未完了の `Task.Delay` タイマー上限は既定で 1,024 です。用途に応じて `VmHostOptions` で変更できます。
動的ロード・コード生成にも PE サイズ、ヒープ、命令数などのポリシーを適用します。

### IL 命令

ECMA-335 の IL 命令をデコードし、実装経路のない命令は `NotSupportedException` で拒否します。
以下の表は VM の対応範囲です。`Full` は CLR 突合テストで検証した範囲を示します。

| 区分 | 意味 |
|---|---|
| `Full` | CLR との比較テストで意味論を検証 |
| `Partial` | CLR との差分を伴う VM の実装 |
| `Prefix no-op` | プレフィックスを受け入れ、効果は適用しない |
| `Prefix modeled` | VM のメモリモデルで効果を適用 |
| `Rejected` | ロード時または実行時に拒否 |

#### Full

| 命令群 | 主な命令 |
|---|---|
| スタック・即値 | `nop`、`dup`、`pop`、`break`、`ldnull`、`ldc.*` |
| 引数・ローカル | `ldarg.*`、`starg.*`、`ldloc.*`、`stloc.*`、`ldarga`、`ldloca`（短形式・長形式を含む） |
| 算術・ビット演算 | `add`、`sub`、`mul`、`div(.un)`、`rem(.un)`、`and`、`or`、`xor`、`shl`、`shr(.un)`、`neg`、`not`、`add/sub/mul.ovf(.un)` |
| 比較 | `ceq`、`cgt(.un)`、`clt(.un)`、`beq/bge/bgt/ble/blt/bne.un`（`.s` と参照比較を含む） |
| 変換 | `conv.*`（`ovf` / `un` を含む全 29 種） |
| 分岐 | `br(.s)`、`brtrue/brfalse(.s)`、`switch` |
| 呼び出し | `call`、`callvirt`、`calli`、`ret`、`constrained.` |
| デリゲート | `ldftn`、`ldvirtftn`、コンストラクタ、Combine / Remove / op_Equality / Invoke |
| フィールド | `ldfld`、`ldflda`、`stfld`、`ldsfld`、`ldsflda`、`stsfld` |
| 配列 | `newarr`、`ldlen`、`ldelem.*`、`stelem.*`、`ldelem`、`stelem`、`ldelema`（共変配列の書き込み検査を含む） |
| オブジェクト | `newobj`、`castclass`、`isinst`、`box`、`unbox`、`unbox.any`、`ldobj`、`stobj`、`cpobj`、`initobj`、`throw` |
| 間接アクセス | `ldind.*`、`stind.*`（ByRef とアンマネージポインタ） |
| 例外処理 | `leave(.s)`、`endfinally`、`endfilter`、`rethrow` |
| 型情報など | `ldtoken`、`mkrefany`、`refanyval`、`refanytype`、`ckfinite` |

#### Partial

| 命令・領域 | CLR との差分 |
|---|---|
| `localloc` | ゼロ初期化し、GC 管理のブロックとして保持。フレーム終了時には解放せず、境界外アクセスを拒否。確保前にヒープ上限を検査 |
| `cpblk` / `initblk` | アンマネージポインタはバイト単位で処理。VM のスロット配置で安全に表現できない ByRef 操作は `InvalidProgramException` で拒否 |
| `sizeof` | ClassLayout の Pack / Size と FieldLayout を反映。ネストした値型の整列は近似で、明示レイアウトの重なりアクセスは非対応 |
| `arglist` | ハンドル生成のみ。varargs の実呼び出しは拒否 |
| `jmp` | 署名が一致するゲストメソッド間でフレームを置換。intrinsic への移行は拒否 |
| アンマネージメモリ | `ldind.r4` / `stind.r4` は 4 バイト幅。`ldobj` は符号付き小整数と列挙型の基底型を反映 |

#### プレフィックス

| 命令 | 動作 |
|---|---|
| `volatile.` | 対応するメモリアクセスの前後にメモリバリアを配置 |
| `unaligned.` | VM の仮想メモリにアラインメント制約がないため no-op |
| `readonly.` | `ldelema` で読み取り専用 ByRef を作成。そこからのフィールド参照にも制約を伝播し、書き込みを拒否 |
| `tail.` | 戻り値型が一致し、保護領域外で、呼び出し元の引数・ローカル参照が脱出しない場合にフレームを置換。それ以外は通常の呼び出し |

varargs の実呼び出し、未登録の InternalCall、代替実装のない P/Invoke などのネイティブ依存は拒否します。
native int の 64 bit 固定は、変換、配列・間接アクセス、ポインタ演算、`sizeof(IntPtr)` にも適用されます。

## トレースとデバッグ

`VirtualMachine.Tracer` は実行したフレームと IL 命令イベントを記録します。
フレームにはアセンブリ名・型名・メソッド名を保持するため、実在 CoreLib と `DotnetVM.CoreLib` の IL 実行を区別できます。
IL 本体を持たない intrinsic / ランタイムバインドへの委譲は、IL フレームとして記録しません。

```csharp
using DotnetVM.Diagnostics;

vm.Tracer.Start(new ExecutionTraceOptions {
    CaptureInstructions = true,
    MaxEvents = 10_000,
    MaxFrames = 1_000,
});
try {
    vm.Invoke("MyApp.Program", "Compute", 42);
} finally {
    vm.Tracer.Stop();
}

foreach (var frame in vm.Tracer.Frames)
    Console.WriteLine(frame);
```

保存上限を超えた件数は `DroppedEventCount` / `DroppedFrameCount` で確認できます。
`VirtualMachine.Debugger` は `AddBreakpoint`、`Pause`、`Continue`、`StepInto`、`StepOver`、`StepOut` を提供し、
`Stopped` イベントからホスト UI へ停止を通知します。
命令トレースやデバッグによる観測が必要な間は、該当コードをインタプリタで実行します。

## ベンチマーク

### 最新の CoreCLR 比較（2026-10-06、今回のPR）

コミット `098e27a` のIL最適化・スカラーJITを測定した値です。25項目すべてが成功しました。
AMD Ryzen 9 3900 / Windows x64（build 26200）/ SDK 10.0.401 / runtime 10.0.12、
Release、host tiered compilation無効。warmup 3回、7 samples、VM JIT昇格閾値2、命令課金有効です。

既存22項目にReflectionのInvoke・属性取得・属性付きDTOのJSON往復を加えた**25項目**です。
成功項目の戻り値をCoreCLRと照合しました。時間は表の入力を処理するゲストメソッド**1呼び出し全体のms中央値**で、
ロード・初回準備・JITコンパイルを含みません。CoreCLR列は同一条件のJIT測定プロセスで取得し、
VMインタプリタ列とVM JIT列はそれぞれ別条件で取得しています。測定前の強制GCは行わず、自然GCは時間に含めます。

| ワークロード | 入力 | CoreCLR ms | VM インタプリタ ms | VM JIT ms |
|---|---:|---:|---:|---:|
| SpanCopies | 1,000 | 0.014829 | 108.399 | 93.882 |
| IntegerFormatting | 1,000 | 0.022687 | 7.147 | 7.582 |
| IntegerParsing | 1,000 | 0.028743 | 4.311 | 5.395 |
| StringCopies | 1,000 | 0.025021 | 4.137 | 4.874 |
| ReflectionInvoke | 100 | 0.002065 | 12.202 | 14.257 |
| ReflectionAttributes | 50 | 0.083047 | 63.374 | 88.773 |
| JsonRoundTrip | 10 | 0.022279 | 37.349 | 58.078 |
| Arithmetic | 5,000 | 0.002827 | 3.441 | 0.460 |
| ArithmeticLoop | 100,000 | 0.151079 | 36.983 | 1.415 |
| BranchLoop | 100,000 | 0.357548 | 57.043 | 2.383 |
| ArraySum | 10,000 | 0.011306 | 12.639 | 12.861 |
| FieldAccess | 5,000 | 0.002824 | 4.482 | 4.010 |
| GenericFieldAccess | 5,000 | 0.002842 | 4.649 | 4.883 |
| MethodCalls | 1,000 | 0.001270 | 1.708 | 0.446 |
| CallLoop | 100,000 | 0.095338 | 139.643 | 1.074 |
| ObjectLoop | 10,000 | 0.034988 | 48.253 | 42.339 |
| List | 500 | 0.002310 | 7.286 | 8.840 |
| ListGrowth | 500 | 0.001526 | 4.711 | 6.834 |
| Linq | 500 | 0.001867 | 11.843 | 12.098 |
| DictionaryInt | 200 | 0.002272 | 17.400 | 18.041 |
| DictionaryGrowth | 200 | 0.002633 | 14.606 | 14.833 |
| DictionaryString | 200 | 0.002887 | 14.857 | 14.759 |
| AsyncCompleted | 200 | 0.001617 | 1.382 | 1.901 |
| ValueTaskCompleted | 200 | 0.000452 | 1.536 | 1.563 |
| AsyncWorkers | 8 | 15.694200 | 15.678 | 15.141 |

CallLoopはCoreCLR比でインタプリタ `1464.7倍` からVM JIT `11.3倍` へ改善しました。
ArithmeticLoopは `9.4倍`、BranchLoopは `6.7倍` で、スカラー整数ループは100倍以下です。
一方、Span・Reflection・コレクション系には100倍を超える項目が残っており、全ワークロードが同じ倍率になったことは主張しません。

ReflectionInvokeの反復呼び出しを修正し、100回のInvokeは14.257 msでした。
CoreLib標準のForceInterpretedInvokeを設定し、元のBCL ILとネイティブ境界で実行します。生成Invokeスタブの完全対応は含みません。
JSONは今回のPRで成功しました。ロード、VM起動設定、初回準備、JIT昇格、JSON初回メタデータ構築は測定時間から除外しました。
AsyncWorkersは待機時間を含むため高速化の評価から除外します。
[PR #34時点の比較・確保量・命令数・生データ・再現手順](docs/performance-pr34.md)は過去記録として参照できます。
[2026-10-03の測定](docs/performance.md)と[BCLの初回測定](docs/performance-bcl.md)は過去の記録として保持し、
そこでの改善率を現在のPRの効果としては扱いません。

### IL/JIT 実行経路の再測定（2026-10-06）

`CallLoop(100_000)` は、純粋な整数メソッド呼出しの IL インライン化とスカラー JIT の命令課金ホットパス改善後、
CoreCLR `0.095338 ms`、VM JIT `1.074 ms`、比率 `11.3倍`（warmup 3回、7サンプルの中央値）でした。
戻り値は毎回 CoreCLR と照合しています。CoreCLR／VM のどちらにも測定前の強制 GC は行わず、実行中の自然 GC を時間へ含めています。
単発サンプルはGCやホスト負荷の影響を受けるため、性能判断には中央値を使用します。

今回の追加分では、JIT中の静的Intrinsic呼出しを通常の仮想dispatchから分離し、直線的なクラスコンストラクターは既存のleaf JITへ接続しました。配列のInt32経路も型変換の共通switchを短縮しています。いずれも動的トークン、仮想dispatch、値型、delegate、特殊Runtime面は従来経路を維持します。無課金JITでもセーフポイントを維持するため、ExecutionTimeoutは有効です。

## プロジェクト構成

| プロジェクト | 役割 |
|---|---|
| [DotnetVM](DotnetVM/) | VM 本体、ホスト API、ポリシー、診断機能 |
| [DotnetVM.CoreLib](DotnetVM.CoreLib/) | managed IL による CoreLib の置換実装 |
| [DotnetVM.Tests](DotnetVM.Tests/) | CLR との比較、互換性、リソース制約、GC などのテスト |
| [DotnetVM.BenchmarkGuest](DotnetVM.BenchmarkGuest/) | 共通のベンチマーク用ゲスト DLL |
| [DotnetVM.Benchmarks](DotnetVM.Benchmarks/) | CoreCLR / VM インタプリタ / VM JIT の測定ツール |

VM 本体の主な構成は次のとおりです。

```text
DotnetVM/
├── Host/                  VirtualMachine、VmHostOptions、MemoryPolicy
├── Runtime/
│   ├── Execution/         インタプリタ、JIT、呼び出しゲート、セーフポイント
│   ├── Types/             型解決、ロードコンテキスト、ファサード合成
│   ├── Objects/           オブジェクトと値の表現
│   ├── Heap/              ヒープ管理、GC 戦略
│   └── Intrinsics/        intrinsic とランタイムバインド
├── Metadata/              ECMA-335 メタデータと署名
├── IL/                    IL 命令の定義とデコード
├── PE/                    PE/COFF 解析
├── Binary/                バイナリ読み取り
├── Policy/                管理例外、I/O ポリシー、ゲートウェイ、ブリッジ
├── Devices/               仮想コンソール
└── Diagnostics/           IL 逆アセンブル、実行トレース、デバッガ
```

実行基盤の依存は `Execution → Types / Objects / Heap → Metadata → PE → Binary` の方向に整理しています。
ホスト API と診断機能は、この実行基盤に接続します。

## テストと開発状況

Roslyn でテスト用アセンブリを生成し、VM と CLR で同じコードを実行して戻り値・例外型を比較します。
GC、メモリ上限、ブリッジの拒否、intrinsic の呼び出し制約、並行実行、JIT、アンロードにも専用の検証があります。
GitHub Actions は Release ビルドとテストを実行します。

<details>
<summary>実装済みの開発マイルストーン</summary>

| マイルストーン | 内容 |
|---|---|
| M0–M1 | PE・メタデータ解析、IL 逆アセンブラ |
| M2–M3 | インタプリタ、継承・仮想呼び出し・box・配列のオブジェクトモデル |
| M4 | finally / filter を含む例外処理 |
| M5 | ジェネリック |
| M6 | GC とメモリポリシー |
| M7 | リソース拒否、ブリッジ、仮想コンソール、intrinsic ゲート |
| C1 | 多アセンブリ解決、依存 DLL の同一ディレクトリ探索 |
| C2 | 参照アセンブリと CoreLib の型統合 |
| C3 | 署名に基づく仮想・インターフェース呼び出し |
| C4 | ランタイムバインド、由来の記録、InternalCall / JIT intrinsic の対応 |
| C5 | CoreLib IL 実行、VM CoreLib の置換、CLR 差分テスト |
| C5.5 | CoreLib 委譲先の分類と監査、数値・文字列・カルチャ処理の対応 |
| C6 | ゲストスレッド、Monitor、並行実行、全スレッド停止による GC |
| C6.1 | Task / ValueTask、async / await、キャンセル、SynchronizationContext |
| M8 | 簡易 JIT とホットメソッドの自動昇格 |
| M9 | 命令トレース、IL ブレークポイント、継続・ステップ実行 |

</details>

ホストへの組み込みと運用については[ホスト統合ガイド](docs/host-integration.md)にまとめています。
