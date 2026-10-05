namespace MoTask.Core.Planning;

/// <summary>
/// planning_add_candidate から渡る 1 件（仕様 §6）。SuggestedAction は 4 値の検証が
/// CandidateValidator の仕事なので、ここでは文字列のまま持つ。
/// 省略された文字列項目は空文字で渡ってくる（ツール側が JSON の欠けを空文字に寄せる）。
/// SuggestedLabels は省略なら null（空と同じ）。
/// </summary>
public sealed record CandidateInput(
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
    string SuggestedAction,
    int? MergeTargetTaskId,
    IReadOnlyList<string>? SuggestedLabels = null);
