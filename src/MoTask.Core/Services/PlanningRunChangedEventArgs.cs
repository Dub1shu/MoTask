using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// 画面に渡す 1 回分の変化。PlanningRun そのものを渡さないのは、追従スレッドから
/// 追跡中のエンティティを UI に触らせないため（AiJobSnapshot と同じ理由）。
/// </summary>
public sealed record PlanningRunSnapshot(
    int RunId,
    DateOnly Date,
    PlanningRunStatus Status,
    int Turns,
    string? ErrorMessage,
    string JobFolder);

public sealed class PlanningRunChangedEventArgs : EventArgs
{
    public PlanningRunChangedEventArgs(PlanningRunSnapshot run, string? warning, bool candidatesChanged)
    {
        Run = run;
        Warning = warning;
        CandidatesChanged = candidatesChanged;
    }

    public PlanningRunSnapshot Run { get; }

    /// <summary>バナーに出す注意(保存失敗・読み捨てた行数など)。無ければ null。</summary>
    public string? Warning { get; }

    /// <summary>候補キューを読み直す必要があるか。</summary>
    public bool CandidatesChanged { get; }
}
