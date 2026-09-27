using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class ModelTests
{
    [Fact]
    public void Column_ActiveCount_ExcludesDeletedTasks()
    {
        var column = new Column { Name = "進行中", WipLimit = 1 };
        column.Tasks.Add(new TaskItem { Title = "a" });
        column.Tasks.Add(new TaskItem { Title = "b", DeletedAt = DateTime.UtcNow });

        column.ActiveCount.Should().Be(1);
        column.IsOverWip.Should().BeFalse();

        column.Tasks.Add(new TaskItem { Title = "c" });
        column.IsOverWip.Should().BeTrue();
    }

    [Fact]
    public void Column_WithoutWipLimit_IsNeverOverWip()
    {
        var column = new Column { Name = "未着手" };
        column.Tasks.Add(new TaskItem { Title = "a" });
        column.IsOverWip.Should().BeFalse();
    }

    [Fact]
    public void Result_Ok_CarriesWarnings()
    {
        var r = Result.Ok(new[] { "warn" });
        r.IsSuccess.Should().BeTrue();
        r.Error.Should().BeNull();
        r.Warnings.Should().ContainSingle().Which.Should().Be("warn");

        var f = Result.Fail<int>("だめ");
        f.IsSuccess.Should().BeFalse();
        f.Error.Should().Be("だめ");
        f.Value.Should().Be(default);
    }

    [Fact]
    public void HistoryDetail_RoundTrips_WithJapanese()
    {
        var changes = new Dictionary<string, FieldChange>
        {
            ["Title"] = new("旧", "新"),
            ["DueDate"] = new(null, "2026-09-10"),
        };
        var json = HistoryDetail.Serialize(changes);
        json.Should().Contain("旧").And.NotContain("\\u");
        var back = HistoryDetail.Deserialize(json);
        back["Title"].Should().Be(new FieldChange("旧", "新"));
        back["DueDate"].From.Should().BeNull();
        HistoryDetail.Deserialize("").Should().BeEmpty();
    }

    [Fact]
    public void Messages_ResolveFromResx()
    {
        Messages.TitleRequired.Should().Be("タイトルを入力してください");
        string.Format(Messages.WipExceededFormat, "進行中", 3).Should().Be("進行中 のカードが上限 3 枚を超えています");
    }
}
