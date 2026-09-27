using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

/// <summary>読み取りは追跡された同一インスタンスを返す（IBoardRepository と同じ契約）。</summary>
public interface IPlanningRepository
{
    void Add(PlanningRun run);
    Task<PlanningRun?> GetRunAsync(int runId, CancellationToken ct = default);

    /// <summary>未完了（Pending / Running）の実行。二重起動の判定に使う。無ければ null。</summary>
    Task<PlanningRun?> GetUnfinishedRunAsync(CancellationToken ct = default);

    /// <summary>Id 降順の先頭。画面が「前回」を出すのに使う。無ければ null。</summary>
    Task<PlanningRun?> GetLatestRunAsync(CancellationToken ct = default);

    /// <summary>これまでの実行の件数。ジョブフォルダの連番に使う。</summary>
    Task<int> CountRunsAsync(CancellationToken ct = default);

    void AddCandidate(TriageCandidate candidate);
    Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default);

    /// <summary>渡した中で既に DB に居る ExternalId だけを返す（取り込み時の重複排除）。</summary>
    Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(
        IReadOnlyCollection<string> externalIds, CancellationToken ct = default);

    /// <summary>候補キュー: この実行の Pending ＋ 過去の実行の Later（Id 昇順・仕様 §9）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);

    /// <summary>この実行の候補を状態を問わず Id 昇順で。プランの解決に使う（仕様 §5）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);
}
