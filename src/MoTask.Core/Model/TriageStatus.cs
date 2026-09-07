namespace MoTask.Core.Model;

/// <summary>候補に対する人の判断の結果（仕様 §9）。</summary>
public enum TriageStatus
{
    Pending = 0,
    Registered = 1,
    Merged = 2,
    /// <summary>翌朝の候補キューに残る唯一の状態。</summary>
    Later = 3,
    Rejected = 4,
}
