namespace MoTask.Data;

/// <summary>Task と Label の多対多の結合エンティティ。Core には出さない。</summary>
public sealed class TaskLabel
{
    public int TaskId { get; set; }
    public int LabelId { get; set; }
}
