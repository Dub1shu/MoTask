using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>フィルタバー。変更を <see cref="Changed"/> で知らせ、ボードが表示を絞り直す。</summary>
public sealed partial class FilterViewModel : ObservableObject
{
    /// <summary>バーに並べる選択中ラベルの上限。超えた分は「+n」にまとめ、バーがあふれないようにする。</summary>
    public const int PreviewLimit = 3;

    public ObservableCollection<ProjectOption> Projects { get; } = new();
    public ObservableCollection<LabelFilterItem> Labels { get; } = new();
    public IReadOnlyList<DueOption> DueOptions { get; } = new[]
    {
        new DueOption(DueFilter.All, Strings.DueAll),
        new DueOption(DueFilter.Today, Strings.DueToday),
        new DueOption(DueFilter.ThisWeek, Strings.DueThisWeek),
        new DueOption(DueFilter.Overdue, Strings.DueOverdue),
    };

    [ObservableProperty] private ProjectOption? _selectedProject;
    [ObservableProperty] private DueOption _selectedDue;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showDeleted;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedLabels))] private int _selectedLabelCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLabelOverflow))] private int _selectedLabelOverflow;

    public bool HasSelectedLabels => SelectedLabelCount > 0;
    public bool HasLabelOverflow => SelectedLabelOverflow > 0;
    /// <summary>選択中のラベルの先頭 <see cref="PreviewLimit"/> 個（並びは <see cref="Labels"/> と同じ）。</summary>
    public ObservableCollection<LabelFilterItem> SelectedLabelsPreview { get; } = new();

    // 一括解除の間は、チップ 1 つずつの変更で絞り込みを走らせない
    private bool _suppressLabelChanged;

    public event EventHandler? Changed;

    public FilterViewModel()
    {
        _selectedDue = DueOptions[0];
        SetProjects(Array.Empty<Project>());
    }

    public void SetProjects(IEnumerable<Project> projects)
    {
        var keep = SelectedProject?.Id;
        Projects.Clear();
        Projects.Add(new ProjectOption(null, Strings.FilterAllProjects));
        foreach (var p in projects.Where(p => !p.Archived).OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Projects.Add(new ProjectOption(p.Id, p.Name));
        }
        SelectedProject = Projects.FirstOrDefault(o => o.Id == keep) ?? Projects[0];
    }

    public void SetLabels(IEnumerable<Label> labels)
    {
        var keep = Labels.Where(l => l.IsSelected).Select(l => l.Id).ToHashSet();
        Labels.Clear();
        // アーカイブ済みは絞り込みの選択肢から外す（プロジェクトと同じ扱い）
        foreach (var l in labels.Where(l => !l.Archived).OrderBy(l => l.Name, StringComparer.CurrentCulture))
        {
            Labels.Add(new LabelFilterItem(l, OnLabelSelectionChanged) { IsSelected = keep.Contains(l.Id) });
        }
        UpdateLabelSummary();
    }

    [RelayCommand]
    private void ClearLabels()
    {
        var selected = Labels.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0) return;
        _suppressLabelChanged = true;
        try
        {
            foreach (var l in selected) l.IsSelected = false;
        }
        finally
        {
            _suppressLabelChanged = false;
        }
        UpdateLabelSummary();
        RaiseChanged();
    }

    private void OnLabelSelectionChanged()
    {
        if (_suppressLabelChanged) return;
        UpdateLabelSummary();
        RaiseChanged();
    }

    private void UpdateLabelSummary()
    {
        var selected = Labels.Where(l => l.IsSelected).ToList();
        SelectedLabelsPreview.Clear();
        foreach (var l in selected.Take(PreviewLimit)) SelectedLabelsPreview.Add(l);
        SelectedLabelCount = selected.Count;
        SelectedLabelOverflow = Math.Max(0, selected.Count - PreviewLimit);
    }

    public TaskFilter ToFilter() => new(
        ProjectId: SelectedProject?.Id,
        LabelIds: Labels.Where(l => l.IsSelected).Select(l => l.Id).ToHashSet(),
        Due: SelectedDue.Value,
        SearchText: SearchText,
        ShowDeleted: ShowDeleted);

    partial void OnSelectedProjectChanged(ProjectOption? value) => RaiseChanged();
    partial void OnSelectedDueChanged(DueOption value) => RaiseChanged();
    partial void OnSearchTextChanged(string value) => RaiseChanged();
    partial void OnShowDeletedChanged(bool value) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>フィルタバーのラベルチップ。選択を切り替えるとフィルタが走る。</summary>
public sealed partial class LabelFilterItem : ObservableObject
{
    private readonly Action _changed;

    public Label Model { get; }
    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;
    public string TextColor { get; }

    [ObservableProperty] private bool _isSelected;

    public LabelFilterItem(Label model, Action changed)
    {
        Model = model;
        _changed = changed;
        TextColor = RampSteps.TextColorOn(model.Color);
    }

    partial void OnIsSelectedChanged(bool value) => _changed();
}
