# ホスト統合ガイド

このドキュメントは、DotnetVM をホストアプリケーションへ組み込む際の基本手順と、
サンドボックス境界を設計するための指針をまとめたものです。API の詳細な対応状況は
[README](../README.md) の機能一覧と IL 互換性表を参照してください。

## 1. 適用範囲と前提

DotnetVM は、ホストが指定した DLL を独自のメタデータモデルと IL インタプリタで実行する
VM です。CLR の `Assembly.Load` や JIT にゲストを渡す仕組みではありません。

- ホストの対象フレームワークは `net10.0` です
- ゲストは DLL を入力にします。EXE のエントリポイントは自動実行しません
- ゲストのコードからホスト CLR のファイル、ネットワーク、プロセス、ネイティブ API へ直接アクセスすることはできません
- 外部 I/O が必要な場合は、ホストがブリッジを実装して `VmHostOptions` に明示的に設定します
- 信頼しないコードを実行する場合は、VM インスタンスをテナント単位で分離し、ホスト側のプロセス・コンテナ境界も併用してください

DotnetVM のリソースクォータは VM 内の制約です。ホストの OS リソース制限や、ブリッジ実装が
保持するキャッシュ・接続・スレッドの制限を代替するものではありません。

## 2. ビルドと参照

リポジトリを取得した後、ソリューションをビルドしてテストを実行します。

```bash
dotnet restore DotnetVM.slnx
dotnet build DotnetVM.slnx --configuration Release --no-restore
dotnet test DotnetVM.Tests/DotnetVM.Tests.csproj \
  --configuration Release --no-build --no-restore
```

ホストプロジェクトからはプロジェクト参照を追加します。

```bash
dotnet add path/to/HostApp.csproj reference DotnetVM/DotnetVM.csproj
```

`LoadHostCoreLib = true` を使用する場合は、ビルド出力に生成された
`DotnetVM.CoreLib.dll` も `DotnetVM.dll` と同じディレクトリへ配置してください。

## 3. VM のライフサイクル

基本的なライフサイクルは「構築 → 設定 → ロード → 実行 → 破棄」です。
intrinsic とランタイムバインドは実行開始前に登録します。

```csharp
using DotnetVM.Host;
using DotnetVM.Policy;

var options = new VmHostOptions {
    Memory = new MemoryPolicy {
        InstructionQuota = 1_000_000,
        TotalAllocationByteLimit = 128L << 20,
        LiveObjectByteLimit = 64L << 20,
        MaxAssemblyBytes = 16L << 20,
    },
};

using var vm = new VirtualMachine(options);
vm.LoadAssembly("guest/MyGuest.dll");

var result = vm.Execute(
    vm.Loaders[^1].FindTypeByFullName("MyGuest.Program")!
      .Methods.Single(m => m.Name == "Compute" && m.IsStatic),
    42);

Console.WriteLine(result.ReturnValue);
Console.WriteLine($"IL instructions: {result.InstructionCount}");
```

通常はメソッドを直接探す必要がない `Invoke` が簡潔です。

```csharp
var value = vm.Invoke("MyGuest.Program", "Compute", 42);
```

### アセンブリのロード

- `LoadAssembly(string path)`: DLL とその依存 DLL を、参照元と同じディレクトリから解決します
- `LoadAssembly(Stream, string? sourcePath = null)`: ストリームからロードします。`sourcePath` を省略した場合、カレントディレクトリへの暗黙フォールバックはありません
- ゲストの `Assembly.Load` / `AssemblyLoadContext` は VM のローダーへ接続されます。パスロードは `StorageBridge` 経由に限定されます

入力は読み込み中にも `MemoryPolicy.MaxAssemblyBytes` で制限されます。参照先が解決できない場合や、
P/Invoke を含む画像は fail-closed で拒否されます。

### インスタンスメソッドとホスト値

インスタンスメソッドは `CreateInstance` と `CallInstance` を使います。

```csharp
var calculator = vm.CreateInstance("MyGuest.Calculator", 10);
var answer = vm.CallInstance(calculator, "Add", 32);
```

ホスト境界で扱える代表的な値は、整数・浮動小数点数・`bool`・`char`・`string`・配列・
VM オブジェクトです。VM オブジェクトは CLR のオブジェクトに変換されず、そのまま
`VmObject` として扱われます。`ExecutionResult.ReturnValue` の `void` は `null` です。

## 4. リソースポリシーを先に決める

信頼しないゲストには、既定値をそのまま使わず、用途に応じて上限を設定してください。
クォータを超えると `ResourceExhaustedException` 系の管理例外になり、ゲストの `catch` では
握りつぶせません。

```csharp
var options = new VmHostOptions {
    Memory = new MemoryPolicy {
        InstructionQuota = 5_000_000,
        MaxRecursionDepth = 128,
        TotalAllocationByteLimit = 64L << 20,
        LiveObjectByteLimit = 32L << 20,
        HostTempAllocationByteLimit = 16L << 20,
        HostWorkBudget = 2_000_000,
        MaxAssemblyBytes = 8L << 20,
        MaxMetadataRows = 200_000,
        MaxMethodBodyBytes = 256 * 1024,
        MaxSignatureDepth = 32,
        MaxGenericNestingDepth = 32,
    },
    MaxGuestThreads = 8,
    MaxTaskWorkers = 8,
    MaxGuestWorkers = 8,
    MaxPendingTaskTimers = 64,
    MaxTaskCombinatorInputs = 1_024,
    ShutdownTimeoutMilliseconds = 2_000,
};
```

主な制限の役割は次のとおりです。

| 領域 | 設定 | 強制される対象 |
|---|---|---|
| 実行 | `InstructionQuota`, `MaxRecursionDepth` | IL 命令数、ゲスト呼出の深さ |
| VM ヒープ | `TotalAllocationByteLimit`, `LiveObjectByteLimit` | 累計確保量、生存オブジェクト量 |
| ホスト処理 | `HostTempAllocationByteLimit`, `HostWorkBudget` | intrinsic 内の一時バッファと CPU 作業の近似量 |
| ローダー | `MaxAssemblyBytes`, `MaxMetadataRows`, `MaxMethodBodyBytes` | PE 入力、メタデータ、メソッド本体 |
| 解析 | `MaxSignatureDepth`, `MaxGenericNestingDepth` | 署名・ジェネリックの再帰深度 |
| 並行性 | `MaxGuestThreads`, `MaxTaskWorkers`, `MaxGuestWorkers` | guest Thread、Task worker、VM 全体の worker |
| タイマー | `MaxPendingTaskTimers` | 未完了 `Task.Delay` / `CancelAfter` |
| 結合 | `MaxTaskCombinatorInputs` | `WhenAll` / `WhenAny` / `WaitAll` の入力数 |

## 5. I/O ブリッジと fail-closed 設計

ネットワークとストレージは、ブリッジを設定しない限り利用できません。未設定時は対応する
ゲストファサード型自体を合成しないため、呼出時だけでなく型解決・ロード時点でも拒否できます。

### ネットワーク

`INetworkBridge` は許可リスト、認証、TLS、名前解決などのホスト側判断を担当します。
`NetworkGateway` はブリッジを通過する送受信バイトを計上し、1 要求ごとと累計の上限を強制します。

```csharp
sealed class AllowlistedNetwork : INetworkBridge
{
    public byte[] Request(NetworkRequest request)
    {
        if (request.Url.Host != "api.example.test")
            throw new InvalidOperationException("host is not allowlisted");

        // request.MaxResponseBytes を超えない応答だけを返す。
        return FetchThroughYourProxy(request);
    }

    private static byte[] FetchThroughYourProxy(NetworkRequest request) => [];
}

var options = new VmHostOptions {
    Network = new NetworkPolicy {
        MaxBytesPerRequest = 1 << 20,
        TotalTransferByteLimit = 10L << 20,
    },
    NetworkBridge = new AllowlistedNetwork(),
};
```

実際の通信を行うブリッジでは、`NetworkRequest.MaxResponseBytes` を取得前に適用し、
ホスト側で巨大な応答を作ってから VM に拒否させる増幅を避けてください。

### ストレージ

`IStorageBridge` はパスの正規化、許可ルート、シンボリックリンク、実ファイルへのアクセスを
担当します。VM はファイルシステムへ直接触れません。

```csharp
sealed class WorkspaceStorage : IStorageBridge
{
    public bool Exists(string path) => IsUnderWorkspace(path) && File.Exists(path);

    public byte[] Read(string path, long maxBytes)
    {
        if (!IsUnderWorkspace(path))
            throw new UnauthorizedAccessException(path);
        // 実装では maxBytes を使ったストリーム読み取りを行う。
        return ReadAtMost(path, maxBytes);
    }

    public void Write(string path, ReadOnlyMemory<byte> contents)
    {
        if (!IsUnderWorkspace(path))
            throw new UnauthorizedAccessException(path);
        WriteAtomically(path, contents);
    }

    public void Delete(string path)
    {
        if (!IsUnderWorkspace(path))
            throw new UnauthorizedAccessException(path);
        File.Delete(path);
    }

    // ルート固定、正規化、リンク追跡の扱いはホストの要件に合わせる。
    private static bool IsUnderWorkspace(string path) => true;
    private static byte[] ReadAtMost(string path, long maxBytes) => [];
    private static void WriteAtomically(string path, ReadOnlyMemory<byte> contents) { }
}
```

ストレージでは `StoragePolicy.MaxBytesPerOperation` と `TotalByteLimit` が読み書きのバイト数を
制限します。`Read` は VM が渡す `maxBytes` も尊重してください。

## 6. 例外の扱い

ホスト側では VM の管理例外と、ゲストが投げた未処理例外を分けて扱います。

```csharp
try
{
    return vm.Invoke("MyGuest.Program", "Run");
}
catch (ResourceExhaustedException ex)
{
    // quota 違反。リトライではなく入力・ポリシー・テナントを確認する。
    LogQuotaViolation(ex);
    throw;
}
catch (OperationNotAllowedException ex)
{
    // P/Invoke、未設定ブリッジ、未登録面など。
    LogRejectedOperation(ex);
    throw;
}
catch (UnhandledGuestException ex)
{
    // ゲストの EH で捕捉されなかった例外。
    LogGuestFailure(ex.ExceptionTypeName, ex.GuestMessage);
    throw;
}
```

代表的な例外は次のとおりです。

- `MemoryQuotaExceededException`: VM ヒープまたはホスト一時バッファの上限
- `InstructionQuotaExceededException`: 命令数上限
- `NetworkQuotaExceededException` / `StorageQuotaExceededException`: I/O の上限またはブリッジ契約違反
- `GuestConcurrencyLimitExceededException`: Thread、Task worker、Timer などの上限
- `OperationNotAllowedException`: P/Invoke、ネイティブ依存、未設定のデバイスなど
- `AssemblyDependencyNotFoundException`: 依存 DLL を解決できない
- `UnhandledGuestException`: ゲスト側で処理されなかった例外

## 7. CoreLib、カルチャ、決定性

### CoreLib

通常は intrinsic ファサードとランタイムバインドを使います。実在の
`System.Private.CoreLib.dll` の managed IL を VM で実行する場合は次のようにします。

```csharp
var options = new VmHostOptions {
    LoadHostCoreLib = true,
};
```

このモードでは、ホストの `System.Private.CoreLib.dll` と、同じ出力ディレクトリの
`DotnetVM.CoreLib.dll` が必要です。`CoreLibBindingProviders` と
`RegisterBinding` は VM の信頼境界に加わる特権コードなので、信頼済みの実装だけを登録します。
登録後、実行を開始した VM の設定を変更することはできません。

### カルチャと時計

VM ごとにカルチャ、時計、タイムゾーンを固定できます。外側のゲスト呼出が終わると、
ホストスレッドのカルチャは復元されます。

```csharp
var options = new VmHostOptions {
    Culture = CultureInfo.GetCultureInfo("ja-JP"),
    ClockProvider = () => DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
    TimeZone = TimeZoneInfo.Utc,
    RandomFill = buffer => buffer.Clear(), // テスト専用。実運用では CSPRNG を使う。
};
```

決定性テストでは `ClockProvider` と `RandomFill` をテスト用に差し替え、ホストの実環境変数を
使わず `SetVirtualEnvironmentVariable` / `GetVirtualEnvironmentVariable` を使用します。

## 8. コンソール、診断、GC

ゲストの `Console` は仮想デバイスに集約され、ホスト標準出力へ直接書き込みません。

```csharp
vm.Console.OutputWritten += output =>
    audit.Write(output.IsError ? "stderr" : "stdout", output.Text);
vm.Console.BindInput(() => inputQueue.TryDequeue(out var line) ? line : null);

var result = (ExecutionResult)vm.Execute(method, args);
foreach (var output in result.ConsoleOutput)
    audit.Write(output.IsError ? "stderr" : "stdout", output.Text);
```

`ExecutionResult` は戻り値、実行中のコンソール出力スナップショット、消費命令数を含みます。
実行後の累積命令数は `vm.InstructionCount` で確認できます。

managed IL の実行経路を調査する場合は、実行前にトレーサーを有効化します。

```csharp
vm.Tracer.Start();
try
{
    vm.Invoke("MyGuest.Program", "Run");
}
finally
{
    vm.Tracer.Stop();
}
```

GC は通常、アロケーション間隔に応じて起動します。ホストが明示的に回収を要求する場合は
`vm.CollectGarbage()` を使います。ホストがゲストオブジェクトを呼出間で保持する場合は
`vm.Handles` の GC handle を使い、通常の CLR 参照だけで VM オブジェクトを保持しないでください。

## 9. 並行実行と破棄

guest Thread と Task worker は VM の共有予算で管理されます。ゲストが作成した worker や
未完了 timer を残したまま `Dispose` すると、設定した `ShutdownTimeoutMilliseconds` の範囲で
停止を待ちます。

- VM はテナントやジョブ単位で作成し、不要になったら必ず `Dispose` します
- 1 つの VM を複数スレッドから呼び出す場合は、ホスト側でもライフサイクルとキャンセルを管理します
- `MaxGuestThreads` / `MaxTaskWorkers` だけでなく `MaxGuestWorkers` も設定し、二重の worker 増加を防ぎます
- ブリッジの実装が作成するスレッド、接続、バッファは VM のクォータ外なので、別途上限を設けます

## 10. セキュリティチェックリスト

本番で信頼しない DLL を実行する前に、少なくとも次を確認してください。

- [ ] `InstructionQuota`、メモリ、PE/メタデータ、再帰、並行性の上限を用途ごとに設定した
- [ ] `NetworkBridge` / `StorageBridge` は必要な場合だけ設定し、allowlist と入力検証を実装した
- [ ] ブリッジは `MaxResponseBytes` / `maxBytes` を取得前に適用する
- [ ] `RegisterIntrinsic`、`RegisterBinding`、`CoreLibBindingProviders` に信頼済みコードだけを渡した
- [ ] P/Invoke、ネイティブ依存、未登録面をエラーとして監視している
- [ ] ゲストのログ、例外型、クォータ違反、命令数を監査できる
- [ ] VM の `Dispose` と worker の終了をジョブの finally で保証している
- [ ] OS のプロセス、コンテナ、CPU、メモリ、ファイルシステム境界を別途設定した

DotnetVM はゲストコードを CLR の安全な AppDomain に隔離するものではありません。ホストの
特権コード（特にブリッジ、intrinsic、バインド）を最小化し、実行対象の入力を信頼しない
運用では OS レベルの防御を重ねてください。
