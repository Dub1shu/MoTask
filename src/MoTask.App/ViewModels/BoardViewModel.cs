using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Ai;
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
    private readonly SynchronizationContext? _ui;
    private Board? _board;
    private bool _syncingSelection;
    /// <summary>重なった <see cref="ReloadAsync"/> の世代。古い方は await から戻った時点で降りる。</summary>
    private int _reloadGeneration;

    public IAiJobService AiJobs { get; }

    /// <summary>成果物や作業フォルダを開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

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

    public BoardViewModel(IBoardService service, IClock clock, IAiJobService aiJobs, IBoardChangeSource externalChanges)
    {
        _service = service;
        _clock = clock;
        AiJobs = aiJobs;
        // 生成は UI スレッド（DI から MainWindow 経由）。JobChanged はワーカーから来るのでここへ戻す。
        _ui = SynchronizationContext.Current;
        // 文脈が無いのはテスト（Application も無い）だけのはず。実アプリで欠けていたら配線ミスで、
        // JobChanged がワーカースレッドのまま UI を触ることになる。起動時に気付けるようにする。
        Debug.Assert(_ui is not null || Application.Current is null,
            "BoardViewModel は UI スレッドで生成すること（SynchronizationContext.Current が null）。");
        _newColumnRole = DefaultColumnRole();
        Filter.Changed += (_, _) => ApplyFilter();
        aiJobs.JobChanged += (_, e) => Post(() => OnJobChanged(e));
        // MCP 経由の書き込みはこの ViewModel を通らないので、丸ごと読み直す。
        externalChanges.BoardChanged += (_, _) => Post(() => _ = ReloadAsync());
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    /// <summary>追加する列の既定の種別は「進行中」。</summary>
    private ColumnRoleOption DefaultColumnRole()
        => NewColumnRoles.First(r => r.Value == ColumnRole.Active);

    public DateOnly Today => _clock.Today;

    // ---------- 読み込み ----------

    public Task LoadAsync() => ReloadAsync();

    /// <summary>
    /// 起動時と、保存の失敗からの復帰時にだけ呼ぶ……はずだったが、外部変更（MCP 経由の書き込み）の
    /// 通知でも呼ばれるようになった。連続で発火しうるので、重なった実行が Columns を組み直す前に
    /// 世代番号で古い方を降ろす（TaskAiPanelViewModel.LoadAsync と同じ手筋）。
    /// </summary>
    public async Task ReloadAsync()
    {
        var generation = ++_reloadGeneration;
        // 照会は例外を Result にしてくれないので、必ずここで包む（さもないと DB エラーでプロセスが落ちる）。
        var board = await GuardAsync(() => _service.GetBoardAsync());
        if (generation != _reloadGeneration) return;
        if (!board.IsSuccess)
        {
            ShowFailure(board);
            return;
        }
        var projects = await QueryAsync(() => _service.GetProjectsAsync());
        if (generation != _reloadGeneration) return;
        if (!projects.IsSuccess)
        {
            ShowFailure(projects);
            return;
        }
        var labels = await QueryAsync(() => _service.GetLabelsAsync());
        if (generation != _reloadGeneration) return;
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
        await ApplyAiStatesAsync();
        if (generation != _reloadGeneration) return;
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

    // ---------- AI ジョブ ----------

    /// <summary>未完了ジョブ（Pending / Running / WaitingForInput）からカードのバッジを組み直す。</summary>
    private async Task ApplyAiStatesAsync()
    {
        var jobs = await QueryAsync(() => AiJobs.GetUnfinishedJobsAsync());
        if (!jobs.IsSuccess)
        {
            ShowFailure(jobs);
            return;
        }
        var byTask = jobs.Value!.GroupBy(j => j.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.Id).First());
        foreach (var card in AllCards())
        {
            card.SetAiState(byTask.TryGetValue(card.Id, out var job) ? Snapshot(job) : null);
        }
    }

    private AiJobSnapshot Snapshot(AiJob job)
        => new(job.Id, job.TaskId, job.Kind, job.Status, job.NumTurns ?? AiJobs.TurnCountOf(job.Id),
            job.ErrorMessage, job.WorkingDirectory, job.JobFolder);

    /// <summary>UI スレッドで呼ばれる。バッジ・バナー・詳細パネル、完了時の列移動の反映。</summary>
    private void OnJobChanged(AiJobChangedEventArgs e)
    {
        var card = AllCards().FirstOrDefault(c => c.Id == e.Job.TaskId);
        card?.SetAiState(e.Job);
        if (e.Warning is not null) BannerMessage = e.Warning;
        Detail?.Ai.OnJobChanged(e);

        if (e.Job.Status == AiJobStatus.Succeeded)
        {
            // AiJobService が BoardService.MoveTask でタスクを Review 列へ動かした。モデルは動いているので表示を追従させる。
            var selected = SelectedCard;
            if (card is not null) ReattachMovedCard(card);
            foreach (var column in Columns) RefreshColumn(column);
            if (selected is not null) SelectCard(selected);
            RunGuarded(AfterTaskChangedAsync);
        }
    }

    /// <summary>
    /// モデルが別の列へ移ったカードの VM を、先に移動先列の <see cref="ColumnViewModel.AllCards"/> へ移す。
    /// <see cref="ColumnViewModel.SyncCardsFromModel"/> の VM 再利用は列ごとに閉じているので、これを
    /// 先にやらないと移動先が新しい VM を作る。すると開いている詳細パネルの <c>Card</c> がどの列にも
    /// 属さない孤児になり、<see cref="SelectCard"/> が全列の選択を外し、その後のバッジ更新も
    /// 見えないカードに書かれる。<see cref="MoveCardAsync"/> と同じ手順（VM を先に動かす）。
    /// 位置は直後の <see cref="RefreshColumn"/> が Position 順に組み直すので、ここでは末尾でよい。
    /// </summary>
    private void ReattachMovedCard(TaskCardViewModel card)
    {
        var target = Columns.FirstOrDefault(c => c.Id == card.Model.ColumnId);
        var source = ColumnOf(card);
        if (target is null || ReferenceEquals(source, target)) return;
        source?.AllCards.Remove(card);
        target.AllCards.Add(card);
    }

    public async Task<bool> StartAiJobAsync(TaskCardViewModel card, AiJobKind kind, string instruction)
    {
        var result = await GuardAsync(() => AiJobs.StartJobAsync(card.Id, kind, instruction));
        if (!await HandleAsync(result)) return false;
        card.SetAiState(Snapshot(result.Value!));
        await AfterTaskChangedAsync();
        return true;
    }

    /// <summary>端末を × で閉じてしまったジョブを、人の手で閉じる。</summary>
    public async Task<bool> CompleteAiJobAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.CompleteJobAsync(jobId)));

    /// <summary>追跡をやめる。端末のプロセスは殺さない。</summary>
    public async Task<bool> StopTrackingAiJobAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.StopTrackingAsync(jobId)));

    /// <summary>--resume で端末を開き直す。</summary>
    public async Task<bool> ReopenAiTerminalAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.ReopenTerminalAsync(jobId)));

    /// <summary>失敗しても空を返す（一覧が出ないだけ）。</summary>
    public async Task<IReadOnlyList<string>> QueryAiArtifactsAsync(int jobId)
    {
        var artifacts = await QueryAsync(() => AiJobs.GetArtifactsAsync(jobId));
        if (artifacts.IsSuccess) return artifacts.Value!;
        ShowFailure(artifacts);
        return Array.Empty<string>();
    }

    /// <summary>失敗したら空を返し、理由はバナーに出す（GetHistoryAsync と同じ流儀）。</summary>
    public async Task<IReadOnlyList<AiJob>> QueryAiJobsAsync(int taskId)
    {
        var jobs = await QueryAsync(() => AiJobs.GetJobsForTaskAsync(taskId));
        if (jobs.IsSuccess) return jobs.Value!;
        ShowFailure(jobs);
        return Array.Empty<AiJob>();
    }

    public async Task<IReadOnlyList<AiJobEvent>> QueryAiEventsAsync(int jobId, int lines)
    {
        var events = await QueryAsync(() => AiJobs.GetEventsAsync(jobId, lines));
        if (events.IsSuccess) return events.Value!;
        ShowFailure(events);
        return Array.Empty<AiJobEvent>();
    }

    public Task<bool> SetProjectWorkingDirectoryAsync(int projectId, string? path)
        => RunClassificationChangeAsync(() => _service.SetProjectWorkingDirectoryAsync(projectId, path));

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

    /// <summary>
    /// 計画の「ボードで開く」とタスク行のクリックから（仕様 §6）。フィルタで隠れていても
    /// 選択（と詳細パネル）は開く。盤面に無ければ何もしない（仕様 §8）。
    /// </summary>
    public void SelectTask(int taskId)
    {
        var card = Columns.SelectMany(c => c.AllCards).FirstOrDefault(c => c.Id == taskId);
        if (card is not null) SelectCard(card);
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
        var result = await GuardAsync(() => _service.AddColumnAsync(NewColumnName, NewColumnRole.Value));
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
        var result = await GuardAsync(() => _service.CreateTaskAsync(column.Id, title));
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

        var result = await GuardAsync(() => _service.MoveTaskAsync(card.Id, target.Id, position));
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
        await AfterTaskChangedAsync();
        return true;
    }

    /// <summary>完了列の末尾へ移す。カードの ✓ と詳細パネルの「完了にする」の共通の実体。</summary>
    public Task<bool> CompleteCardAsync(TaskCardViewModel card)
    {
        var done = Columns.FirstOrDefault(c => c.IsDone);
        // 削除済みは動かさない（Delete と同じ扱い）。既に完了列にいるなら並べ直さない。
        if (done is null || card.IsDeleted || done.AllCards.Contains(card)) return Task.FromResult(false);
        return MoveCardAsync(card, done, int.MaxValue);
    }

    /// <summary>カードの ✓ から。XAML は CommandParameter でカードを渡す。</summary>
    [RelayCommand]
    private async Task CompleteAsync(TaskCardViewModel? card)
    {
        if (card is not null) await CompleteCardAsync(card);
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
        var result = await GuardAsync(() => _service.CreateProjectAsync(name));
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
        var result = await GuardAsync(() => _service.CreateLabelAsync(name, color));
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

    public Task<bool> ArchiveProjectAsync(int projectId)
        => RunClassificationChangeAsync(() => _service.ArchiveProjectAsync(projectId));

    public Task<bool> UnarchiveProjectAsync(int projectId)
        => RunClassificationChangeAsync(() => _service.UnarchiveProjectAsync(projectId));

    public Task<bool> ArchiveLabelAsync(int labelId)
        => RunClassificationChangeAsync(() => _service.ArchiveLabelAsync(labelId));

    public Task<bool> UnarchiveLabelAsync(int labelId)
        => RunClassificationChangeAsync(() => _service.UnarchiveLabelAsync(labelId));

    /// <summary>
    /// プロジェクト・ラベルの一覧そのものを変える操作。タスクは動かないので履歴も再読み込みも要らないが、
    /// フィルタバーと、開いている詳細パネルの選択肢は入れ替える必要がある。
    /// </summary>
    private async Task<bool> RunClassificationChangeAsync(Func<Task<Result>> action)
    {
        var result = await GuardAsync(action);
        if (!await HandleAsync(result)) return false;
        return await ReloadClassificationsAsync();
    }

    /// <summary>プロジェクトとラベルを読み直し、フィルタバーと詳細パネルの選択肢へ反映する。</summary>
    private async Task<bool> ReloadClassificationsAsync()
    {
        var projects = await QueryAsync(() => _service.GetProjectsAsync());
        if (!projects.IsSuccess)
        {
            ShowFailure(projects);
            return false;
        }
        var labels = await QueryAsync(() => _service.GetLabelsAsync());
        if (!labels.IsSuccess)
        {
            ShowFailure(labels);
            return false;
        }

        Projects = projects.Value!;
        Labels = labels.Value!;
        Filter.SetProjects(Projects);
        Filter.SetLabels(Labels);
        // 絞り込みに使っていたラベルをアーカイブすると条件そのものが消えるので、表示を絞り直す
        ApplyFilter();
        Detail?.Refresh();
        return true;
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
        var result = await GuardAsync(() => _service.DeleteColumnAsync(column.Id));
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
        var result = await GuardAsync(() => _service.ReorderColumnsAsync(order.Select(c => c.Id).ToList()));
        if (result.IsSuccess) return true;

        ShowFailure(result);
        if (IsSaveFailure(result)) await ReloadAsync();
        else RestoreColumnOrder();
        return false;
    }

    // ---------- 内部 ----------

    private async Task<bool> RunTaskChangeAsync(TaskCardViewModel card, Func<Task<Result>> operation)
    {
        var result = await GuardAsync(operation);
        if (!await HandleAsync(result)) return false;
        var column = ColumnOf(card);
        // 削除も復元も列の Position を振り直す（削除は繰り上げ、復元は末尾送り）ので、
        // カード1枚を Refresh するだけでは AllCards の並びがモデルから外れる。外れると
        // 次のドロップ位置が AllCards 上で数えられてモデルの別の場所へ保存される（無言でずれる）。
        if (column is not null) RefreshColumn(column);
        await AfterTaskChangedAsync();
        return true;
    }

    private async Task<bool> RunColumnChangeAsync(ColumnViewModel column, Func<Task<Result>> operation)
    {
        var result = await GuardAsync(operation);
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
    /// Result を返す照会・更新を包む。BoardService が握るのは SaveChanges の PersistenceException だけなので、
    /// 読み取り段で出る生の例外（SqliteException など）は Result にならずそのまま抜けてくる。素通しすると
    /// AsyncRelayCommand の内部 async void まで届いてプロセスが落ちる（仕様 §8）。
    /// </summary>
    private static async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<T>($"{Messages.SaveFailed}: {ex.Message}");
        }
    }

    /// <summary>値を返さない更新用の <see cref="GuardAsync{T}"/>。</summary>
    private static async Task<Result> GuardAsync(Func<Task<Result>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
        }
    }

    /// <summary>
    /// 待てない同期の入口（D&amp;D の <c>IDropTarget.Drop</c> は Task を返せない）から非同期操作を始める。
    /// 単に discard すると失敗が誰にも観測されないので、ここで受けてバナーへ回す（仕様 §8）。
    /// </summary>
    public void RunGuarded(Func<Task> operation) => _ = ObserveAsync(operation);

    /// <summary>例外を投げないので、呼び出し側の discard が失敗を握りつぶすことにならない。</summary>
    private async Task ObserveAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // 取り消しは失敗ではない。誰も待っていないのでここで終わらせる。
        }
        catch (Exception ex)
        {
            BannerMessage = $"{Messages.SaveFailed}: {ex.Message}";
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

    /// <summary>
    /// タスク変更後、開いている詳細パネルの表示をモデルの最新値へ合わせる。
    /// 履歴も読み直すこと: <see cref="TaskDetailViewModel.Refresh"/> はモデルの各項目しか見ないので、
    /// これが無いと詳細パネル自身の操作でだけ履歴が伸び、カードをドラッグして動かしたり
    /// ボードで Delete したりしたときは古いまま残る。
    /// </summary>
    private async Task AfterTaskChangedAsync()
    {
        var detail = Detail;
        if (detail is null) return;
        detail.Refresh();
        await detail.LoadHistoryAsync();
    }

    private void ApplyFilter()
    {
        var filter = Filter.ToFilter();
        foreach (var column in Columns) column.ApplyFilter(filter, Today);
    }

    private IEnumerable<TaskCardViewModel> AllCards() => Columns.SelectMany(c => c.AllCards);

    private ColumnViewModel? ColumnOf(TaskCardViewModel card)
        => Columns.FirstOrDefault(c => c.AllCards.Contains(card));
}
