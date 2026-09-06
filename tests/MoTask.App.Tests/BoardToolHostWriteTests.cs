using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Ai.BoardTools;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class BoardToolHostWriteTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Board _board = TestBoards.Sample();
    private readonly BoardToolHost _host;
    private int _changed;

    public BoardToolHostWriteTests()
    {
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
        _service.UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _service.SetTaskLabelsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _host = new BoardToolHost(_service, new TestClock());
        _host.BoardChanged += (_, _) => _changed++;
    }

    /// <summary>CreateTaskAsync が呼ばれたら、その列に実際にカードが増えたことにする（読み戻しのため）。</summary>
    private void StubCreate(int newId, params string[] warnings)
        => _service.CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var column = _board.Columns.Single(c => c.Id == call.ArgAt<int>(0));
                var task = new TaskItem
                {
                    Id = newId, Title = call.ArgAt<string>(1), ColumnId = column.Id, Position = column.Tasks.Count,
                    CreatedAt = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                };
                column.Tasks.Add(task);
                return Task.FromResult(Result.Ok(task, warnings));
            });

    private async Task<(JsonElement Json, bool IsError, string Text)> CallAsync(string tool, string argumentsJson)
    {
        var target = _host.Tools.Single(t => t.Name == tool);
        using var doc = JsonDocument.Parse(argumentsJson);
        var result = await target.InvokeAsync(doc.RootElement, CancellationToken.None);
        return (result.IsError ? default : JsonDocument.Parse(result.Text).RootElement, result.IsError, result.Text);
    }

    [Fact]
    public async Task AddTask_WithoutColumn_UsesTheFirstActiveColumn()
    {
        StubCreate(50);

        var (json, isError, _) = await CallAsync(BoardToolHost.AddTask, """{"title":"MCP I/F を作る"}""");

        isError.Should().BeFalse();
        await _service.Received(1).CreateTaskAsync(2, "MCP I/F を作る", Arg.Any<CancellationToken>());
        json.GetProperty("id").GetInt32().Should().Be(50);
        json.GetProperty("column").GetString().Should().Be("進行中");
        _changed.Should().Be(1);
    }

    [Fact]
    public async Task AddTask_WithColumnName_UsesThatColumn()
    {
        StubCreate(51);

        await CallAsync(BoardToolHost.AddTask, """{"title":"あとで","column":"未着手"}""");

        await _service.Received(1).CreateTaskAsync(1, "あとで", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTask_WithOptionalFields_UpdatesAndLabelsAfterCreating()
    {
        StubCreate(52);

        await CallAsync(BoardToolHost.AddTask,
            """{"title":"見積","description":"本文","project":"顧客A対応","due":"2026-09-10","labels":["至急"]}""");

        await _service.Received(1).UpdateTaskAsync(
            new TaskUpdate(52, "見積", "本文", 100, new DateOnly(2026, 9, 10)), Arg.Any<CancellationToken>());
        await _service.Received(1).SetTaskLabelsAsync(52, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 200),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTask_WithoutOptionalFields_DoesNotCallUpdate()
    {
        StubCreate(53);

        await CallAsync(BoardToolHost.AddTask, """{"title":"素朴"}""");

        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
        await _service.DidNotReceive().SetTaskLabelsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTask_CarriesTheWipWarning()
    {
        StubCreate(54, "「進行中」の WIP 上限 1 を超えています");

        var (json, _, _) = await CallAsync(BoardToolHost.AddTask, """{"title":"詰め込み"}""");

        json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Equal("「進行中」の WIP 上限 1 を超えています");
    }

    [Fact]
    public async Task AddTask_WithoutTitle_ReturnsToolError_AndCreatesNothing()
    {
        var (_, isError, _) = await CallAsync(BoardToolHost.AddTask, """{"column":"未着手"}""");

        isError.Should().BeTrue();
        await _service.DidNotReceive().CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _changed.Should().Be(0);
    }

    [Fact]
    public async Task AddTask_ServiceFails_ReturnsTheServiceMessage()
    {
        _service.CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail<TaskItem>("保存に失敗しました: disk full")));

        var (_, isError, text) = await CallAsync(BoardToolHost.AddTask, """{"title":"x"}""");

        isError.Should().BeTrue();
        text.Should().Be("保存に失敗しました: disk full");
        _changed.Should().Be(0);
    }

    [Fact]
    public async Task AddTask_UpdateFailsAfterCreating_ReportsTheCreatedIdAndTheFailure()
    {
        StubCreate(53);
        _service.UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail("保存に失敗しました: disk full")));

        var (_, isError, text) = await CallAsync(BoardToolHost.AddTask,
            """{"title":"見積","description":"本文"}""");

        isError.Should().BeTrue();
        text.Should().Contain("53").And.Contain("保存に失敗しました: disk full");
        _changed.Should().Be(1);
    }

    [Fact]
    public async Task AddTask_LabelsFailAfterCreating_ReportsTheCreatedIdAndTheFailure()
    {
        StubCreate(54);
        _service.SetTaskLabelsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail("ラベルの保存に失敗しました")));

        var (_, isError, text) = await CallAsync(BoardToolHost.AddTask, """{"title":"見積","labels":["至急"]}""");

        isError.Should().BeTrue();
        text.Should().Contain("54").And.Contain("ラベルの保存に失敗しました");
    }

    [Fact]
    public async Task UpdateTask_WithANonIntegerTask_SaysItMustBeAnInteger()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.UpdateTask, """{"task":"42","title":"x"}""");

        isError.Should().BeTrue();
        text.Should().Be(Strings.McpTaskNotAnInteger);
        text.Should().NotBe(Strings.McpTaskRequired);
    }

    [Fact]
    public async Task UpdateTask_OmittedFields_KeepTheirCurrentValues()
    {
        var task = _board.Columns[0].Tasks[0];
        task.Description = "元の本文";

        await CallAsync(BoardToolHost.UpdateTask, """{"task":10,"title":"新しい題"}""");

        await _service.Received(1).UpdateTaskAsync(
            new TaskUpdate(10, "新しい題", "元の本文", 100, new DateOnly(2026, 9, 8)), Arg.Any<CancellationToken>());
        _changed.Should().Be(1);
    }

    [Fact]
    public async Task UpdateTask_ExplicitNull_ClearsProjectAndDue()
    {
        await CallAsync(BoardToolHost.UpdateTask, """{"task":10,"project":null,"due":null}""");

        await _service.Received(1).UpdateTaskAsync(
            new TaskUpdate(10, "請求先情報を更新する", "", null, null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_ExplicitValues_SetsTheNewProjectAndDue()
    {
        await CallAsync(BoardToolHost.UpdateTask, """{"task":11,"project":"顧客A対応","due":"2026-09-12"}""");

        await _service.Received(1).UpdateTaskAsync(
            new TaskUpdate(11, "求人票の文面を見直す", "", 100, new DateOnly(2026, 9, 12)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_SetsLabelsOnlyWhenTheyAreGiven()
    {
        await CallAsync(BoardToolHost.UpdateTask, """{"task":11,"title":"題だけ"}""");
        await _service.DidNotReceive().SetTaskLabelsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());

        await CallAsync(BoardToolHost.UpdateTask, """{"task":11,"labels":[]}""");
        await _service.Received(1).SetTaskLabelsAsync(11, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_BadDue_ReturnsToolError()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.UpdateTask, """{"task":10,"due":"明日"}""");

        isError.Should().BeTrue();
        text.Should().Contain("YYYY-MM-DD");
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_DeletedTask_ReturnsNotFound()
    {
        TestBoards.SoftDelete(_board.Columns[0], 11, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        var (_, isError, _) = await CallAsync(BoardToolHost.UpdateTask, """{"task":11,"title":"x"}""");

        isError.Should().BeTrue();
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveTask_ByColumnName_DefaultsToTheEnd()
    {
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                TestBoards.Move(_board.Columns.Single(c => c.Tasks.Any(t => t.Id == call.ArgAt<int>(0))),
                    _board.Columns.Single(c => c.Id == call.ArgAt<int>(1)), call.ArgAt<int>(0), call.ArgAt<int>(2));
                return Task.FromResult(Result.Ok());
            });

        var (json, isError, _) = await CallAsync(BoardToolHost.MoveTask, """{"task":10,"column":"完了"}""");

        isError.Should().BeFalse();
        await _service.Received(1).MoveTaskAsync(10, 3, int.MaxValue, Arg.Any<CancellationToken>());
        json.GetProperty("column").GetString().Should().Be("完了");
        _changed.Should().Be(1);
    }

    [Fact]
    public async Task MoveTask_WithPosition_PassesItThrough()
    {
        await CallAsync(BoardToolHost.MoveTask, """{"task":10,"column":2,"position":0}""");

        await _service.Received(1).MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveTask_WithoutColumn_ReturnsToolError()
    {
        var (_, isError, text) = await CallAsync(BoardToolHost.MoveTask, """{"task":10}""");

        isError.Should().BeTrue();
        text.Should().Contain("column");
        _changed.Should().Be(0);
    }

    [Fact]
    public async Task MoveTask_DeletedTask_ReturnsNotFound()
    {
        TestBoards.SoftDelete(_board.Columns[0], 11, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        var (_, isError, text) = await CallAsync(BoardToolHost.MoveTask, """{"task":11,"column":"完了"}""");

        isError.Should().BeTrue();
        text.Should().Be(Messages.TaskNotFound);
        await _service.DidNotReceive().MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveTask_CarriesTheWipWarning()
    {
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new[] { "「進行中」の WIP 上限 1 を超えています" })));

        var (json, _, _) = await CallAsync(BoardToolHost.MoveTask, """{"task":10,"column":2}""");

        json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Equal("「進行中」の WIP 上限 1 を超えています");
    }

    [Fact]
    public void BoardToolHost_IsABoardChangeSource()
        => _host.Should().BeAssignableTo<IBoardChangeSource>();
}
