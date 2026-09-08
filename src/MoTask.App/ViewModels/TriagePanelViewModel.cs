using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>統合先の選択肢。完了列と論理削除済みは出さない（仕様 §6）。</summary>
public sealed record TaskChoice(int Id, string Title, string ColumnName)
{
    public string Display => string.Format(Strings.MorningTaskChoiceFormat, Title, ColumnName);
}

/// <summary>
/// 左パネル・状態 1「仕分け中」（仕様 §6、ワイヤー 4a）。編集フォームと 4 アクションを持つ。
/// 候補キューと実行の状態は画面（MorningPlanViewModel）が持ち、片づいた後の読み直しも
/// 画面に任せる（afterDecision）。この VM はキューの中身を知らない。
/// </summary>
public sealed partial class TriagePanelViewModel : ObservableObject
{
    private readonly IMorningService _service;
    private readonly Func<Result, Task> _afterDecision;
    private readonly Action<string> _openPath;

    public TriagePanelViewModel(IMorningService service, Func<Result, Task> afterDecision, Action<string> openPath)
    {
        _service = service;
        _afterDecision = afterDecision;
        _openPath = openPath;
    }

    public string HeadingText => Strings.MorningTriageHeading;
    public string KeyHint => Strings.MorningKeyHint;

    /// <summary>登録先に選べる列。完了列は選ばせない（親仕様 §11）。</summary>
    public ObservableCollection<ColumnChoice> ColumnChoices { get; } = new();

    /// <summary>統合先に選べるタスク。</summary>
    public ObservableCollection<TaskChoice> MergeTargets { get; } = new();

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private string _positionText = "";

    // 編集フォーム。期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）。
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editProjectName = "";
    [ObservableProperty] private int _editColumnId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private int? _editMergeTargetId;

    /// <summary>統合先が選ばれているか。E キーと「統合」ボタンの活性。</summary>
    public bool CanMerge => EditMergeTargetId is not null;

    public void SetChoices(IEnumerable<ColumnChoice> columns, IEnumerable<TaskChoice> targets)
    {
        ColumnChoices.Clear();
        foreach (var column in columns) ColumnChoices.Add(column);
        MergeTargets.Clear();
        foreach (var target in targets) MergeTargets.Add(target);
        if (ColumnChoices.All(c => c.Id != EditColumnId)) EditColumnId = ColumnChoices.FirstOrDefault()?.Id ?? 0;
        if (EditMergeTargetId is int chosen && MergeTargets.All(t => t.Id != chosen)) EditMergeTargetId = null;
    }

    /// <summary>候補を 1 件見せる。推薦された統合先が一覧にあれば初期選択にする。</summary>
    public void Show(CandidateItemViewModel? candidate, int index, int count)
    {
        Selected = candidate;
        EditTitle = candidate?.Title ?? "";
        EditDueDate = candidate?.SuggestedDueDate?.ToDateTime(TimeOnly.MinValue);
        EditProjectName = candidate?.SuggestedProject ?? "";
        EditMergeTargetId = candidate?.SuggestedMergeTaskId is int suggested && MergeTargets.Any(t => t.Id == suggested)
            ? suggested
            : null;
        PositionText = candidate is null ? "" : string.Format(Strings.MorningPositionFormat, index + 1, count);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Selected is null) return;
        var due = EditDueDate is DateTime date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var registered = await _service.RegisterAsync(new CandidateDecision(
            Selected.CandidateId, EditTitle, due, EditProjectName, EditColumnId)).ConfigureAwait(true);
        await _afterDecision(registered).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (Selected is null || EditMergeTargetId is not int target) return;
        var merged = await _service.MergeAsync(Selected.CandidateId, target).ConfigureAwait(true);
        await _afterDecision(merged).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PostponeAsync()
    {
        if (Selected is null) return;
        await _afterDecision(await _service.PostponeAsync(Selected.CandidateId).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (Selected is null) return;
        await _afterDecision(await _service.RejectAsync(Selected.CandidateId).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Selected is { HasLink: true } candidate) _openPath(candidate.Link);
    }
}
