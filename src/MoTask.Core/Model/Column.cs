namespace MoTask.Core.Model;

public sealed class Column
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public int? WipLimit { get; set; }
    public ColumnRole Role { get; set; } = ColumnRole.Active;
    public List<TaskItem> Tasks { get; set; } = new();

    /// <summary>論理削除済みを除いた件数。WIP 判定と列ヘッダーの表示に使う。</summary>
    public int ActiveCount => Tasks.Count(t => t.DeletedAt is null);

    public bool IsOverWip => WipLimit is int limit && ActiveCount > limit;
}
