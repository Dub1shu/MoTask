using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// 候補 1 件の検証（仕様 §6）。MoTask と Claude の接点なので純関数にして固定する。
/// 落ちたときの理由は Claude がそのまま読んで直せる文にする。
/// 「mergeTargetTaskId が盤面に在るか」は盤面が要るので PlanningService が見る（理由は同じ文言）。
/// </summary>
public static class CandidateValidator
{
    /// <summary>suggestedAction に許す 4 値（仕様 §6）。</summary>
    public static readonly IReadOnlyList<string> Actions = new[] { "register", "merge", "later", "reject" };

    public static Result<CandidateRecord> Validate(CandidateInput input)
    {
        var externalId = input.ExternalId.Trim();
        if (externalId.Length == 0) return Missing("externalId");

        var source = input.Source.Trim();
        if (source.Length == 0) return Missing("source");

        var title = input.Title.Trim();
        if (title.Length == 0) return Missing("title");

        // 根拠の無い候補は人が判断できないので受け取らない（仕様 §6）
        var evidence = input.Evidence.Trim();
        if (evidence.Length == 0) return Result.Fail<CandidateRecord>(Messages.CandidateEvidenceRequired);

        if (!TryAction(input.SuggestedAction.Trim(), out var action))
        {
            return Result.Fail<CandidateRecord>(Messages.CandidateActionInvalid);
        }
        if (action == TriageAction.Merge && input.MergeTargetTaskId is null)
        {
            return Result.Fail<CandidateRecord>(Messages.CandidateMergeTargetMissing);
        }

        return Result.Ok(new CandidateRecord(
            externalId, source, title, evidence,
            input.From.Trim(), input.Link.Trim(), input.Reasoning.Trim(),
            input.ReceivedAt, input.SuggestedDueDate, input.SuggestedProject.Trim(),
            action, input.MergeTargetTaskId));
    }

    private static Result<CandidateRecord> Missing(string field)
        => Result.Fail<CandidateRecord>(string.Format(Messages.CandidateFieldRequiredFormat, field));

    private static bool TryAction(string value, out TriageAction action)
    {
        switch (value)
        {
            case "register": action = TriageAction.Register; return true;
            case "merge": action = TriageAction.Merge; return true;
            case "later": action = TriageAction.Later; return true;
            case "reject": action = TriageAction.Reject; return true;
            default: action = TriageAction.Register; return false;
        }
    }
}
