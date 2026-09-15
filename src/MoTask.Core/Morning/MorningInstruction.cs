namespace MoTask.Core.Morning;

/// <summary>
/// instruction.md（仕様 §8）。前半は人が AI 設定で書き換えられる収集方針、
/// 後半は MoTask が必ず付けるツールの呼び出し手順。
/// 契約を人に編集させると、ツールの呼び方との対応が黙って壊れる。
/// </summary>
public static class MorningInstruction
{
    public static string DefaultTemplate => Messages.MorningInstructionDefault;

    /// <summary>
    /// runId は DB の採番なので、呼び手は行を保存してからここへ来ること
    /// （各引数の形は MCP のスキーマが持つので、契約文に JSON の例は並べない）。
    /// </summary>
    public static string Build(string? template, DateOnly date, int runId)
    {
        var head = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        var contract = string.Format(
            Messages.MorningInstructionContractFormat, date.ToString("yyyy-MM-dd"), runId);
        return head + "\n\n" + contract + "\n";
    }
}
