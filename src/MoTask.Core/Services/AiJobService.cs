using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// AI ジョブのライフサイクル（仕様 §7）。状態遷移・同時実行上限・完了時の列移動・承認の配線を持つ。
/// DB は BoardService と共有の OperationGate で直列化する。ゲートの中から IBoardService を呼ぶと
/// デッドロックするので、完了時の列移動（MoveToReviewAsync）はゲートの外で呼ぶ。
/// JobChanged もゲートの外で上げる（購読側が同期的にサービスを呼び返しても詰まらないように）。
/// </summary>
public sealed class AiJobService : IAiJobService
{
    private enum StopReason
    {
        None,
        Stop,
        Suspend,
    }

    /// <summary>子プロセスが生きているジョブの制御ハンドル。DB には無い。</summary>
    private sealed class RunningJob
    {
        /// <summary>まだ決定が返っていない承認要求。CLI は複数のツールを同時に呼ぶので 1 つとは限らない。</summary>
        private readonly List<TaskCompletionSource> _pendingPermissions = new();
        private StopReason _reason;

        public required int JobId { get; init; }
        public required int TaskId { get; init; }
        public required string TaskTitle { get; init; }
        public required int? ProjectId { get; init; }
        /// <summary>取り消すとランナーがプロセスを殺す。</summary>
        public CancellationTokenSource ProcessCts { get; } = new();
        /// <summary>取り消すと保留中の承認ダイアログが deny で返る。プロセスより先に取り消す。</summary>
        public CancellationTokenSource PromptCts { get; } = new();
        public int Seq { get; set; }
        public int TurnCount { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>停止・終了の理由。BeginShutdown で None 以外になり、以後は承認要求を受け付けない。</summary>
        public StopReason Reason
        {
            get { lock (_pendingPermissions) return _reason; }
        }

        /// <summary>
        /// 畳み始める。理由を立てて、待つべき保留要求のスナップショットを返す。受付と同じロックの中で
        /// やるので、ここで返した一覧に漏れる要求は以後 1 件も受け付けられない。
        /// </summary>
        public IReadOnlyList<Task> BeginShutdown(StopReason reason)
        {
            lock (_pendingPermissions)
            {
                _reason = reason;
                return _pendingPermissions.Select(p => p.Task).ToList();
            }
        }

        /// <summary>
        /// 承認要求の受付。決定が返るまでこの TaskCompletionSource が保留中として残る。
        /// 畳み始めたあとは false を返す（呼び出し側が何も触らずに deny する）。
        /// </summary>
        public bool TryAddPendingPermission(out TaskCompletionSource pending)
        {
            lock (_pendingPermissions)
            {
                if (_reason != StopReason.None)
                {
                    pending = default!;
                    return false;
                }
                pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPermissions.Add(pending);
                return true;
            }
        }

        public void RemovePendingPermission(TaskCompletionSource pending)
        {
            lock (_pendingPermissions) _pendingPermissions.Remove(pending);
        }

        /// <summary>この要求が最後の 1 件か（＝決めれば AwaitingApproval が解ける）。ゲートの中から呼ぶ。</summary>
        public bool IsLastPendingPermission()
        {
            lock (_pendingPermissions) return _pendingPermissions.Count <= 1;
        }
    }

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAiJobRepository _jobs;
    private readonly IPermissionRuleRepository _rules;
    private readonly IBoardRepository _boards;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly OperationGate _gate;
    private readonly IAgentRunner _runner;
    private readonly IPermissionPolicy _policy;
    private readonly IPermissionPrompt _prompt;
    private readonly IAiSettingsStore _settings;
    private readonly IBoardService _boardService;
    private readonly ConcurrentDictionary<int, RunningJob> _running = new();

    public event EventHandler<AiJobChangedEventArgs>? JobChanged;

    public AiJobService(
        IAiJobRepository jobs, IPermissionRuleRepository rules, IBoardRepository boards, IHistoryRepository history,
        IUnitOfWork uow, IClock clock, OperationGate gate, IAgentRunner runner, IPermissionPolicy policy,
        IPermissionPrompt prompt, IAiSettingsStore settings, IBoardService boardService)
    {
        _jobs = jobs;
        _rules = rules;
        _boards = boards;
        _history = history;
        _uow = uow;
        _clock = clock;
        _gate = gate;
        _runner = runner;
        _policy = policy;
        _prompt = prompt;
        _settings = settings;
        _boardService = boardService;
    }

    // ---------- 照会 ----------

    public Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetForTaskAsync(taskId, ct), ct);

    public Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetEventsAsync(jobId, ct), ct);

    public Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetByStatusAsync(
            new[] { AiJobStatus.Running, AiJobStatus.AwaitingApproval, AiJobStatus.Suspended }, ct), ct);

    public int TurnCountOf(int jobId) => _running.TryGetValue(jobId, out var entry) ? entry.TurnCount : 0;

    // ---------- 開始 ----------

    public async Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default)
    {
        instruction = instruction.Trim();
        if (instruction.Length == 0) return Result.Fail<AiJob>(Messages.InstructionRequired);

        var available = _runner.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<AiJob>(available.Error!);

        var settings = _settings.Load();
        RunningJob? entry = null;
        var result = await _gate.RunAsync(async () =>
        {
            try
            {
                var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
                if (task is null) return Result.Fail<AiJob>(Messages.TaskNotFound);
                if (task.IsDeleted) return Result.Fail<AiJob>(Messages.TaskDeletedCannotRunAi);
                if (_running.Values.Any(r => r.TaskId == taskId)) return Result.Fail<AiJob>(Messages.TaskAlreadyHasActiveJob);
                if (LimitError(settings) is string limit) return Result.Fail<AiJob>(limit);

                var cwd = await ResolveWorkingDirectoryAsync(task, settings, ct).ConfigureAwait(false);
                if (!cwd.IsSuccess) return Result.Fail<AiJob>(cwd.Error!);

                var now = _clock.UtcNow;
                var job = new AiJob
                {
                    TaskId = task.Id, Kind = kind, Status = AiJobStatus.Running, SessionId = Guid.NewGuid(),
                    Instruction = instruction, WorkingDirectory = cwd.Value!, StartedAt = now,
                };
                _jobs.Add(job);
                _history.Add(new HistoryEntry
                {
                    Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.AiJobStarted,
                    Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(kind, null)),
                });
                await _uow.SaveChangesAsync(ct).ConfigureAwait(false);

                // ゲートの中で登録しておくと、並行する開始要求が上限の数え漏れをしない
                entry = Register(job, task, seq: 0);
                return Result.Ok(job);
            }
            catch (PersistenceException ex)
            {
                return Result.Fail<AiJob>($"{Messages.SaveFailed}: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess) Launch(result.Value!, entry!, instruction, resume: false);
        return result;
    }

    private string? LimitError(AiSettings settings)
    {
        var active = _running.Count;
        return active >= settings.MaxConcurrentJobs
            ? string.Format(Messages.ConcurrencyLimitFormat, active, settings.MaxConcurrentJobs)
            : null;
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
            return Result.Fail<string>(string.Format(Messages.WorkingDirectoryMissingFormat, fallback));
        }
        return Result.Ok(fallback);
    }

    private RunningJob Register(AiJob job, TaskItem task, int seq)
    {
        var entry = new RunningJob { JobId = job.Id, TaskId = task.Id, TaskTitle = task.Title, ProjectId = task.ProjectId, Seq = seq };
        _running[job.Id] = entry;
        return entry;
    }

    private void Launch(AiJob job, RunningJob entry, string prompt, bool resume)
    {
        var request = new AgentRunRequest(
            job.Id, job.SessionId, job.Kind, prompt, job.WorkingDirectory, resume,
            (req, ct) => HandlePermissionAsync(job, entry, req, ct),
            ev => RecordEventAsync(job, entry, ev));
        entry.Completion = Task.Run(() => ExecuteAsync(job, entry, request, resume));
        Raise(job, entry, null, null);
    }

    // ---------- 停止・中断・再開 ----------

    public async Task<Result> StopJobAsync(int jobId, CancellationToken ct = default)
    {
        var exists = await _gate.RunAsync(async () => await _jobs.GetAsync(jobId, ct).ConfigureAwait(false) is not null, ct).ConfigureAwait(false);
        if (!exists) return Result.Fail(Messages.AiJobNotFound);
        if (!_running.TryGetValue(jobId, out var entry)) return Result.Fail(Messages.AiJobNotActive);

        await ShutdownAsync(entry, StopReason.Stop).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task SuspendAllAsync()
    {
        var entries = _running.Values.ToList();
        // 承認ダイアログへの deny → プロセス終了 → Suspended の順。並行に畳んでよい。
        await Task.WhenAll(entries.Select(e => ShutdownAsync(e, StopReason.Suspend))).ConfigureAwait(false);
    }

    /// <summary>
    /// 保留中の承認要求すべてに決定が付くのを待って（最大 2 秒）からプロセスを殺す。待つのは
    /// HandlePermissionAsync が決定を返し終えるところまでで、その決定が子プロセスへ実際に書き込まれる
    /// ことまではここでは保証しない（フラッシュは IAgentRunner の実装側の責務）。
    /// 畳み始めたあとに届いた要求は RunningJob が受付を断り、HandlePermissionAsync が即 deny で返す。
    /// 終了の理由は entry.Reason に残し、ExecuteAsync が最終状態を決める。
    /// ゲートの外から呼ぶこと（承認の後始末も ExecuteAsync もゲートを取る）。
    /// </summary>
    private static async Task ShutdownAsync(RunningJob entry, StopReason reason)
    {
        try
        {
            var pending = entry.BeginShutdown(reason);
            Cancel(entry.PromptCts);
            if (pending.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            Cancel(entry.ProcessCts);
            await entry.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ExecuteAsync は例外を握って状態を確定させるので、ここへ来るのは起動そのものの失敗と、
            // 畳む手順自体がこけた場合だけ。アプリ終了時に SuspendAllAsync が投げ返さないよう握り潰す。
        }
    }

    /// <summary>Cancel は登録済みコールバックの例外を AggregateException でまとめて投げうる。畳む手順を止めない。</summary>
    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (Exception)
        {
            // 承認ダイアログやランナー側のコールバックの失敗で、プロセスの kill を落とさない
        }
    }

    public async Task<Result> ResumeJobAsync(int jobId, CancellationToken ct = default)
    {
        var available = _runner.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail(available.Error!);

        var settings = _settings.Load();
        RunningJob? entry = null;
        AiJob? job = null;
        var result = await _gate.RunAsync(async () =>
        {
            try
            {
                job = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
                if (job is null) return Result.Fail(Messages.AiJobNotFound);
                if (job.Status != AiJobStatus.Suspended) return Result.Fail(Messages.AiJobNotSuspended);
                // 中断中に同じタスクで別のジョブを始められる（Suspended は Active ではない）。
                // その後で再開すると 1 タスクに実行中 2 件になるので、開始と同じ条件でここでも弾く。
                if (_running.Values.Any(r => r.TaskId == job.TaskId)) return Result.Fail(Messages.TaskAlreadyHasActiveJob);
                if (LimitError(settings) is string limit) return Result.Fail(limit);

                var task = await _boards.GetTaskAsync(job.TaskId, ct).ConfigureAwait(false);
                if (task is null) return Result.Fail(Messages.TaskNotFound);
                // 中断中に消されたタスクは再開しない（完了時の確認待ちへの移動が削除済みタスクを動かしてしまう）。
                if (task.IsDeleted) return Result.Fail(Messages.TaskDeletedCannotRunAi);

                var events = await _jobs.GetEventsAsync(job.Id, ct).ConfigureAwait(false);
                job.Status = AiJobStatus.Running;
                job.ErrorMessage = null;
                await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                // 開始と同じくゲートの中で登録する。再開したジョブが _running に戻っていないと、
                // 同じタスクの二重起動を弾く StartJobAsync の判定が空振りする。
                entry = Register(job, task, seq: events.Count == 0 ? 0 : events.Max(e => e.Seq));
                return Result.Ok();
            }
            catch (PersistenceException ex)
            {
                return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess) Launch(job!, entry!, Messages.ResumeInstruction, resume: true);
        return result;
    }

    public Task RecoverOnStartupAsync(CancellationToken ct = default) => _gate.RunAsync(async () =>
    {
        var orphans = await _jobs.GetByStatusAsync(new[] { AiJobStatus.Running, AiJobStatus.AwaitingApproval }, ct).ConfigureAwait(false);
        if (orphans.Count == 0) return;
        foreach (var job in orphans) job.Status = AiJobStatus.Suspended;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    // ---------- ルール管理 ----------

    public Task<IReadOnlyList<AiPermissionRule>> GetPermissionRulesAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _rules.GetAllAsync(ct), ct);

    public Task<Result> DeletePermissionRuleAsync(int ruleId, CancellationToken ct = default) => _gate.RunAsync(async () =>
    {
        var rule = await _rules.GetAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null) return Result.Fail(Messages.PermissionRuleNotFound);
        _rules.Remove(rule);
        try
        {
            await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (PersistenceException ex)
        {
            return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
        }
    }, ct);

    // ---------- 実行と完了 ----------

    private async Task ExecuteAsync(AiJob job, RunningJob entry, AgentRunRequest request, bool resume)
    {
        AgentRunOutcome? outcome = null;
        string? failure = null;
        try
        {
            outcome = await _runner.RunAsync(request, entry.ProcessCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止・中断で殺した。理由は entry.Reason に入っている。
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        var final = AiJobStatus.Failed;
        var warning = await _gate.RunAsync(async () =>
        {
            var now = _clock.UtcNow;
            final = entry.Reason switch
            {
                StopReason.Stop => AiJobStatus.Cancelled,
                StopReason.Suspend => AiJobStatus.Suspended,
                _ => outcome is { ExitCode: 0, Result: not { IsError: true } } && failure is null
                    ? AiJobStatus.Succeeded
                    : AiJobStatus.Failed,
            };
            job.Status = final;
            if (outcome?.Result is { } info)
            {
                job.NumTurns = info.NumTurns ?? job.NumTurns;
                job.TotalCostUsd = info.TotalCostUsd ?? job.TotalCostUsd;
            }
            if (final != AiJobStatus.Suspended) job.EndedAt = now;
            if (final == AiJobStatus.Failed) job.ErrorMessage = FailureMessage(outcome, failure, resume);
            if (final.IsTerminal())
            {
                _history.Add(new HistoryEntry
                {
                    TaskId = job.TaskId, At = now, Kind = HistoryKind.AiJobFinished,
                    Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(job.Kind, final)),
                });
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        _running.TryRemove(job.Id, out _);
        if (final == AiJobStatus.Succeeded) warning ??= await MoveToReviewAsync(job.TaskId).ConfigureAwait(false);
        Raise(job, entry, null, warning);
    }

    private static string FailureMessage(AgentRunOutcome? outcome, string? failure, bool resume)
    {
        var detail = outcome?.Result?.ResultText;
        if (string.IsNullOrWhiteSpace(detail)) detail = failure;
        if (string.IsNullOrWhiteSpace(detail)) detail = outcome?.StderrTail?.Trim();
        if (string.IsNullOrWhiteSpace(detail)) detail = string.Format(Messages.AgentExitedWithCodeFormat, outcome?.ExitCode ?? -1);
        return string.Format(resume ? Messages.ResumeFailedFormat : Messages.AgentFailedFormat, detail);
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

    // ---------- イベント ----------

    private async Task RecordEventAsync(AiJob job, RunningJob entry, AgentEvent ev)
    {
        AiJobEvent? stored = null;
        var warning = await _gate.RunAsync(async () =>
        {
            stored = AddEvent(job, entry, ev.Kind, ev.ToolName, ev.Payload);
            if (ev.Kind is AiJobEventKind.AssistantText or AiJobEventKind.ToolUse) entry.TurnCount++;
            if (ev.Result is { } info)
            {
                // 値が無い result 行で、既に取れている値を潰さない（ExecuteAsync の完了時と同じ方針）。
                job.NumTurns = info.NumTurns ?? job.NumTurns;
                job.TotalCostUsd = info.TotalCostUsd ?? job.TotalCostUsd;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        Raise(job, entry, stored, warning);
    }

    /// <summary>ゲートの中で呼ぶ。Seq を進めて追記する（保存は呼び出し側）。</summary>
    private AiJobEvent AddEvent(AiJob job, RunningJob entry, AiJobEventKind kind, string? toolName, string payload)
    {
        entry.Seq++;
        var stored = new AiJobEvent { JobId = job.Id, Seq = entry.Seq, At = _clock.UtcNow, Kind = kind, ToolName = toolName, Payload = payload };
        _jobs.AddEvent(stored);
        return stored;
    }

    /// <summary>ワーカースレッドからの保存失敗でジョブを殺さない。理由は警告として UI へ回す。</summary>
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

    // ---------- 承認 ----------

    /// <summary>
    /// 承認ツールから呼ばれる。ルールで決まればダイアログ無し。AskHuman なら IPermissionPrompt を待つ。
    /// タイムアウトはさせない。停止・終了で PromptCts が取り消されたときだけ deny を返す。
    /// 並行して何件でも来る（CLI は複数のツールを同時に呼ぶ）ので、保留中の要求は entry に積む。
    /// 畳み始めたあとの要求は、状態もイベントも触らずに即 deny する。
    /// </summary>
    private async Task<PermissionDecision> HandlePermissionAsync(AiJob job, RunningJob entry, PermissionRequest request, CancellationToken ct)
    {
        // 停止・終了の deny を受け取った CLI が、プロセスが死ぬ前に次のツールを呼ぶことがある。
        // ここで受け付けて AwaitingApproval を書くと、最終状態を確定した ExecuteAsync を追い越して
        // 終了済みのジョブを Running に蘇らせてしまう（EndedAt と終了履歴が付いたまま実体が無い状態）。
        // 受付の可否は保留一覧と同じロックの中で決まるので、ShutdownAsync が待つ一覧から漏れて
        // なお受け付けられる、という隙間は無い。
        if (!entry.TryAddPendingPermission(out var pending))
        {
            return PermissionDecision.Deny(
                entry.Reason == StopReason.Stop ? Messages.StoppedByUser : Messages.SuspendedByShutdown);
        }

        try
        {
            AiJobEvent? asked = null;
            var warning = await _gate.RunAsync(async () =>
            {
                job.Status = AiJobStatus.AwaitingApproval;
                asked = AddEvent(job, entry, AiJobEventKind.PermissionAsked, request.ToolName, AskedPayload(request));
                return await SaveQuietlyAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            Raise(job, entry, asked, warning);

            var rules = await _gate.RunAsync(() => _rules.GetAllAsync(ct), ct).ConfigureAwait(false);
            var verdict = _policy.Evaluate(entry.ProjectId, rules, request);

            PermissionDecision decision;
            string source;
            switch (verdict)
            {
                case PolicyVerdict.Allow:
                    decision = PermissionDecision.Allow();
                    source = "rule";
                    break;
                case PolicyVerdict.Deny:
                    decision = PermissionDecision.Deny(Messages.DeniedByRule);
                    source = "rule";
                    break;
                default:
                    (decision, source) = await AskHumanAsync(job, entry, request, ct).ConfigureAwait(false);
                    break;
            }

            AiJobEvent? decided = null;
            warning = await _gate.RunAsync(async () =>
            {
                // まだ他の要求が保留中なら AwaitingApproval のまま。最後の 1 件が決まって初めて Running へ戻す。
                // 数えるのはゲートの中（他の要求の受付・決定もゲートを通るので、ここでの数え方が競らない）。
                if (job.Status == AiJobStatus.AwaitingApproval && entry.IsLastPendingPermission()) job.Status = AiJobStatus.Running;
                decided = AddEvent(job, entry, AiJobEventKind.PermissionDecided, request.ToolName, DecidedPayload(decision, source));
                return await SaveQuietlyAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            Raise(job, entry, decided, warning);
            return decision;
        }
        finally
        {
            pending.TrySetResult();
            entry.RemovePendingPermission(pending);
        }
    }

    private async Task<(PermissionDecision Decision, string Source)> AskHumanAsync(AiJob job, RunningJob entry, PermissionRequest request, CancellationToken ct)
    {
        var pattern = PermissionPattern.ForRemembering(request);
        var context = new PermissionPromptContext(job, entry.TaskTitle, entry.ProjectId, request, pattern);

        HumanDecision human;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.PromptCts.Token, ct);
        try
        {
            human = await _prompt.AskAsync(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止か終了。人が拒否したのだと誤解させない文言で deny を返す（仕様 §9）。
            var message = entry.Reason == StopReason.Stop ? Messages.StoppedByUser : Messages.SuspendedByShutdown;
            return (PermissionDecision.Deny(message), "shutdown");
        }
        catch (Exception)
        {
            // ダイアログ側の失敗（Dispatcher の異常、画面破棄後の呼び出しなど）。承認要求はタイムアウトさせない
            // 設計なので、ここで例外を素通しすると PermissionDecided が残らず AwaitingApproval のまま二度と
            // 進まなくなる。安全側（拒否）に倒して必ず決定イベントを残す。
            // source は "human" のまま: payload の source は rule / human / shutdown の 3 値契約で、
            // 4 つ目を足すと Task 11 の整形が黙って取りこぼす。人に聞く経路の失敗なので "human" が正しい。
            return (PermissionDecision.Deny(Messages.ApprovalUiFailed), "human");
        }

        if (human.Remember)
        {
            var scope = human.Scope == RuleScope.Project && entry.ProjectId is not null ? RuleScope.Project : RuleScope.Global;
            await _gate.RunAsync(async () =>
            {
                _rules.Add(new AiPermissionRule
                {
                    Scope = scope,
                    ProjectId = scope == RuleScope.Project ? entry.ProjectId : null,
                    ToolName = request.ToolName,
                    Pattern = pattern,
                    Decision = human.Decision,
                    CreatedAt = _clock.UtcNow,
                });
                await SaveQuietlyAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        var decision = human.Decision == RuleDecision.Allow
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny(Messages.DeniedByHuman);
        return (decision, "human");
    }

    private static string AskedPayload(PermissionRequest request)
    {
        object input;
        try
        {
            using var doc = JsonDocument.Parse(request.InputJson);
            input = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            input = request.InputJson;
        }
        return JsonSerializer.Serialize(new { type = "motask_permission_asked", tool_name = request.ToolName, tool_use_id = request.ToolUseId, input }, PayloadOptions);
    }

    private static string DecidedPayload(PermissionDecision decision, string source)
        => JsonSerializer.Serialize(new
        {
            type = "motask_permission_decided",
            behavior = decision.IsAllowed ? "allow" : "deny",
            source,
            message = decision.Message,
        }, PayloadOptions);

    // ---------- 通知 ----------

    private void Raise(AiJob job, RunningJob? entry, AiJobEvent? newEvent, string? warning)
    {
        var turns = job.NumTurns ?? entry?.TurnCount ?? 0;
        var snapshot = new AiJobSnapshot(job.Id, job.TaskId, job.Kind, job.Status, turns, job.TotalCostUsd, job.ErrorMessage, job.WorkingDirectory);
        JobChanged?.Invoke(this, new AiJobChangedEventArgs(snapshot, newEvent, warning));
    }
}
