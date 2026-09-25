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
}
