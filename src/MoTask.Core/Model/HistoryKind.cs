namespace MoTask.Core.Model;

public enum HistoryKind
{
    Created = 0,
    Moved = 1,
    Edited = 2,
    Deleted = 3,
    Restored = 4,
    /// <summary>Detail は AiJobHistoryDetail（Status は null）。</summary>
    AiJobStarted = 5,
    /// <summary>Detail は AiJobHistoryDetail（Status は Succeeded / Failed / Cancelled）。</summary>
    AiJobFinished = 6,
    /// <summary>Detail は TriageHistoryDetail。候補から新しいタスクを作った。</summary>
    CandidateRegistered = 7,
    /// <summary>Detail は TriageHistoryDetail。候補を既存タスクへ統合した。</summary>
    CandidateMerged = 8,
}
