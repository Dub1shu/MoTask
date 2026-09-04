using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>フィルタバー。変更を <see cref="Changed"/> で知らせ、ボードが表示を絞り直す。</summary>
public sealed partial class FilterViewModel : ObservableObject
{
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
        foreach (var l in labels.OrderBy(l => l.Name, StringComparer.CurrentCulture))
        {
            Labels.Add(new LabelFilterItem(l, RaiseChanged) { IsSelected = keep.Contains(l.Id) });
        }
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
