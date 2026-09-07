namespace MoTask.Core.Morning;

/// <summary>
/// result/ を 1 回読んだ結果。DiscardedLines は画面に出す（黙って減らさない・仕様 §8）。
/// </summary>
public sealed record MorningResult(
    IReadOnlyList<CandidateRecord> Candidates,
    int DiscardedLines,
    string PlanJson)
{
    /// <summary>取り込んでよいか。候補 0 件でもプランが妥当なら成功（仕様 §8）。</summary>
    public bool IsUsable => PlanJson.Length > 0;
}
