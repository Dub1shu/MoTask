using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>列1本。ヘッダーの表示と、その列の中で完結する編集（インライン追加・名前の変更・カードの上限）を持つ。</summary>
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
    // 列メニューで今の役割にチェックを付けるため。
    [ObservableProperty] private bool _isBacklog;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isReview;
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
        IsBacklog = Role == ColumnRole.Backlog;
        IsActive = Role == ColumnRole.Active;
        IsReview = Role == ColumnRole.Review;
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
        // Cards は ListBox の ItemsSource なので、Clear が発火する Reset で Selector は選択を解除し、
        // null を SelectedCard へ書き戻す。絞り込んだ後もまだ表示されるカードの選択はここで戻す。
        var selected = SelectedCard;
        Cards.Clear();
        foreach (var card in AllCards)
        {
            if (visible.Contains(card.Model)) Cards.Add(card);
        }
        if (selected is not null && Cards.Contains(selected)) SelectedCard = selected;
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

    /// <summary>成功したときだけ閉じる。却下されたら理由を読みながら直せるよう入力を残す。</summary>
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming) return;
        if (await _board.RenameColumnAsync(this, RenameText)) IsRenaming = false;
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

    /// <summary>
    /// 空欄は「制限なし」。数字でない入力は打ち間違いとして拒否し、黙って制限を消さない。
    /// 成功したときだけ閉じる。
    /// </summary>
    [RelayCommand]
    private async Task CommitWipAsync()
    {
        if (!IsEditingWip) return;

        var text = WipText.Trim();
        int? limit;
        if (text.Length == 0)
        {
            limit = null;
        }
        else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n))
        {
            limit = n;
        }
        else
        {
            _board.ShowBanner(Messages.WipLimitMustBePositive);
            return;
        }

        if (await _board.SetWipLimitAsync(this, limit)) IsEditingWip = false;
    }

    [RelayCommand]
    private void CancelEditWip() => IsEditingWip = false;

    [RelayCommand]
    private Task ClearWipAsync() => _board.SetWipLimitAsync(this, null);

    [RelayCommand]
    private Task DeleteColumnAsync() => _board.DeleteColumnAsync(this);
}
