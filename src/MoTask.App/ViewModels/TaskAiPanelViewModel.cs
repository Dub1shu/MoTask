using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

public sealed record ArtifactItem(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 詳細パネルの AI セクション（仕様 §10）。タスクの最新ジョブ 1 件を表示する。
/// TaskDetailViewModel から分けているのは、あちらが既に 200 行あり、ここに AI の面倒まで足すと肥大するため。
/// JobChanged は BoardViewModel が UI スレッドへ載せ替えてから OnJobChanged に渡す。
/// </summary>
public sealed partial class TaskAiPanelViewModel : ObservableObject
{
    /// <summary>画面に残すログの行数。進行は端末で見えるので、直近だけ出す。</summary>
    private const int LogLines = 3;

    /// <summary>結果（最終回答）を探しに遡る行数。これより古い Stop は諦める。</summary>
    private const int ResultScanLines = 200;

    private readonly TaskCardViewModel _card;
    private readonly BoardViewModel _board;
    private AiJob? _job;
    /// <summary>重なった <see cref="LoadAsync"/> の世代。古い方は await から戻った時点で降りる。</summary>
    private int _loadGeneration;

    [ObservableProperty] private bool _hasJob;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isWaitingForInput;
    /// <summary>「開き直す」「完了にする」「追跡をやめる」を出してよいか（＝まだ追跡中か）。</summary>
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private string? _jobFolder;

    [ObservableProperty] private bool _isComposing;
    [ObservableProperty] private AiJobKind _composingKind;
    [ObservableProperty] private string _composingTitle = "";
    [ObservableProperty] private string _instruction = "";

    public ObservableCollection<AiLogLine> Log { get; } = new();
    public ObservableCollection<ArtifactItem> Artifacts { get; } = new();

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public int? CurrentJobId => _job?.Id;

    public TaskAiPanelViewModel(TaskCardViewModel card, BoardViewModel board)
    {
        _card = card;
        _board = board;
        Apply(null);
        PendingLoad = LoadAsync();
    }

    /// <summary>
    /// 最新ジョブとそのイベントを読み直す。5 か所から呼ばれ、Log.Clear() → await → Log.Add() の形なので、
    /// 重なると「A が消す・B が消す・A が足す・B が足す」でログが 2 倍になる。実際に重なる:
    /// AiJobService は StartJobAsync の中で同期的に JobChanged を上げるため、ConfirmStartAsync が
    /// まだ待っている間に OnJobChanged 側の読み込みが始まる。世代番号で古い方を降ろす。
    /// </summary>
    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        var jobs = await _board.QueryAiJobsAsync(_card.Id);
        if (generation != _loadGeneration) return;
        var latest = jobs.FirstOrDefault();
        Apply(latest);
        Log.Clear();
        Artifacts.Clear();
        ResultText = null;
        if (latest is null) return;

        // 結果は末尾 3 行の外にあることが多いので、広めに読んでから表示だけ絞る
        var events = await _board.QueryAiEventsAsync(latest.Id, ResultScanLines);
        if (generation != _loadGeneration) return;
        foreach (var e in events.TakeLast(LogLines)) Log.Add(AiJobEventFormatter.Format(e));
        ResultText = AiJobEventFormatter.ResultText(events);

        // 成果物はジョブフォルダの artifacts/ を見たサービスから来る（仕様 §6）
        var artifacts = await _board.QueryAiArtifactsAsync(latest.Id);
        if (generation != _loadGeneration) return;
        foreach (var path in artifacts) Artifacts.Add(new ArtifactItem(path));
    }

    /// <summary>BoardViewModel から（UI スレッドで）呼ばれる。</summary>
    public void OnJobChanged(AiJobChangedEventArgs e)
    {
        if (e.Job.TaskId != _card.Id) return;
        if (_job is null || _job.Id != e.Job.JobId)
        {
            // 新しいジョブが始まった（このパネル以外から、または再開）。全部読み直す。
            PendingLoad = LoadAsync();
            return;
        }

        ApplySnapshot(e.Job);
        if (e.NewEvent is { } ev)
        {
            Log.Add(AiJobEventFormatter.Format(ev));
            while (Log.Count > LogLines) Log.RemoveAt(0);
            // 成果物一覧は LoadAsync の読み直しで拾う（ToolUse からは組み立てない）
            if (ev.Kind == AiJobEventKind.TurnEnded) ResultText = AiJobEventFormatter.ResultText(new[] { ev }) ?? ResultText;
        }
        if (e.Job.Status.IsTerminal()) PendingLoad = LoadAsync();
    }

    private void Apply(AiJob? job)
    {
        _job = job;
        if (job is null)
        {
            HasJob = false;
            IsActive = false;
            IsWaitingForInput = false;
            CanControl = false;
            StatusText = "";
            ErrorMessage = null;
            WorkingDirectory = null;
            JobFolder = null;
            CanStart = !_card.IsDeleted;
            return;
        }
        ApplySnapshot(new AiJobSnapshot(job.Id, job.TaskId, job.Kind, job.Status,
            job.NumTurns ?? _board.AiJobs.TurnCountOf(job.Id), job.ErrorMessage, job.WorkingDirectory, job.JobFolder));
    }

    private void ApplySnapshot(AiJobSnapshot s)
    {
        HasJob = true;
        IsActive = s.Status.IsActive();
        IsWaitingForInput = s.Status == AiJobStatus.WaitingForInput;
        // 追跡中のジョブがある間は新しい依頼を受けない
        CanStart = !IsActive && !_card.IsDeleted;
        // 追跡中なら「開き直す」「完了にする」「追跡をやめる」が押せる
        CanControl = IsActive;
        WorkingDirectory = s.WorkingDirectory;
        JobFolder = s.JobFolder;
        ErrorMessage = s.Status == AiJobStatus.Failed ? s.ErrorMessage : null;
        StatusText = s.Status switch
        {
            AiJobStatus.Running => s.Kind == AiJobKind.Research ? Strings.AiStatusResearching : Strings.AiStatusExecuting,
            AiJobStatus.WaitingForInput => Strings.AiStatusWaitingForInput,
            AiJobStatus.Succeeded => Strings.AiStatusSucceeded,
            AiJobStatus.Failed => Strings.AiStatusFailed,
            AiJobStatus.Cancelled => Strings.AiStatusCancelled,
            _ => Strings.AiStatusPending,
        };
    }

    // ---- 依頼 ----

    [RelayCommand]
    private void BeginResearch() => BeginCompose(AiJobKind.Research);

    [RelayCommand]
    private void BeginExecute() => BeginCompose(AiJobKind.Execute);

    private void BeginCompose(AiJobKind kind)
    {
        ComposingKind = kind;
        ComposingTitle = kind == AiJobKind.Research ? Strings.AiComposeResearch : Strings.AiComposeExecute;
        var format = kind == AiJobKind.Research ? Strings.AiResearchInstructionFormat : Strings.AiExecuteInstructionFormat;
        // Instruction は編集用 TextBox に出るので、この穴埋めも人が読む文言になる。
        var description = string.IsNullOrWhiteSpace(_card.Model.Description) ? Strings.AiNoDescription : _card.Model.Description;
        Instruction = string.Format(CultureInfo.CurrentCulture, format, _card.Model.Title, description);
        IsComposing = true;
    }

    [RelayCommand]
    private async Task ConfirmStartAsync()
    {
        if (!await _board.StartAiJobAsync(_card, ComposingKind, Instruction)) return;
        IsComposing = false;
        await LoadAsync();
    }

    [RelayCommand]
    private void CancelCompose() => IsComposing = false;

    // ---- 制御 ----

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_job is null) return;
        if (await _board.CompleteAiJobAsync(_job.Id)) await LoadAsync();
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_job is null) return;
        if (await _board.StopTrackingAiJobAsync(_job.Id)) await LoadAsync();
    }

    [RelayCommand]
    private async Task ReopenTerminalAsync()
    {
        if (_job is null) return;
        await _board.ReopenAiTerminalAsync(_job.Id);
    }

    [RelayCommand]
    private void OpenArtifact(ArtifactItem item) => _board.OpenPath(item.Path);

    [RelayCommand]
    private void OpenWorkingDirectory()
    {
        if (WorkingDirectory is { Length: > 0 } dir) _board.OpenPath(dir);
    }

    [RelayCommand]
    private void OpenJobFolder()
    {
        if (JobFolder is { Length: > 0 } folder) _board.OpenPath(folder);
    }
}
