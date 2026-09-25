namespace DotnetVM.BenchmarkGuest;

public static class Workloads {
    public static int ArithmeticLoop(int iterations) {
        var state = 17;
        for (var i = 0; i < iterations; i++) {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            state ^= i;
            state += i % 31;
        }
        return state;
    }

    public static int BranchLoop(int iterations) {
        var state = 0;
        for (var i = 0; i < iterations; i++) {
            if ((i & 1) == 0)
                state += i;
            else
                state ^= i;
            if (state % 3 == 0)
                state = unchecked((state * 31) + 7);
        }
        return state;
    }

    public static int ArraySum(int length) {
        var values = new int[length];
        for (var i = 0; i < values.Length; i++)
            values[i] = unchecked((i * 3) ^ (i >> 2));

        var sum = 0;
        for (var i = 0; i < values.Length; i++)
            sum = unchecked(sum + values[i]);
        return sum;
    }

    public static int CallLoop(int iterations) {
        var state = 17;
        for (var i = 0; i < iterations; i++)
            state = Mix(state, i);
        return state;
    }

    public static int ObjectLoop(int iterations) {
        var sum = 0;
        for (var i = 0; i < iterations; i++)
            sum = unchecked(sum + new Holder(i ^ 17).Value);
        return sum;
    }

    private static int Mix(int state, int value) =>
        unchecked((state * 31) + value + 7);

    public sealed class Holder(int value) {
        public int Value { get; } = value;
    }
}
