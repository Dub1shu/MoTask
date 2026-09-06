using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai.BoardTools;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class BoardJsonTests
{
    private static readonly IReadOnlyDictionary<int, string> ProjectNames = new Dictionary<int, string> { [100] = "顧客A対応" };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BoardShape_ListsColumnsInOrder_WithRoleWipAndCounts()
    {
        var board = TestBoards.Sample();
        board.Columns[1].Tasks.Add(new TaskItem { Id = 20, Title = "もう 1 件", ColumnId = 2, Position = 1 });

        var json = Parse(BoardJson.Serialize(
            BoardJson.BoardShape(board, new[] { TestBoards.ProjectA() }, new[] { TestBoards.Urgent() }),
            Array.Empty<string>()));

        var columns = json.GetProperty("columns");
        columns.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Equal("未着手", "進行中", "完了");
        columns[0].GetProperty("role").GetString().Should().Be("Backlog");
        columns[0].GetProperty("wipLimit").ValueKind.Should().Be(JsonValueKind.Null);
        columns[1].GetProperty("wipLimit").GetInt32().Should().Be(1);
        columns[1].GetProperty("taskCount").GetInt32().Should().Be(2);
        columns[1].GetProperty("overWip").GetBoolean().Should().BeTrue();
        json.GetProperty("projects")[0].GetProperty("name").GetString().Should().Be("顧客A対応");
        json.GetProperty("projects")[0].GetProperty("archived").GetBoolean().Should().BeFalse();
        json.GetProperty("labels")[0].GetProperty("color").GetString().Should().Be("accent-500");
    }

    [Fact]
    public void BoardShape_CountsExcludeDeletedTasks()
    {
        var board = TestBoards.WithDeletedCard();

        var json = Parse(BoardJson.Serialize(
            BoardJson.BoardShape(board, Array.Empty<Project>(), Array.Empty<Label>()), Array.Empty<string>()));

        json.GetProperty("columns")[0].GetProperty("taskCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public void TaskSummary_TruncatesTheDescriptionAt200Characters()
    {
        var board = TestBoards.Sample();
        var column = board.Columns[0];
        var task = column.Tasks[0];
        task.Description = new string('あ', 250);

        var json = Parse(BoardJson.Serialize(BoardJson.TaskSummary(task, column, ProjectNames), Array.Empty<string>()));

        var description = json.GetProperty("description").GetString()!;
        description.Should().HaveLength(201);
        description.Should().EndWith("…");
        description[..200].Should().Be(new string('あ', 200));
    }

    [Fact]
    public void Truncate_DoesNotSplitASurrogatePairAtTheBoundary()
    {
        // 😀(U+1F600) は UTF-16 だとサロゲートペア(2 コード単位)。199 文字目までを 'あ' で埋めて、
        // 200 文字目(高位サロゲート)と 201 文字目(低位サロゲート)が limit=200 の境界をまたぐようにする。
        var text = new string('あ', 199) + "😀";

        var truncated = BoardJson.Truncate(text, 200);

        truncated.Should().HaveLength(200);
        truncated.Should().EndWith("…");
        var body = truncated[..^1];
        body.Should().HaveLength(199);
        char.IsSurrogate(body[^1]).Should().BeFalse();

        // シリアライズしても孤立サロゲートが置換文字に化けないことを確認する。
        JsonSerializer.Serialize(truncated).Should().NotContain("�");
    }

    [Fact]
    public void Truncate_NonSurrogateBoundary_TruncatesAtTheExactLimit()
    {
        var text = new string('あ', 250);

        var truncated = BoardJson.Truncate(text, 200);

        truncated.Should().HaveLength(201);
        truncated.Should().EndWith("…");
        truncated[..200].Should().Be(new string('あ', 200));
    }

    [Fact]
    public void TaskSummary_ShapesTheFieldsFromTheSpec()
    {
        var board = TestBoards.Sample();
        var column = board.Columns[0];
        var task = column.Tasks[0];
        task.Description = "短い";

        var json = Parse(BoardJson.Serialize(BoardJson.TaskSummary(task, column, ProjectNames), Array.Empty<string>()));

        json.GetProperty("id").GetInt32().Should().Be(10);
        json.GetProperty("title").GetString().Should().Be("請求先情報を更新する");
        json.GetProperty("column").GetString().Should().Be("未着手");
        json.GetProperty("project").GetString().Should().Be("顧客A対応");
        json.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Should().Equal("至急");
        json.GetProperty("dueDate").GetString().Should().Be("2026-09-08");
        json.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.Null);
        json.TryGetProperty("createdAt", out _).Should().BeFalse();
    }

    [Fact]
    public void TaskSummary_WithoutProject_ReturnsNull()
    {
        var board = TestBoards.Sample();

        var json = Parse(BoardJson.Serialize(
            BoardJson.TaskSummary(board.Columns[0].Tasks[1], board.Columns[0], ProjectNames), Array.Empty<string>()));

        json.GetProperty("project").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TaskDetail_KeepsTheWholeDescription_AndAddsTimestamps()
    {
        var board = TestBoards.Sample();
        var column = board.Columns[0];
        var task = column.Tasks[0];
        task.Description = new string('い', 250);
        task.CompletedAt = new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc);

        var json = Parse(BoardJson.Serialize(BoardJson.TaskDetail(task, column, ProjectNames), Array.Empty<string>()));

        json.GetProperty("description").GetString().Should().HaveLength(250);
        json.GetProperty("createdAt").GetString().Should().StartWith("2026-09-01T00:00:00");
        json.GetProperty("updatedAt").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("completedAt").GetString().Should().StartWith("2026-09-05T01:02:03");
        json.GetProperty("position").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Serialize_AddsWarningsOnlyWhenThereAreSome()
    {
        var withNone = Parse(BoardJson.Serialize(new Dictionary<string, object?> { ["ok"] = true }, Array.Empty<string>()));
        withNone.TryGetProperty("warnings", out _).Should().BeFalse();

        var withSome = Parse(BoardJson.Serialize(new Dictionary<string, object?> { ["ok"] = true }, new[] { "WIP 超過" }));
        withSome.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Equal("WIP 超過");
    }

    [Fact]
    public void Serialize_LeavesJapaneseUnescaped()
        => BoardJson.Serialize(new Dictionary<string, object?> { ["name"] = "進行中" }, Array.Empty<string>())
            .Should().Contain("進行中");
}
