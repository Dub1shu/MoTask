namespace MoTask.Core.Model;

public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
    /// <summary>カードの名前の前に出す色丸のランプ段（LabelPalette の色）。null は色なしで、丸を出さない。</summary>
    public string? Color { get; set; }
    /// <summary>AI ジョブの cwd。null なら設定の既定ワークフォルダを使う。設定されていて存在しなければジョブを開始しない（仕様 §8）。</summary>
    public string? WorkingDirectory { get; set; }
}
