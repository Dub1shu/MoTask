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
/// 朝の実行プラン画面（仕様 §11）。この計画では候補一覧と 4 アクションまで。
/// ワイヤー 4a / 4b の左パネルのモード切替とプランの 4 区分は 2 本目の計画で作る。
/// RunChanged はワーカースレッドから来るので UI スレッドへ載せ替える（BoardViewModel と同じ）。
/// </summary>
public sealed partial class MorningPlanViewModel : ObservableObject
{
    private readonly IMorningService _service;
    private readonly IBoardService _boardService;
    private readonly SynchronizationContext? _ui;
    private MorningRun? _run;

    /// <summary>リンクや成果物を開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();

    /// <summary>登録先に選べる列。完了列は選ばせない。LoadAsync で埋める。</summary>
    public ObservableCollection<ColumnChoice> ColumnChoices { get; } = new();

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _hasNoCandidates;
    [ObservableProperty] private string _headingText = "";
    [ObservableProperty] private string _positionText = "";
    /// <summary>プラン未生成のときの「前回: 9/5」（仕様 §11）。無ければ空文字。</summary>
    [ObservableProperty] private string _lastRunText = "";
    /// <summary>実行中の進捗。ターン数と直近のツール使用（仕様 §11）。</summary>
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _warningMessage;

    // 編集フォーム。期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）。
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editProjectName = "";
    [ObservableProperty] private int _editColumnId;

    public MorningPlanViewModel(IMorningService service, IBoardService boardService)
    {
        _service = service;
        _boardService = boardService;
        _ui = SynchronizationContext.Current;
        Debug.Assert(_ui is not null || Application.Current is null,
            "MorningPlanViewModel は UI スレッドで生成すること。");

        service.RunChanged += (_, e) => Post(() => OnRunChanged(e));
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public async Task LoadAsync()
    {
        await LoadColumnChoicesAsync().ConfigureAwait(true);
        _run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        await ReloadQueueAsync().ConfigureAwait(true);
        ApplyRunState();
    }

    /// <summary>
    /// 登録先の選択肢。完了列は選ばせない（仕様 §11）。
    /// 盤面の取得に失敗したら選択肢は空のままにして、画面そのものは出す。
    /// </summary>
    private async Task LoadColumnChoicesAsync()
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        ColumnChoices.Clear();
        if (!board.IsSuccess) return;
        foreach (var column in board.Value!.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order))
            ColumnChoices.Add(new ColumnChoice(column.Id, column.Name));
        if (EditColumnId == 0 && ColumnChoices.Count > 0) EditColumnId = ColumnChoices[0].Id;
    }

    private async Task ReloadQueueAsync()
    {
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
        EditTitle = candidate?.Title ?? "";
        EditDueDate = candidate?.SuggestedDueDate?.ToDateTime(TimeOnly.MinValue);
        EditProjectName = candidate?.SuggestedProject ?? "";
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        HeadingText = Strings.MorningTriageHeading;
        PositionText = Selected is null
            ? ""
            : string.Format(Strings.MorningPositionFormat, Candidates.IndexOf(Selected) + 1, Candidates.Count);
        HasNoCandidates = Candidates.Count == 0 && _run is { Status: MorningRunStatus.Ingested };
        CanStart = _run is null || (_run.Status.IsTerminal() && Candidates.Count == 0);
        IsRunning = _run is not null && _run.Status.IsActive();
        IsFailed = _run is { Status: MorningRunStatus.Failed };
        CanControl = IsRunning;
        ProgressText = IsRunning ? string.Format(Strings.MorningTurnsFormat, _service.TurnCountOf(_run!.Id)) : "";
        // 「今日のプランはまだありません（前回: 9/5）」（仕様 §11）
        LastRunText = _run is null || !CanStart
            ? ""
            : string.Format(Strings.MorningLastRunFormat, _run.Date.ToString("M/d", CultureInfo.InvariantCulture));
        ErrorMessage = IsFailed ? _run!.ErrorMessage : ErrorMessage;
    }

    private void ApplyRunState() => UpdateCounters();

    private void OnRunChanged(MorningRunChangedEventArgs e)
    {
        if (_run is not null && _run.Id != e.Run.RunId) return;
        WarningMessage = e.Warning ?? WarningMessage;
        if (_run is not null)
        {
            _run.Status = e.Run.Status;
            _run.ErrorMessage = e.Run.ErrorMessage;
        }
        else if (e.Run.Status.IsActive())
        {
            // 開始直後の 1 通目より先にフックの行が届くことがある。実行を知らないまま捨てない。
            _run = new MorningRun
            {
                Id = e.Run.RunId, Date = e.Run.Date, Status = e.Run.Status,
                ErrorMessage = e.Run.ErrorMessage, JobFolder = e.Run.JobFolder,
            };
        }
        UpdateCounters();
        if (e.CandidatesChanged) PendingLoad = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    // ---------- 操作 ----------

    [RelayCommand]
    private async Task StartAsync()
    {
        ErrorMessage = null;
        WarningMessage = null;
        var started = await _service.StartAsync().ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            ErrorMessage = started.Error;
            return;
        }
        _run = started.Value;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_run is null) return;
        var done = await _service.CompleteAsync(_run.Id).ConfigureAwait(true);
        if (!done.IsSuccess) ErrorMessage = done.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_run is null) return;
        var stopped = await _service.StopTrackingAsync(_run.Id).ConfigureAwait(true);
        if (!stopped.IsSuccess) ErrorMessage = stopped.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        var due = EditDueDate is DateTime date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var registered = await _service.RegisterAsync(new CandidateDecision(
            Selected.CandidateId, EditTitle, due, EditProjectName, EditColumnId)).ConfigureAwait(true);
        await AfterDecisionAsync(registered).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Selected?.SuggestedMergeTaskId is not int target) return;
        ErrorMessage = null;
        var merged = await _service.MergeAsync(Selected.CandidateId, target).ConfigureAwait(true);
        await AfterDecisionAsync(merged).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PostponeAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        await AfterDecisionAsync(await _service.PostponeAsync(Selected.CandidateId).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        await AfterDecisionAsync(await _service.RejectAsync(Selected.CandidateId).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    /// <summary>片づいたら次の 1 件へ。失敗したらキューはそのままで理由だけ出す。</summary>
    private async Task AfterDecisionAsync(Result result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        if (result.Warnings.Count > 0) WarningMessage = string.Join(" / ", result.Warnings);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Selected is { HasLink: true } candidate) OpenPath(candidate.Link);
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
