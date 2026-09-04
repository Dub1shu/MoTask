using MoTask.Core.Model;

namespace MoTask.Core.Filtering;

public enum DueStatus
{
    None = 0,
    Upcoming = 1,
    Today = 2,
    Overdue = 3,
}

public static class DueStatuses
{
    /// <summary>カードの期限表示色の判定。完了済みタスクは超過扱いにしない。</summary>
    public static DueStatus Of(TaskItem task, DateOnly today)
    {
        if (task.DueDate is not DateOnly due) return DueStatus.None;
        if (due == today) return DueStatus.Today;
        if (due < today && task.CompletedAt is null) return DueStatus.Overdue;
        return DueStatus.Upcoming;
    }
}
