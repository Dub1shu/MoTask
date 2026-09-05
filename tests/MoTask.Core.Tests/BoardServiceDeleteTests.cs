using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
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
        _service = new BoardService(_store, _store, _store, _clock, new OperationGate());
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
        // 削除した行自身も後ろへ送る。古い Position（1）を抱えたままだと繰り上がった third と
        // 重複し、Core（Position 順）と表示（Position, Id 順）が同じ列を別の順序で見る。
        second.Position.Should().Be(2);
        Order(column).Should().Equal("a", "c", "b");
        Positions(column).Should().Equal(0, 1, 2);
    }

    /// <summary>削除を重ねても、列内の Position は 0..n-1 で一意のまま。</summary>
    [Fact]
    public async Task Delete_Twice_KeepsColumnPositionsUnique()
    {
        var column = _store.Board.Columns.First(c => c.Id == _task.ColumnId);
        var b = _store.SeedTask(column, "b");
        var c = _store.SeedTask(column, "c");
        _store.SeedTask(column, "d");

        await _service.DeleteTaskAsync(b.Id);
        await _service.DeleteTaskAsync(c.Id);

        Order(column).Should().Equal("a", "d", "c", "b");
        Positions(column).Should().Equal(0, 1, 2, 3);
    }

    /// <summary>
    /// 裁定: v1 では復元したタスクを元の位置には戻さず、列（未削除分）の末尾へ置く。
    /// 表示側もモデルから組み直すので、ここを変えると画面の並びも変わる。
    /// </summary>
    [Fact]
    public async Task Restore_PutsTaskAtTheEndOfTheColumn()
    {
        var column = _store.Board.Columns.First(c => c.Id == _task.ColumnId);
        var second = _store.SeedTask(column, "b");
        _store.SeedTask(column, "c");
        await _service.DeleteTaskAsync(second.Id);

        var result = await _service.RestoreTaskAsync(second.Id);

        result.IsSuccess.Should().BeTrue();
        Order(column).Should().Equal("a", "c", "b");
        Positions(column).Should().Equal(0, 1, 2);
    }

    /// <summary>他に削除済みが残っていても、復元後の Position は列内で一意のまま。</summary>
    [Fact]
    public async Task Restore_WithAnotherDeletedTask_KeepsColumnPositionsUnique()
    {
        var column = _store.Board.Columns.First(c => c.Id == _task.ColumnId);
        var b = _store.SeedTask(column, "b");
        var c = _store.SeedTask(column, "c");
        _store.SeedTask(column, "d");
        await _service.DeleteTaskAsync(b.Id);
        await _service.DeleteTaskAsync(c.Id);

        await _service.RestoreTaskAsync(b.Id);

        Order(column).Should().Equal("a", "d", "b", "c");
        Positions(column).Should().Equal(0, 1, 2, 3);
    }

    private static string[] Order(Column c)
        => c.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id).Select(t => t.Title).ToArray();

    private static int[] Positions(Column c)
        => c.Tasks.Select(t => t.Position).OrderBy(p => p).ToArray();
}
