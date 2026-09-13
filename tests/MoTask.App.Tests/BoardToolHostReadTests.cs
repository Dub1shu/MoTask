using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Ai.BoardTools;
using MoTask.App.Tests.Fakes;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using Xunit;

namespace MoTask.App.Tests;

public class BoardToolHostReadTests
{
    private readonly FakeBoardService _service = new();
    private readonly Board _board = TestBoards.Sample();
    private readonly BoardToolHost _host;

    public BoardToolHostReadTests()
    {
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { TestBoards.Urgent() };
        // TestClock の既定日(2026-09-04, 金)だと、タスク 10 の期日(2026-09-08)が
        // WeekOf の週境界(2026-08-31〜2026-09-06)から外れて this_week に入らない
        // (tests/MoTask.Core.Tests/TaskFilterTests.cs の WeekOf_StartsMonday で検証済み)。
        // ListTasks_FiltersByDue が意図通り検証できるよう、期日と同じ週の月曜に固定する。
        _host = new BoardToolHost(_service, new TestClock { Today = new DateOnly(2026, 9, 7) });
    }

    private async Task<(JsonElement Json, bool IsError, string Text)> CallAsync(string tool, string argumentsJson = "{}")
    {
        var target = _host.Tools.Single(t => t.Name == tool);
        using var doc = JsonDocument.Parse(argumentsJson);
        var result = await target.InvokeAsync(doc.RootElement, CancellationToken.None);
        return (result.IsError ? default : JsonDocument.Parse(result.Text).RootElement, result.IsError, result.Text);
    }

    [Fact]
    public void Tools_ExposesTheSixBoardTools_WithObjectSchemas()
    {
        _host.Tools.Select(t => t.Name).Should()
            .Equal("get_board", "list_tasks", "get_task", "add_task", "update_task", "move_task");
        _host.Tools.Should().OnlyContain(t => t.Description.Length > 0);
        foreach (var tool in _host.Tools)
        {
            JsonSerializer.SerializeToElement(tool.InputSchema).GetProperty("type").GetString().Should().Be("object");
        }
    }

    [Fact]
    public async Task GetBoard_ReturnsColumnsProjectsAndLabels()
    {
        var (json, isError, _) = await CallAsync(BoardToolHost.GetBoard);

        isError.Should().BeFalse();
        json.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Equal("未着手", "進行中", "完了");
        json.GetProperty("projects")[0].GetProperty("id").GetInt32().Should().Be(100);
        json.GetProperty("labels")[0].GetProperty("name").GetString().Should().Be("至急");
    }

    [Fact]
    public async Task GetBoard_ServiceFails_ReturnsToolError()
    {
        _service.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("ボードがありません"));

        var (_, isError, text) = await CallAsync(BoardToolHost.GetBoard);

        isError.Should().BeTrue();
        text.Should().Be("ボードがありません");
    }

    [Fact]
    public async Task ListTasks_GroupsByColumn_InPositionOrder()
    {
        var (json, _, _) = await CallAsync(BoardToolHost.ListTasks);

        var columns = json.GetProperty("columns");
        columns.GetArrayLength().Should().Be(3);
        columns[0].GetProperty("name").GetString().Should().Be("未着手");
        columns[0].GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetInt32())
            .Should().Equal(10, 11);
        columns[2].GetProperty("tasks").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListTasks_HidesDeletedTasks()
    {
        TestBoards.SeedDeletedInTheMiddle(_board.Columns[0]);

        var (json, _, _) = await CallAsync(BoardToolHost.ListTasks);

        json.GetProperty("columns")[0].GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("id").GetInt32()).Should().Equal(10, 13, 14);
    }

    [Fact]
    public async Task ListTasks_FiltersByColumnName()
    {
        var (json, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"column":"進行中"}""");

        json.GetProperty("columns").GetArrayLength().Should().Be(1);
        json.GetProperty("columns")[0].GetProperty("tasks")[0].GetProperty("id").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task ListTasks_FiltersByColumnId()
    {
        var (json, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"column":2}""");

        json.GetProperty("columns")[0].GetProperty("id").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ListTasks_FiltersByProjectAndLabelAndSearch()
    {
        var (byProject, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"project":"顧客A対応"}""");
        Ids(byProject).Should().Equal(10);

        var (byLabel, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"label":["至急"]}""");
        Ids(byLabel).Should().Equal(10);

        var (bySearch, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"search":"求人"}""");
        Ids(bySearch).Should().Equal(11);
    }

    [Fact]
    public async Task ListTasks_FiltersByDue()
    {
        var (overdue, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"due":"overdue"}""");
        Ids(overdue).Should().BeEmpty();

        var (thisWeek, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"due":"this_week"}""");
        Ids(thisWeek).Should().Equal(10);

        var (all, _, _) = await CallAsync(BoardToolHost.ListTasks, """{"due":"all"}""");
        Ids(all).Should().Equal(10, 11, 12);
    }

    [Fact]
    public async Task ListTasks_UnknownDue_ReturnsToolError()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.ListTasks, """{"due":"tomorrow"}""");

        isError.Should().BeTrue();
        text.Should().Contain("this_week").And.Contain("tomorrow");
    }

    [Fact]
    public async Task ListTasks_UnknownColumn_ListsTheCandidates()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.ListTasks, """{"column":"レビュー"}""");

        isError.Should().BeTrue();
        text.Should().Contain("レビュー").And.Contain("進行中");
    }

    [Fact]
    public async Task ListTasks_BadArgumentKind_ReturnsToolError()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.ListTasks, """{"column":true}""");

        isError.Should().BeTrue();
        text.Should().Contain("列");
    }

    [Fact]
    public async Task GetTask_ReturnsTheFullDescription()
    {
        _board.Columns[0].Tasks[0].Description = new string('あ', 250);

        var (json, _, _) = await CallAsync(BoardToolHost.GetTask, """{"task":10}""");

        json.GetProperty("description").GetString().Should().HaveLength(250);
        json.GetProperty("createdAt").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("column").GetString().Should().Be("未着手");
    }

    [Fact]
    public async Task GetTask_WithoutTask_ReturnsToolError()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.GetTask);

        isError.Should().BeTrue();
        text.Should().Contain("task");
    }

    [Fact]
    public async Task GetTask_DeletedOrUnknown_ReturnsNotFound()
    {
        TestBoards.SoftDelete(_board.Columns[0], 11, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        (await CallAsync(BoardToolHost.GetTask, """{"task":11}""")).IsError.Should().BeTrue();
        (await CallAsync(BoardToolHost.GetTask, """{"task":999}""")).IsError.Should().BeTrue();
    }

    private static int[] Ids(JsonElement json)
        => json.GetProperty("columns").EnumerateArray()
            .SelectMany(c => c.GetProperty("tasks").EnumerateArray())
            .Select(t => t.GetProperty("id").GetInt32())
            .ToArray();
}
