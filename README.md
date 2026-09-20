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
| メモリ | `VmHeap.Allocate` 入口で即拒否 (localloc / newarr はホスト側の実確保**前**に `Reserve` で検査)。GC と連動した生存上限もあり。intrinsic が VM heap 外で確保するバッファ (補間ハンドラ内部の StringBuilder 等) も累計上限に計上 |
| 命令数 | インタプリタの命令境界。intrinsic 呼出も追加消費 (IL 実行と等価) |
| ネットワーク | ゲストの通信はすべて `NetworkGateway` (プロキシ) 経由。バイト計上 + クォータのみ VM が担い、**許可の判断はブリッジのホスト実装**が行う |
| ストレージ | 同構造 (`StorageGateway` + `IStorageBridge`) |

**界面の再現制御**: ブリッジが設定されていない場合、対応するファサード型 (`System.IO.File` / `System.Net.WebClient`) を**そもそも合成しません**。ゲストにその面が存在しないため、ロード/呼出の時点で fail-closed になります。

### GC
- マーク & スイープ。実体は `IGcStrategy` の後ろに隠れており、差し替えで世代別 GC に拡張可能 (`VmObject.Generation` を初段から保持)
- **セーフポイント起動のみ** — 命令境界 (全ゲスト状態がフレームに含まれる時点) で回収するため、newobj 処理中の誤回収が構造的に起きない
- ルート源: 実行中フレーム (引数/ローカル/評価スタック/送出中例外)、静的フィールド、`GcHandleTable` (ホスト保持参照)、intrinsic 静的フィールド
- 循環参照も回収。ByRef は参照先コンテナを展開して走査 (`ObjectGraphWalker` 共通基盤)

### intrinsic 機構 + ランタイムバインド層 (BCL 不実装との両立)
BCL は実装しない代わりに、`System.String` / `Math` / `Console` / `Convert` / 例外ファサード等の**面を intrinsic / ランタイムバインドとして合成**します。

セキュリティ上の核心: intrinsic とバインドはホストの任意コードへの自由な脱出ハッチではありません。**必ずインタプリタの呼出ゲートを経由**し、IL 実行と完全に等価な制約 (① 命令クォータ消費 ② セーフポイント検査 ③ メモリは `VmHeap.Allocate` 経由で計上 ④ I/O は仮想デバイス/ブリッジ経由のみ ⑤ 値は VM オブジェクトモデルに正規化) を受けます。登録は VM 起動時のみ (`Seal` 以降は拒否)。

ホストは自前の intrinsic 契約アセンブリ (ファサードの C# 側シグネチャ) を持ち込めます (`VirtualMachine.RegisterIntrinsic`)。

**メソッド解決の優先順位** (C4 ランタイムバインド層):
1. **ランタイムバインド** — `BindingKey(型完全名, メソッド名, パラメータ型名, this 有無)` の署名照合
2. **IL 本体実行** — CoreLib を含む全アセンブリの managed IL
3. **legacy intrinsic** — 名前 + 引数個数のレガシー照合 (既存面との互換)
4. **fail-closed** — 未登録の InternalCall は `NotSupportedException`、未登録の P/Invoke は `OperationNotAllowedException` (ネイティブ実行は構造的に禁止。代替実装が登録済みの面のみ `PInvokeReplacement` として委譲)

バインドには由来 (`BindingOrigin` = Managed / InternalCall / PInvokeReplacement / Device) が付与され、`vm.Bindings` で監査できます。CoreLib 画像 (`LoadHostCoreLib = true`) の InternalCall 面 / JIT intrinsic 面 (`RuntimeHelpers.GetMethodTable` のダミー再帰 IL、`Enum.HasFlag` の生データビット演算等) は `CoreLibBindings` が同等意味論の実装で握ります。

### CoreLib ロード (実在 System.Private.CoreLib の実行)
`VmHostOptions.LoadHostCoreLib = true` でホスト自身の System.Private.CoreLib.dll をロードし、CoreLib の managed IL を VM インタプリタで実行します。参照アセンブリ (System.Runtime 等) との型ユニフィケーション、署名精度の VTable / InterfaceMap ディスパッチ (EII 含む)、依存 DLL の同一ディレクトリ自動解決を備えます。

### 仮想コンソールデバイス
ゲストの `Console` 入出力はすべて VM 内部の `VmConsole` デバイスに集約されます。ホスト物理 I/O を VM は知りません。

```csharp
vm.Console.OutputWritten += ev => Console.Error.Write(ev.Text);  // 出力をイベント購読
vm.Console.BindInput(() => inputQueue.Dequeue());                // 決定的な ReadLine 供給
vm.Console.BindImplementation(customImpl);                       // 完全差し替え
```

### その他
- **DLL のみ対応** (.NET 5 〜 .NET 10)。EXE 固有の考慮 (エントリポイント探索等) はなし。ホストからメソッド明示指定で実行
- **Win32 API / P/Invoke / ネイティブ依存は非対応** — ネイティブ実行は構造的に禁止。P/Invoke 面は代替バインド (`PInvokeReplacement`) 未登録なら `OperationNotAllowedException` で fail-closed 拒否
- 例外は ECMA-335 準拠の EH (try / catch / finally / fault / filter)。VM 内部例外もゲスト例外化され、ゲストで捕捉可能
- ジェネリック完全対応: TypeSpec/MethodSpec、`constrained.`、変性付き castclass、ジェネリック継承・ネスト型
- 外部呼出: `Invoke` (静的) / `CreateInstance` + `CallInstance` (インスタンス) / `GcHandleTable` (GC をまたぐ参照保持)

## IL 命令の互換性

ECMA-335 の 218 opcode (1 バイト命令 + `0xFE` 2 バイト命令) は**すべてデコード対象**で、実装経路のない命令は `NotSupportedException` で fail-closed になります (サイレントな誤動作なし)。ただし「命令を処理する」ことと「意味論が CLR と完全一致」は別で、次の 4 段階で管理しています。

| 状態 | 意味 |
|---|---|
| **Full** | CLR 突合テストで意味論を検証済み |
| **Partial** | 受け入れるが VM の安全側モデルに簡略化 (差分を明記) |
| **Prefix no-op** | プレフィックス命令として受け入れるが効果なし |
| **Rejected** | ロード / 実行時に拒否 |

### Full

| 命令群 | 命令 |
|---|---|
| スタック / 即値 | `nop` `dup` `pop` `break` `ldnull` `ldc.*` / 引数・ローカル: `ldarg.*` `starg.*` `ldloc.*` `stloc.*` (短形式 / 長形式 / `ldarga` `ldloca` 含む) |
| 算術 | `add` `sub` `mul` `div(.un)` `rem(.un)` `and` `or` `xor` `shl` `shr(.un)` `neg` `not` `add/sub/mul.ovf(.un)` |
| 比較 | `ceq` `cgt(.un)` `clt(.un)` `beq/bge/bgt/ble/blt/bne.un` (`.s` 含む、参照比較の cgt.un 規約 = ECMA-335 III.2 含む) |
| 変換 | `conv.*` 全 29 種 (ovf / un 含む) |
| 分岐 | `br(.s)` `brtrue/brfalse(.s)` `switch` |
| 呼出 | `call` `callvirt` (仮想ディスパッチ / インターフェース / デリゲート `Invoke`) `calli` (関数ポインタ経由) `ret` `constrained.` |
| デリゲート | `ldftn` `ldvirtftn` (+ delegate `.ctor` / `Combine` / `Remove` / `op_Equality` 面) |
| フィールド | `ldfld` `ldflda` `stfld` `ldsfld` `ldsflda` `stsfld` |
| 配列 | `newarr` `ldlen` `ldelem.*`(11) `stelem.*`(8) `ldelem` `stelem` `ldelema` (共変書き込み検査含む) |
| オブジェクト | `newobj` `castclass` `isinst` `box` `unbox` `unbox.any` `ldobj` `stobj` `cpobj` `initobj` `throw` |
| 間接アクセス | `ldind.*`(11) `stind.*`(8) — マネージポインタ (ByRef) と unmanaged ポインタの両対応 |
| 例外 | `leave(.s)` `endfinally` `endfilter` `rethrow` |
| 型情報 | `ldtoken` (FieldRVA / Type / Method) `mkrefany` `refanyval` `refanytype` (`__makeref` 系) `ckfinite` |

### Partial

| 命令 | CLR との差分 |
|---|---|
| `localloc` | 初期化は 0 (実 CLR は不定値)。ブロックは GC 管理 (フレーム終了で解放しない = 脱出 stackalloc も安全側に動く)。ブロック外アクセスは境界検査で拒否 (実 CLR は未定義動作 = アドレス空間破壊)。確保は `VmHeap` 会計の対象で、上限検査はホスト実確保より先に実施 |
| `cpblk` / `initblk` | unmanaged ポインタ間はバイト粒度、ByRef 間は 8 バイト切り上げのスロット粒度 |
| `sizeof` | ゲスト値型は順次レイアウト近似 (プリミティブ整列。明示的パッキングは未対応) |
| `arglist` | ハンドル生成のみ。varargs 実呼出は fail-closed (C# 産 IL では生成されない) |
| `jmp` | 尾呼び移行として実装 (残フレームを実行せず呼出先の戻り値を引き継ぐ)。intrinsic 面への移行は拒否 |

### Prefix no-op

`volatile.` `unaligned.` `readonly.` `tail.` — 受け入れて無視します。`tail.` + `call` は通常の呼出に置き換わるため、深い末尾再帰は `MaxRecursionDepth` で拒否されうる点に注意 (実 CLR ではスタック消費なしで回る)。

### Rejected

- **P/Invoke** (`pinvokeimpl`) — `ImplFlags` 検出でロード時拒否
- **varargs 実呼出** / ネイティブ依存の面全般

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

197 テスト (2026-09-20 時点)。

## 状況

- [x] M0-M1: PE/メタデータパーサ + IL 逆アセンブラ
- [x] M2-M3: インタプリタ / オブジェクトモデル (継承・仮想ディスパッチ・box・配列)
- [x] M4: 例外処理 (finally / filter 含む)
- [x] M5: ジェネリック
- [x] M6: GC + メモリポリシー
- [x] M7: リソース拒否 + ブリッジ + 仮想コンソール + intrinsic ゲート
- [x] C1: 多アセンブリ解決 (AssemblyContext / 依存 DLL 自動探索)
- [x] C2: 参照アセンブリ⇔CoreLib 型ユニフィケーション + 実型置換
- [x] C3: 署名精度の仮想/インターフェースディスパッチ (VTable / InterfaceMap / EII)
- [x] C4: ランタイムバインド層 (BindingKey + BindingOrigin / 優先順位 ①〜④ / CoreLib InternalCall・JIT intrinsic 面のバインド)
- [ ] C5: CoreLib IL 実行の全面化 + 差分テスト (String 表現境界の撤去、ExecutionTracer で IL 実行証明)
- [ ] C6: ゲストスレッド対応 (スレッドモデル + Monitor の真の競合・ブロッキング。現行の Monitor バインドは単一スレッド前提の暫定ファサード)
- [ ] M8: 簡易 JIT (IL → 式ツリー → デリゲート昇格、ホットメソッド自動昇格)
- [ ] M9: デバッガ / 実行トレース

プロダクト本体は依存ゼロ (`Microsoft.CodeAnalysis.CSharp` / `xunit` はテストプロジェクトのみ)。
