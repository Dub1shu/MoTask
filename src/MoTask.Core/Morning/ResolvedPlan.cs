using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>plan.json の groups[].key の 4 値。並びは表示順であり、重複排除の走査順でもある（仕様 §4）。</summary>
public enum PlanGroupKey
{
    Today = 0,
    IfTime = 1,
    AiReady = 2,
    Waiting = 3,
}

/// <summary>タスク行が今朝の候補から生まれたものかどうか。「新規」「統合」の印に使う。</summary>
public enum TaskRowOrigin
{
    None = 0,
    RegisteredThisMorning = 1,
    MergedThisMorning = 2,
}

/// <summary>プランの 1 行。実タスクか、まだ仕分けていない候補かのどちらか。</summary>
public abstract record PlanRow(string Title);

public sealed record TaskRow(
    int TaskId,
    string Title,
    string? ProjectName,
    DateOnly? DueDate,
    string ColumnName,
    bool IsDone,
    TaskRowOrigin Origin) : PlanRow(Title);

public sealed record CandidateRow(
    int CandidateId,
    string Title,
    string Source,
    DateOnly? SuggestedDueDate,
    string SuggestedProject) : PlanRow(Title);

public sealed record PlanGroup(PlanGroupKey Key, IReadOnlyList<PlanRow> Rows)
{
    public int TaskCount => Rows.Count(r => r is TaskRow);
    public int CandidateCount => Rows.Count(r => r is CandidateRow);

    public static PlanGroup Empty(PlanGroupKey key) => new(key, Array.Empty<PlanRow>());
}

/// <summary>その実行の候補の内訳。今日の Later はキューに乗らないので決着済みとして数える。</summary>
public sealed record TriageSummary(int Total, int Registered, int Merged, int Rejected, int Later, int Pending)
{
    public static readonly TriageSummary None = new(0, 0, 0, 0, 0, 0);

    public static TriageSummary Of(IEnumerable<TriageCandidate> candidates)
    {
        var all = candidates.ToList();
        int Count(TriageStatus status) => all.Count(c => c.Status == status);
        return new TriageSummary(
            all.Count,
            Count(TriageStatus.Registered), Count(TriageStatus.Merged),
            Count(TriageStatus.Rejected), Count(TriageStatus.Later), Count(TriageStatus.Pending));
    }
}

/// <summary>
/// PlanJson を画面の行に解決した結果（仕様 §4）。Groups は 4 つ固定・PlanGroupKey の順。
/// FirstThingIsFallback は firstThing が解決できず today の先頭へ繰り下げたとき（today も空なら FirstThing は null）。
/// </summary>
public sealed record ResolvedPlan(
    PlanRow? FirstThing,
    string FirstThingReason,
    bool FirstThingIsFallback,
    IReadOnlyList<PlanGroup> Groups,
    TriageSummary Summary)
{
    public static ResolvedPlan Empty(TriageSummary summary) => new(
        null, "", false,
        Enum.GetValues<PlanGroupKey>().Select(PlanGroup.Empty).ToList(),
        summary);
}
