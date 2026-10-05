using MoTask.Core.Model;
using MoTask.Core.Planning;

namespace MoTask.Core.Services;

/// <summary>
/// 候補を登録するときに人が確定した内容(編集後の値)。LabelIds は付けるラベル。null は空と同じ。
/// </summary>
public sealed record CandidateDecision(
    int CandidateId,
    string Title,
    DateOnly? DueDate,
    string ProjectName,
    int ColumnId,
    IReadOnlyList<int>? LabelIds = null);

public interface IPlanningService
{
    event EventHandler<PlanningRunChangedEventArgs>? RunChanged;

    /// <summary>
    /// 仕分け(登録・統合)でボードを書き換えた。ボード画面はこの書き込みを通らないので、これを見て読み直す。
    /// ワーカースレッドから上がりうるので、購読側で UI スレッドへ載せ替えること。
    /// </summary>
    event EventHandler? BoardChanged;

    // 照会
    Task<PlanningRun?> GetCurrentRunAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);

    /// <summary>この実行の候補を状態を問わず。計画の解決に使う（仕様 §5）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);

    /// <summary>events.jsonl の末尾。進行の表示に使う(DB には持たない)。</summary>
    Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default);
    /// <summary>記憶しているターン数。追跡していなければ 0。</summary>
    int TurnCountOf(int runId);

    // 実行
    Task<Result<PlanningRun>> StartAsync(CancellationToken ct = default);
    /// <summary>
    /// 人の「完了にする」。planning_complete と同じ状態遷移を共有し、閉じ方だけが違う(その場で閉じる)。
    /// 計画が未提出なら受理せず理由を返す。
    /// </summary>
    Task<Result> CompleteAsync(int runId, CancellationToken ct = default);
    /// <summary>追跡をやめる。端末は殺さない。</summary>
    Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default);

    // MCP 経由の受け口（仕様 §6）。Fail は宛先違い＝ツールエラー、Ok(outcome) は通常の結果。

    /// <summary>対象日と盤面（BoardSnapshot.Build の出力そのまま）を返す。</summary>
    Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default);

    /// <summary>候補を 1 件積む。受理のたびに RunChanged(candidatesChanged: true) が上がる。</summary>
    Task<Result<CandidateOutcome>> AddCandidateAsync(
        int runId, CandidateInput input, CancellationToken ct = default);

    /// <summary>計画を出す。何度でも呼べて、最後に受理されたものが残る。</summary>
    Task<Result<PlanningOutcome>> SubmitPlanAsync(int runId, string planJson, CancellationToken ct = default);

    /// <summary>
    /// この計画づくりを終える。closeNow が false なら閉じるのを予約し、次の Stop(か 60 秒の保険)で
    /// 端末を閉じる。true ならその場で閉じる(仕様 §7)。
    /// </summary>
    Task<Result<PlanningOutcome>> CompleteRunAsync(
        int runId, bool closeNow, CancellationToken ct = default);

    // 仕分け
    Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default);
    Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default);
    Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default);
    Task<Result> RejectAsync(int candidateId, CancellationToken ct = default);

    // 一括（仕様 §5）。既存の 4 アクションを順に呼ぶだけで、1 件の失敗で止まらない
    /// <summary>キューの候補を SuggestedAction どおりに処理する。登録先は registerColumnId。</summary>
    Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default);
    /// <summary>キューの候補をすべて「あとで」にする。</summary>
    Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default);

    /// <summary>
    /// 計画の「今日中」グループのうち未着手の列にあるタスクを、今日中の列（役割 Today の先頭）の末尾へ
    /// 計画の並び順で移す（仕様 2026-10-05-today-column §5）。値は移した件数。今日中の列が無ければ 0。
    /// 1 件の失敗では止めず、理由を警告に入れる。WIP 超過も警告に入る。
    /// </summary>
    Task<Result<int>> MoveTodayToColumnAsync(int runId, CancellationToken ct = default);
}
