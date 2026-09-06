using FluentAssertions;
using MoTask.App;
using Xunit;

namespace MoTask.App.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => $@"Local\MoTaskTests.{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_FirstCallerWins_SecondGetsNull()
    {
        var name = UniqueName();

        using var first = SingleInstance.TryAcquire(name);
        var second = SingleInstance.TryAcquire(name);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryAcquire_AfterTheFirstIsDisposed_SucceedsAgain()
    {
        var name = UniqueName();

        SingleInstance.TryAcquire(name)!.Dispose();

        using var again = SingleInstance.TryAcquire(name);
        again.Should().NotBeNull();
    }

    [Fact]
    public void MutexName_IsSessionLocal()
        => SingleInstance.MutexName.Should().Be(@"Local\MoTask.SingleInstance");
}
