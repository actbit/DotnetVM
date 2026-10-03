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

### GC

既定はマーク＆スイープです。`IGcStrategy` を差し替えて別の回収戦略を実装でき、オブジェクトには世代情報を保持します。
回収はセーフポイントで行い、並行実行中は全ゲストスレッドを停止します。

GC ルートには、実行フレームの引数・ローカル・評価スタック・送出中の例外、静的フィールド、
`GcHandleTable` によるホスト保持参照、intrinsic の静的状態などを含みます。
循環参照を回収でき、ByRef から参照先の所有オブジェクトも走査します。

### ネットワークとストレージ

ホストが `INetworkBridge` / `IStorageBridge` を実装し、`VmHostOptions` に設定します。
**接続先やパスの許可判断はブリッジが行い、VM は転送量の計上と上限の強制を担います。**

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

### 測定方法

`DotnetVM.BenchmarkGuest` の同じ Release ビルド済みアセンブリを、次の 3 通りで実行します。

| 実行方式 | 設定 |
|---|---|
| CoreCLR | 型付きデリゲートでゲストメソッドを直接実行 |
| VM インタプリタ | `EnableJit = false` |
| VM JIT | `EnableJit = true`、`JitPromotionThreshold = 1` |

算術、分岐、配列アクセス、メソッド呼び出し、オブジェクト確保の 5 パターンを測定し、VM の結果を CoreCLR の結果と照合します。
ロードと JIT コンパイルを計測から除外し、3 回のウォームアップ後、**9 サンプル（各 5 回実行）の中央値**を表示します。
表の時間は 1 回の呼び出し時間ではなく、各サンプルの 5 回分の合計です。

```bash
dotnet build DotnetVM.slnx --configuration Release
dotnet run --project DotnetVM.Benchmarks/DotnetVM.Benchmarks.csproj --configuration Release --no-build --no-restore
```

以下の記録はいずれも AMD Ryzen 9 3900 / Windows x64 / .NET SDK 10.0.401 / runtime 10.0.12 で測定しています。
時間削減率は `1 - 変更後 / 変更前` です。値は環境と負荷で変動し、小さな差には測定変動も含まれます。
VM は命令ごとのクォータ検査やフレーム管理を行うため、CoreCLR のネイティブ JIT とは速度特性が異なります。

### 最新の結果: オブジェクト確保の改善（2026-10-03）

[PR #29](https://github.com/actbit/DotnetVM/pull/29) の変更前（`25c5740`、PR #27・#28 マージ後）と変更後（`7211c60`）の比較です。

| パターン (入力) | CoreCLR (ms) | 変更前 interp (ms) | 変更後 interp (ms) | interp 時間削減 | 変更前 JIT (ms) | 変更後 JIT (ms) | JIT 時間削減 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Arithmetic (100,000) | 0.782 | 551.551 | 546.315 | 1.0% | 377.949 | 374.608 | 0.9% |
| Branches (100,000) | 1.819 | 538.902 | 518.720 | 3.7% | 377.342 | 347.572 | 7.9% |
| Array access (10,000) | 0.100 | 84.725 | 84.884 | -0.2% | 60.066 | 58.797 | 2.1% |
| Method calls (100,000) | 0.510 | 937.594 | 925.334 | 1.3% | 374.097 | 361.722 | 3.3% |
| Object allocation (10,000) | 0.217 | 2,698.126 | 196.940 | 92.7% | 2,644.748 | 145.416 | 94.5% |

`interp` は VM インタプリタを表します。オブジェクト確保はインタプリタで **13.7 倍**、VM JIT で **18.2 倍**高速化しました。
コンストラクタからの `System.Object::.ctor` 呼び出しで、依存 DLL の存在確認と署名・バインド解決を繰り返していたため、
検証済みの intrinsic MemberRef 呼び出し先をローダーごとに再利用します。
レジストリが確定した親のないロードコンテキストを対象とし、アセンブリの追加・除去・コンテキスト破棄時には再利用を無効化します。
ジェネリック呼び出し元、動的トークン、親コンテキストを持つローダーは通常の解決経路を使います。

### 過去の結果

<details>
<summary>2026-10-03: セーフポイントの改善（PR #27）</summary>

[PR #27](https://github.com/actbit/DotnetVM/pull/27) の変更前（`93da98e`）と変更後（`b07122e`）の比較です。
この表の `master` / `current` は、この測定時点の変更前 / 変更後を指します。

| パターン (入力) | CoreCLR (ms) | master interp (ms) | current interp (ms) | interp 改善 | master JIT (ms) | current JIT (ms) | JIT 改善 | interp / JIT |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Arithmetic (100,000) | 0.786 | 1,197.132 | 550.522 | 54.0% | 960.356 | 388.441 | 59.6% | 1.42x |
| Branches (100,000) | 1.869 | 1,130.681 | 524.019 | 53.7% | 910.749 | 364.704 | 60.0% | 1.44x |
| Array access (10,000) | 0.102 | 182.278 | 84.974 | 53.4% | 148.618 | 60.630 | 59.2% | 1.40x |
| Method calls (100,000) | 0.512 | 1,331.274 | 963.212 | 27.6% | 725.634 | 365.238 | 49.7% | 2.64x |
| Object allocation (10,000) | 0.323 | 2,750.111 | 2,737.643 | 0.5% | 2,720.397 | 2,660.970 | 2.2% | 1.03x |

GC 要求がない命令境界で、全スレッドを停止するロックと lease の確保を省きました。
オブジェクト確保以外の 4 パターンでは、インタプリタで 27.6〜54.0%、VM JIT で 49.7〜60.0% の時間を削減しました。
オブジェクト確保の改善幅は小さく、その後の PR #29 で呼び出し先の解決を最適化しています。

</details>

<details>
<summary>2026-09-25: 実行経路の改善</summary>

この表の `master` は当時の `origin/master`（`1897c72`）、`current` は当時の最適化後です。

| パターン (入力) | CoreCLR (ms) | master interp (ms) | current interp (ms) | interp 改善 | master JIT (ms) | current JIT (ms) | JIT 改善 | interp / JIT |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Arithmetic (100,000) | 0.805 | 1,944.108 | 1,063.670 | 45.3% | 1,837.696 | 955.202 | 48.0% | 1.11x |
| Branches (100,000) | 1.843 | 2,098.228 | 1,006.932 | 52.0% | 1,978.085 | 898.421 | 54.6% | 1.12x |
| Array access (10,000) | 0.106 | 297.485 | 163.757 | 45.0% | 279.503 | 144.034 | 48.5% | 1.14x |
| Method calls (100,000) | 0.520 | 2,119.416 | 986.856 | 53.4% | 1,975.761 | 571.500 | 71.1% | 1.73x |
| Object allocation (10,000) | 0.228 | 2,307.112 | 2,070.939 | 10.2% | 2,307.804 | 2,033.294 | 11.9% | 1.02x |

</details>

ベンチマークの追加・変更時は、実行条件と比較対象のコミットを添えて結果を更新してください。

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
