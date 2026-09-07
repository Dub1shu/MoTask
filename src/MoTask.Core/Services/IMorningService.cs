using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>候補を登録するときに人が確定した内容(編集後の値)。</summary>
public sealed record CandidateDecision(
    int CandidateId,
    string Title,
    DateOnly? DueDate,
    string ProjectName,
    int ColumnId);

public interface IMorningService
{
    event EventHandler<MorningRunChangedEventArgs>? RunChanged;

    // 照会
    Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);
    /// <summary>events.jsonl の末尾。進行の表示に使う(DB には持たない)。</summary>
    Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default);
    /// <summary>記憶しているターン数。追跡していなければ 0。</summary>
    int TurnCountOf(int runId);

    // 実行
    Task<Result<MorningRun>> StartAsync(CancellationToken ct = default);
    /// <summary>端末を × で閉じられた後の「完了にする」。取り込みを走らせる。</summary>
    Task<Result> CompleteAsync(int runId, CancellationToken ct = default);
    /// <summary>追跡をやめる。端末は殺さない。</summary>
    Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default);

    // 仕分け
    Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default);
    Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default);
    Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default);
    Task<Result> RejectAsync(int candidateId, CancellationToken ct = default);
}
