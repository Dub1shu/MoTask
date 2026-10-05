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
    [ObservableProperty] private bool _isAddingProject;
    [ObservableProperty] private bool _isAddingLabel;
    [ObservableProperty] private bool _canComplete;

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
            foreach (var p in _board.Projects.Where(p => !p.Archived || p.Id == m.ProjectId).InDisplayOrder())
            {
                Projects.Add(new ProjectOption(p.Id, p.Name));
            }
            SelectedProject = Projects.FirstOrDefault(o => o.Id == m.ProjectId) ?? Projects[0];

            DueDate = m.DueDate?.ToDateTime(TimeOnly.MinValue);
            SelectedColumn = _board.Columns.FirstOrDefault(c => c.Id == m.ColumnId);
            IsDeleted = m.IsDeleted;
            // 「完了にする」は完了列に入るまでの片道。削除済みは「復元」を出す段なので、そこでも消す。
            CanComplete = !IsDeleted && SelectedColumn?.IsDone != true;
            // 完了列を出ると CompletedAt は null に戻るので、行ごと消える
            CompletedAtText = m.CompletedAt is DateTime completed ? HistoryFormatter.Timestamp(completed) : null;

            Labels.Clear();
            // アーカイブ済みは選択肢から外すが、このタスクが既に持っているものは残す
            // （プロジェクトのドロップダウンと同じ規則）
            foreach (var l in _board.Labels.Where(l => !l.Archived || m.Labels.Any(x => x.Id == l.Id)).InDisplayOrder())
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
        QueueSave();
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

    /// <summary>完了列の末尾へ移す。列の ComboBox 経由と同じく、移動後に履歴を読み直す。</summary>
    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (await _board.CompleteCardAsync(Card)) await LoadHistoryAsync();
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

    // 新しいプロジェクト／ラベルの入力欄は、使うときだけ「＋」で開く。
    // 列の「＋ 追加」と同じく、空の Enter は拒否して開いたまま、作れなかったときも入力を残す。

    [RelayCommand]
    private void BeginAddProject()
    {
        NewProjectName = "";
        IsAddingProject = true;
    }

    [RelayCommand]
    private void CancelAddProject()
    {
        NewProjectName = "";
        IsAddingProject = false;
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0) return;
        var project = await _board.CreateProjectAsync(name);
        if (project is null) return;
        NewProjectName = "";
        IsAddingProject = false;
        Refresh();
        SelectedProject = Projects.First(o => o.Id == project.Id); // 変更として保存される
    }

    [RelayCommand]
    private void BeginAddLabel()
    {
        NewLabelName = "";
        IsAddingLabel = true;
    }

    [RelayCommand]
    private void CancelAddLabel()
    {
        NewLabelName = "";
        IsAddingLabel = false;
    }

    [RelayCommand]
    private async Task CreateLabelAsync()
    {
        var name = NewLabelName.Trim();
        if (name.Length == 0) return;
        var label = await _board.CreateLabelAsync(name);
        if (label is null) return;
        NewLabelName = "";
        IsAddingLabel = false;
        // 同名の既存ラベルが返ってくることがある（サービスは同名を作らない）ので、二重に付けない
        var ids = Card.Model.Labels.Select(l => l.Id).Append(label.Id).Distinct().ToList();
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
