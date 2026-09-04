namespace MoTask.Core.Model;

public sealed class Board
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Column> Columns { get; set; } = new();
}
