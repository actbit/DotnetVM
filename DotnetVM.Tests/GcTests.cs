using System.Reflection;
using DotnetVM.Host;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// M6 GC の突合テスト。マーク &amp; スイープの到達性 (循環参照含む)、セーフポイント起動
/// (実行中フレームのローカル/静的フィールドが回収されないこと)、メモリポリシーの拒否
/// (累計/生存上限)、GcHandleTable によるホスト側ルート保持を検証する。
/// </summary>
public class GcTests {
    private const string Source = """
        using System;
        namespace Vm {
            // GC 対象の参照グラフ供給源 (循環参照を作れる最小のクラス)
            public class Node {
                public int Value;
                public Node? Next;
                public Node(int value) { Value = value; }
                public Node(int value, Node next) { Value = value; Next = next; }
            }

            public static class Gc {
                // 循環参照 (a→b→a) を作って捨てる。到達不能な循環はマーク&スイープで回収される
                public static int BuildCycles(int count) {
                    for (var i = 0; i < count; i++) {
                        var a = new Node(1);
                        var b = new Node(2, a);
                        a.Next = b; // a→b→a
                    }
                    return count;
                }

                // 実行中に GC が走る (アロケーション間隔をテスト側で小さく設定)。
                // ローカル keep はセーフポイントをまたいで生存しなければならない
                public static int GcDuringRun() {
                    var keep = new Node(42);
                    var sum = 0;
                    for (var i = 0; i < 2000; i++) {
                        var garbage = new Node(i);
                        sum += garbage.Value >= 0 ? 1 : 0;
                    }
                    return keep.Value + sum; // 42 + 2000
                }

                // 静的フィールドが GC ルートであること (ストレージ源: ObjectModel.StaticStorage)
                private static Node? _kept;
                public static int KeepStaticThenCollect() {
                    _kept = new Node(7);
                    for (var i = 0; i < 2000; i++) { var g = new Node(i); }
                    return _kept.Value; // 7
                }

                // 生存し続ける配列 + 大量ゴミ。生存上限に達したら MemoryQuotaExceededException
                public static int LiveLimitVictim(int n) {
                    var keep = new Node[n];
                    for (var i = 0; i < n; i++) keep[i] = new Node(i);
                    for (var i = 0; i < 5000; i++) { var g = new Node(i); }
                    return keep[n - 1]!.Value;
                }
            }
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source);
        return (Assembly.Load(bytes), bytes);
    }

    private static (Assembly Clr, byte[] Bytes) Compile() => Compiled;

    private static VirtualMachine CreateVm(MemoryPolicy? memory = null) {
        var (_, bytes) = Compile();
        var vm = new VirtualMachine(new VmHostOptions {
            Memory = memory ?? new MemoryPolicy { InstructionQuota = 100_000_000 },
        });
        using var stream = new MemoryStream(bytes);
        vm.LoadAssembly(stream);
        return vm;
    }

    // ---- マーク & スイープ (戦略単体) ----

    [Fact]
    public void MarkSweep_KeepsReachableIncludingCycles() {
        var objectType = new VmIntrinsicType { Namespace = "System", Name = "Object", IsValue = false };
        VmBoxedValue Make(params StackSlot[] fields) =>
            new(objectType, fields);

        var c = Make(StackSlot.Null);         // C→A (循環)
        var b = Make(StackSlot.OfObject(c));  // B→C
        var a = Make(StackSlot.OfObject(b));  // A→B
        c.Fields[0] = StackSlot.OfObject(a);
        var orphan = Make();                  // ルートから到達不能

        var strategy = new MarkSweepStrategy();
        var live = strategy.Collect(new GcCollectionContext {
            HeapObjects = [a, b, c, orphan],
            Roots = [a],
        });

        Assert.Contains(a, live);
        Assert.Contains(b, live);
        Assert.Contains(c, live);
        Assert.DoesNotContain(orphan, live);
    }

    // ---- ヒープ (収集・計上) ----

    [Fact]
    public void Heap_CollectSweepsUnrootedAndUpdatesLiveBytes() {
        var heap = new VmHeap(new MemoryPolicy());
        var itemType = VmFieldRvaData.HandleType;
        var arrType = new VmArrayType { ElementType = itemType };

        VmArray MakeArray() => heap.Allocate(new VmArray(arrType, [
            StackSlot.OfObject(heap.Allocate(new VmFieldRvaData { Data = new byte[8] })),
            StackSlot.OfObject(heap.Allocate(new VmFieldRvaData { Data = new byte[8] })),
        ]));

        var rooted = MakeArray();
        heap.AddRootObjectSource(() => [rooted]);
        var garbage = MakeArray(); // ルート未登録 → 回収される

        var before = heap.Snapshot();
        Assert.True(before.LiveBytes > 0);

        var after = heap.Collect();
        // rooted: 配列 24+16*2=56 + 中身 24*2=48 = 104 バイトだけ残る
        Assert.Equal(104, after.LiveBytes);
        Assert.Equal(1, after.CollectionCount);
        Assert.Contains(rooted, heap.TrackedObjects);
        Assert.DoesNotContain(garbage, heap.TrackedObjects);
        // 累計計上は減らない
        Assert.Equal(before.TotalAllocatedBytes, after.TotalAllocatedBytes);
    }

    [Fact]
    public void Heap_AllocateRejectsWhenLiveWouldExceedLimit() {
        var heap = new VmHeap(new MemoryPolicy {
            LiveObjectByteLimit = 200,
        });
        var itemType = VmFieldRvaData.HandleType;
        var arrType = new VmArrayType { ElementType = itemType };

        heap.Allocate(new VmArray(arrType, new StackSlot[5])); // 24+16*5 = 104 → OK
        // 104 + 104 = 208 > 200 → 確保の入口で即拒否 (GC を待たない = クォータ+拒否方式)
        Assert.Throws<MemoryQuotaExceededException>(
            () => heap.Allocate(new VmArray(arrType, new StackSlot[5])));
    }

    // ---- ゲスト実行との統合 ----

    [Fact]
    public void UnreachableCycle_IsCollectedAfterInvoke() {
        using var vm = CreateVm();
        Assert.Equal(100, vm.Invoke("Vm.Gc", "BuildCycles", 100));

        var stats = vm.CollectGarbage();
        // 帰納後に Node は 1 つも到達不能 → 全回収
        Assert.Empty(vm.Heap.TrackedObjects.OfType<VmClassInstance>());
        Assert.Equal(0, stats.LiveBytes);
    }

    [Fact]
    public void LocalRoots_SurviveGcDuringRun() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            GcTriggerAllocationInterval = 40, // Node 1 個 (40 バイト) ごとに回収要求
        });
        // 実行中に複数回 GC が走るが、ローカル keep は回収されず結果が正しい
        Assert.Equal(2042, vm.Invoke("Vm.Gc", "GcDuringRun"));
        Assert.True(vm.CollectGarbage().CollectionCount >= 1);
    }

    [Fact]
    public void StaticFieldRoots_SurviveGcDuringRun() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            GcTriggerAllocationInterval = 40,
        });
        Assert.Equal(7, vm.Invoke("Vm.Gc", "KeepStaticThenCollect"));
    }

    [Fact]
    public void TotalAllocationByteLimit_RejectsDuringGuestRun() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            TotalAllocationByteLimit = 2_000, // Node 2000 個分もない
            GcTriggerAllocationInterval = 40,
        });
        // 累計計上は GC で減らないため、回収しても最終的に拒否される
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("Vm.Gc", "GcDuringRun"));
    }

    [Fact]
    public void LiveObjectByteLimit_RejectsWhenGuestKeepsObjects() {
        using var vm = CreateVm(new MemoryPolicy {
            InstructionQuota = 100_000_000,
            // keep (配列 24+16*10=184 + Node 40*10=400 = 584 バイト) が上限を超える構え
            LiveObjectByteLimit = 500,
            GcTriggerAllocationInterval = 40,
        });
        Assert.Throws<MemoryQuotaExceededException>(() => vm.Invoke("Vm.Gc", "LiveLimitVictim", 10));
    }

    // ---- GcHandleTable ----

    [Fact]
    public void GcHandle_KeepsObjectAliveAcrossCollect() {
        using var vm = CreateVm();
        var instance = vm.CreateInstance("Vm.Node", 42);

        var handle = vm.Handles.Register(instance);
        Assert.True(handle.IsAllocated);
        Assert.Equal(1, vm.Handles.Count);

        // ホスト参照を失った前提でも、ハンドルがルートなので回収されない
        vm.CollectGarbage();
        Assert.Contains(instance, vm.Heap.TrackedObjects);
        Assert.Same(instance, vm.Handles.GetTarget(handle));

        // 解除すれば次の GC で回収される (二重解除も許容)
        vm.Handles.Free(handle);
        vm.Handles.Free(handle);
        Assert.Equal(0, vm.Handles.Count);
        vm.CollectGarbage();
        Assert.DoesNotContain(instance, vm.Heap.TrackedObjects);
        Assert.Null(vm.Handles.GetTarget(handle));
    }

    [Fact]
    public void UnrootedInstance_IsCollected() {
        using var vm = CreateVm();
        var instance = vm.CreateInstance("Vm.Node", 42);
        // ハンドルを登録しなければホストローカルは VM の GC ルートではない
        vm.CollectGarbage();
        Assert.DoesNotContain(instance, vm.Heap.TrackedObjects);
    }

    // ---- GcHandleTable 単体 ----

    [Fact]
    public void HandleTable_RegisterGetFree() {
        var table = new GcHandleTable();
        var obj1 = new VmFieldRvaData { Data = new byte[4] };
        var obj2 = new VmFieldRvaData { Data = new byte[4] };

        var h1 = table.Register(obj1);
        var h2 = table.Register(obj2);
        Assert.NotEqual(h1.Id, h2.Id);
        Assert.Equal(2, table.Count);
        Assert.Same(obj1, table.GetTarget(h1));
        Assert.Same(obj2, table.GetTarget(h2));

        table.Free(h1);
        Assert.Null(table.GetTarget(h1));
        Assert.Same(obj2, table.GetTarget(h2));
        Assert.Equal(1, table.Count);

        // 既定値ハンドルは無効
        Assert.False(default(GcHandle).IsAllocated);
        Assert.Null(table.GetTarget(default));
    }
}
