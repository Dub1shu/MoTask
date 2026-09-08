using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Ai;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// 朝の実行プラン画面（仕様 §6・§7）。実行の状態と候補キューを持ち、左パネル（LeftPanel）を
/// 状態で切り替える。編集フォームと 4 アクションは TriagePanelViewModel（Triage）が持つ。
/// RunChanged はワーカースレッドから来るので UI スレッドへ載せ替える（BoardViewModel と同じ）。
/// </summary>
public sealed partial class MorningPlanViewModel : ObservableObject
{
    /// <summary>
    /// 画面が持つ「今の実行」の複製。MorningService が返す MorningRun / MorningRunSnapshot から
    /// 必要な分だけ写し取る。DbContext が追跡するエンティティ(GetCurrentRunAsync / StartAsync の
    /// 戻り値)への参照はここでは絶対に保持しない。追従スレッドから届く MorningRunChangedEventArgs
    /// は非同期に(SynchronizationContext.Post 経由で)届くので、その時点で実行がすでに
    /// Cancelled / Ingested になっていることがある。そのタイミングでも、追跡中のエンティティへ
    /// 書き戻すと(共有 DbContext の) 次の SaveChangesAsync が古い状態を復活させてしまうため。
    /// </summary>
    private sealed record RunState(
        int Id, DateOnly Date, MorningRunStatus Status, string? ErrorMessage, string JobFolder, bool HasPlan)
    {
        public static RunState From(MorningRun run) => new(
            run.Id, run.Date, run.Status, run.ErrorMessage, run.JobFolder,
            run.Status == MorningRunStatus.Ingested);

        public static RunState From(MorningRunSnapshot snapshot) => new(
            snapshot.RunId, snapshot.Date, snapshot.Status, snapshot.ErrorMessage, snapshot.JobFolder,
            snapshot.Status == MorningRunStatus.Ingested);
    }

    private readonly IMorningService _service;
    private readonly IBoardService _boardService;
    private readonly SynchronizationContext? _ui;
    private RunState? _run;

    /// <summary>リンクや成果物を開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();

    /// <summary>左パネル・状態 1。生成は 1 度だけで、Show で中身を差し替える。</summary>
    public TriagePanelViewModel Triage { get; }

    /// <summary>左パネルに出す VM。Triage か null（Task 5 で FirstThingViewModel が加わる）。</summary>
    [ObservableProperty] private object? _leftPanel;

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _hasNoCandidates;
    /// <summary>
    /// 「今日のプランはまだありません」を出すべきか。取り込み済み(Ingested)なら候補が 0 件でも
    /// この朝のプランは存在するので出さない(仕様 §4・§11)。CanStart とは目的が違う値なので
    /// 分けている(CanStart はボタンの活性、こちらは案内文の要否)。
    /// </summary>
    [ObservableProperty] private bool _hasNoPlanYet;
    /// <summary>プラン未生成のときの「前回: 9/5」（仕様 §11）。無ければ空文字。</summary>
    [ObservableProperty] private string _lastRunText = "";
    /// <summary>実行中の進捗。ターン数と直近のツール使用（仕様 §11）。</summary>
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _warningMessage;

    public MorningPlanViewModel(IMorningService service, IBoardService boardService)
    {
        _service = service;
        _boardService = boardService;
        _ui = SynchronizationContext.Current;
        Debug.Assert(_ui is not null || Application.Current is null,
            "MorningPlanViewModel は UI スレッドで生成すること。");

        Triage = new TriagePanelViewModel(service, AfterDecisionAsync, path => OpenPath(path));
        service.RunChanged += (_, e) => Post(() => OnRunChanged(e));
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public async Task LoadAsync()
    {
        var run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        _run = run is null ? null : RunState.From(run);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    /// <summary>
    /// 登録先の列（完了以外）と統合先のタスク（完了列と論理削除済み以外）を盤面から作る。
    /// 盤面の取得に失敗したら選択肢は空のままにして、画面そのものは出す。
    /// </summary>
    private async Task LoadBoardChoicesAsync()
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        if (!board.IsSuccess) return;
        var columns = board.Value!.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order).ToList();
        Triage.SetChoices(
            columns.Select(c => new ColumnChoice(c.Id, c.Name)),
            columns.SelectMany(c => c.Tasks.Where(t => !t.IsDeleted).OrderBy(t => t.Position)
                .Select(t => new TaskChoice(t.Id, t.Title, c.Name))));
    }

    private async Task ReloadQueueAsync()
    {
        await LoadBoardChoicesAsync().ConfigureAwait(true);
        var previous = Selected?.CandidateId;
        Candidates.Clear();
        if (_run is not null)
        {
            foreach (var candidate in await _service.GetQueueAsync(_run.Id).ConfigureAwait(true))
                Candidates.Add(new CandidateItemViewModel(candidate));
        }
        Select(Candidates.FirstOrDefault(c => c.CandidateId == previous) ?? Candidates.FirstOrDefault());
    }

    private void Select(CandidateItemViewModel? candidate)
    {
        Selected = candidate;
        Triage.Show(candidate, candidate is null ? 0 : Candidates.IndexOf(candidate), Candidates.Count);
        UpdateCounters();
    }

    /// <summary>候補キューで別の 1 件を選んだとき（ListBox の SelectedItem から）。</summary>
    partial void OnSelectedChanged(CandidateItemViewModel? value)
    {
        if (!ReferenceEquals(Triage.Selected, value))
            Triage.Show(value, value is null ? 0 : Candidates.IndexOf(value), Candidates.Count);
    }

    private void UpdateCounters()
    {
        var ingested = _run is { Status: MorningRunStatus.Ingested };
        HasNoCandidates = Candidates.Count == 0 && ingested;
        CanStart = _run is null || (_run.Status.IsTerminal() && Candidates.Count == 0);
        IsRunning = _run is not null && _run.Status.IsActive();
        IsFailed = _run is { Status: MorningRunStatus.Failed };
        CanControl = IsRunning;
        ProgressText = IsRunning ? string.Format(Strings.MorningTurnsFormat, _service.TurnCountOf(_run!.Id)) : "";
        // 「今日のプランはまだありません（前回: 9/5）」（仕様 §11）。取り込み済み(Ingested)なら
        // 候補が 0 件でもこの朝のプランは存在するので出さない（仕様 §4）。実行中も
        // 「実行中」表示と重ねて出さない。
        HasNoPlanYet = !IsRunning && _run is not { HasPlan: true };
        LastRunText = _run is null || !CanStart
            ? ""
            : string.Format(Strings.MorningLastRunFormat, _run.Date.ToString("M/d", CultureInfo.InvariantCulture));
        ErrorMessage = IsFailed ? _run!.ErrorMessage : ErrorMessage;
        LeftPanel = ingested && Candidates.Count > 0 ? Triage : null;
    }

    private void OnRunChanged(MorningRunChangedEventArgs e)
    {
        if (_run is not null && _run.Id != e.Run.RunId) return;
        WarningMessage = e.Warning ?? WarningMessage;
        if (_run is not null)
        {
            // 実行の状態遷移は Pending/Running → 終了状態の一方向で、終了状態から後戻りする遷移は
            // 無い(MorningService)。なので、こちらが既に終了状態を知っているのに違う状態の
            // スナップショットが届いたら、それは配送が追い越された古い通知でしかない。無視する
            // (終了状態のまま維持する)ことで、蘇ったように見せない。
            if (!_run.Status.IsTerminal())
            {
                // 複製のフィールドを更新するだけで、リポジトリが返したオブジェクトには一切触れない
                // (仕様どおり、追跡中のエンティティへ書き戻さない)。
                _run = _run with
                {
                    Status = e.Run.Status, ErrorMessage = e.Run.ErrorMessage,
                    HasPlan = e.Run.Status == MorningRunStatus.Ingested,
                };
            }
        }
        else if (e.Run.Status.IsActive())
        {
            // 開始直後の 1 通目より先にフックの行が届くことがある。実行を知らないまま捨てない。
            _run = RunState.From(e.Run);
        }
        UpdateCounters();
        if (e.CandidatesChanged) PendingLoad = RefreshAsync();
    }

    /// <summary>
    /// 候補が動いた後の読み直し。ここで投げた例外は誰も待っていない Task(PendingLoad はテスト
    /// 専用のハンドル)の中で握りつぶされてしまうので、ここで捕まえて警告バナーへ回す
    /// (MainWindow.OnLoaded が起動失敗をバナーへ回すのと同じ考え方)。
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            var run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
            _run = run is null ? null : RunState.From(run);
            await ReloadQueueAsync().ConfigureAwait(true);
            UpdateCounters();
        }
        catch (Exception ex)
        {
            WarningMessage = string.Format(CultureInfo.CurrentCulture, Strings.MorningRefreshFailedFormat, ex.Message);
        }
    }

    // ---------- 操作 ----------

    /// <summary>
    /// 何か操作を始める前にバナーを消す。古いエラー・警告は今回の操作の結果ではないので、
    /// 成功すれば消えているべきで、居座らせない（失敗・警告が出るならこの後で改めて立つ）。
    /// </summary>
    private void ClearBanners()
    {
        ErrorMessage = null;
        WarningMessage = null;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ClearBanners();
        var started = await _service.StartAsync().ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            ErrorMessage = started.Error;
            return;
        }
        _run = RunState.From(started.Value!);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var done = await _service.CompleteAsync(_run.Id).ConfigureAwait(true);
        if (!done.IsSuccess) ErrorMessage = done.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var stopped = await _service.StopTrackingAsync(_run.Id).ConfigureAwait(true);
        if (!stopped.IsSuccess) ErrorMessage = stopped.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Triage の 4 アクションが終わった後。片づいたら次の 1 件へ。失敗したらキューはそのままで理由だけ出す。
    /// 警告は結果から無条件に写す: 今回は無ければ null にして、前回の警告を居座らせない。
    /// バナーは操作の前に消す（ClearBanners と同じ意図。Triage は画面のバナーを知らない）。
    /// </summary>
    private async Task AfterDecisionAsync(Result result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        ErrorMessage = null;
        WarningMessage = result.Warnings.Count > 0 ? string.Join(" / ", result.Warnings) : null;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private void OpenJobFolder()
    {
        if (_run is { JobFolder.Length: > 0 } run) OpenPath(run.JobFolder);
    }

    [RelayCommand]
    private void DismissBanner()
    {
        ErrorMessage = null;
        WarningMessage = null;
    }
}

/// <summary>登録先の列の選択肢。</summary>
public sealed record ColumnChoice(int Id, string Name);
