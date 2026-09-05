using FluentAssertions;
using MoTask.Core.Abstractions;
using Xunit;

namespace MoTask.Core.Tests;

public class OperationGateTests
{
    [Fact]
    public async Task RunAsync_SerializesConcurrentCallers()
    {
        var gate = new OperationGate();
        var inside = 0;
        var maxInside = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => gate.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref inside);
            return 0;
        }));
        await Task.WhenAll(tasks);

        maxInside.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ReleasesGateWhenActionThrows()
    {
        var gate = new OperationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(() => throw new InvalidOperationException()));

        var ran = false;
        await gate.RunAsync(() => { ran = true; return Task.CompletedTask; });
        ran.Should().BeTrue();
    }
}
