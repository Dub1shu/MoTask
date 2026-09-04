namespace MoTask.Core.Model;

/// <summary>仕様上の "Task"。System.Threading.Tasks.Task との衝突を避けるため TaskItem と命名。</summary>
public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int ColumnId { get; set; }
    public int Position { get; set; }
    public int? ProjectId { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public List<Label> Labels { get; set; } = new();

    public bool IsDeleted => DeletedAt is not null;
}
