using FluentAssertions;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class TaskFilterTests
{
    // 2026-09-04 は金曜日。週は月曜 8/31 〜 日曜 9/6。
    private static readonly DateOnly Today = new(2026, 9, 4);

    private static readonly Label Urgent = new() { Id = 1, Name = "至急" };
    private static readonly Label Routine = new() { Id = 2, Name = "定例" };

    private static TaskItem Make(int id, string title, string description = "", int? projectId = null,
        DateOnly? due = null, bool deleted = false, bool completed = false, params Label[] labels)
        => new()
        {
            Id = id, Title = title, Description = description, ProjectId = projectId, DueDate = due,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            CompletedAt = completed ? DateTime.UtcNow : null,
            Labels = labels.ToList(),
        };

    private static readonly TaskItem[] Tasks =
    {
        Make(1, "請求先情報を更新する", projectId: 10, due: new DateOnly(2026, 9, 8), labels: new[] { Urgent }),
        Make(2, "会場候補を3つに絞る", "合宿の下見", projectId: 20, due: new DateOnly(2026, 9, 4), labels: new[] { Urgent, Routine }),
        Make(3, "求人票の文面を見直す", due: new DateOnly(2026, 9, 1)),
        Make(4, "古いタスク", due: new DateOnly(2026, 9, 1), completed: true),
        Make(5, "削除済み", deleted: true),
        Make(6, "週末までに", due: new DateOnly(2026, 9, 6)),
    };

    private static int[] Ids(TaskFilter f) => f.Apply(Tasks, Today).Select(t => t.Id).ToArray();

    [Fact]
    public void None_HidesDeletedOnly()
    {
        Ids(TaskFilter.None).Should().Equal(1, 2, 3, 4, 6);
    }

    [Fact]
    public void ShowDeleted_IncludesDeletedAndLive()
    {
        Ids(new TaskFilter(ShowDeleted: true)).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void Project_FiltersExactly()
    {
        Ids(new TaskFilter(ProjectId: 10)).Should().Equal(1);
    }

    [Fact]
    public void Labels_AreAnded()
    {
        Ids(new TaskFilter(LabelIds: new HashSet<int> { 1 })).Should().Equal(1, 2);
        Ids(new TaskFilter(LabelIds: new HashSet<int> { 1, 2 })).Should().Equal(2);
        Ids(new TaskFilter(LabelIds: new HashSet<int>())).Should().Equal(1, 2, 3, 4, 6);
    }

    [Fact]
    public void Due_Today_ThisWeek_Overdue()
    {
        Ids(new TaskFilter(Due: DueFilter.Today)).Should().Equal(2);
        Ids(new TaskFilter(Due: DueFilter.ThisWeek)).Should().Equal(new[] { 2, 3, 4, 6 }, because: "月曜〜日曜の週全体");
        Ids(new TaskFilter(Due: DueFilter.Overdue)).Should().Equal(new[] { 3 }, because: "完了済みは期限切れに含めない");
    }

    [Fact]
    public void Search_MatchesTitleOrDescription_CaseInsensitive()
    {
        Ids(new TaskFilter(SearchText: "合宿")).Should().Equal(2);
        Ids(new TaskFilter(SearchText: " 求人 ")).Should().Equal(3);
        Ids(new TaskFilter(SearchText: "ない")).Should().BeEmpty();
    }

    [Fact]
    public void Combination_AppliesAllConditions()
    {
        var f = new TaskFilter(ProjectId: 20, LabelIds: new HashSet<int> { 2 }, Due: DueFilter.Today, SearchText: "会場");
        Ids(f).Should().Equal(2);
    }

    [Fact]
    public void WeekOf_StartsMonday()
    {
        TaskFilter.WeekOf(new DateOnly(2026, 9, 4)).Should().Be((new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 6)));
        TaskFilter.WeekOf(new DateOnly(2026, 9, 6)).Should().Be((new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 6)));
        TaskFilter.WeekOf(new DateOnly(2026, 9, 7)).Should().Be((new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 13)));
    }

    [Fact]
    public void DueStatuses_Of()
    {
        DueStatuses.Of(Tasks[0], Today).Should().Be(DueStatus.Upcoming);
        DueStatuses.Of(Tasks[1], Today).Should().Be(DueStatus.Today);
        DueStatuses.Of(Tasks[2], Today).Should().Be(DueStatus.Overdue);
        DueStatuses.Of(Tasks[3], Today).Should().Be(DueStatus.Upcoming, because: "完了済みは超過扱いにしない");
        DueStatuses.Of(Tasks[4], Today).Should().Be(DueStatus.None);
    }
}
