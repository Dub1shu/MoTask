using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 計画の「今日中」を今日中の列へ移す（仕様 2026-10-05-today-column §5.1）。
/// 一括操作と同じく、ゲートの中から IBoardService を呼ぶ実装にすると返ってこない。
/// </summary>
public class PlanningServiceTodayTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 10, 5) };
    private readonly PlanningService _service;
    private readonly Column _backlog;
    private readonly Column _today;
    private readonly Column _active;
    private readonly TaskItem _a;
    private readonly TaskItem _b;
    private readonly TaskItem _inProgress;
    private readonly TaskItem _alreadyToday;
    private readonly PlanningRun _run;
    private int _boardChanged;

    public PlanningServiceTodayTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _today = _store.SeedColumn("今日中", ColumnRole.Today);
        _active = _store.SeedColumn("進行中", ColumnRole.Active);
        _a = _store.SeedTask(_backlog, "A");
        _b = _store.SeedTask(_backlog, "B");
        _alreadyToday = _store.SeedTask(_today, "もう今日中");
        _inProgress = _store.SeedTask(_active, "進行中のもの");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new PlanningService(_store, _store, _store, _store, _store, _clock, gate,
            new FakeSessionLauncher(), new FakeJobFolder(), new FakeJobEventSource(), new InMemorySettingsStore(), boardService);
        _service.BoardChanged += (_, _) => _boardChanged++;
        _run = _store.SeedRun(new DateOnly(2026, 10, 5), PlanningRunStatus.Ingested);
    }

    private void Plan(params int[] todayTaskIds)
        => _run.PlanJson =
            $"{{\"date\":\"2026-10-05\",\"groups\":[{{\"key\":\"today\",\"items\":[{string.Join(",", todayTaskIds.Select(id => $"{{\"taskId\":{id}}}"))}]}}]}}";

    private static async Task<T> WithinLimitAsync<T>(Task<T> work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(Limit));
        finished.Should().BeSameAs(work, "ゲートの中から IBoardService を呼ぶとデッドロックする");
        return await work;
    }

    private static int[] IdsIn(Column column)
        => column.Tasks.Where(t => !t.IsDeleted).OrderBy(t => t.Position).Select(t => t.Id).ToArray();

    [Fact]
    public async Task MovesBacklogTasksToTheEndOfTheTodayColumn_InPlanOrder()
    {
        Plan(_b.Id, _inProgress.Id, _alreadyToday.Id, _a.Id);

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Be(2);
        IdsIn(_today).Should().Equal(_alreadyToday.Id, _b.Id, _a.Id);
        IdsIn(_backlog).Should().BeEmpty();
        IdsIn(_active).Should().Equal(new[] { _inProgress.Id }, "進行中のものは戻さない");
        _boardChanged.Should().Be(1, "ボード画面に読み直させる");
    }

    [Fact]
    public async Task ATaskListedTwice_MovesOnce()
    {
        Plan(_a.Id, _a.Id);

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.Value.Should().Be(1);
        IdsIn(_today).Should().Equal(_alreadyToday.Id, _a.Id);
    }

    [Fact]
    public async Task WithoutATodayColumn_MovesNothing()
    {
        _today.Role = ColumnRole.Active;
        Plan(_a.Id);

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        IdsIn(_backlog).Should().Equal(_a.Id, _b.Id);
        _boardChanged.Should().Be(0);
    }

    [Fact]
    public async Task NothingToMove_DoesNotRaiseBoardChanged()
    {
        Plan(_inProgress.Id);

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.Value.Should().Be(0);
        _boardChanged.Should().Be(0);
    }

    [Fact]
    public async Task OverTheWipLimit_StillMoves_AndWarns()
    {
        _today.WipLimit = 1;
        Plan(_a.Id);

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.Value.Should().Be(1);
        result.Warnings.Should().NotBeEmpty("今日中の列が上限を超えた");
    }

    /// <summary>選んでから移すまでの間に消されたタスクは移さず、理由を警告に出す。残りは移す。</summary>
    [Fact]
    public async Task ATaskDeletedAfterSelection_IsSkippedWithAWarning()
    {
        Plan(_a.Id, _b.Id);
        var interrupted = false;
        _store.OnGetTask = id =>
        {
            if (id != _a.Id || interrupted) return;
            interrupted = true;
            _b.DeletedAt = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        };

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.Value.Should().Be(1);
        result.Warnings.Should().Contain(Messages.TaskAlreadyDeleted);
        IdsIn(_today).Should().Equal(_alreadyToday.Id, _a.Id);
        _b.ColumnId.Should().Be(_backlog.Id, "消されたタスクは今日中へ移さない");
    }

    /// <summary>選んでから移すまでの間に人が進行中へ動かしたタスクは、今日中へ引き戻さない。</summary>
    [Fact]
    public async Task ATaskMovedOutOfTheBacklogAfterSelection_StaysWhereItWent()
    {
        Plan(_a.Id, _b.Id);
        var interrupted = false;
        _store.OnGetTask = id =>
        {
            if (id != _a.Id || interrupted) return;
            interrupted = true;
            _backlog.Tasks.Remove(_b);
            _active.Tasks.Add(_b);
            _b.ColumnId = _active.Id;
            _b.Position = 1;
        };

        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(_run.Id));

        result.Value.Should().Be(1);
        result.Warnings.Should().BeEmpty("人が自分で動かしたので知らせることは無い");
        _b.ColumnId.Should().Be(_active.Id);
    }

    [Fact]
    public async Task AnUnknownRun_Fails()
    {
        var result = await WithinLimitAsync(_service.MoveTodayToColumnAsync(9999));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.PlanningRunNotFound);
    }
}
