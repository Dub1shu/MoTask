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
    private readonly TaskCardViewModel _card;
    private readonly BoardViewModel _board;
    private AiJob? _job;

    [ObservableProperty] private bool _hasJob;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSuspended;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _costText;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _workingDirectory;

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

    /// <summary>最新ジョブとそのイベントを読み直す。</summary>
    public async Task LoadAsync()
    {
        var jobs = await _board.QueryAiJobsAsync(_card.Id);
        var latest = jobs.FirstOrDefault();
        Apply(latest);
        Log.Clear();
        Artifacts.Clear();
        ResultText = null;
        if (latest is null) return;

        var events = await _board.QueryAiEventsAsync(latest.Id);
        foreach (var e in events) Log.Add(AiJobEventFormatter.Format(e));
        foreach (var path in AiJobEventFormatter.ArtifactPaths(events)) Artifacts.Add(new ArtifactItem(path));
        ResultText = AiJobEventFormatter.ResultText(events);
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
            if (AiJobEventFormatter.ArtifactPathOf(ev) is { } path
                && !Artifacts.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Artifacts.Add(new ArtifactItem(path));
            }
            if (ev.Kind == AiJobEventKind.Result) ResultText = AiJobEventFormatter.ResultText(new[] { ev });
        }
        if (e.Job.Status.IsTerminal() || e.Job.Status == AiJobStatus.Suspended) PendingLoad = LoadAsync();
    }

    private void Apply(AiJob? job)
    {
        _job = job;
        if (job is null)
        {
            HasJob = false;
            IsActive = false;
            IsSuspended = false;
            StatusText = "";
            CostText = null;
            ErrorMessage = null;
            WorkingDirectory = null;
            CanStart = !_card.IsDeleted;
            return;
        }
        ApplySnapshot(new AiJobSnapshot(job.Id, job.TaskId, job.Kind, job.Status,
            job.NumTurns ?? _board.AiJobs.TurnCountOf(job.Id), job.TotalCostUsd, job.ErrorMessage, job.WorkingDirectory));
    }

    private void ApplySnapshot(AiJobSnapshot s)
    {
        HasJob = true;
        IsActive = s.Status.IsActive();
        IsSuspended = s.Status == AiJobStatus.Suspended;
        CanStart = !IsActive && !IsSuspended && !_card.IsDeleted;
        WorkingDirectory = s.WorkingDirectory;
        ErrorMessage = s.Status == AiJobStatus.Failed ? s.ErrorMessage : null;
        StatusText = s.Status switch
        {
            AiJobStatus.Running => s.Kind == AiJobKind.Research ? Strings.AiStatusResearching : Strings.AiStatusExecuting,
            AiJobStatus.AwaitingApproval => Strings.AiStatusAwaiting,
            AiJobStatus.Suspended => Strings.AiStatusSuspended,
            AiJobStatus.Succeeded => Strings.AiStatusSucceeded,
            AiJobStatus.Failed => Strings.AiStatusFailed,
            AiJobStatus.Cancelled => Strings.AiStatusCancelled,
            _ => Strings.AiStatusPending,
        };
        CostText = s.TotalCostUsd is decimal cost
            ? string.Format(CultureInfo.InvariantCulture, Strings.AiCostFormat, cost, s.TurnCount)
            : null;
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
        var description = string.IsNullOrWhiteSpace(_card.Model.Description) ? "（なし）" : _card.Model.Description;
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
    private async Task StopAsync()
    {
        if (_job is null) return;
        await _board.StopAiJobAsync(_job.Id);
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_job is null) return;
        if (await _board.ResumeAiJobAsync(_job.Id)) await LoadAsync();
    }

    [RelayCommand]
    private void OpenArtifact(ArtifactItem item) => _board.OpenPath(item.Path);

    [RelayCommand]
    private void OpenWorkingDirectory()
    {
        if (WorkingDirectory is { Length: > 0 } dir) _board.OpenPath(dir);
    }
}
