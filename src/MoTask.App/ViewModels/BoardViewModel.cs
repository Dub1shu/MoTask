using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// ボード全体。操作は BoardService に即時コミットし、成功したら差分（Refresh）で表示を更新する。
/// 失敗したらバナーを出す。保存そのものに失敗したときだけ、メモリ上の状態が信用できないので
/// GetBoard で全体を読み直す。検証で却下されただけなら読み直さず、その場で巻き戻す。
/// </summary>
public sealed partial class BoardViewModel : ObservableObject
{
    private static readonly string[] LabelColors =
        { "accent-300", "accent-500", "accent-200", "accent-700", "accent-400" };

    private readonly IBoardService _service;
    private readonly IClock _clock;
    private Board? _board;
    private bool _syncingSelection;

    public ObservableCollection<ColumnViewModel> Columns { get; } = new();
    public FilterViewModel Filter { get; } = new();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<Label> Labels { get; private set; } = Array.Empty<Label>();

    [ObservableProperty] private TaskCardViewModel? _selectedCard;
    [ObservableProperty] private TaskDetailViewModel? _detail;
    [ObservableProperty] private string? _bannerMessage;
    [ObservableProperty] private bool _isAddingColumn;
    [ObservableProperty] private string _newColumnName = "";
    [ObservableProperty] private ColumnRoleOption _newColumnRole;
    [ObservableProperty] private bool _isLoaded;

    /// <summary>列を追加するときに選べる種別。Done 列は1つだけなので選ばせない（仕様 §5）。</summary>
    public IReadOnlyList<ColumnRoleOption> NewColumnRoles { get; } = new[]
    {
        new ColumnRoleOption(ColumnRole.Backlog, Strings.RoleBacklog),
        new ColumnRoleOption(ColumnRole.Active, Strings.RoleActive),
        new ColumnRoleOption(ColumnRole.Review, Strings.RoleReview),
    };

    public BoardViewModel(IBoardService service, IClock clock)
    {
        _service = service;
        _clock = clock;
        _newColumnRole = DefaultColumnRole();
        Filter.Changed += (_, _) => ApplyFilter();
    }

    /// <summary>追加する列の既定の種別は「進行中」。</summary>
    private ColumnRoleOption DefaultColumnRole()
        => NewColumnRoles.First(r => r.Value == ColumnRole.Active);

    public DateOnly Today => _clock.Today;

    // ---------- 読み込み ----------

    public Task LoadAsync() => ReloadAsync();

    /// <summary>起動時と、保存の失敗からの復帰時にだけ呼ぶ。</summary>
    public async Task ReloadAsync()
    {
        // 照会は例外を Result にしてくれないので、必ずここで包む（さもないと DB エラーでプロセスが落ちる）。
        var board = await GuardAsync(() => _service.GetBoardAsync());
        if (!board.IsSuccess)
        {
            ShowFailure(board);
            return;
        }
        var projects = await QueryAsync(() => _service.GetProjectsAsync());
        if (!projects.IsSuccess)
        {
            ShowFailure(projects);
            return;
        }
        var labels = await QueryAsync(() => _service.GetLabelsAsync());
        if (!labels.IsSuccess)
        {
            ShowFailure(labels);
            return;
        }

        _board = board.Value!;
        Projects = projects.Value!;
        Labels = labels.Value!;
        Filter.SetProjects(Projects);
        Filter.SetLabels(Labels);

        var selectedId = SelectedCard?.Id;
        Columns.Clear();
        foreach (var column in _board.Columns.OrderBy(c => c.Order))
        {
            var vm = new ColumnViewModel(column, this);
            vm.SyncCardsFromModel(ProjectName, Today);
            Columns.Add(vm);
        }
        ApplyFilter();
        SelectCard(selectedId is int id ? AllCards().FirstOrDefault(c => c.Id == id) : null);
        IsLoaded = true;
    }

    /// <summary>失敗したら空の履歴を返し、理由はバナーに出す。</summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId)
    {
        var history = await QueryAsync(() => _service.GetHistoryAsync(taskId));
        if (!history.IsSuccess)
        {
            ShowFailure(history);
            return Array.Empty<HistoryEntry>();
        }
        return history.Value!;
    }

    public string? ProjectName(int? projectId)
        => projectId is int id ? Projects.FirstOrDefault(p => p.Id == id)?.Name : null;

    public string ColumnName(int columnId)
        => _board?.Columns.FirstOrDefault(c => c.Id == columnId)?.Name ?? Strings.UnknownColumn;

    // ---------- 選択 ----------

    public void SelectCard(TaskCardViewModel? card)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            SelectedCard = card;
            foreach (var column in Columns)
            {
                column.SelectedCard = card is not null && column.AllCards.Contains(card) ? card : null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    partial void OnSelectedCardChanged(TaskCardViewModel? value)
    {
        Detail = value is null ? null : new TaskDetailViewModel(value, this);
    }

    [RelayCommand]
    private void CloseDetail() => SelectCard(null);

    [RelayCommand]
    private void DismissBanner() => BannerMessage = null;

    [RelayCommand]
    private void NewTask()
    {
        var column = (SelectedCard is { } card ? ColumnOf(card) : null) ?? Columns.FirstOrDefault();
        if (column is null) return;
        foreach (var other in Columns.Where(c => !ReferenceEquals(c, column))) other.IsAddingTask = false;
        column.BeginAddTaskCommand.Execute(null);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedCard is { IsDeleted: false } card) await DeleteTaskAsync(card);
    }

    // ---------- 列の追加 ----------

    [RelayCommand]
    private void BeginAddColumn()
    {
        NewColumnName = "";
        NewColumnRole = DefaultColumnRole();
        IsAddingColumn = true;
    }

    [RelayCommand]
    private async Task CommitAddColumnAsync()
    {
        if (string.IsNullOrWhiteSpace(NewColumnName)) return;
        var result = await _service.AddColumnAsync(NewColumnName, NewColumnRole.Value);
        if (!await HandleAsync(result)) return;
        IsAddingColumn = false;
        NewColumnName = "";
        var vm = new ColumnViewModel(result.Value!, this);
        vm.ApplyFilter(Filter.ToFilter(), Today);
        Columns.Add(vm);
    }

    [RelayCommand]
    private void CancelAddColumn() => IsAddingColumn = false;

    // ---------- タスク操作（ColumnViewModel / TaskDetailViewModel / D&D から呼ぶ） ----------

    public async Task<bool> CreateTaskAsync(ColumnViewModel column, string title)
    {
        var result = await _service.CreateTaskAsync(column.Id, title);
        if (!await HandleAsync(result)) return false;
        RefreshColumn(column);
        var card = column.AllCards.FirstOrDefault(c => c.Id == result.Value!.Id);
        if (card is not null) SelectCard(card);
        return true;
    }

    /// <param name="position">移動先列で、移動カードを除いた <see cref="ColumnViewModel.AllCards"/> 上の挿入位置。</param>
    public async Task<bool> MoveCardAsync(TaskCardViewModel card, ColumnViewModel target, int position)
    {
        var source = ColumnOf(card);
        if (source is null) return false;

        // 楽観的更新: 先に表示を動かす
        var wasSelected = ReferenceEquals(SelectedCard, card);
        source.AllCards.Remove(card);
        position = Math.Clamp(position, 0, target.AllCards.Count);
        target.AllCards.Insert(position, card);
        var filter = Filter.ToFilter();
        source.ApplyFilter(filter, Today);
        target.ApplyFilter(filter, Today);

        var result = await _service.MoveTaskAsync(card.Id, target.Id, position);
        if (!result.IsSuccess)
        {
            ShowFailure(result);
            // 保存に失敗したなら読み直しが巻き戻しを兼ねる。却下されただけならモデルは動いていないので、
            // 両列をモデルから組み直せば楽観的更新が消える。
            if (IsSaveFailure(result)) await ReloadAsync();
            else RollbackMove(source, target, card, wasSelected);
            return false;
        }

        RefreshColumn(source);
        if (!ReferenceEquals(source, target)) RefreshColumn(target);
        if (wasSelected) SelectCard(card);
        AfterTaskChanged();
        return true;
    }

    public Task<bool> DeleteTaskAsync(TaskCardViewModel card)
        => RunTaskChangeAsync(card, () => _service.DeleteTaskAsync(card.Id));

    public Task<bool> RestoreTaskAsync(TaskCardViewModel card)
        => RunTaskChangeAsync(card, () => _service.RestoreTaskAsync(card.Id));

    public Task<bool> UpdateTaskAsync(TaskCardViewModel card, TaskUpdate update)
        => RunTaskChangeAsync(card, () => _service.UpdateTaskAsync(update));

    public Task<bool> SetTaskLabelsAsync(TaskCardViewModel card, IReadOnlyCollection<int> labelIds)
        => RunTaskChangeAsync(card, () => _service.SetTaskLabelsAsync(card.Id, labelIds));

    public async Task<Project?> CreateProjectAsync(string name)
    {
        var result = await _service.CreateProjectAsync(name);
        if (!await HandleAsync(result)) return null;
        var projects = await QueryAsync(() => _service.GetProjectsAsync());
        if (!projects.IsSuccess)
        {
            ShowFailure(projects);
            return null;
        }
        Projects = projects.Value!;
        Filter.SetProjects(Projects);
        return result.Value;
    }

    public async Task<Label?> CreateLabelAsync(string name)
    {
        var color = LabelColors[Labels.Count % LabelColors.Length];
        var result = await _service.CreateLabelAsync(name, color);
        if (!await HandleAsync(result)) return null;
        var labels = await QueryAsync(() => _service.GetLabelsAsync());
        if (!labels.IsSuccess)
        {
            ShowFailure(labels);
            return null;
        }
        Labels = labels.Value!;
        Filter.SetLabels(Labels);
        return result.Value;
    }

    // ---------- 列操作 ----------

    public Task<bool> RenameColumnAsync(ColumnViewModel column, string name)
        => RunColumnChangeAsync(column, () => _service.RenameColumnAsync(column.Id, name));

    public Task<bool> SetColumnRoleAsync(ColumnViewModel column, ColumnRole role)
        => RunColumnChangeAsync(column, () => _service.SetColumnRoleAsync(column.Id, role));

    public Task<bool> SetWipLimitAsync(ColumnViewModel column, int? limit)
        => RunColumnChangeAsync(column, () => _service.SetWipLimitAsync(column.Id, limit));

    public async Task<bool> DeleteColumnAsync(ColumnViewModel column)
    {
        var result = await _service.DeleteColumnAsync(column.Id);
        if (!await HandleAsync(result)) return false;
        Columns.Remove(column);
        return true;
    }

    public async Task<bool> ReorderColumnsAsync(IReadOnlyList<ColumnViewModel> order)
    {
        // 楽観的更新
        for (var i = 0; i < order.Count; i++)
        {
            var current = Columns.IndexOf(order[i]);
            if (current >= 0 && current != i) Columns.Move(current, i);
        }
        var result = await _service.ReorderColumnsAsync(order.Select(c => c.Id).ToList());
        if (result.IsSuccess) return true;

        ShowFailure(result);
        if (IsSaveFailure(result)) await ReloadAsync();
        else RestoreColumnOrder();
        return false;
    }

    // ---------- 内部 ----------

    private async Task<bool> RunTaskChangeAsync(TaskCardViewModel card, Func<Task<Result>> operation)
    {
        var result = await operation();
        if (!await HandleAsync(result)) return false;
        var column = ColumnOf(card);
        if (column is not null)
        {
            card.Refresh(ProjectName, Today);
            column.RefreshHeader();
            column.ApplyFilter(Filter.ToFilter(), Today);
        }
        AfterTaskChanged();
        return true;
    }

    private async Task<bool> RunColumnChangeAsync(ColumnViewModel column, Func<Task<Result>> operation)
    {
        var result = await operation();
        if (!await HandleAsync(result)) return false;
        column.RefreshHeader();
        return true;
    }

    /// <summary>
    /// 失敗ならバナーを出す。保存に失敗したときだけ全体を読み直す。検証で却下されただけなら
    /// 入力途中の編集も列 VM もそのまま残す（仕様 §8: GetBoard は起動時と復帰時だけ）。
    /// WIP 超過は Warnings で来る成功なので、バナーには出さない。
    /// </summary>
    private async Task<bool> HandleAsync(Result result)
    {
        if (result.IsSuccess) return true;
        ShowFailure(result);
        if (IsSaveFailure(result)) await ReloadAsync();
        return false;
    }

    /// <summary>ViewModel 側で弾いた入力の理由をバナーに出す（サービスを呼ぶ前の拒否）。</summary>
    public void ShowBanner(string message) => BannerMessage = message;

    private void ShowFailure(Result result) => BannerMessage = result.Error;

    /// <summary>
    /// 保存の失敗か検証の却下かを見分ける。BoardService は PersistenceException を
    /// "<see cref="Messages.SaveFailed"/>: 詳細" に変換するので、その前置きで判別する。
    /// </summary>
    private static bool IsSaveFailure(Result result)
        => result.Error?.StartsWith(Messages.SaveFailed, StringComparison.Ordinal) == true;

    /// <summary>
    /// Result を返さない照会（履歴・プロジェクト・ラベル）を包む。これらは PersistenceException を
    /// 握らないので、素通しにすると DB エラーでバナーではなくプロセス終了になる（仕様 §8）。
    /// </summary>
    private static async Task<Result<T>> QueryAsync<T>(Func<Task<T>> query)
    {
        try
        {
            return Result.Ok(await query());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<T>($"{Messages.SaveFailed}: {ex.Message}");
        }
    }

    /// <summary>
    /// Result を返す照会を包む。BoardService が握るのは SaveChanges の PersistenceException だけなので、
    /// 読み取り段で出る生の例外（SqliteException など）は Result にならずそのまま抜けてくる。
    /// </summary>
    private static async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> query)
    {
        try
        {
            return await query();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<T>($"{Messages.SaveFailed}: {ex.Message}");
        }
    }

    /// <summary>楽観的に動かしたカードをモデルの並びへ戻す。</summary>
    private void RollbackMove(ColumnViewModel source, ColumnViewModel target, TaskCardViewModel card, bool wasSelected)
    {
        // 先に card を元の列へ戻す。SyncCardsFromModel が同じ VM を再利用する条件は
        // 「その列の AllCards に同じ id があり Model も同一インスタンス」なので、戻さずに組み直すと
        // source は id を作り直し target は落とし、card がどの列にも属さない孤児になる。
        target.AllCards.Remove(card);
        if (!source.AllCards.Contains(card)) source.AllCards.Add(card);

        RefreshColumn(source);
        if (!ReferenceEquals(source, target)) RefreshColumn(target);
        if (wasSelected) SelectCard(card);
    }

    /// <summary>楽観的に動かした列をモデルの Order へ戻す。</summary>
    private void RestoreColumnOrder()
    {
        if (_board is null) return;
        var ordered = _board.Columns
            .OrderBy(c => c.Order)
            .Select(c => Columns.FirstOrDefault(vm => vm.Id == c.Id))
            .OfType<ColumnViewModel>()
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Columns.IndexOf(ordered[i]);
            if (current >= 0 && current != i) Columns.Move(current, i);
        }
    }

    private void RefreshColumn(ColumnViewModel column)
    {
        column.SyncCardsFromModel(ProjectName, Today);
        column.RefreshHeader();
        column.ApplyFilter(Filter.ToFilter(), Today);
    }

    /// <summary>タスク変更後、開いている詳細パネルの表示をモデルの最新値へ合わせる。</summary>
    private void AfterTaskChanged() => Detail?.Refresh();

    private void ApplyFilter()
    {
        var filter = Filter.ToFilter();
        foreach (var column in Columns) column.ApplyFilter(filter, Today);
    }

    private IEnumerable<TaskCardViewModel> AllCards() => Columns.SelectMany(c => c.AllCards);

    private ColumnViewModel? ColumnOf(TaskCardViewModel card)
        => Columns.FirstOrDefault(c => c.AllCards.Contains(card));
}
