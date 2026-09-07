using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// board.json（仕様 §7）。文字列比較ではなく JSON として読み直して見る
/// （整形を変えてもテストが割れないように）。
/// </summary>
public class BoardSnapshotTests
{
    private static readonly DateTime Updated = new(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

    private static Board Sample()
    {
        var backlog = new Column { Id = 1, Name = "やること", Order = 0, Role = ColumnRole.Backlog };
        var active = new Column { Id = 2, Name = "今日中", Order = 1, Role = ColumnRole.Active };
        var done = new Column { Id = 3, Name = "完了", Order = 2, Role = ColumnRole.Done };

        active.Tasks.Add(new TaskItem
        {
            Id = 45, Title = "Q4企画書の内容を確定する", ColumnId = 2, Position = 0, ProjectId = 100,
            DueDate = new DateOnly(2026, 9, 8), CreatedAt = Updated, UpdatedAt = Updated,
            Labels = { new Label { Id = 200, Name = "社内", Color = "accent-500" } },
        });
        backlog.Tasks.Add(new TaskItem
        {
            Id = 46, Title = "消したもの", ColumnId = 1, Position = 0,
            CreatedAt = Updated, UpdatedAt = Updated, DeletedAt = Updated,
        });
        done.Tasks.Add(new TaskItem
        {
            Id = 47, Title = "終わったもの", ColumnId = 3, Position = 0,
            CreatedAt = Updated, UpdatedAt = Updated, CompletedAt = Updated,
        });

        var board = new Board { Id = 1, Name = "テスト" };
        board.Columns.AddRange(new[] { backlog, active, done });
        return board;
    }

    private static readonly Dictionary<int, string> Projects = new() { [100] = "プロジェクトQ4" };

    private static JsonElement Build(Board? board = null, IReadOnlyCollection<int>? busy = null)
        => JsonDocument.Parse(BoardSnapshot.Build(
                board ?? Sample(), new DateOnly(2026, 9, 7), Projects, busy ?? Array.Empty<int>()))
            .RootElement.Clone();

    [Fact]
    public void Build_WritesTheDateAndEveryColumn()
    {
        var root = Build();

        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Equal("やること", "今日中", "完了");
        root.GetProperty("columns")[1].GetProperty("id").GetInt32().Should().Be(2);
        root.GetProperty("columns")[1].GetProperty("role").GetString().Should().Be("Active");
    }

    [Fact]
    public void Build_WritesEveryFieldOfATask()
    {
        var task = Build().GetProperty("tasks").EnumerateArray().Single();

        task.GetProperty("id").GetInt32().Should().Be(45);
        task.GetProperty("title").GetString().Should().Be("Q4企画書の内容を確定する");
        task.GetProperty("columnId").GetInt32().Should().Be(2);
        task.GetProperty("columnRole").GetString().Should().Be("Active");
        task.GetProperty("project").GetString().Should().Be("プロジェクトQ4");
        task.GetProperty("due").GetString().Should().Be("2026-09-08");
        task.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Should().Equal("社内");
        task.GetProperty("updatedAt").GetString().Should().Be("2026-09-05T10:00:00Z");
        task.GetProperty("hasActiveAiJob").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Build_LeavesOutDeletedAndDoneTasks()
    {
        var ids = Build().GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("id").GetInt32()).ToList();

        ids.Should().Equal(45);
    }

    [Fact]
    public void Build_MarksTasksThatAlreadyHaveAnAiJob()
        => Build(busy: new[] { 45 }).GetProperty("tasks").EnumerateArray().Single()
            .GetProperty("hasActiveAiJob").GetBoolean().Should()
            .BeTrue("プランの『AI 準備完了』区分に要る（仕様 §7）");

    [Fact]
    public void Build_WritesNullForAMissingProjectAndDueDate()
    {
        var board = Sample();
        board.Columns[1].Tasks[0].ProjectId = null;
        board.Columns[1].Tasks[0].DueDate = null;

        var task = Build(board).GetProperty("tasks").EnumerateArray().Single();

        task.GetProperty("project").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("due").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Build_DoesNotEscapeJapaneseText()
        => BoardSnapshot.Build(Sample(), new DateOnly(2026, 9, 7), Projects, Array.Empty<int>())
            .Should().Contain("今日中", "人が開いて読めるファイルにする");
}
