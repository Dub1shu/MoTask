using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>列1本。ヘッダーの表示と、その列の中で完結する編集（インライン追加・改名・WIP）を持つ。</summary>
public sealed partial class ColumnViewModel : ObservableObject
{
    private readonly BoardViewModel _board;

    public Column Model { get; }
    public BoardViewModel Board => _board;
    public int Id => Model.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int? _wipLimit;
    [ObservableProperty] private ColumnRole _role;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private bool _isOverWip;
    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private bool _isAddingTask;
    [ObservableProperty] private string _newTaskTitle = "";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isEditingWip;
    [ObservableProperty] private string _wipText = "";
    [ObservableProperty] private TaskCardViewModel? _selectedCard;

    /// <summary>列内の全カード（Position 順、削除済み含む）。移動先の挿入位置はこの並びで数える。</summary>
    public List<TaskCardViewModel> AllCards { get; } = new();

    /// <summary>フィルタ適用後の表示カード。ListBox にバインドする。</summary>
    public ObservableCollection<TaskCardViewModel> Cards { get; } = new();

    public ColumnViewModel(Column model, BoardViewModel board)
    {
        Model = model;
        _board = board;
        RefreshHeader();
    }

    public void RefreshHeader()
    {
        Name = Model.Name;
        WipLimit = Model.WipLimit;
        Role = Model.Role;
        IsDone = Role == ColumnRole.Done;
        ActiveCount = Model.ActiveCount;
        IsOverWip = Model.IsOverWip;
        CountText = WipLimit is int limit
            ? string.Format(CultureInfo.CurrentCulture, Strings.ColumnCountWithLimitFormat, ActiveCount, limit)
            : string.Format(CultureInfo.CurrentCulture, Strings.ColumnCountFormat, ActiveCount);
    }

    /// <summary>Model.Tasks から AllCards を組み直す。既存のカード VM は再利用する。</summary>
    public void SyncCardsFromModel(Func<int?, string?> projectName, DateOnly today)
    {
        var existing = AllCards.ToDictionary(c => c.Id);
        AllCards.Clear();
        foreach (var task in Model.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id))
        {
            var card = existing.TryGetValue(task.Id, out var e) && ReferenceEquals(e.Model, task)
                ? e
                : new TaskCardViewModel(task);
            card.Refresh(projectName, today);
            AllCards.Add(card);
        }
    }

    public void ApplyFilter(TaskFilter filter, DateOnly today)
    {
        // TaskItem は Equals を上書きしないので、既定の HashSet がそのまま参照一致になる。
        var visible = filter.Apply(AllCards.Select(c => c.Model), today).ToHashSet();
        Cards.Clear();
        foreach (var card in AllCards)
        {
            if (visible.Contains(card.Model)) Cards.Add(card);
        }
    }

    partial void OnSelectedCardChanged(TaskCardViewModel? value)
    {
        if (value is not null) _board.SelectCard(value);
    }

    // ---- インライン作成 ----

    [RelayCommand]
    private void BeginAddTask()
    {
        NewTaskTitle = "";
        IsAddingTask = true;
    }

    [RelayCommand]
    private async Task CommitAddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle)) return; // 空は拒否し入力欄に留まる
        if (await _board.CreateTaskAsync(this, NewTaskTitle))
        {
            NewTaskTitle = "";
            IsAddingTask = false;
        }
    }

    [RelayCommand]
    private void CancelAddTask()
    {
        NewTaskTitle = "";
        IsAddingTask = false;
    }

    // ---- ヘッダーメニュー ----

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        await _board.RenameColumnAsync(this, RenameText);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task SetRoleAsync(ColumnRole role) => _board.SetColumnRoleAsync(this, role);

    [RelayCommand]
    private void BeginEditWip()
    {
        WipText = WipLimit?.ToString(CultureInfo.CurrentCulture) ?? "";
        IsEditingWip = true;
    }

    [RelayCommand]
    private async Task CommitWipAsync()
    {
        if (!IsEditingWip) return;
        IsEditingWip = false;
        int? limit = int.TryParse(WipText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) ? n : null;
        await _board.SetWipLimitAsync(this, limit);
    }

    [RelayCommand]
    private void CancelEditWip() => IsEditingWip = false;

    [RelayCommand]
    private Task ClearWipAsync() => _board.SetWipLimitAsync(this, null);

    [RelayCommand]
    private Task DeleteColumnAsync() => _board.DeleteColumnAsync(this);
}
