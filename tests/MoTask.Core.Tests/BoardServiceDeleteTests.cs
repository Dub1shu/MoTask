using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceDeleteTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly TaskItem _task;

    public BoardServiceDeleteTests()
    {
        var backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("完了", ColumnRole.Done);
        _task = _store.SeedTask(backlog, "a");
        _service = new BoardService(_store, _store, _store, _clock);
    }

    [Fact]
    public async Task Delete_SetsDeletedAt_AndWritesDeletedHistory()
    {
        _clock.UtcNow = new DateTime(2026, 9, 4, 2, 0, 0, DateTimeKind.Utc);

        var result = await _service.DeleteTaskAsync(_task.Id);

        result.IsSuccess.Should().BeTrue();
        _task.DeletedAt.Should().Be(_clock.UtcNow);
        _task.IsDeleted.Should().BeTrue();
        _task.UpdatedAt.Should().Be(_clock.UtcNow);
        var history = await _service.GetHistoryAsync(_task.Id);
        history.Should().ContainSingle().Which.Kind.Should().Be(HistoryKind.Deleted);
    }

    [Fact]
    public async Task Delete_Twice_IsRejected()
    {
        await _service.DeleteTaskAsync(_task.Id);
        var result = await _service.DeleteTaskAsync(_task.Id);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TaskAlreadyDeleted);
        _store.History.Should().ContainSingle();
    }

    [Fact]
    public async Task Restore_ClearsDeletedAt_AndWritesRestoredHistory()
    {
        await _service.DeleteTaskAsync(_task.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await _service.RestoreTaskAsync(_task.Id);

        result.IsSuccess.Should().BeTrue();
        _task.DeletedAt.Should().BeNull();
        var history = await _service.GetHistoryAsync(_task.Id);
        history.Select(h => h.Kind).Should().Equal(HistoryKind.Restored, HistoryKind.Deleted);
    }

    [Fact]
    public async Task Restore_NotDeleted_IsRejected()
    {
        var result = await _service.RestoreTaskAsync(_task.Id);
        result.Error.Should().Be(Messages.TaskNotDeleted);
    }

    [Fact]
    public async Task Delete_UnknownTask_IsRejected()
    {
        (await _service.DeleteTaskAsync(999)).Error.Should().Be(Messages.TaskNotFound);
        (await _service.RestoreTaskAsync(999)).Error.Should().Be(Messages.TaskNotFound);
    }

    [Fact]
    public async Task Delete_FromMiddle_RenumbersSurvivingTasksContiguously()
    {
        var column = _store.Board.Columns.First(c => c.Id == _task.ColumnId);
        var second = _store.SeedTask(column, "b");
        var third = _store.SeedTask(column, "c");

        var result = await _service.DeleteTaskAsync(second.Id);

        result.IsSuccess.Should().BeTrue();
        _task.Position.Should().Be(0);
        third.Position.Should().Be(1);
        second.DeletedAt.Should().NotBeNull();
    }
}
