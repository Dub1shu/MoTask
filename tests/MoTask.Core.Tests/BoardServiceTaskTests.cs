using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceTaskTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceTaskTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active, wipLimit: 1);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock, new OperationGate());
    }

    [Fact]
    public async Task CreateTask_AppendsToColumn_AndWritesCreatedHistory()
    {
        _store.SeedTask(_backlog, "既存");

        var result = await _service.CreateTaskAsync(_backlog.Id, "  新しいタスク  ");

        result.IsSuccess.Should().BeTrue();
        var task = result.Value!;
        task.Id.Should().BePositive();
        task.Title.Should().Be("新しいタスク");
        task.ColumnId.Should().Be(_backlog.Id);
        task.Position.Should().Be(1);
        task.CreatedAt.Should().Be(_clock.UtcNow);
        task.UpdatedAt.Should().Be(_clock.UtcNow);
        task.CompletedAt.Should().BeNull();
        _backlog.Tasks.Should().Contain(task);

        var history = await _service.GetHistoryAsync(task.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Created);
        history[0].ToColumnId.Should().Be(_backlog.Id);
        history[0].FromColumnId.Should().BeNull();
        history[0].Detail.Should().BeEmpty();
        _store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateTask_InDoneColumn_SetsCompletedAt()
    {
        var result = await _service.CreateTaskAsync(_done.Id, "完了扱い");
        result.Value!.CompletedAt.Should().Be(_clock.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTask_EmptyTitle_IsRejected(string title)
    {
        var result = await _service.CreateTaskAsync(_backlog.Id, title);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TitleRequired);
        _backlog.Tasks.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTask_UnknownColumn_IsRejected()
    {
        var result = await _service.CreateTaskAsync(999, "x");
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.ColumnNotFound);
    }

    [Fact]
    public async Task CreateTask_OverWip_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "1件目");

        var result = await _service.CreateTaskAsync(_active.Id, "2件目");

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be(string.Format(Messages.WipExceededFormat, "進行中", 1));
    }

    [Fact]
    public async Task CreateTask_WhenSaveFails_ReturnsFailure()
    {
        _store.FailNextSave = true;

        var result = await _service.CreateTaskAsync(_backlog.Id, "x");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith(Messages.SaveFailed);
    }

    [Fact]
    public async Task GetBoard_ReturnsBoard()
    {
        var result = await _service.GetBoardAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Columns.Should().HaveCount(3);
    }
}
