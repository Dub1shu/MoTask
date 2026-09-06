using System.Collections.Concurrent;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// AI ジョブのライフサイクル（仕様 §8）。MoTask はプロセスを所有せず、events.jsonl を読んで
/// 状態を写すだけ。DB は BoardService と共有の OperationGate で直列化する。ゲートの中から
/// IBoardService を呼ぶとデッドロックするので、完了時の列移動はゲートの外で呼ぶ。
/// JobChanged もゲートの外で上げる。
/// </summary>
public sealed class AiJobService : IAiJobService
{
    /// <summary>追跡中のジョブの数え。DB には持たない。</summary>
    private sealed class TrackedJob
    {
        public required int TaskId { get; init; }
        public int Seq { get; set; }
        public int Turns { get; set; }
    }

    private readonly IAiJobRepository _jobs;
    private readonly IBoardRepository _boards;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly OperationGate _gate;
    private readonly ISessionLauncher _launcher;
    private readonly IJobFolder _folder;
    private readonly IJobEventSource _events;
    private readonly IAiSettingsStore _settings;
    private readonly IBoardService _boardService;
    private readonly ConcurrentDictionary<int, TrackedJob> _tracked = new();

    public event EventHandler<AiJobChangedEventArgs>? JobChanged;

    public AiJobService(
        IAiJobRepository jobs, IBoardRepository boards, IHistoryRepository history, IUnitOfWork uow,
        IClock clock, OperationGate gate, ISessionLauncher launcher, IJobFolder folder,
        IJobEventSource events, IAiSettingsStore settings, IBoardService boardService)
    {
        _jobs = jobs;
        _boards = boards;
        _history = history;
        _uow = uow;
        _clock = clock;
        _gate = gate;
        _launcher = launcher;
        _folder = folder;
        _events = events;
        _settings = settings;
        _boardService = boardService;
    }

    // ---------- 照会 ----------

    public Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetForTaskAsync(taskId, ct), ct);

    public async Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, int lines, CancellationToken ct = default)
    {
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId, ct), ct).ConfigureAwait(false);
        if (job is null || job.JobFolder.Length == 0) return Array.Empty<AiJobEvent>();

        var tail = _folder.ReadTail(job.JobFolder, lines);
        var events = new List<AiJobEvent>(tail.Count);
        var seq = 0;
        foreach (var line in tail)
        {
            var parsed = HookEventParser.Parse(line);
            events.Add(new AiJobEvent
            {
                // フックの行には時刻が無いので、読んだ時刻（今）を実際の発生時刻として偽らない。
                // 表示側（AiJobEventFormatter）が At == default を「時刻なし」として扱う。
                JobId = jobId, Seq = ++seq, At = default,
                Kind = parsed.Kind, ToolName = parsed.ToolName, Payload = parsed.Payload,
            });
        }
        return events;
    }

    public Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetByStatusAsync(
            new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct), ct);

    public async Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId, ct), ct).ConfigureAwait(false);
        // 一覧はファイルシステムが真実。ToolUse からは拾わない（仕様 §6）。
        return job is null || job.JobFolder.Length == 0
            ? Array.Empty<string>()
            : _folder.ListArtifacts(job.JobFolder);
    }

    public int TurnCountOf(int jobId) => _tracked.TryGetValue(jobId, out var tracked) ? tracked.Turns : 0;

    // ---------- 開始 ----------

    public async Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default)
    {
        instruction = instruction.Trim();
        if (instruction.Length == 0) return Result.Fail<AiJob>(Messages.InstructionRequired);

        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<AiJob>(available.Error!);

        var settings = _settings.Load();
        var title = "";
        var created = await _gate.RunAsync(async () =>
        {
            try
            {
                var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
                if (task is null) return Result.Fail<AiJob>(Messages.TaskNotFound);
                if (task.IsDeleted) return Result.Fail<AiJob>(Messages.TaskDeletedCannotRunAi);
                // 追跡中かどうかは DB で数える。MoTask を閉じても端末は走り続けるので記憶に頼れない。
                var existing = await _jobs.GetForTaskAsync(taskId, ct).ConfigureAwait(false);
                if (existing.Any(j => !j.Status.IsTerminal())) return Result.Fail<AiJob>(Messages.TaskAlreadyHasActiveJob);

                var cwd = await ResolveWorkingDirectoryAsync(task, settings, ct).ConfigureAwait(false);
                if (!cwd.IsSuccess) return Result.Fail<AiJob>(cwd.Error!);

                var now = _clock.UtcNow;
                var job = new AiJob
                {
                    TaskId = task.Id, Kind = kind, Status = AiJobStatus.Pending, SessionId = Guid.NewGuid(),
                    Instruction = instruction, WorkingDirectory = cwd.Value!, StartedAt = now,
                };
                _jobs.Add(job);
                _history.Add(new HistoryEntry
                {
                    Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.AiJobStarted,
                    Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(kind, null)),
                });
                await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                title = task.Title;
                _tracked[job.Id] = new TrackedJob { TaskId = task.Id };
                return Result.Ok(job);
            }
            catch (PersistenceException ex)
            {
                return Result.Fail<AiJob>($"{Messages.SaveFailed}: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);

        if (!created.IsSuccess) return created;
        var job = created.Value!;

        // ここから先はファイル操作と端末の起動なので、ゲートの外でやる。
        var folder = _folder.Create(new JobFolderRequest(job.Id, title, instruction));
        if (!folder.IsSuccess) return await FailAsync(job, folder.Error!).ConfigureAwait(false);

        var command = _launcher.BuildCommand(
            new SessionLaunchRequest(job.SessionId, folder.Value!, job.WorkingDirectory, Resume: false));
        if (!command.IsSuccess) return await FailAsync(job, command.Error!).ConfigureAwait(false);

        // job.json は起動コマンドまで決まってから書く（DB が壊れてもフォルダだけで素性が分かる）
        _folder.WriteJobJson(folder.Value!, new JobDescriptor(
            job.Id, job.SessionId, job.Kind, job.WorkingDirectory, command.Value!.Display, job.StartedAt ?? _clock.UtcNow));

        var launched = _launcher.Launch(command.Value!);
        if (!launched.IsSuccess) return await FailAsync(job, launched.Error!).ConfigureAwait(false);

        var warning = await _gate.RunAsync(async () =>
        {
            job.JobFolder = folder.Value!;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        Follow(job.Id, folder.Value!, skipLines: 0);
        Raise(job, null, warning);
        return Result.Ok(job);
    }

    /// <summary>
    /// プロジェクトに作業フォルダがあればそれ（無ければ既定へフォールバックせず失敗: 意図した場所と違うところで
    /// 任意コマンドを走らせないため）。プロジェクト無し／未設定なら既定ワークフォルダを作って使う。
    /// </summary>
    private async Task<Result<string>> ResolveWorkingDirectoryAsync(TaskItem task, AiSettings settings, CancellationToken ct)
    {
        if (task.ProjectId is int pid)
        {
            var project = await _boards.GetProjectAsync(pid, ct).ConfigureAwait(false);
            if (project?.WorkingDirectory is { Length: > 0 } dir)
            {
                return Directory.Exists(dir)
                    ? Result.Ok(dir)
                    : Result.Fail<string>(string.Format(Messages.WorkingDirectoryMissingFormat, dir));
            }
        }

        var fallback = settings.DefaultWorkingDirectory;
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail<string>(string.Format(Messages.DefaultWorkingDirectoryFailedFormat, fallback));
        }
        return Result.Ok(fallback);
    }

    private async Task<Result<AiJob>> FailAsync(AiJob job, string error)
    {
        await _gate.RunAsync(async () =>
        {
            job.ErrorMessage = error;
            Finish(job, AiJobStatus.Failed);
            await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _tracked.TryRemove(job.Id, out _);
        Raise(job, null, null);
        return Result.Fail<AiJob>(error);
    }

    // ---------- 追従 ----------

    private void Follow(int jobId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            jobId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(jobId, line),
            message => OnProblemAsync(jobId, message)));
    }

    /// <summary>フックが 1 行書くたびに呼ばれる（行の順序どおり、直列）。</summary>
    private async Task OnHookLineAsync(int jobId, string line)
    {
        var parsed = HookEventParser.Parse(line);
        AiJob? job = null;
        AiJobEvent? stored = null;
        var finished = false;

        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId).ConfigureAwait(false);
            // 追跡をやめた後・完了にした後に届いた行は捨てる（終わったジョブを蘇らせない）
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            job = current;

            var tracked = await TrackedForAsync(current).ConfigureAwait(false);
            tracked.Seq++;
            current.ProcessedLines = tracked.Seq;
            // DB には残さない。画面に出す 1 件だけを組み立てて JobChanged で渡す。
            stored = new AiJobEvent
            {
                JobId = jobId, Seq = tracked.Seq, At = _clock.UtcNow,
                Kind = parsed.Kind, ToolName = parsed.ToolName, Payload = parsed.Payload,
            };

            switch (parsed.Kind)
            {
                case AiJobEventKind.SessionStarted:
                case AiJobEventKind.ToolUse:
                    current.Status = AiJobStatus.Running;
                    break;
                case AiJobEventKind.TurnEnded:
                    current.Status = AiJobStatus.WaitingForInput;
                    tracked.Turns++;
                    current.NumTurns = tracked.Turns;
                    break;
                case AiJobEventKind.SessionEnded:
                    Finish(current, AiJobStatus.Succeeded);
                    finished = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (job is null) return;
        if (finished)
        {
            _events.StopFollowing(jobId);
            _tracked.TryRemove(jobId, out _);
            // 保存に失敗していても列移動は必ず試みる。バナーは 1 本なので保存失敗の方を優先する。
            var moveWarning = await MoveToReviewAsync(job.TaskId).ConfigureAwait(false);
            warning ??= moveWarning;
        }
        Raise(job, stored, warning);
    }

    /// <summary>events.jsonl が消えた／作り直された（仕様 §12）。状態は変えず、注意だけ出す。</summary>
    private async Task OnProblemAsync(int jobId, string message)
    {
        _events.StopFollowing(jobId);
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId)).ConfigureAwait(false);
        if (job is null) return;
        Raise(job, null, message);
    }

    // ---------- 人の操作 ----------

    public async Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default)
    {
        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return available;

        AiJob? job = null;
        var found = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.AiJobNotFound);
            if (current.Status.IsTerminal()) return Result.Fail(Messages.AiJobAlreadyFinished);
            if (current.JobFolder.Length == 0) return Result.Fail(Messages.AiJobFolderMissing);
            job = current;
            return Result.Ok();
        }, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return found;

        var command = _launcher.BuildCommand(
            new SessionLaunchRequest(job!.SessionId, job.JobFolder, job.WorkingDirectory, Resume: true));
        if (!command.IsSuccess) return Result.Fail(command.Error!);
        var launched = _launcher.Launch(command.Value!);
        if (!launched.IsSuccess) return launched;

        // 前に追従が切れていても掛け直す。取り込み済みの行は読み飛ばす。
        // 先に古い追従を止め、その後ゲート越しに件数を読む。ゲートを介さず job.ProcessedLines を
        // 直接読むと、ちょうど取り込み中の 1 行がまだ反映されておらず 1 少ない値を拾うことがある。
        // 短い位置から追従を始めるとその行を二重に取り込み、以後 ProcessedLines が実際より
        // 先に進んで、次の再開で本物の行を 1 つ読み飛ばしてしまう（SessionEnd 消失の原因）。
        _events.StopFollowing(jobId);
        var skip = await _gate.RunAsync(() => Task.FromResult(job.ProcessedLines), ct).ConfigureAwait(false);
        Follow(jobId, job.JobFolder, skip);
        return Result.Ok();
    }

    public Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default)
        => FinishByHandAsync(jobId, AiJobStatus.Succeeded, ct);

    public Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default)
        => FinishByHandAsync(jobId, AiJobStatus.Cancelled, ct);

    private async Task<Result> FinishByHandAsync(int jobId, AiJobStatus status, CancellationToken ct)
    {
        AiJob? job = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.AiJobNotFound);
            if (current.Status.IsTerminal()) return Result.Fail(Messages.AiJobAlreadyFinished);
            job = current;
            Finish(current, status);
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return result;

        _events.StopFollowing(jobId);
        _tracked.TryRemove(jobId, out _);
        // 「完了にする」は SessionEnd と同じ扱い。「追跡をやめる」は仕事が終わったわけではないので動かさない。
        if (status == AiJobStatus.Succeeded) warning ??= await MoveToReviewAsync(job!.TaskId).ConfigureAwait(false);
        Raise(job!, null, warning);
        return result;
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var unfinished = await GetUnfinishedJobsAsync(ct).ConfigureAwait(false);
        foreach (var job in unfinished)
        {
            if (job.JobFolder.Length == 0) continue;
            _tracked[job.Id] = new TrackedJob { TaskId = job.TaskId, Seq = job.ProcessedLines, Turns = job.NumTurns ?? 0 };
            Follow(job.Id, job.JobFolder, job.ProcessedLines);
        }
    }

    // ---------- 補助 ----------

    /// <summary>
    /// ゲートの中で呼ぶ。記憶に無ければ ProcessedLines から数え直す
    /// （events.jsonl は「1 行 = 1 イベント」なので、件数がそのまま Seq とオフセットになる）。
    /// </summary>
    private Task<TrackedJob> TrackedForAsync(AiJob job)
    {
        if (_tracked.TryGetValue(job.Id, out var tracked)) return Task.FromResult(tracked);
        tracked = new TrackedJob { TaskId = job.TaskId, Seq = job.ProcessedLines, Turns = job.NumTurns ?? 0 };
        _tracked[job.Id] = tracked;
        return Task.FromResult(tracked);
    }

    /// <summary>ゲートの中で呼ぶ。終了状態を書いて履歴を残す（保存は呼び出し側）。</summary>
    private void Finish(AiJob job, AiJobStatus status)
    {
        var now = _clock.UtcNow;
        job.Status = status;
        job.EndedAt = now;
        _history.Add(new HistoryEntry
        {
            TaskId = job.TaskId, At = now, Kind = HistoryKind.AiJobFinished,
            Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(job.Kind, status)),
        });
    }

    /// <summary>ゲートの外から呼ぶ（BoardService も同じゲートを取る）。戻り値はバナー向けの警告。</summary>
    private async Task<string?> MoveToReviewAsync(int taskId)
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(false);
        if (!board.IsSuccess) return board.Error;

        var review = board.Value!.Columns.Where(c => c.Role == ColumnRole.Review).OrderBy(c => c.Order).FirstOrDefault();
        if (review is null) return Messages.NoReviewColumn;

        var moved = await _boardService.MoveTaskAsync(taskId, review.Id, int.MaxValue).ConfigureAwait(false);
        if (!moved.IsSuccess) return moved.Error;
        return moved.Warnings.Count > 0 ? string.Join(" / ", moved.Warnings) : null;
    }

    /// <summary>追従スレッドからの保存失敗でジョブを殺さない。理由は警告として UI へ回す。</summary>
    private async Task<string?> SaveQuietlyAsync()
    {
        try
        {
            await _uow.SaveChangesAsync().ConfigureAwait(false);
            return null;
        }
        catch (PersistenceException ex)
        {
            return $"{Messages.SaveFailed}: {ex.Message}";
        }
    }

    private void Raise(AiJob job, AiJobEvent? newEvent, string? warning)
    {
        var snapshot = new AiJobSnapshot(
            job.Id, job.TaskId, job.Kind, job.Status, job.NumTurns ?? TurnCountOf(job.Id),
            job.ErrorMessage, job.WorkingDirectory, job.JobFolder);
        JobChanged?.Invoke(this, new AiJobChangedEventArgs(snapshot, newEvent, warning));
    }
}
