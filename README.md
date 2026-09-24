# DotnetVM

C# で実装した .NET 10 互換の CoreCLR 風 VM。実在する .NET アセンブリ (IL) をロードして実行し、**メモリ / CPU 作業 / ネットワーク / ストレージ / 命令数 / 並行 worker のリソース制約を強制**できるサンドボックスです。

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
| メモリ | `VmHeap.Allocate` 入口で即拒否 (localloc / newarr はホスト側の実確保**前**に `Reserve` で検査)。GC と連動した生存上限もあり。intrinsic が VM heap 外で確保する一時バッファも累計上限に計上し、アセンブリ入力サイズにも上限あり |
| CPU 作業 | 重い host 側 intrinsic の作業量を `HostWorkBudget` で制限 |
| 命令数 | インタプリタの命令境界。intrinsic 呼出も追加消費 (IL 実行と等価) |
| ネットワーク | ゲストの通信はすべて `NetworkGateway` (プロキシ) 経由。バイト計上 + クォータのみ VM が担い、**許可の判断はブリッジのホスト実装**が行う |
| ストレージ | 同構造 (`StorageGateway` + `IStorageBridge`) |
| 並行実行 | guest Thread / Task worker の個別上限と VM 全体の worker 上限、未完了 `Task.Delay` の Timer 上限を強制 |

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

**動的コード関連 API** (`Assembly.Load`、式木、`Reflection.Emit`) は標準で利用できます。追加オプションは不要です。これらは CLR のアセンブリロードやコード生成へ脱出せず、`Assembly.Load` は VM の `TypeLoader` に PE を登録し、式木の `Compile` と `Reflection.Emit` の生成コードは VM の実行モデルへ変換して実行します。PE サイズ・ヒープ・命令数の既存ポリシーも適用されます。VM のメタデータモデルで表現できる API 面は同じ経路で扱い、未登録のネイティブ依存面だけは従来どおり fail-closed になります。

`System.Runtime.Loader.AssemblyLoadContext` と `System.Reflection.AssemblyName` もゲスト面として利用できます。`Default`、名前付きコンテキスト、`LoadFromAssemblyBytes`、`LoadFromStream`、`LoadFromAssemblyPath`、`LoadFromAssemblyName`、`Assemblies`、`Unload` を VM の `VmAssemblyContext` に接続します。パスロードは `StorageBridge` 経由に限定し、CLR の `AssemblyLoadContext` や CLR 動的コードをゲストへ公開しません。

**メソッド解決の優先順位** (C4 ランタイムバインド層):
1. **ランタイムバインド** — `BindingKey(型完全名, メソッド名, パラメータ型名, this 有無)` の署名照合
2. **IL 本体実行** — CoreLib を含む全アセンブリの managed IL (`IlPreferred` 面の型は 3. より先にこちらへ解決。置換面 `VmCoreLibSurfaces` に載った面は解決後に `DotnetVM.CoreLib` の managed IL へ差し替え)
3. **legacy intrinsic** — 名前 + 引数個数のレガシー照合 (既存面との互換)
4. **fail-closed** — 未登録の InternalCall は `NotSupportedException`、未登録の P/Invoke は `OperationNotAllowedException` (ネイティブ実行は構造的に禁止。代替実装が登録済みの面のみ `PInvokeReplacement` として委譲)

バインドには由来 (`BindingOrigin` = Managed / InternalCall / PInvokeReplacement / Device) が付与され、`vm.Bindings` で監査できます。CoreLib 画像 (`LoadHostCoreLib = true`) の InternalCall 面 / JIT intrinsic 面 (`RuntimeHelpers.GetMethodTable` のダミー再帰 IL、`Enum.HasFlag` の生データビット演算等) は `CoreLibBindings` が同等意味論の実装で握ります。

### CoreLib ロード (実在 System.Private.CoreLib の実行)
`VmHostOptions.LoadHostCoreLib = true` でホスト自身の System.Private.CoreLib.dll をロードし、CoreLib の managed IL を VM インタプリタで実行します。参照アセンブリ (System.Runtime 等) との型ユニフィケーション、署名精度の VTable / InterfaceMap ディスパッチ (EII 含む)、依存 DLL の同一ディレクトリ自動解決を備えます。

### VM CoreLib (DotnetVM.CoreLib) による置換面
実在 CoreLib の一部の面は、その managed IL が VM の表現モデルに落ちません (`Number.Formatting` の byte* 生ポインタ演算 / NumberBuffer / culture 機構の静的キャッシュ等)。これらは自前の互換ライブラリ **DotnetVM.CoreLib** の managed IL に差し替えて実行します (`VmCoreLibSurfaces` の監査可能な対応表)。

- DotnetVM.CoreLib は外部依存ゼロの普通の .NET クラスライブラリでもあり、**CLR 上でそのまま動作**します (不変カルチャで `int.ToString()` / `int.Parse` / `Convert` 系と同一結果)。CLR 差分テストで正当性を担保してから VM に配線します
- 差し替えは `Interpreter.Invoke` (唯一の IL 実行入口) で行われるため、MemberRef 解決でも CoreLib IL 内の仮想呼出 (box 済み int の `callvirt ToString` 等) でも同一の面に置換されます。数値の書式・解析や `String.Format` の互換置換面は、現在も不変カルチャ規約 (符号 "-" / 桁区切りなし) です
- IL 実行されるため ExecutionTracer には `DotnetVM.CoreLib` フレームとして記録され、legacy intrinsic 委譲と区別できます
- 対応表に載っていない面 (ボックス化 `object` 経由の `Convert.ToInt32(object)` 等は従来どおり legacy intrinsic 委譲) を含む全委譲面の分類は、次節の監査表 (`CoreLibSurfaceAudit`) が唯一の真実源です

### CoreLib 委譲面の監査 (C5.5)
実在 CoreLib の面のうち managed IL を実行せず VM 側で処理する面は、すべて `CoreLibSurfaceAudit` 監査表に**分類 + 正当化**つきで登録されます。未分類の新規登録は監査テストが検出して落ちるため、未分類ゼロが構造的に保証されます。

| 分類 | 意味 |
|---|---|
| **RealCoreLibIl** | 実在 CoreLib の managed IL を VM で実行。監査表上の委譲キーは CoreLib 未ロード時の代替経路としての影 |
| **VmCoreLibSubstitute** | DotnetVM.CoreLib の置換 IL で実行 (実配線は `VmCoreLibSurfaces.Faces`) |
| **RuntimeInternal** | 実 CLR も IL を実行しない面。正当化タグ: `jit-intrinsic` (HasFlag / Unsafe / Interlocked 等の JIT 置換面) / `internal-call` (FastAllocateString / Monitor FastPath 等) / `runtime-representation` (Object.GetType / Type・MethodBase / 例外等のランタイム内部表現依存) / `culture-out-of-scope` (CultureInfo / CompareInfo の guest 実装が未対応のためホスト BCL に委譲) |
| **Device** | VM デバイス / ゲートウェイ面 (Console / File / WebClient / IDisposable no-op 既定)。I/O は仮想デバイス経由限定という設計原則 |

`CoreLibSurfaceAuditTests` が双方向 (順方向: 全登録キーが監査表に載っている / 逆方向: 監査表エントリに実体登録がある) とシャドウ禁止 (置換面にバインドを登録する退行の検出) を常時検査します。

ロケールは `VmHostOptions.Culture` に `CultureInfo` を指定できます (既定は `InvariantCulture`)。例: `new VmHostOptions { Culture = CultureInfo.GetCultureInfo("ja-JP") }`。VM は各外側 guest 呼出の間だけ `CurrentCulture` と `CurrentUICulture` に反映し、終了時にホスト thread の値を復元します。文字列比較・大文字小文字変換、DateTime / TimeSpan の Parse・ToString、および decimal.Parse はこの設定を使います。`CultureInfo` オブジェクト自体の guest 面は未実装です。また DotnetVM.CoreLib の数値書式・整数 Parse や `String.Format` 置換面は不変カルチャ規約を維持します。ordinal で意味論確定の面 (CompareOrdinal / IndexOf(char) / Contains / Replace / Split 全 overload) は DotnetVM.CoreLib 置換面 (`StringOrdinalOps`) が CLR と同じ結果を提供します。

### 実行トレース (ExecutionTracer)
`vm.Tracer.Start()` 〜 `Stop()` の間に IL 本体を実行したフレームが (アセンブリ名, 型完全名, メソッド名) で記録されます。intrinsic / ランタイムバインドへの委譲は IL フレームを持たないため記録されず、「CoreLib の managed IL が実際に走ったこと」の証明に使います。

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
| **Prefix modeled** | VM のメモリモデルに合う範囲で効果を反映 |
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
| `sizeof` | ゲスト値型は ClassLayout の Pack/Size と FieldLayout の明示オフセットを反映。順次配置のネスト値型の整列は近似。値型フィールド自体は VM のスロットとして保持するため、明示レイアウトの重なりアクセスは未対応 |
| `arglist` | ハンドル生成のみ。varargs 実呼出は fail-closed (C# 産 IL では生成されない) |
| `jmp` | 尾呼び移行として実装 (残フレームを実行せず呼出先の戻り値を引き継ぐ)。intrinsic 面への移行は拒否 |
| unmanaged 生メモリ | `ldind.r4` / `stind.r4` は 4 バイト幅、`ldobj` は符号付き小整数と列挙型の基底型を反映 |

### Prefix behavior

`volatile.` は対応するメモリアクセス前後にメモリバリアを置きます。`unaligned.` は VM の仮想メモリがアラインメント制約を持たないため no-op です。`readonly. ldelema` は読み取り専用 ByRef を作り、書き込み命令で拒否します。`tail.` + `call`/`callvirt`/`calli` は、戻り値型が一致し、protected region 外で、呼出元の引数/ローカルへの参照を渡さない場合にフレームを置き換えます。それ以外は通常の呼出にフォールバックします。`jmp` は評価スタックを空にし、呼出元と呼出先のシグネチャが一致するゲストメソッド間でフレームを置き換えます。

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
- [x] C5: CoreLib IL 実行の全面化 + 差分テスト (String/整数 ToString・Parse/Convert/Math の IL 実行、ExecutionTracer で IL 実行証明、VM CoreLib (DotnetVM.CoreLib) 置換面、newobj string 構築面、例外既定文言のホスト CLR 委譲)
- [x] C5.5: CoreLib 委譲面の全面監査 (分類ゼロ構造保証: RealCoreLibIl / VmCoreLibSubstitute / RuntimeInternal / Device + 正当化タグ)。Convert(object) / Enum IConvertible / 整数・浮動小数点書式・Parse / String ordinal・Split・Format 面を実 CoreLib IL または DotnetVM.CoreLib 置換 IL へ移行、culture 面 (Compare / 大文字小文字等) は設定カルチャのホスト委譲で監査確定
- [x] C6: ゲストスレッド / 並行実行対応 (guest Thread、Monitor の競合・待機、並列ホスト呼出、スレッド別 interpreter frame、stop-the-world GC)
- [x] C6.1: Task / ValueTask / async-await (Task / Task<T>、ValueTask / ValueTask<T>、Delay / Run / FromResult、各 awaiter、ConfigureAwait、async state machine)
- [ ] M8: 簡易 JIT (IL → 式ツリー → デリゲート昇格、ホットメソッド自動昇格)
- [ ] M9: デバッガ / 実行トレース

C6.1 は `Task` / `Task<T>` と `ValueTask` / `ValueTask<T>` の基本 await、`Task.Delay`、`Task.Run`、`Task.FromResult` / `ValueTask.FromResult`、`ConfigureAwait(bool)` に対応する。`IValueTaskSource` / `IValueTaskSource<T>`、`OnCompleted` / `UnsafeOnCompleted` を使う独自 awaiter、キャンセル token、`Task.WhenAll` / `WhenAny` / `WaitAll`、および `SynchronizationContext` の捕捉・`ConfigureAwait(false)` にも対応する。guest Thread と Task worker は `VmHostOptions` の個別上限と VM 全体の上限で制御されます (既定はいずれも最大 64 worker、`Task.Delay` の未完了 Timer は最大 1024)。

プロダクト本体は依存ゼロ (`Microsoft.CodeAnalysis.CSharp` / `xunit` はテストプロジェクトのみ)。同梱の DotnetVM.CoreLib も依存ゼロのクラスライブラリで、VM の置換面として DotnetVM.dll と同じディレクトリに配置される。
