using FluentAssertions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class ClassificationOrderTests
{
    [Fact]
    public void InDisplayOrder_SortsByOrderFirst()
    {
        var items = new[]
        {
            new Project { Id = 1, Name = "A", Order = 2 },
            new Project { Id = 2, Name = "B", Order = 0 },
            new Project { Id = 3, Name = "C", Order = 1 },
        };

        items.InDisplayOrder().Select(p => p.Name).Should().Equal("B", "C", "A");
    }

    /// <summary>既存の行は全部 Order = 0。そのうちは今までの名前順がそのまま出る。</summary>
    [Fact]
    public void InDisplayOrder_SameOrder_FallsBackToNameThenId()
    {
        var items = new[]
        {
            new Label { Id = 3, Name = "B" },
            new Label { Id = 2, Name = "A" },
            new Label { Id = 1, Name = "A" },
        };

        items.InDisplayOrder().Select(l => l.Id).Should().Equal(1, 2, 3);
    }
}
