using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// planning_add_candidate の引数を検証したもの（仕様 §6）。ここから先は DB のエンティティに写すだけ。
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
