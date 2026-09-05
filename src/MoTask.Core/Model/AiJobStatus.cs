namespace MoTask.Core.Model;

public enum AiJobStatus
{
    Pending = 0,
    Running = 1,
    AwaitingApproval = 2,
    Suspended = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    /// <summary>Stop フックが来て、人の入力を待っている。</summary>
    WaitingForInput = 7,
}

public static class AiJobStatusExtensions
{
    /// <summary>子プロセスが生きている状態。同時実行数はこれを数える。</summary>
    public static bool IsActive(this AiJobStatus status)
        => status is AiJobStatus.Running or AiJobStatus.AwaitingApproval;

    /// <summary>もう動かない状態。Suspended は再開できるので含めない。</summary>
    public static bool IsTerminal(this AiJobStatus status)
        => status is AiJobStatus.Succeeded or AiJobStatus.Failed or AiJobStatus.Cancelled;
}
