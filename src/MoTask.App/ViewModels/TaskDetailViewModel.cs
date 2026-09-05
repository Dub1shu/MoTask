using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// 詳細パネル。各項目は値が変わった時点（TextBox はフォーカスアウト）で即保存する。保存ボタンは置かない。
/// PendingSave はテストが保存完了を待つためのハンドル。
/// </summary>
public sealed partial class TaskDetailViewModel : ObservableObject
{
    private readonly BoardViewModel _board;
    private bool _loading;

    public TaskCardViewModel Card { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private ProjectOption? _selectedProject;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private ColumnViewModel? _selectedColumn;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private string? _completedAtText;
    [ObservableProperty] private bool _hasTitleError;
    [ObservableProperty] private string _newProjectName = "";
    [ObservableProperty] private string _newLabelName = "";
    [ObservableProperty] private string? _projectWorkingDirectory;
    [ObservableProperty] private bool _hasProject;

    /// <summary>AI セクション。仕様 §10: 詳細パネルが子として持つ。</summary>
    public TaskAiPanelViewModel Ai { get; }

    public ObservableCollection<ProjectOption> Projects { get; } = new();
    public ObservableCollection<LabelToggleViewModel> Labels { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public ObservableCollection<ColumnViewModel> Columns => _board.Columns;

    public Task PendingSave { get; private set; } = Task.CompletedTask;

    public TaskDetailViewModel(TaskCardViewModel card, BoardViewModel board)
    {
        Card = card;
        _board = board;
        Ai = new TaskAiPanelViewModel(card, board);
        Refresh();
        PendingSave = LoadHistoryAsync();
    }

    /// <summary>Card.Model の現在値を表示に取り込む。保存は起こさない。</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var m = Card.Model;
            Title = m.Title;
            HasTitleError = false;
            Description = m.Description;

            Projects.Clear();
            Projects.Add(new ProjectOption(null, Strings.NoProject));
            foreach (var p in _board.Projects.Where(p => !p.Archived || p.Id == m.ProjectId).OrderBy(p => p.Name))
            {
                Projects.Add(new ProjectOption(p.Id, p.Name));
            }
            SelectedProject = Projects.FirstOrDefault(o => o.Id == m.ProjectId) ?? Projects[0];
            SyncProjectWorkingDirectory();

            DueDate = m.DueDate?.ToDateTime(TimeOnly.MinValue);
            SelectedColumn = _board.Columns.FirstOrDefault(c => c.Id == m.ColumnId);
            IsDeleted = m.IsDeleted;
            // 完了列を出ると CompletedAt は null に戻るので、行ごと消える
            CompletedAtText = m.CompletedAt is DateTime completed ? HistoryFormatter.Timestamp(completed) : null;

            Labels.Clear();
            // アーカイブ済みは選択肢から外すが、このタスクが既に持っているものは残す
            // （プロジェクトのドロップダウンと同じ規則）
            foreach (var l in _board.Labels.Where(l => !l.Archived || m.Labels.Any(x => x.Id == l.Id)).OrderBy(l => l.Name))
            {
                Labels.Add(new LabelToggleViewModel(l, m.Labels.Any(x => x.Id == l.Id), OnLabelToggled));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public async Task LoadHistoryAsync()
    {
        var entries = await _board.GetHistoryAsync(Card.Id);
        History.Clear();
        foreach (var e in entries) History.Add(HistoryFormatter.Format(e, _board.ColumnName));
    }

    partial void OnTitleChanged(string value) => QueueSave();
    partial void OnDescriptionChanged(string value) => QueueSave();
    partial void OnDueDateChanged(DateTime? value) => QueueSave();

    partial void OnSelectedProjectChanged(ProjectOption? value)
    {
        if (_loading)
        {
            return;
        }
        // 保存の前に作業フォルダ欄を切り替える。_loading を立てて欄の変更が保存扱いにならないようにする。
        _loading = true;
        try
        {
            SyncProjectWorkingDirectory();
        }
        finally
        {
            _loading = false;
        }
        QueueSave();
    }

    partial void OnProjectWorkingDirectoryChanged(string? value)
    {
        if (_loading || SelectedProject?.Id is not int projectId) return;
        PendingSave = _board.SetProjectWorkingDirectoryAsync(projectId, value);
    }

    /// <summary>選択中プロジェクトの作業フォルダを欄に写す。呼び出し側が _loading を立てておく。</summary>
    private void SyncProjectWorkingDirectory()
    {
        var project = SelectedProject?.Id is int id ? _board.Projects.FirstOrDefault(p => p.Id == id) : null;
        HasProject = project is not null;
        ProjectWorkingDirectory = project?.WorkingDirectory;
    }

    partial void OnSelectedColumnChanged(ColumnViewModel? value)
    {
        if (_loading || value is null || value.Id == Card.Model.ColumnId) return;
        PendingSave = MoveAsync(value);
    }

    private void QueueSave()
    {
        if (_loading) return;
        PendingSave = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            HasTitleError = true; // 空タイトルは拒否し、入力欄に留まる
            return;
        }
        HasTitleError = false;
        var update = new TaskUpdate(
            Card.Id, Title, Description, SelectedProject?.Id,
            DueDate is DateTime d ? DateOnly.FromDateTime(d) : null);
        if (await _board.UpdateTaskAsync(Card, update)) await LoadHistoryAsync();
    }

    private async Task MoveAsync(ColumnViewModel target)
    {
        if (await _board.MoveCardAsync(Card, target, int.MaxValue)) await LoadHistoryAsync();
    }

    private void OnLabelToggled(LabelToggleViewModel _)
    {
        if (_loading) return;
        var ids = Labels.Where(l => l.IsSelected).Select(l => l.Id).ToList();
        PendingSave = SaveLabelsAsync(ids);
    }

    private async Task SaveLabelsAsync(IReadOnlyCollection<int> ids)
    {
        if (await _board.SetTaskLabelsAsync(Card, ids)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await _board.DeleteTaskAsync(Card)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (await _board.RestoreTaskAsync(Card)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private void Close() => _board.CloseDetailCommand.Execute(null);

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0) return;
        var project = await _board.CreateProjectAsync(name);
        if (project is null) return;
        NewProjectName = "";
        Refresh();
        SelectedProject = Projects.First(o => o.Id == project.Id); // 変更として保存される
    }

    [RelayCommand]
    private async Task CreateLabelAsync()
    {
        var name = NewLabelName.Trim();
        if (name.Length == 0) return;
        var label = await _board.CreateLabelAsync(name);
        if (label is null) return;
        NewLabelName = "";
        var ids = Card.Model.Labels.Select(l => l.Id).Append(label.Id).ToList();
        if (await _board.SetTaskLabelsAsync(Card, ids)) await LoadHistoryAsync();
    }
}

public sealed partial class LabelToggleViewModel : ObservableObject
{
    private readonly Action<LabelToggleViewModel> _toggled;

    public Label Model { get; }
    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;

    [ObservableProperty] private bool _isSelected;

    public LabelToggleViewModel(Label model, bool isSelected, Action<LabelToggleViewModel> toggled)
    {
        Model = model;
        _isSelected = isSelected;
        _toggled = toggled;
    }

    partial void OnIsSelectedChanged(bool value) => _toggled(this);
}
