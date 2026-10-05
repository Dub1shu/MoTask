namespace MoTask.Core.Model;

public sealed class Label : IClassification
{
    public const string DefaultColor = "accent-300";

    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>アクセントランプの段の名前（"accent-300" など）。</summary>
    public string Color { get; set; } = DefaultColor;
    /// <summary>
    /// 一覧から退けるだけで、既にこのラベルが付いているタスクからは外さない。
    /// 過去のタスクの表示と履歴の文言を壊さないため、削除ではなくアーカイブにしている。
    /// </summary>
    public bool Archived { get; set; }
    /// <summary>表示順（管理ダイアログのドラッグで決める）。</summary>
    public int Order { get; set; }
}
