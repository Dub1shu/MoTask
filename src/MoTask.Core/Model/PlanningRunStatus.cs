namespace MoTask.Core.Model;

/// <summary>朝の実行1回の状態（仕様 §9）。</summary>
public enum PlanningRunStatus
{
    /// <summary>行は作ったが、まだ SessionStart フックが来ていない。</summary>
    Pending = 0,
    Running = 1,
    /// <summary>Claude が planning_complete を呼び、候補とプランが揃った。</summary>
    Ingested = 2,
    Failed = 3,
    Cancelled = 4,
}

public static class PlanningRunStatusExtensions
{
    /// <summary>もう追いかけない実行。端末が生きているかどうかは MoTask には分からない。</summary>
    public static bool IsTerminal(this PlanningRunStatus status)
        => status is PlanningRunStatus.Ingested or PlanningRunStatus.Failed or PlanningRunStatus.Cancelled;

    public static bool IsActive(this PlanningRunStatus status) => !status.IsTerminal();
}
