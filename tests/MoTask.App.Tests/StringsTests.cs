using FluentAssertions;
using MoTask.App.Resources;
using Xunit;

namespace MoTask.App.Tests;

public class StringsTests
{
    [Fact]
    public void Strings_ResolveFromResx()
    {
        Strings.Brand.Should().Be("TASKS");
        string.Format(Strings.HistoryMovedFormat, "未着手", "進行中").Should().Be("未着手 → 進行中");
    }
}
