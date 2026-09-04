namespace MoTask.Core.Model;

/// <summary>追記専用。更新も削除もしない。</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }
    public int TaskId { get; set; }
    /// <summary>新規作成タスクと同じ SaveChanges で保存するためのナビゲーション。DB 列にはならない。</summary>
    public TaskItem? Task { get; set; }
    public DateTime At { get; set; }
    public HistoryKind Kind { get; set; }
    public int? FromColumnId { get; set; }
    public int? ToColumnId { get; set; }
    /// <summary>Edited のとき変更項目と前後の値を JSON で持つ（HistoryDetail 参照）。それ以外は空文字。</summary>
    public string Detail { get; set; } = "";
}
