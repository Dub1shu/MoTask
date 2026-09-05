namespace MoTask.Core.Model;

/// <summary>追記専用。更新も削除もしない（HistoryEntry と同じ思想）。</summary>
public sealed class AiJobEvent
{
    public long Id { get; set; }
    public int JobId { get; set; }
    /// <summary>ジョブ内の順序（1 から）。</summary>
    public int Seq { get; set; }
    public DateTime At { get; set; }
    public AiJobEventKind Kind { get; set; }
    /// <summary>ToolUse / PermissionAsked のときのツール名。</summary>
    public string? ToolName { get; set; }
    /// <summary>stream-json の 1 行を生のまま。MoTask 自身が作るイベント（承認）は MoTask 形式の JSON。</summary>
    public string Payload { get; set; } = "";
}
