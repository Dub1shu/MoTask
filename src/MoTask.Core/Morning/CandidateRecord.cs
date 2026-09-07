using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// candidates.jsonl の 1 行を検証したもの（仕様 §8）。ここから先は DB のエンティティに写すだけ。
/// </summary>
public sealed record CandidateRecord(
    string ExternalId,
    string Source,
    string Title,
    string Evidence,
    string From,
    string Link,
    string Reasoning,
    DateTime? ReceivedAt,
    DateOnly? SuggestedDueDate,
    string SuggestedProject,
    TriageAction SuggestedAction,
    int? MergeTargetTaskId);
