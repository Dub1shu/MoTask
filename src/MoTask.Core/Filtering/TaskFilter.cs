using MoTask.Core.Model;

namespace MoTask.Core.Filtering;

/// <summary>
/// フィルタ条件と、それを適用する純関数。ViewModel とテストが同じロジックを使う。
/// LabelIds は AND（すべて付いているタスクだけ）。null または空は条件なし。
/// ShowDeleted=false で削除済みを隠す。true では削除済みも生きているものも表示する。
/// </summary>
public sealed record TaskFilter(
    int? ProjectId = null,
    IReadOnlySet<int>? LabelIds = null,
    DueFilter Due = DueFilter.All,
    string SearchText = "",
    bool ShowDeleted = false)
{
    public static readonly TaskFilter None = new();

    public IEnumerable<TaskItem> Apply(IEnumerable<TaskItem> tasks, DateOnly today)
    {
        var search = SearchText.Trim();
        var (weekStart, weekEnd) = WeekOf(today);

        foreach (var task in tasks)
        {
            if (!ShowDeleted && task.IsDeleted) continue;
            if (ProjectId is int projectId && task.ProjectId != projectId) continue;
            if (LabelIds is { Count: > 0 } ids && !ids.All(id => task.Labels.Any(l => l.Id == id))) continue;
            if (!MatchesDue(task, today, weekStart, weekEnd)) continue;
            if (search.Length > 0 && !Contains(task.Title, search) && !Contains(task.Description, search)) continue;
            yield return task;
        }
    }

    /// <summary>月曜始まり・日曜終わりの週。</summary>
    public static (DateOnly Start, DateOnly End) WeekOf(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        var start = day.AddDays(-offset);
        return (start, start.AddDays(6));
    }

    private bool MatchesDue(TaskItem task, DateOnly today, DateOnly weekStart, DateOnly weekEnd) => Due switch
    {
        DueFilter.All => true,
        DueFilter.Today => task.DueDate == today,
        DueFilter.ThisWeek => task.DueDate is DateOnly d && d >= weekStart && d <= weekEnd,
        DueFilter.Overdue => task.DueDate is DateOnly d && d < today && task.CompletedAt is null,
        _ => true,
    };

    private static bool Contains(string text, string search)
        => text.Contains(search, StringComparison.OrdinalIgnoreCase);
}
