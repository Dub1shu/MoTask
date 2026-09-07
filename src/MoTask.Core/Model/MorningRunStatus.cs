namespace MoTask.Core.Model;

/// <summary>朝の実行1回の状態（仕様 §9）。</summary>
public enum MorningRunStatus
{
    /// <summary>行は作ったが、まだ SessionStart フックが来ていない。</summary>
    Pending = 0,
    Running = 1,
    /// <summary>result/ を読んで候補とプランを保存し終えた。</summary>
    Ingested = 2,
    Failed = 3,
    Cancelled = 4,
}

public static class MorningRunStatusExtensions
{
    /// <summary>もう追いかけない実行。端末が生きているかどうかは MoTask には分からない。</summary>
    public static bool IsTerminal(this MorningRunStatus status)
        => status is MorningRunStatus.Ingested or MorningRunStatus.Failed or MorningRunStatus.Cancelled;

    public static bool IsActive(this MorningRunStatus status) => !status.IsTerminal();
}
