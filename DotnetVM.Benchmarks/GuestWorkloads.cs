using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DotnetVM.Benchmarks;

public static class GuestWorkloads {
    public static int SpanCopies(int count) {
        var source = new int[256]; var target = new int[256]; source[255] = 7;
        var total = 0;
        for (var i = 0; i < count; i++) { new System.Span<int>(source).CopyTo(new System.Span<int>(target)); total += target[255]; }
        return total;
    }
    public static int IntegerFormatting(int count) {
        var total = 0;
        for (var i = 0; i < count; i++) total += (-i).ToString().Length;
        return total;
    }
    public static int IntegerParsing(int count) {
        var total = 0;
        for (var i = 0; i < count; i++) total += int.Parse("-12345");
        return total;
    }
    public static int StringCopies(int count) {
        var total = 0;
        var chars = new[] { '日', '本', '\uD83D', '\uDE00' };
        for (var i = 0; i < count; i++) total += new string(chars).Length;
        return total;
    }
    public static int Arithmetic(int count) {
        var sum = 0;
        for (var i = 0; i < count; i++) sum += (i * 3) ^ (i >> 2);
        return sum;
    }

    private sealed class Counter { public int Value; }
    private sealed class Holder<T> { public T Value = default!; }

    public static int FieldAccess(int count) {
        var counter = new Counter();
        for (var i = 0; i < count; i++) counter.Value += (i * 3) ^ (i >> 2);
        return counter.Value;
    }

    public static int GenericFieldAccess(int count) {
        var counter = new Holder<int>();
        for (var i = 0; i < count; i++) counter.Value += (i * 3) ^ (i >> 2);
        return counter.Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Add(int value) => value + 1;

    public static int MethodCalls(int count) {
        var sum = 0;
        for (var i = 0; i < count; i++) sum += Add(i);
        return sum;
    }

    public static int List(int count) {
        var values = new List<int>(count);
        for (var i = 0; i < count; i++) values.Add(i);
        for (var i = 0; i < count; i++) values[i] += 1;
        var sum = 0;
        foreach (var value in (IEnumerable<int>)values) sum += value;
        return sum;
    }

    public static int Linq(int count) => Enumerable.Range(0, count)
        .Where(value => (value & 1) == 0).Select(value => value * 3).Sum();

    public static int ListGrowth(int count) {
        var values = new List<int>();
        for (var i = 0; i < count; i++) values.Add(i);
        var sum = 0;
        foreach (var value in values) sum += value;
        return sum;
    }

    public static int DictionaryGrowth(int count) {
        var values = new Dictionary<int, int>();
        for (var i = 0; i < count; i++) values.Add(i, i + 1);
        var sum = 0;
        for (var i = 0; i < count; i++) sum += values[i];
        return sum;
    }

    public static int DictionaryInt(int count) {
        var values = new Dictionary<int, int>(count);
        for (var i = 0; i < count; i++) values[i] = i * 3;
        var sum = 0;
        for (var i = 0; i < count; i++) {
            if (values.TryGetValue(i, out var value)) sum += value;
            values.Remove(i);
        }
        return sum + values.Count;
    }

    public static int DictionaryString(int count) {
        var values = new Dictionary<string, int>(8);
        // Fixed keys keep number formatting out of this workload.
        var keys = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta" };
        var sum = 0;
        for (var i = 0; i < count; i++) {
            var key = keys[i & 7];
            values[key] = i;
            if (values.TryGetValue(key, out var value)) sum += value;
        }
        return sum + values.Count;
    }

    private static async Task<int> CompletedAsync(int count) {
        var sum = 0;
        for (var i = 0; i < count; i++) sum += await Task.FromResult(i).ConfigureAwait(false);
        return sum;
    }

    public static int AsyncCompleted(int count) => CompletedAsync(count).GetAwaiter().GetResult();

    private static async ValueTask<int> CompletedValueAsync(int count) {
        var sum = 0;
        for (var i = 0; i < count; i++) sum += await new ValueTask<int>(i).ConfigureAwait(false);
        return sum;
    }

    public static int ValueTaskCompleted(int count) => CompletedValueAsync(count).GetAwaiter().GetResult();

    private static async Task<int> WorkerAsync(int value) {
        await Task.Delay(1).ConfigureAwait(false);
        return value + 1;
    }

    public static int AsyncWorkers(int count) {
        var tasks = new Task<int>[count];
        for (var i = 0; i < count; i++) {
            var value = i;
            tasks[i] = Task.Run(() => WorkerAsync(value));
        }
        var values = Task.WhenAll(tasks).GetAwaiter().GetResult();
        var sum = 0;
        foreach (var value in values) sum += value;
        return sum;
    }
}
