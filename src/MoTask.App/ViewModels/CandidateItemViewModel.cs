using System.Globalization;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>候補 1 件の読み取り専用の見え方。編集中の値は PlanViewModel が持つ。</summary>
public sealed class CandidateItemViewModel
{
    private readonly TriageCandidate _candidate;

    public CandidateItemViewModel(TriageCandidate candidate)
    {
        _candidate = candidate;
    }

    public int CandidateId => _candidate.Id;
    public string Source => _candidate.Source;
    public string From => _candidate.From;
    public string Title => _candidate.Title;
    public string Evidence => _candidate.Evidence;
    public string Reasoning => _candidate.Reasoning;
    public string Link => _candidate.Link;
    public bool HasLink => _candidate.Link.Length > 0;
    public string SuggestedProject => _candidate.SuggestedProject;
    public DateOnly? SuggestedDueDate => _candidate.SuggestedDueDate;
    public TriageAction SuggestedAction => _candidate.SuggestedAction;
    public int? SuggestedMergeTaskId => _candidate.SuggestedMergeTaskId;
    public IReadOnlyList<int> SuggestedLabelIds => _candidate.SuggestedLabelIds;

    /// <summary>統合先が推薦されているか。候補キューの「統合が推奨」バッジに使う（統合できるかは TriagePanelViewModel.CanMerge）。</summary>
    public bool IsMergeSuggested => _candidate.SuggestedMergeTaskId is not null;

    /// <summary>受信時刻は DB に UTC で入っているので現地時刻へ直す。</summary>
    public string ReceivedText => _candidate.ReceivedAt is DateTime at
        ? HistoryFormatter.Timestamp(at)
        : "";

    public string DueText => _candidate.SuggestedDueDate?.ToString("M/d", CultureInfo.InvariantCulture) ?? "";

    /// <summary>候補キューの推奨バッジ（仕様 §7）。SuggestedAction を文言に写すだけ。</summary>
    public string SuggestionText => _candidate.SuggestedAction switch
    {
        TriageAction.Merge => Strings.PlanSuggestMerge,
        TriageAction.Later => Strings.PlanSuggestLater,
        TriageAction.Reject => Strings.PlanSuggestReject,
        _ => Strings.PlanSuggestRegister,
    };

    /// <summary>差出人／期限の小さな 1 行。</summary>
    public string Caption => string.Join(" / ", new[] { From, DueText }.Where(s => s.Length > 0));
}
