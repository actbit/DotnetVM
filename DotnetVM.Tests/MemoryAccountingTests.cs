using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// メモリ会計の正確性に関するテスト:
/// - localloc / newarr はホスト側の実確保 (new byte[] 等) の**前**に上限を検査する
///   (VmHeap.Reserve → 実確保 → VmHeap.Register の順。巨大確保がチェック前にホスト側を圧迫しない)
/// - localloc ブロックは VmNativePointer から VmLocallocMemory 参照で保持され、GC グラフ上でも
///   到達可能 (参照中ブロックが回収されて会計から消えることがない)
/// - intrinsic がホスト側で確保するバッファ (DefaultInterpolatedStringHandler 内部の StringBuilder 等)
///   も累計上限に計上される (ChargeHostBuffer)
/// </summary>
public class MemoryAccountingTests {
    private const string Source = """
        using System;
        namespace Vm {
            public static unsafe class Account {
                // GC をまたいで stackalloc ブロックが生存すること (ブロックは GC 管理、
                // ポインタ → VmLocallocMemory 参照で GC グラフに載る)
                public static long PointerSurvivesGc(int n) {
                    int* p = stackalloc int[8];
                    for (var i = 0; i < 8; i++) p[i] = i * 7;
                    // 大量確保で GC 要求を立てさせる (GcTriggerAllocationInterval 超過 → セーフポイントで回収)
                    for (var i = 0; i < n; i++) { var o = new object(); }
                    var sum = 0L;
                    for (var i = 0; i < 8; i++) sum += p[i];
                    return sum;
                }

                // 巨大 stackalloc: 上限はホスト確保より先に検査される → VM はメモリ拒否
                // (実 CLR では 64MB の stackalloc はスタックを破壊しうるため CLR 側では実行しない)
                public static long HugeStackalloc() {
                    byte* p = stackalloc byte[64 * 1024 * 1024];
                    return p[0];
                }

                // 巨大配列: 同様に実確保より先に拒否 (実 CLR では OOM のため CLR 側では実行しない)
                public static long HugeArray() {
                    var a = new long[int.MaxValue / 2];
                    return a[0];
                }

                // 補間ハンドラのホスト側バッファ (StringBuilder) が累計上限に計上されること。
                // 生成文字列は毎回同一 (文字列プールで初回のみ計上) なので、拒否されるなら
                // ハンドラ内部バッファの計上 (ChargeHostBuffer) が効いている場合だけ
                public static long BurnInterpolations(int x, int n) {
                    for (var i = 0; i < n; i++) { var t = $"{x}"; }
                    return n;
                }
            }
        }
        """;

    private static readonly byte[] PeBytes =
        TestAssemblyCompiler.CompileToBytes(Source, "AccountAsm", allowUnsafe: true);

    private static readonly Assembly ClrAssembly = Assembly.Load(PeBytes);

    private static VirtualMachine CreateVm(MemoryPolicy? memory = null) {
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = memory ?? new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(PeBytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    // ---- localloc ブロックの GC 生存 (VmNativePointer → VmLocallocMemory 参照) ----

    [Fact]
    public void StackallocBlock_SurvivesCollectionAndKeepsAccounting() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            GcTriggerAllocationInterval = 40, // object 1 個 (24 バイト計上) でも回収要求が立つ
        });
        // ポインタがローカルに残ったまま GC が起きる → ブロックは回収されず、
        // ポインタ経由の読み書きも壊れない
        Assert.Equal(196L, vm.Invoke("Vm.Account", "PointerSurvivesGc", 10_000));
        Assert.True(vm.Heap.Snapshot().CollectionCount >= 1);
        // 会計からも消えていない: ブロック (24 + 32 バイト) がヒープに残っている
        // (実行中の最終 GC 時点でポインタはフレーム ローカル = ルート到達)
        Assert.Contains(vm.Heap.TrackedObjects, o => o is DotnetVM.Runtime.Objects.VmLocallocMemory);
    }

    // ---- 実確保より先の上限検査 (Reserve → 実確保 → Register) ----

    [Fact]
    public void HugeStackalloc_RejectedBeforeHostAllocation() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 1 << 20, // 1MB << 64MB 要求
        });
        Assert.Throws<MemoryQuotaExceededException>(
            () => vm.Invoke("Vm.Account", "HugeStackalloc"));
    }

    [Fact]
    public void HugeArray_RejectedBeforeHostAllocation() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 1 << 20, // 1MB << 16GB 要求
        });
        Assert.Throws<MemoryQuotaExceededException>(
            () => vm.Invoke("Vm.Account", "HugeArray"));
    }

    // ---- ホスト側バッファ (補間ハンドラの StringBuilder) の計上 ----

    [Fact]
    public void InterpolationHandlerHostBuffer_CountedTowardTotalLimit() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 4_096, // 生成文字列はプール済みでほぼゼロ。バッファ計上で溢れる
        });
        Assert.Throws<MemoryQuotaExceededException>(
            () => vm.Invoke("Vm.Account", "BurnInterpolations", 42, 100_000));
    }

    [Fact]
    public void InterpolationHandler_SmallUsageSucceeds() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 4_096,
        });
        // 同じ上限でも回数が少なければ通る (計上は使用量に比例)
        Assert.Equal(50L, vm.Invoke("Vm.Account", "BurnInterpolations", 42, 50));
    }
}
