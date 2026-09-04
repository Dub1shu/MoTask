using FluentAssertions;
using Xunit;

namespace MoTask.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TestInfrastructure_Works()
    {
        (1 + 1).Should().Be(2);
    }
}
