using FluentAssertions;
using MoTask.App.Ai.BoardTools;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class BoardLookupTests
{
    private static readonly IReadOnlyList<Column> Columns = new[]
    {
        new Column { Id = 1, Name = "未着手", Order = 0, Role = ColumnRole.Backlog },
        new Column { Id = 2, Name = "進行中", Order = 1, Role = ColumnRole.Active },
        new Column { Id = 3, Name = "完了", Order = 2, Role = ColumnRole.Done },
    };

    private static readonly IReadOnlyList<Label> Duplicated = new[]
    {
        new Label { Id = 10, Name = "bug" },
        new Label { Id = 11, Name = "BUG" },
    };

    private static Result<Column> Resolve(McpRef reference)
        => BoardLookup.Resolve(Columns, reference, c => c.Id, c => c.Name, Strings.McpKindColumn);

    [Fact]
    public void Resolve_ById()
        => Resolve(McpRef.FromId(2)).Value!.Name.Should().Be("進行中");

    [Fact]
    public void Resolve_ByName_IgnoresCaseAndSurroundingSpace()
        => Resolve(McpRef.FromName("  進行中 ")).Value!.Id.Should().Be(2);

    [Fact]
    public void Resolve_UnknownId_FailsAndListsCandidates()
    {
        var result = Resolve(McpRef.FromId(99));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("列").And.Contain("99").And.Contain("未着手").And.Contain("完了");
    }

    [Fact]
    public void Resolve_UnknownName_FailsAndListsCandidates()
    {
        var result = Resolve(McpRef.FromName("レビュー"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("レビュー").And.Contain("進行中");
    }

    [Fact]
    public void Resolve_AmbiguousName_Fails()
    {
        var result = BoardLookup.Resolve(Duplicated, McpRef.FromName("bug"), l => l.Id, l => l.Name, Strings.McpKindLabel);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("ラベル").And.Contain("bug");
    }

    [Fact]
    public void Resolve_AmbiguousName_ResolvesByIdInstead()
        => BoardLookup.Resolve(Duplicated, McpRef.FromId(11), l => l.Id, l => l.Name, Strings.McpKindLabel)
            .Value!.Name.Should().Be("BUG");
}
