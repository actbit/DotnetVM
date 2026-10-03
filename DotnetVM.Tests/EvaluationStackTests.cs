using DotnetVM.Runtime.Execution;
using Xunit;

namespace DotnetVM.Tests;

public class EvaluationStackTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void CapacityAndUnderflowAreChecked(int capacity) {
        var stack = new EvaluationStack(capacity);
        Assert.Throws<BadImageFormatException>(() => stack.Pop());
        Assert.Throws<BadImageFormatException>(() => stack.Peek());
        for (var i = 0; i < capacity; i++)
            stack.Push(StackSlot.OfInt32(i));
        Assert.Throws<BadImageFormatException>(() => stack.Push(StackSlot.Null));
        Assert.Equal(capacity, stack.Count);
        for (var i = capacity - 1; i >= 0; i--)
            Assert.Equal(i, stack.Pop().AsInt32);
        Assert.Throws<BadImageFormatException>(() => stack.Pop());
        Assert.Equal(0, stack.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void PeekAtRejectsInvalidDepth(int depth) {
        var stack = new EvaluationStack(2);
        stack.Push(StackSlot.OfInt32(11));
        stack.Push(StackSlot.OfInt32(22));
        Assert.Throws<BadImageFormatException>(() => stack.PeekAt(depth));
        Assert.Equal(22, stack.PeekAt(0).AsInt32);
        Assert.Equal(11, stack.PeekAt(1).AsInt32);
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void PopAndClearEraseReferencesAndAllowReuse() {
        var stack = new EvaluationStack(2);
        var value = new object();
        stack.Push(StackSlot.OfObject(value));
        ref var first = ref stack.Peek();
        stack.Push(StackSlot.OfByRef(value));
        ref var second = ref stack.Peek();
        Assert.Same(value, stack.Pop().ObjectValue);
        Assert.Equal(StackKind.Empty, second.Kind);
        Assert.Null(second.ObjectValue);
        stack.Clear();
        Assert.Equal(StackKind.Empty, first.Kind);
        Assert.Null(first.ObjectValue);
        Assert.Equal(0, stack.Count);
        stack.Push(StackSlot.OfFloat(1.5));
        Assert.Equal(1.5, stack.Pop().DoubleValue);
        Assert.Equal(default(StackSlot), first);
    }
}
