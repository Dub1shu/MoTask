namespace MoTask.Core.Ai;

/// <summary>
/// 組み立てた端末の起動コマンド。Arguments は生のコマンドライン文字列
/// （起動テンプレートが人の書いた 1 行なので、要素の配列には戻せない）。
/// </summary>
public sealed record TerminalCommand(string FileName, string Arguments, string WorkingDirectory)
{
    /// <summary>job.json と ErrorMessage に出す 1 行表示。</summary>
    public string Display => Arguments.Length == 0 ? FileName : $"{FileName} {Arguments}";
}
