using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceMoveTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceMoveTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active, wipLimit: 2);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock);
    }

    private static int[] Positions(Column c) => c.Tasks.OrderBy(t => t.Position).Select(t => t.Position).ToArray();
    private static string[] TitlesInOrder(Column c) => c.Tasks.OrderBy(t => t.Position).Select(t => t.Title).ToArray();

    [Fact]
    public async Task Move_AcrossColumns_RenumbersBothColumns()
    {
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        var c = _store.SeedTask(_backlog, "c");
        var x = _store.SeedTask(_active, "x");

        var result = await _service.MoveTaskAsync(b.Id, _active.Id, position: 0);

        result.IsSuccess.Should().BeTrue();
        b.ColumnId.Should().Be(_active.Id);
        TitlesInOrder(_backlog).Should().Equal("a", "c");
        Positions(_backlog).Should().Equal(0, 1);
        TitlesInOrder(_active).Should().Equal("b", "x");
        Positions(_active).Should().Equal(0, 1);
        _backlog.Tasks.Should().NotContain(b);
        _active.Tasks.Should().Contain(b);
    }

    [Fact]
    public async Task Move_WithinColumn_ReordersAndWritesNoHistory()
    {
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        var c = _store.SeedTask(_backlog, "c");
        var before = _store.History.Count;

        // a を c の後ろへ（a を除いたリスト [b, c] の位置 2 = 末尾）
        var result = await _service.MoveTaskAsync(a.Id, _backlog.Id, position: 2);

        result.IsSuccess.Should().BeTrue();
        TitlesInOrder(_backlog).Should().Equal("b", "c", "a");
        Positions(_backlog).Should().Equal(0, 1, 2);
        _store.History.Count.Should().Be(before);
        a.UpdatedAt.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "並び替えだけでは更新時刻を変えない");
        _store.SaveCount.Should().Be(1);
    }

    /// <summary>
    /// 保険: Position が重複している列でも、Core は表示側（<c>ColumnViewModel.SyncCardsFromModel</c> =
    /// Position, Id 順）と同じ順序で挿入位置を数える。Tasks コレクションの並び順で決着させると、
    /// 画面が見せた場所と別の場所に保存される。重複は今の実装では作られないが、
    /// 旧版が書いた DB を開いたときには残っている。
    /// </summary>
    [Fact]
    public async Task Move_WithTiedPositions_CountsInTheSameOrderAsTheUi()
    {
        // a を後から backlog へ動かすことで、コレクション順（b, a）を Id 順（a, b）とわざとずらす。
        var a = _store.SeedTask(_active, "a");
        var b = _store.SeedTask(_backlog, "b");
        await _service.MoveTaskAsync(a.Id, _backlog.Id, position: 0);
        _backlog.Tasks.Select(t => t.Title).Should().Equal("b", "a");

        b.Position = a.Position;   // 旧版が残した重複を再現する
        var c = _store.SeedTask(_backlog, "c");

        // 画面は [a, b, c] と見えている。c を先頭へ落とせば [c, a, b] になるはず。
        var result = await _service.MoveTaskAsync(c.Id, _backlog.Id, position: 0);

        result.IsSuccess.Should().BeTrue();
        TitlesInOrder(_backlog).Should().Equal("c", "a", "b");
        Positions(_backlog).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Move_PositionOutOfRange_IsClamped()
    {
        var a = _store.SeedTask(_backlog, "a");
        _store.SeedTask(_active, "x");

        (await _service.MoveTaskAsync(a.Id, _active.Id, position: 99)).IsSuccess.Should().BeTrue();
        TitlesInOrder(_active).Should().Equal("x", "a");

        (await _service.MoveTaskAsync(a.Id, _active.Id, position: -5)).IsSuccess.Should().BeTrue();
        TitlesInOrder(_active).Should().Equal("a", "x");
    }

    [Fact]
    public async Task Move_IntoDone_SetsCompletedAt_AndOutOfDone_ClearsIt()
    {
        var a = _store.SeedTask(_backlog, "a");
        _clock.UtcNow = new DateTime(2026, 9, 4, 8, 40, 0, DateTimeKind.Utc);

        await _service.MoveTaskAsync(a.Id, _done.Id, 0);
        a.CompletedAt.Should().Be(_clock.UtcNow);
        a.UpdatedAt.Should().Be(_clock.UtcNow);

        _clock.Advance(TimeSpan.FromHours(1));
        await _service.MoveTaskAsync(a.Id, _active.Id, 0);
        a.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Move_AcrossColumns_WritesMovedHistoryOnce()
    {
        var a = _store.SeedTask(_backlog, "a");

        await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        var history = await _service.GetHistoryAsync(a.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Moved);
        history[0].FromColumnId.Should().Be(_backlog.Id);
        history[0].ToColumnId.Should().Be(_active.Id);
        history[0].At.Should().Be(_clock.UtcNow);
        history[0].Detail.Should().BeEmpty();
    }

    [Fact]
    public async Task Move_OverWip_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "x");
        _store.SeedTask(_active, "y");
        var a = _store.SeedTask(_backlog, "a");

        var result = await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be(string.Format(Messages.WipExceededFormat, "進行中", 2));
        a.ColumnId.Should().Be(_active.Id);
    }

    [Fact]
    public async Task Move_WipCount_IgnoresDeletedTasks()
    {
        var x = _store.SeedTask(_active, "x");
        x.DeletedAt = _clock.UtcNow;
        _store.SeedTask(_active, "y");
        var a = _store.SeedTask(_backlog, "a");

        var result = await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Move_UnknownTaskOrColumn_IsRejected()
    {
        var a = _store.SeedTask(_backlog, "a");
        (await _service.MoveTaskAsync(999, _active.Id, 0)).Error.Should().Be(Messages.TaskNotFound);
        (await _service.MoveTaskAsync(a.Id, 999, 0)).Error.Should().Be(Messages.ColumnNotFound);
    }
}
