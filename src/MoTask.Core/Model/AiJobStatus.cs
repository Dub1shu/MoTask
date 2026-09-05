namespace MoTask.Core.Model;

public enum AiJobStatus
{
    /// <summary>ジョブは作ったが、まだ SessionStart フックが来ていない（端末が開くまでの数百 ms）。</summary>
    Pending = 0,
    Running = 1,
    /// <summary>2 と 3 は廃止した AwaitingApproval / Suspended の番号。空けたままにする。</summary>
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    /// <summary>Stop フックが来て、人の入力を待っている。</summary>
    WaitingForInput = 7,
}

public static class AiJobStatusExtensions
{
    /// <summary>MoTask がまだ追いかけているジョブ（Pending / Running / WaitingForInput）。</summary>
    public static bool IsActive(this AiJobStatus status) => !status.IsTerminal();

    /// <summary>もう追いかけないジョブ。端末が生きているかどうかは MoTask には分からない。</summary>
    public static bool IsTerminal(this AiJobStatus status)
        => status is AiJobStatus.Succeeded or AiJobStatus.Failed or AiJobStatus.Cancelled;
}
