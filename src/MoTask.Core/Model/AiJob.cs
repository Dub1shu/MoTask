namespace MoTask.Core.Model;

/// <summary>タスク 1 件に対する AI の 1 回の依頼。仕様 §6。</summary>
public sealed class AiJob
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public AiJobKind Kind { get; set; }
    public AiJobStatus Status { get; set; } = AiJobStatus.Pending;
    /// <summary>MoTask が採番して --session-id に渡す。再開は --resume で同じ値を使う。</summary>
    public Guid SessionId { get; set; }
    /// <summary>人が確認・編集した指示文。</summary>
    public string Instruction { get; set; } = "";
    /// <summary>実行時に解決した cwd のスナップショット。後でプロジェクトの作業フォルダを変えても、どこで走ったかが残る。</summary>
    public string WorkingDirectory { get; set; } = "";
    /// <summary>ジョブフォルダの絶対パス（仕様 §6）。起動に失敗したジョブでは空のまま。</summary>
    public string JobFolder { get; set; } = "";
    /// <summary>events.jsonl から取り込み済みの行数。追従を張り直すときの読み飛ばし数になる。</summary>
    public int ProcessedLines { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>Stop フックを数えたターン数（仕様 §9）。費用はフックに来ないので持たない。</summary>
    public int? NumTurns { get; set; }
    public string? ErrorMessage { get; set; }
}
