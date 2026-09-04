namespace MoTask.Core.Model;

public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
}
