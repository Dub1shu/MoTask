using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>計画の「今日中」から今日中の列へ移すものの選び方（仕様 2026-10-05-today-column §5.1）。</summary>
public class TodayMoveTests
{
    private readonly Board _board = new() { Id = 1, Name = "テスト" };
    private readonly Column _backlog = new() { Id = 1, Name = "未着手", Order = 0, Role = ColumnRole.Backlog };
    private readonly Column _today = new() { Id = 2, Name = "今日やる", Order = 1, Role = ColumnRole.Today };
    private readonly Column _active = new() { Id = 3, Name = "進行中", Order = 2, Role = ColumnRole.Active };
    private readonly Column _done = new() { Id = 4, Name = "完了", Order = 3, Role = ColumnRole.Done };
    private readonly List<TriageCandidate> _candidates = new();

    public TodayMoveTests()
    {
        _board.Columns.AddRange(new[] { _backlog, _today, _active, _done });
        _backlog.Tasks.Add(new TaskItem { Id = 10, Title = "未着手A", ColumnId = 1 });
        _backlog.Tasks.Add(new TaskItem { Id = 11, Title = "未着手B", ColumnId = 1 });
        _today.Tasks.Add(new TaskItem { Id = 20, Title = "今日中にある", ColumnId = 2 });
        _active.Tasks.Add(new TaskItem { Id = 30, Title = "進行中にある", ColumnId = 3 });
        _done.Tasks.Add(new TaskItem { Id = 40, Title = "完了にある", ColumnId = 4 });
        _candidates.Add(new TriageCandidate
        {
            Id = 1, PlanningRunId = 1, ExternalId = "outlook:001", Source = "Outlook", Title = "候補",
            Status = TriageStatus.Pending,
        });
    }

    private ResolvedPlan Resolve(string todayItems)
        => PlanResolver.Resolve(
            $"{{\"date\":\"2026-10-05\",\"groups\":[{{\"key\":\"today\",\"items\":[{todayItems}]}},{{\"key\":\"ifTime\",\"items\":[{{\"taskId\":10}}]}}]}}",
            _candidates, _board, _ => null);

    [Fact]
    public void TasksToMove_TakesOnlyBacklogTasks_InPlanOrder()
    {
        var plan = Resolve("{\"taskId\":11},{\"taskId\":20},{\"taskId\":30},{\"taskId\":40},{\"externalId\":\"outlook:001\"},{\"taskId\":10}");

        TodayMove.TasksToMove(plan).Should().Equal(11, 10);
    }

    [Fact]
    public void TasksToMove_IgnoresOtherGroups()
    {
        var plan = Resolve("");

        TodayMove.TasksToMove(plan).Should().BeEmpty("ifTime にある未着手は対象外");
    }

    [Fact]
    public void TasksToMove_OfAnEmptyPlan_IsEmpty()
        => TodayMove.TasksToMove(ResolvedPlan.Empty(TriageSummary.None)).Should().BeEmpty();

    [Fact]
    public void TargetOf_PicksTheTodayColumnByRole_NotByName()
        => TodayMove.TargetOf(_board).Should().BeSameAs(_today);

    [Fact]
    public void TargetOf_PicksTheLowestOrder_WhenThereAreSeveral()
    {
        var earlier = new Column { Id = 5, Name = "朝イチ", Order = -1, Role = ColumnRole.Today };
        _board.Columns.Add(earlier);

        TodayMove.TargetOf(_board).Should().BeSameAs(earlier);
    }

    [Fact]
    public void TargetOf_IsNull_WithoutATodayColumn()
    {
        _today.Role = ColumnRole.Active;

        TodayMove.TargetOf(_board).Should().BeNull();
        TodayMove.TargetOf(null).Should().BeNull();
    }
}
