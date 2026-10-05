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
using MoTask.Core.Planning;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// 計画画面（仕様 §6・§7）。実行の状態と候補キューを持ち、左パネル（LeftPanel）を
/// 状態で切り替える。編集フォームと 4 アクションは TriagePanelViewModel（Triage）が持つ。
/// RunChanged はワーカースレッドから来るので UI スレッドへ載せ替える（BoardViewModel と同じ）。
/// </summary>
public sealed partial class PlanViewModel : ObservableObject
{
    /// <summary>
    /// 画面が持つ「今の実行」の複製。PlanningService が返す PlanningRun / PlanningRunSnapshot から
    /// 必要な分だけ写し取る。DbContext が追跡するエンティティ(GetCurrentRunAsync / StartAsync の
    /// 戻り値)への参照はここでは絶対に保持しない。追従スレッドから届く PlanningRunChangedEventArgs
    /// は非同期に(SynchronizationContext.Post 経由で)届くので、その時点で実行がすでに
    /// Cancelled / Ingested になっていることがある。そのタイミングでも、追跡中のエンティティへ
    /// 書き戻すと(共有 DbContext の) 次の SaveChangesAsync が古い状態を復活させてしまうため。
    /// </summary>
    private sealed record RunState(
        int Id, DateOnly Date, PlanningRunStatus Status, string? ErrorMessage, string JobFolder, bool HasPlan, string PlanJson)
    {
        public static RunState From(PlanningRun run) => new(
            run.Id, run.Date, run.Status, run.ErrorMessage, run.JobFolder,
            run.Status == PlanningRunStatus.Ingested, run.PlanJson);

        public static RunState From(PlanningRunSnapshot snapshot) => new(
            snapshot.RunId, snapshot.Date, snapshot.Status, snapshot.ErrorMessage, snapshot.JobFolder,
            snapshot.Status == PlanningRunStatus.Ingested, "");
    }

    private readonly IPlanningService _service;
    private readonly IBoardService _boardService;
    private readonly SynchronizationContext? _ui;
    private RunState? _run;

    /// <summary>重なった <see cref="ReloadQueueAsync"/> の世代。古い方は await から戻った時点で降りる
    /// (BoardViewModel.ReloadAsync と同じ手筋)。AfterDecisionAsync 自身の読み直しと、
    /// PlanningService.RunChanged 経由の読み直し(OnRunChanged→RefreshAsync)が同じ FIFO
    /// OperationGate 上で重なりうるので、これが無いと Candidates が二重に積まれる。</summary>
    private int _reloadGeneration;

    /// <summary>リンクや成果物を開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

    /// <summary>「推奨をまとめて適用」の確認。既定は MessageBox、テストでは差し替える（OpenPath と同じ流儀）。</summary>
    public Func<string, bool> Confirm { get; set; } = message =>
        MessageBox.Show(message, Strings.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();

    /// <summary>左パネル・状態 1。生成は 1 度だけで、Show で中身を差し替える。</summary>
    public TriagePanelViewModel Triage { get; }

    /// <summary>左パネル・状態 2。</summary>
    public FirstThingViewModel FirstThing { get; }

    /// <summary>右カラムの 4 区分。PlanGroupKey の順で固定。</summary>
    public IReadOnlyList<PlanSectionViewModel> Sections { get; }

    /// <summary>ボードへ飛ぶ（「ボードで開く」とタスク行のクリック）。MainWindow が購読する。</summary>
    public event EventHandler<int>? NavigateToTask;

    private Board? _board;
    private IReadOnlyList<Project> _projects = Array.Empty<Project>();
    private TriageSummary _summary = TriageSummary.None;

    /// <summary>左パネルに出す VM。Triage か FirstThing か null。</summary>
    [ObservableProperty] private object? _leftPanel;

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _hasNoCandidates;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PostponeAllCommand))]
    private bool _isTriaging;
    [ObservableProperty] private bool _isPlanReady;
    [ObservableProperty] private string _dateHeading = "";
    [ObservableProperty] private string _statusLine = "";
    /// <summary>右カラムの「01 ／ 最初にやる1件」。仕分け中と繰り下げは「（暫定）」を付ける。</summary>
    [ObservableProperty] private string _firstThingHeadingText = "";
    /// <summary>タブのバッジ。候補キューの件数。</summary>
    [ObservableProperty] private int _pendingCount;
    /// <summary>タブのバッジを出すか。0 件のときは出さない。</summary>
    [ObservableProperty] private bool _hasPendingCandidates;
    /// <summary>
    /// 「今日の計画はまだありません」を出すべきか。取り込み済み(Ingested)なら候補が 0 件でも
    /// この計画は存在するので出さない(仕様 §4・§11)。CanStart とは目的が違う値なので
    /// 分けている(CanStart はボタンの活性、こちらは案内文の要否)。
    /// </summary>
    [ObservableProperty] private bool _hasNoPlanYet;
    /// <summary>
    /// 右カラム(候補キュー＋計画)を出すか。仕様 §7: 実行前・実行中・失敗のときは右カラムを空にする。
    /// HasNoPlanYet は取り込み済みなら候補 0 件でも false になる値なので、実行中はそれだけでは
    /// 右カラムを隠せない(finding I3)。ここは「見せる」側の値として持つ。
    /// </summary>
    [ObservableProperty] private bool _hasPlanView;
    /// <summary>計画未生成のときの「前回: 9/5」（仕様 §11）。無ければ空文字。</summary>
    [ObservableProperty] private string _lastRunText = "";
    /// <summary>実行中の進捗。ターン数と直近のツール使用（仕様 §11）。</summary>
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _warningMessage;

    public PlanViewModel(IPlanningService service, IBoardService boardService)
    {
        _service = service;
        _boardService = boardService;
        _ui = SynchronizationContext.Current;
        Debug.Assert(_ui is not null || Application.Current is null,
            "PlanViewModel は UI スレッドで生成すること。");

        Triage = new TriagePanelViewModel(service, AfterDecisionAsync, path => OpenPath(path));
        FirstThing = new FirstThingViewModel(id => NavigateToTask?.Invoke(this, id));
        Sections = Enum.GetValues<PlanGroupKey>().Select(key => new PlanSectionViewModel(key, OpenRow)).ToList();
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
    /// 盤面・プロジェクト・ラベルを読み、登録先の列（完了以外）と統合先のタスク（完了列と論理削除済み以外）と
    /// 付けられるラベル（アーカイブ済み以外）を Triage に渡す。盤面の取得に失敗したら理由を警告に出し、盤面なしで続ける（候補の仕分けは
    /// 盤面が無くても動く・仕様 §8）。generation は呼び出し元(ReloadQueueAsync)の世代。await から
    /// 戻るたびに確かめ、その間に新しい読み直しが始まっていたら以降のフィールド書き換えをやめる
    /// (このメソッドは ReloadQueueAsync からしか呼ばない)。
    /// </summary>
    private async Task LoadBoardAsync(int generation)
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        if (generation != _reloadGeneration) return;
        if (!board.IsSuccess)
        {
            _board = null;
            WarningMessage = board.Error;
            Triage.SetChoices(Array.Empty<ColumnChoice>(), Array.Empty<TaskChoice>(), Array.Empty<Label>());
            return;
        }
        _board = board.Value!;
        _projects = await _boardService.GetProjectsAsync().ConfigureAwait(true);
        if (generation != _reloadGeneration) return;
        var labels = await _boardService.GetLabelsAsync().ConfigureAwait(true);
        if (generation != _reloadGeneration) return;
        var columns = _board.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order).ToList();
        Triage.SetChoices(
            columns.Select(c => new ColumnChoice(c.Id, c.Name)),
            columns.SelectMany(c => c.Tasks.Where(t => !t.IsDeleted).OrderBy(t => t.Position)
                .Select(t => new TaskChoice(t.Id, t.Title, c.Name))),
            labels.Where(l => !l.Archived));
    }

    private string? ProjectName(int? projectId)
        => projectId is int id ? _projects.FirstOrDefault(p => p.Id == id)?.Name : null;

    /// <summary>
    /// 候補キューと計画の読み直し。AfterDecisionAsync の読み直しと、PlanningService.RunChanged
    /// (candidatesChanged) 経由の読み直し(OnRunChanged→RefreshAsync)が同じ 1 件の仕分けの後に
    /// どちらも走り、FIFO の OperationGate 上で重なりうる。素朴に Clear() してから
    /// await で問い合わせると、2 つの読み直しの Clear と Add が入り乱れて候補が二重に積まれるので、
    /// (a) まずキューを取得してから Clear と Add をまとめて(await を挟まず)行い、(b) 世代番号で
    /// 古い方の読み直しが新しい方の結果を上書きしないようにする(BoardViewModel.ReloadAsync と同じ手筋)。
    /// </summary>
    private async Task ReloadQueueAsync()
    {
        var generation = ++_reloadGeneration;
        await LoadBoardAsync(generation).ConfigureAwait(true);
        if (generation != _reloadGeneration) return;

        var queue = _run is null
            ? Array.Empty<TriageCandidate>()
            : await _service.GetQueueAsync(_run.Id).ConfigureAwait(true);
        if (generation != _reloadGeneration) return;

        var previous = Selected?.CandidateId;
        Candidates.Clear();
        foreach (var candidate in queue) Candidates.Add(new CandidateItemViewModel(candidate));

        await ResolvePlanAsync(generation).ConfigureAwait(true);
        if (generation != _reloadGeneration) return;

        Select(Candidates.FirstOrDefault(c => c.CandidateId == previous) ?? Candidates.FirstOrDefault());
    }

    /// <summary>
    /// PlanJson を行に解決する（仕様 §4・§6）。仕分けの 1 件ごとに呼ばれるので、登録した候補の行は
    /// その場で実タスクに変わり、却下した行は消える。取り込み前は空の計画。generation の扱いは
    /// LoadBoardAsync と同じ(ReloadQueueAsync からしか呼ばない)。
    /// </summary>
    private async Task ResolvePlanAsync(int generation)
    {
        ResolvedPlan plan;
        if (_run is { Status: PlanningRunStatus.Ingested } run)
        {
            var candidates = await _service.GetCandidatesOfRunAsync(run.Id).ConfigureAwait(true);
            if (generation != _reloadGeneration) return;
            plan = PlanResolver.Resolve(run.PlanJson, candidates, _board, ProjectName);
        }
        else
        {
            plan = ResolvedPlan.Empty(TriageSummary.None);
        }
        _summary = plan.Summary;
        FirstThing.Update(plan);
        for (var i = 0; i < Sections.Count; i++) Sections[i].Update(plan.Groups[i]);
    }

    /// <summary>タスク行はボードへ、候補行は候補キューの選択へ。</summary>
    private void OpenRow(PlanRowViewModel row)
    {
        if (row.TaskId is int taskId) NavigateToTask?.Invoke(this, taskId);
        else if (row.CandidateId is int candidateId)
            Select(Candidates.FirstOrDefault(c => c.CandidateId == candidateId) ?? Selected);
    }

    private void Select(CandidateItemViewModel? candidate)
    {
        // Selected の代入が OnSelectedChanged を呼び、そこで Triage.Show する(候補の参照は読み直しの
        // たびに新しく作り直すので、ここで選ぶインスタンスは常に前と違う参照になり、確実に呼ばれる)。
        // ここで重ねて Show を呼ぶと同じ内容を 2 回描き直すだけなので呼ばない。
        Selected = candidate;
        UpdateCounters();
    }

    /// <summary>
    /// Selected が変わったとき。候補キューの ListBox の SelectedItem からと、Select() からの
    /// どちらでも呼ばれる(Selected プロパティの代入がトリガーなので)。参照が変わらない代入では
    /// 呼ばれない(値が同じなら PropertyChanged を上げない、CommunityToolkit.Mvvm の既定の挙動)。
    /// </summary>
    partial void OnSelectedChanged(CandidateItemViewModel? value)
        => Triage.Show(value, value is null ? 0 : Candidates.IndexOf(value), Candidates.Count);

    private void UpdateCounters()
    {
        var ingested = _run is { Status: PlanningRunStatus.Ingested };
        CanStart = _run is null || (_run.Status.IsTerminal() && Candidates.Count == 0);
        IsRunning = _run is not null && _run.Status.IsActive();
        IsFailed = _run is { Status: PlanningRunStatus.Failed };
        CanControl = IsRunning;
        ProgressText = IsRunning ? string.Format(Strings.PlanTurnsFormat, _service.TurnCountOf(_run!.Id)) : "";
        // 「今日の計画はまだありません（前回: 9/5）」（仕様 §11）。取り込み済み(Ingested)なら
        // 候補が 0 件でもこの計画は存在するので出さない（仕様 §4）。実行中も
        // 「実行中」表示と重ねて出さない。
        HasNoPlanYet = !IsRunning && _run is not { HasPlan: true };
        LastRunText = _run is null || !CanStart
            ? ""
            : string.Format(Strings.PlanLastRunFormat, _run.Date.ToString("M/d", CultureInfo.InvariantCulture));
        ErrorMessage = IsFailed ? _run!.ErrorMessage : ErrorMessage;
        HasNoCandidates = ingested && _summary.Total == 0 && Candidates.Count == 0;
        IsTriaging = ingested && Candidates.Count > 0;
        IsPlanReady = ingested && Candidates.Count == 0;
        LeftPanel = IsTriaging ? Triage : IsPlanReady ? FirstThing : null;
        HasPlanView = IsTriaging || IsPlanReady;
        PendingCount = Candidates.Count;
        HasPendingCandidates = Candidates.Count > 0;
        DateHeading = _run is null
            ? ""
            : string.Format(Strings.PlanDateHeadingFormat, _run.Date.ToString("M月d日（ddd）", new CultureInfo("ja-JP")));
        StatusLine = IsTriaging
            ? string.Format(Strings.PlanTriagingFormat, Candidates.Count)
            : IsPlanReady
                ? string.Format(Strings.PlanTriageDoneFormat,
                    _summary.Total, _summary.Registered, _summary.Merged, _summary.Rejected, _summary.Later)
                : "";
        FirstThingHeadingText = Strings.PlanFirstThingHeading
            + (IsTriaging || FirstThing.IsFallback ? Strings.PlanFirstThingProvisional : "");
    }

    private void OnRunChanged(PlanningRunChangedEventArgs e)
    {
        if (_run is not null && _run.Id != e.Run.RunId) return;
        WarningMessage = e.Warning ?? WarningMessage;
        var wasTerminal = _run is not null && _run.Status.IsTerminal();
        if (_run is not null)
        {
            // 実行の状態遷移は Pending/Running → 終了状態の一方向で、終了状態から後戻りする遷移は
            // 無い(PlanningService)。なので、こちらが既に終了状態を知っているのに違う状態の
            // スナップショットが届いたら、それは配送が追い越された古い通知でしかない。無視する
            // (終了状態のまま維持する)ことで、蘇ったように見せない。
            if (!_run.Status.IsTerminal())
            {
                // 複製のフィールドを更新するだけで、リポジトリが返したオブジェクトには一切触れない
                // (仕様どおり、追跡中のエンティティへ書き戻さない)。
                _run = _run with
                {
                    Status = e.Run.Status, ErrorMessage = e.Run.ErrorMessage,
                    HasPlan = e.Run.Status == PlanningRunStatus.Ingested,
                };
            }
        }
        else if (e.Run.Status.IsActive())
        {
            // 開始直後の 1 通目より先にフックの行が届くことがある。実行を知らないまま捨てない。
            _run = RunState.From(e.Run);
        }
        UpdateCounters();
        var becameIngested = !wasTerminal && e.Run.Status == PlanningRunStatus.Ingested;
        if (e.CandidatesChanged || becameIngested) PendingLoad = RefreshAsync();
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
            WarningMessage = string.Format(CultureInfo.CurrentCulture, Strings.PlanRefreshFailedFormat, ex.Message);
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
    /// バナーは操作の前ではなく、ここ(結果が分かった後)で書き換える。成功なら警告は結果から無条件に
    /// 写す(今回は無ければ null にして、前回の警告を居座らせない)。失敗ならエラーを出し、警告も
    /// 一緒に消す(前回の警告を新しいエラーの隣に居座らせない。Triage は画面のバナーを知らない)。
    /// </summary>
    private async Task AfterDecisionAsync(Result result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            WarningMessage = null;
            return;
        }
        ErrorMessage = null;
        WarningMessage = result.Warnings.Count > 0 ? string.Join(" / ", result.Warnings) : null;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    // ---------- 一括（仕様 §6） ----------

    /// <summary>登録先は完了以外の先頭の列に固定し、確認の文言に明記する（仕様 §3）。</summary>
    [RelayCommand(CanExecute = nameof(IsTriaging))]
    private async Task ApplySuggestionsAsync()
    {
        if (_run is null) return;
        var column = Triage.ColumnChoices.FirstOrDefault();
        if (column is null)
        {
            ClearBanners();
            ErrorMessage = Strings.PlanBulkNoColumn;
            return;
        }
        if (!Confirm(string.Format(Strings.PlanApplyConfirmFormat, Candidates.Count, column.Name))) return;
        ClearBanners();
        var outcome = await _service.ApplySuggestionsAsync(_run.Id, column.Id).ConfigureAwait(true);
        await AfterBulkAsync(outcome).ConfigureAwait(true);
    }

    /// <summary>取り消しが容易なので確認なし（親仕様 §11）。</summary>
    [RelayCommand(CanExecute = nameof(IsTriaging))]
    private async Task PostponeAllAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var outcome = await _service.PostponeAllAsync(_run.Id).ConfigureAwait(true);
        await AfterBulkAsync(outcome).ConfigureAwait(true);
    }

    /// <summary>件数と見送り理由を 1 行で警告バナーへ。成功の警告（WIP 超過など）はその後ろに続ける。</summary>
    private async Task AfterBulkAsync(Result<BulkOutcome> result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        var outcome = result.Value!;
        var summary = outcome.Skipped.Count == 0
            ? string.Format(Strings.PlanBulkAppliedFormat, outcome.Applied)
            : string.Format(Strings.PlanBulkResultFormat, outcome.Applied, outcome.Skipped.Count, string.Join(" / ", outcome.Skipped));
        WarningMessage = result.Warnings.Count > 0 ? summary + " / " + string.Join(" / ", result.Warnings) : summary;
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
