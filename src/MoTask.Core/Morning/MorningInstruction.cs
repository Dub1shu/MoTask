using MoTask.Core.Ai;

namespace MoTask.Core.Morning;

/// <summary>
/// instruction.md（仕様 §6）。前半は人が AI 設定で書き換えられる収集方針、
/// 後半は出力先と形の契約で、MoTask が必ず付ける。
/// 契約を人に編集させると MorningResultReader が読める形との対応が黙って壊れる。
/// </summary>
public static class MorningInstruction
{
    public static string DefaultTemplate => Messages.MorningInstructionDefault;

    public static string Build(string? template, JobFolderPaths paths, DateOnly date)
    {
        var head = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        var contract = string.Format(
            Messages.MorningInstructionContractFormat,
            date.ToString("yyyy-MM-dd"), paths.BoardJson, paths.CandidatesJsonl, paths.PlanJson);
        return head + "\n\n" + contract + "\n";
    }
}
