namespace MoTask.Core.Model;

public sealed class Label
{
    public const string DefaultColor = "accent-300";

    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>アクセントランプの段の名前（"accent-300" など）。</summary>
    public string Color { get; set; } = DefaultColor;
}
