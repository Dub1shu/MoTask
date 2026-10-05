using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Planning;

namespace MoTask.App.ViewModels;

/// <summary>右カラムの区分 1 つ（仕様 §7）。4 つを画面が固定で持ち、Update で中身だけ差し替える。</summary>
public sealed partial class PlanSectionViewModel : ObservableObject
{
    private readonly Action<PlanRowViewModel> _open;

    public PlanSectionViewModel(PlanGroupKey key, Action<PlanRowViewModel> open)
    {
        Key = key;
        _open = open;
    }

    public PlanGroupKey Key { get; }

    /// <summary>「今日中の列へ移す」ボタンを出す区分か。</summary>
    public bool IsToday => Key == PlanGroupKey.Today;

    public string Heading => Key switch
    {
        PlanGroupKey.Today => Strings.PlanGroupToday,
        PlanGroupKey.IfTime => Strings.PlanGroupIfTime,
        PlanGroupKey.AiReady => Strings.PlanGroupAiReady,
        _ => Strings.PlanGroupWaiting,
    };

    public string EmptyText => Strings.PlanGroupEmpty;
    public ObservableCollection<PlanRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty = true;

    public void Update(PlanGroup group)
    {
        Rows.Clear();
        foreach (var row in group.Rows) Rows.Add(new PlanRowViewModel(row, _open));
        IsEmpty = Rows.Count == 0;
        CountText = group.CandidateCount > 0
            ? string.Format(Strings.PlanGroupCountWithCandidatesFormat, group.TaskCount, group.CandidateCount)
            : string.Format(Strings.PlanGroupCountFormat, group.TaskCount);
    }
}

/// <summary>計画の 1 行。タスク行はクリックでボードへ、候補行はクリックで候補キューの選択になる。</summary>
public sealed partial class PlanRowViewModel
{
    private readonly Action<PlanRowViewModel> _open;

    public PlanRowViewModel(PlanRow row, Action<PlanRowViewModel> open)
    {
        Row = row;
        _open = open;
    }

    public PlanRow Row { get; }
    public string Title => Row.Title;
    public int? TaskId => (Row as TaskRow)?.TaskId;
    public int? CandidateId => (Row as CandidateRow)?.CandidateId;
    public bool IsDone => Row is TaskRow { IsDone: true };

    /// <summary>プロジェクト／期限の小さな 1 行。候補行はソースも前に付ける。</summary>
    public string Caption => Row switch
    {
        TaskRow task => Join(task.ProjectName, Due(task.DueDate)),
        CandidateRow candidate => Join(candidate.Source, candidate.SuggestedProject, Due(candidate.SuggestedDueDate)),
        _ => "",
    };

    public string? BadgeText => Row switch
    {
        TaskRow { Origin: TaskRowOrigin.RegisteredThisRun } => Strings.PlanRowNew,
        TaskRow { Origin: TaskRowOrigin.MergedThisRun } => Strings.PlanRowMerged,
        CandidateRow => Strings.PlanRowPendingCandidate,
        _ => null,
    };

    public bool HasBadge => BadgeText is not null;

    [RelayCommand]
    private void Open() => _open(this);

    private static string Due(DateOnly? date)
        => date is DateOnly d ? string.Format(CultureInfo.CurrentCulture, Strings.CardDueFormat, d.Month, d.Day) : "";

    private static string Join(params string?[] parts)
        => string.Join(" / ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
