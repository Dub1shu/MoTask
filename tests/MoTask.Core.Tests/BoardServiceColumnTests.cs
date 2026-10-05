using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceColumnTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceColumnTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock, new OperationGate());
    }

    [Fact]
    public async Task AddColumn_AppendsWithNextOrder_DefaultRoleActive()
    {
        var result = await _service.AddColumnAsync(" 保留 ");

        result.IsSuccess.Should().BeTrue();
        var column = result.Value!;
        column.Id.Should().BePositive();
        column.Name.Should().Be("保留");
        column.Role.Should().Be(ColumnRole.Active);
        column.Order.Should().Be(3);
        column.BoardId.Should().Be(_store.Board.Id);
        _store.Board.Columns.Should().Contain(column);
    }

    [Fact]
    public async Task AddColumn_WithDoneRole_IsRejected()
    {
        var result = await _service.AddColumnAsync("もう一つの完了", ColumnRole.Done);
        result.Error.Should().Be(Messages.CannotAssignDoneRole);
        _store.Board.Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task AddColumn_EmptyName_IsRejected()
    {
        (await _service.AddColumnAsync("  ")).Error.Should().Be(Messages.ColumnNameRequired);
    }

    [Fact]
    public async Task RenameColumn_Works_AndRejectsEmpty()
    {
        (await _service.RenameColumnAsync(_backlog.Id, " 未処理 ")).IsSuccess.Should().BeTrue();
        _backlog.Name.Should().Be("未処理");
        (await _service.RenameColumnAsync(_backlog.Id, "")).Error.Should().Be(Messages.ColumnNameRequired);
        (await _service.RenameColumnAsync(999, "x")).Error.Should().Be(Messages.ColumnNotFound);
    }

    [Fact]
    public async Task SetColumnRole_ChangesNonDoneColumns()
    {
        (await _service.SetColumnRoleAsync(_backlog.Id, ColumnRole.Review)).IsSuccess.Should().BeTrue();
        _backlog.Role.Should().Be(ColumnRole.Review);
    }

    /// <summary>今日中は完了と違って特別扱いしない。役割として付け外しできる。</summary>
    [Fact]
    public async Task SetColumnRole_AcceptsToday()
    {
        (await _service.SetColumnRoleAsync(_active.Id, ColumnRole.Today)).IsSuccess.Should().BeTrue();
        _active.Role.Should().Be(ColumnRole.Today);
    }

    [Fact]
    public async Task SetColumnRole_OnDoneColumn_IsRejected()
    {
        var result = await _service.SetColumnRoleAsync(_done.Id, ColumnRole.Active);
        result.Error.Should().Be(Messages.DoneColumnCannotChangeRole);
        _done.Role.Should().Be(ColumnRole.Done);
    }

    [Fact]
    public async Task SetColumnRole_ToDone_IsRejected()
    {
        var result = await _service.SetColumnRoleAsync(_active.Id, ColumnRole.Done);
        result.Error.Should().Be(Messages.CannotAssignDoneRole);
        _active.Role.Should().Be(ColumnRole.Active);
    }

    [Fact]
    public async Task ReorderColumns_AssignsOrderByIndex()
    {
        var result = await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _active.Id });

        result.IsSuccess.Should().BeTrue();
        _done.Order.Should().Be(0);
        _backlog.Order.Should().Be(1);
        _active.Order.Should().Be(2);
    }

    [Fact]
    public async Task ReorderColumns_MustIncludeEveryColumnExactlyOnce()
    {
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _backlog.Id })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _active.Id, 999 })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
    }

    [Fact]
    public async Task SetWipLimit_AcceptsPositiveOrNull_RejectsZero()
    {
        (await _service.SetWipLimitAsync(_active.Id, 3)).IsSuccess.Should().BeTrue();
        _active.WipLimit.Should().Be(3);
        (await _service.SetWipLimitAsync(_active.Id, null)).IsSuccess.Should().BeTrue();
        _active.WipLimit.Should().BeNull();
        (await _service.SetWipLimitAsync(_active.Id, 0)).Error.Should().Be(Messages.WipLimitMustBePositive);
        (await _service.SetWipLimitAsync(_active.Id, -1)).Error.Should().Be(Messages.WipLimitMustBePositive);
    }

    [Fact]
    public async Task SetWipLimit_BelowCurrentCount_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "x");
        _store.SeedTask(_active, "y");

        var result = await _service.SetWipLimitAsync(_active.Id, 1);

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteColumn_EmptyNonDone_RemovesAndRenumbersOrder()
    {
        var result = await _service.DeleteColumnAsync(_backlog.Id);

        result.IsSuccess.Should().BeTrue();
        _store.Board.Columns.Should().NotContain(_backlog);
        _active.Order.Should().Be(0);
        _done.Order.Should().Be(1);
    }

    [Fact]
    public async Task DeleteColumn_Done_IsRejected()
    {
        (await _service.DeleteColumnAsync(_done.Id)).Error.Should().Be(Messages.DoneColumnCannotBeDeleted);
        _store.Board.Columns.Should().Contain(_done);
    }

    [Fact]
    public async Task DeleteColumn_WithTasks_IncludingDeleted_IsRejected()
    {
        var t = _store.SeedTask(_backlog, "a");
        t.DeletedAt = _clock.UtcNow;

        var result = await _service.DeleteColumnAsync(_backlog.Id);

        result.Error.Should().Be(Messages.ColumnHasTasks);
        _store.Board.Columns.Should().Contain(_backlog);
    }
}
