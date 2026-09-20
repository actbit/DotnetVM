# DotnetVM

C# で実装した .NET 10 互換の CoreCLR 風 VM。実在する .NET アセンブリ (IL) をロードして実行し、**メモリ / ネットワーク / ストレージ / 命令数のリソース制約を強制**できるサンドボックスです。

```csharp
using var vm = new VirtualMachine(new VmHostOptions {
    Memory = new MemoryPolicy {
        InstructionQuota = 100_000_000,          // 命令数クォータ (停止性保証)
        TotalAllocationByteLimit = 512 << 20,    // 累計アロケーション上限
        LiveObjectByteLimit = 128 << 20,         // 生存オブジェクト上限 (GC と連動)
        MaxRecursionDepth = 512,
    },
    StorageBridge = new MyStorageBridge(...),    // 設定した面だけがゲストに現れる
});
vm.LoadAssembly("MyGuest.dll");                  // DLL アセンブリをロード

var result = vm.Invoke("MyApp.Program", "Compute", 42);
```

## 特徴

### 実行方式
- **IL インタプリタ** (主) — 事前デコードした IL を命令境界ごとに実行。命令クォータとセーフポイント (GC 掛かり口) をここで強制
- **独自オブジェクトモデル** — CLR オブジェクトを流用しない。`VmObject` / `VmClassInstance` / `VmStructValue` / `VmArray` / `VmBoxedValue` の全生成が `VmHeap.Allocate` を通るため、アロケーション計上と GC 制御が正確

### リソース制約 (クォータ + 拒否方式)
超過は `ResourceExhaustedException` 系の**管理例外**として拒否され、ゲストの `catch` には握りつぶされません。

| 面 | 強制点 |
|---|---|
| メモリ | `VmHeap.Allocate` 入口で即拒否。GC と連動した生存上限もあり |
| 命令数 | インタプリタの命令境界。intrinsic 呼出も追加消費 (IL 実行と等価) |
| ネットワーク | ゲストの通信はすべて `NetworkGateway` (プロキシ) 経由。バイト計上 + クォータのみ VM が担い、**許可の判断はブリッジのホスト実装**が行う |
| ストレージ | 同構造 (`StorageGateway` + `IStorageBridge`) |

**界面の再現制御**: ブリッジが設定されていない場合、対応するファサード型 (`System.IO.File` / `System.Net.WebClient`) を**そもそも合成しません**。ゲストにその面が存在しないため、ロード/呼出の時点で fail-closed になります。

### GC
- マーク & スイープ。実体は `IGcStrategy` の後ろに隠れており、差し替えで世代別 GC に拡張可能 (`VmObject.Generation` を初段から保持)
- **セーフポイント起動のみ** — 命令境界 (全ゲスト状態がフレームに含まれる時点) で回収するため、newobj 処理中の誤回収が構造的に起きない
- ルート源: 実行中フレーム (引数/ローカル/評価スタック/送出中例外)、静的フィールド、`GcHandleTable` (ホスト保持参照)、intrinsic 静的フィールド
- 循環参照も回収。ByRef は参照先コンテナを展開して走査 (`ObjectGraphWalker` 共通基盤)

### intrinsic 機構 (BCL 不実装との両立)
BCL は実装しない代わりに、`System.String` / `Math` / `Console` / `Convert` / 例外ファサード等の**最小面を intrinsic として合成**します。

セキュリティ上の核心: intrinsic はホストの任意コードへの自由な脱出ハッチではありません。**必ずインタプリタの呼出ゲートを経由**し、IL 実行と完全に等価な制約 (① 命令クォータ消費 ② セーフポイント検査 ③ メモリは `VmHeap.Allocate` 経由で計上 ④ I/O は仮想デバイス/ブリッジ経由のみ ⑤ 値は VM オブジェクトモデルに正規化) を受けます。登録は VM 起動時のみ (`Seal` 以降は拒否)。

ホストは自前の intrinsic 契約アセンブリ (ファサードの C# 側シグネチャ) を持ち込めます (`VirtualMachine.RegisterIntrinsic`)。

### 仮想コンソールデバイス
ゲストの `Console` 入出力はすべて VM 内部の `VmConsole` デバイスに集約されます。ホスト物理 I/O を VM は知りません。

```csharp
vm.Console.OutputWritten += ev => Console.Error.Write(ev.Text);  // 出力をイベント購読
vm.Console.BindInput(() => inputQueue.Dequeue());                // 決定的な ReadLine 供給
vm.Console.BindImplementation(customImpl);                       // 完全差し替え
```

### その他
- **DLL のみ対応** (.NET 5 〜 .NET 10)。EXE 固有の考慮 (エントリポイント探索等) はなし。ホストからメソッド明示指定で実行
- **Win32 API / P/Invoke / ネイティブ依存は非対応** (`ImplFlags` で検出して拒否)
- 例外は ECMA-335 準拠の EH (try / catch / finally / fault / filter)。VM 内部例外もゲスト例外化され、ゲストで捕捉可能
- ジェネリック完全対応: TypeSpec/MethodSpec、`constrained.`、変性付き castclass、ジェネリック継承・ネスト型
- 外部呼出: `Invoke` (静的) / `CreateInstance` + `CallInstance` (インスタンス) / `GcHandleTable` (GC をまたぐ参照保持)

## アーキテクチャ

```
VirtualMachine (Host/)          組み込みファサード
 ├─ AssemblyImage (Metadata/)   PE/COFF + ECMA-335 (メタデータ行は動的レイアウト計算)
 ├─ TypeLoader (Runtime/Types/) 型解決 + intrinsic ファサード合成
 ├─ Interpreter (Runtime/Execution/)  命令実行 + intrinsic 呼出ゲート + セーフポイント
 ├─ VmHeap (Runtime/Heap/)      唯一のアロケーション入口 + IGcStrategy
 ├─ Policy/                     クォータ定義 + ゲートウェイ + ブリッジ I/F
 ├─ Devices/VmConsole           仮想コンソールデバイス
 ├─ Intrinsics/                 最小 BCL 面 (起動時登録のみ)
 └─ Diagnostics/IlDisassembler  IL 逆アセンブル (デバッグ/検証)
```

依存方向は `Diagnostics/Host → Execution → Types/Objects/Heap → Metadata → PE → Binary` の一方向。

## テスト

**核**: Roslyn でテストアセンブリをインメモリ生成 → VM で実行 → 同一アセンブリを CLR で反射実行した結果と突合 (値・例外型)。GC / メモリ上限 / ブリッジ拒否 / intrinsic ゲートの制約等価性も専用テストで検証しています。

```
dotnet test DotnetVM.Tests
```

137 テスト (2026-09-20 時点)。

## 状況

- [x] M0-M1: PE/メタデータパーサ + IL 逆アセンブラ
- [x] M2-M3: インタプリタ / オブジェクトモデル (継承・仮想ディスパッチ・box・配列)
- [x] M4: 例外処理 (finally / filter 含む)
- [x] M5: ジェネリック
- [x] M6: GC + メモリポリシー
- [x] M7: リソース拒否 + ブリッジ + 仮想コンソール + intrinsic ゲート
- [ ] M8: 簡易 JIT (IL → 式ツリー → デリゲート昇格、ホットメソッド自動昇格)
- [ ] M9: デバッガ / 実行トレース

プロダクト本体は依存ゼロ (`Microsoft.CodeAnalysis.CSharp` / `xunit` はテストプロジェクトのみ)。
