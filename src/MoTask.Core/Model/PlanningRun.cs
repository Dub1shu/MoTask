namespace MoTask.Core.Model;

/// <summary>
/// 朝の実行1回（仕様 §9）。AiJob には相乗りしない（AiJob.TaskId は必須で、朝の実行には対象タスクが無い）。
/// イベントは DB に持たず、events.jsonl と ProcessedLines だけで足りるようにする。
/// </summary>
public sealed class PlanningRun
{
    public int Id { get; set; }
    /// <summary>対象日（ローカル）。</summary>
    public DateOnly Date { get; set; }
    public PlanningRunStatus Status { get; set; } = PlanningRunStatus.Pending;
    /// <summary>MoTask が採番して --session-id に渡す。</summary>
    public Guid SessionId { get; set; }
    /// <summary>実行時に組み立てた指示文のスナップショット。</summary>
    public string Instruction { get; set; } = "";
    /// <summary>ジョブフォルダの絶対パス。DB に行が出来る時点で必ず埋まっている（仕様 §12）。</summary>
    public string JobFolder { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>events.jsonl から読んだ行数。追従を張り直すときの読み飛ばし数になる。</summary>
    public int ProcessedLines { get; set; }
    /// <summary>planning_submit_plan で受けた検証済みの生 JSON。未取り込みは空文字（仕様 §9）。</summary>
    public string PlanJson { get; set; } = "";
}
