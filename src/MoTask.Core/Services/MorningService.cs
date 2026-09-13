using System.Collections.Concurrent;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Morning;

namespace MoTask.Core.Services;

/// <summary>
/// 朝の実行のライフサイクル(仕様 §6・§10)。AiJobService と同じ構えだが、終了時の副作用が違う
/// (あちらはタスクを確認待ちへ動かし、こちらは候補とプランを取り込む)ので共通化しない。
/// DB は BoardService / AiJobService と共有の OperationGate で直列化する。
/// <b>ゲートの中から IBoardService を呼ぶとデッドロックする</b>ので、登録・統合はゲートの外で呼ぶ。
/// </summary>
public sealed class MorningService : IMorningService
{
    private readonly IMorningRepository _runs;
    private readonly IBoardRepository _boards;
    private readonly IAiJobRepository _jobs;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly OperationGate _gate;
    private readonly ISessionLauncher _launcher;
    private readonly IJobFolder _folder;
    private readonly IJobEventSource _events;
    private readonly IAiSettingsStore _settings;
    private readonly IBoardService _boardService;

    /// <summary>追跡中の実行のターン数。DB には持たない(仕様 §9)。</summary>
    private readonly ConcurrentDictionary<int, int> _turns = new();

    /// <summary>
    /// 開始は 1 本ずつ。ゲートを 2 回に分けて取る(フォルダ作成と端末起動はゲートの外)ので、
    /// 2 つの StartAsync が同じ連番のフォルダを作らないようにここで直列化する。
    /// </summary>
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public event EventHandler<MorningRunChangedEventArgs>? RunChanged;

    public MorningService(
        IMorningRepository runs, IBoardRepository boards, IAiJobRepository jobs, IHistoryRepository history,
        IUnitOfWork uow, IClock clock, OperationGate gate, ISessionLauncher launcher, IJobFolder folder,
        IJobEventSource events, IAiSettingsStore settings, IBoardService boardService)
    {
        _runs = runs;
        _boards = boards;
        _jobs = jobs;
        _history = history;
        _uow = uow;
        _clock = clock;
        _gate = gate;
        _launcher = launcher;
        _folder = folder;
        _events = events;
        _settings = settings;
        _boardService = boardService;

        // 所有した端末が先に死んだら気づけるようにする(仕様 §7)。MorningService も
        // ISessionLauncher もアプリに 1 つずつの singleton なので、外すことはしない。
        _launcher.OwnedSessionExited += OnOwnedSessionExited;
    }

    // ---------- 照会 ----------

    public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetLatestRunAsync(ct), ct);

    public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetQueueAsync(runId, ct), ct);

    public Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetCandidatesOfRunAsync(runId, ct), ct);

    public async Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default)
    {
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId, ct), ct).ConfigureAwait(false);
        return run is null || run.JobFolder.Length == 0
            ? Array.Empty<string>()
            : _folder.ReadTail(run.JobFolder, lines);
    }

    public int TurnCountOf(int runId) => _turns.TryGetValue(runId, out var turns) ? turns : 0;

    // ---------- 開始 ----------

    private sealed record Prepared(int RunNumber, string BoardJson);

    public async Task<Result<MorningRun>> StartAsync(CancellationToken ct = default)
    {
        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<MorningRun>(available.Error!);

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings.Load();
            var date = _clock.Today;

            var prepared = await _gate.RunAsync(async () =>
            {
                // 二重起動の防止(仕様 §12)。追跡中かどうかは DB で数える。
                var unfinished = await _runs.GetUnfinishedRunAsync(ct).ConfigureAwait(false);
                if (unfinished is not null) return Result.Fail<Prepared>(Messages.MorningRunAlreadyRunning);

                var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
                if (board is null) return Result.Fail<Prepared>(Messages.BoardNotFound);

                var projects = await _boards.GetProjectsAsync(ct).ConfigureAwait(false);
                var busy = await _jobs.GetByStatusAsync(
                    new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct)
                    .ConfigureAwait(false);

                var snapshot = BoardSnapshot.Build(
                    board, date,
                    projects.ToDictionary(p => p.Id, p => p.Name),
                    busy.Select(j => j.TaskId).ToHashSet());

                // フォルダ名の連番。DB の採番を待たずに決まるので、行の保存を後ろへ回せる(仕様 §12)。
                var runNumber = await _runs.CountRunsAsync(ct).ConfigureAwait(false) + 1;
                return Result.Ok(new Prepared(runNumber, snapshot));
            }, ct).ConfigureAwait(false);
            if (!prepared.IsSuccess) return Result.Fail<MorningRun>(prepared.Error!);

            // ここから先はファイル操作と端末の起動なので、ゲートの外でやる。
            var request = new JobFolderRequest(prepared.Value!.RunNumber, date.ToString("yyyy-MM-dd"), "")
            {
                Category = JobFolderPaths.MorningDirectoryName,
                OutputDirectoryName = JobFolderPaths.ResultDirectoryName,
            };
            // 指示文は出力先の実パスを含むので、フォルダのパスが決まってから組み立てる。
            var root = _folder.ResolveRoot(request);
            var instruction = MorningInstruction.Build(settings.MorningInstruction, JobFolderPaths.For(root), date);

            var created = _folder.Create(request with { Instruction = instruction });
            if (!created.IsSuccess) return Result.Fail<MorningRun>(created.Error!);

            var wroteBoard = _folder.WriteText(root, JobFolderPaths.BoardJsonName, prepared.Value!.BoardJson);
            if (!wroteBoard.IsSuccess) return Result.Fail<MorningRun>(wroteBoard.Error!);

            var sessionId = Guid.NewGuid();
            // cwd はジョブフォルダ自身。朝の実行はソースツリーに用が無い(仕様 §6)。
            // 成果物の出力先は result/(AI 遂行の既定 artifacts/ とは違う)。起動プロンプトを
            // instruction.md の指示と一致させる。
            var command = _launcher.BuildCommand(new SessionLaunchRequest(
                sessionId, root, root, Resume: false,
                OutputDirectoryName: JobFolderPaths.ResultDirectoryName, CloseOnExit: true));
            if (!command.IsSuccess) return Result.Fail<MorningRun>(command.Error!);

            // フォルダとコマンドが確定してから DB に書く。
            // AiJobService の「JobFolder が空のまま Pending で残る」窓をこちらでは作らない(仕様 §12)。
            var now = _clock.UtcNow;
            var run = new MorningRun
            {
                Date = date, Status = MorningRunStatus.Pending, SessionId = sessionId,
                Instruction = instruction, JobFolder = root, StartedAt = now,
            };
            var saved = await _gate.RunAsync(async () =>
            {
                try
                {
                    _runs.Add(run);
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Result.Ok();
                }
                catch (PersistenceException ex)
                {
                    return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result.Fail<MorningRun>(saved.Error!);

            // 朝の実行は MoTask が所有する。完了時に窓を閉じるには Process ハンドルが要る（仕様 §5.3）。
            var owned = _launcher.LaunchOwned(run.Id, command.Value!);
            if (!owned.IsSuccess) return await FailAsync(run, owned.Error!).ConfigureAwait(false);

            // 掛け直しの材料（pid と開始時刻）は起動できてからでないと書けないので、run.json は
            // 起動の後に書く。書けなくても実行そのものは続ける（端末はもう走っている）が、
            // 再起動後に掛け直せなくなるので警告として人に見せる（仕様 §7）。
            var wroteRun = _folder.WriteText(root, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
                new MorningRunDescriptor(run.Id, date, sessionId, root, command.Value!.Display, now,
                    owned.Value!.ProcessId, owned.Value!.StartedAt)));

            _turns[run.Id] = 0;
            Follow(run.Id, root, skipLines: 0);
            Raise(run, wroteRun.IsSuccess ? null : wroteRun.Error, candidatesChanged: false);
            return Result.Ok(run);
        }
        finally
        {
            _startLock.Release();
        }
    }

    // ---------- 追従 ----------

    private void Follow(int runId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            runId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(runId, line),
            message => OnProblemAsync(runId, message)));
    }

    /// <summary>フックが 1 行書くたびに呼ばれる(行の順序どおり、直列)。</summary>
    private async Task OnHookLineAsync(int runId, string line)
    {
        var parsed = HookEventParser.Parse(line);
        MorningRun? run = null;
        var lookAtResult = false;
        var lastChance = false;

        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            // 追跡をやめた後・取り込んだ後に届いた行は捨てる(終わった実行を蘇らせない)
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            run = current;
            current.ProcessedLines++;

            switch (parsed.Kind)
            {
                case AiJobEventKind.SessionStarted:
                case AiJobEventKind.ToolUse:
                    current.Status = MorningRunStatus.Running;
                    break;
                case AiJobEventKind.TurnEnded:
                    current.Status = MorningRunStatus.Running;
                    _turns.AddOrUpdate(runId, 1, (_, turns) => turns + 1);
                    // Stop のたびに result/ を見に行く。揃っていなければ何もしない(仕様 §6)。
                    lookAtResult = true;
                    break;
                case AiJobEventKind.SessionEnded:
                    lookAtResult = true;
                    lastChance = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        if (!lookAtResult)
        {
            Raise(run, warning, candidatesChanged: false);
            return;
        }

        var ingest = await IngestAsync(run, lastChance).ConfigureAwait(false);
        Raise(run, warning ?? ingest.Warning, ingest.CandidatesChanged);
    }

    /// <summary>events.jsonl が消えた／作り直された(仕様 §12)。状態は変えず、注意だけ出す。</summary>
    private async Task OnProblemAsync(int runId, string message)
    {
        _events.StopFollowing(runId);
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId)).ConfigureAwait(false);
        if (run is null) return;
        Raise(run, message, candidatesChanged: false);
    }

    // ---------- 取り込み ----------

    private sealed record IngestOutcome(bool CandidatesChanged, string? Warning);

    /// <summary>
    /// result/ を読んで取り込む。プランがまだ書かれていなければ何もしない(Stop は何度でも来る)。
    /// lastChance が true のときだけ、揃っていない実行を Failed にする(仕様 §8)。
    /// </summary>
    private async Task<IngestOutcome> IngestAsync(MorningRun run, bool lastChance)
    {
        // ファイル読みはゲートの外
        var result = MorningResultReader.Read(
            _folder.ReadText(run.JobFolder, JobFolderPaths.CandidatesRelativePath),
            _folder.ReadText(run.JobFolder, JobFolderPaths.PlanRelativePath));

        if (!result.IsUsable)
        {
            if (!lastChance) return new IngestOutcome(false, null);

            // 確認と書き込みを1回のゲートで行う(仕様 §12)。取り込みや追跡解除が先に終わっていたら
            // Failed で上書きしない(AiJobService.FinishByHandAsync と同じ規律)。
            var finished = await _gate.RunAsync(async () =>
            {
                if (run.Status.IsTerminal()) return false;
                run.Status = MorningRunStatus.Failed;
                run.ErrorMessage = Messages.MorningResultUnreadable;
                run.EndedAt = _clock.UtcNow;
                await SaveQuietlyAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            if (finished)
            {
                _events.StopFollowing(run.Id);
                _turns.TryRemove(run.Id, out _);
                // 読めないまま終わった実行でも窓は畳む(仕様 §7)
                _launcher.CloseOwned(run.Id);
            }
            return new IngestOutcome(false, finished ? Messages.MorningResultUnreadable : null);
        }

        var added = 0;
        var warning = await _gate.RunAsync(async () =>
        {
            // 取り込みは 1 度だけ。Stop が複数回来ても 2 度目はここで降りる(仕様 §14)。
            if (run.Status.IsTerminal()) return (string?)null;

            var known = (await _runs.GetKnownExternalIdsAsync(
                result.Candidates.Select(c => c.ExternalId).ToList()).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

            foreach (var record in result.Candidates)
            {
                // 却下・登録済みの ExternalId は翌朝また出てきても黙って捨てる(仕様 §9)
                if (!known.Add(record.ExternalId)) continue;
                _runs.AddCandidate(new TriageCandidate
                {
                    MorningRunId = run.Id,
                    ExternalId = record.ExternalId,
                    Source = record.Source,
                    From = record.From,
                    Title = record.Title,
                    Evidence = record.Evidence,
                    Link = record.Link,
                    Reasoning = record.Reasoning,
                    ReceivedAt = record.ReceivedAt,
                    SuggestedDueDate = record.SuggestedDueDate,
                    SuggestedProject = record.SuggestedProject,
                    SuggestedAction = record.SuggestedAction,
                    SuggestedMergeTaskId = record.MergeTargetTaskId,
                    Status = TriageStatus.Pending,
                });
                added++;
            }

            run.PlanJson = result.PlanJson;
            run.Status = MorningRunStatus.Ingested;
            run.EndedAt = _clock.UtcNow;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        // 取り込みが終わったら窓を畳む。知らない ownerId は launcher が黙って無視するので、
        // 2 度目の Stop で重ねて呼ばれても実害は無い(仕様 §5.3)。
        _launcher.CloseOwned(run.Id);

        // 保存失敗のほうが重い。バナーは 1 本なのでそちらを優先する。
        warning ??= result.DiscardedLines > 0
            ? string.Format(Messages.MorningCandidatesDiscardedFormat,
                result.Candidates.Count + result.DiscardedLines, result.DiscardedLines)
            : null;
        return new IngestOutcome(added > 0, warning);
    }

    // ---------- 人の操作 ----------

    public async Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
    {
        var found = await FindActiveRunAsync(runId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);

        var run = found.Value!;
        // 「完了にする」は SessionEnd と同じ扱い(仕様 §12)
        var ingest = await IngestAsync(run, lastChance: true).ConfigureAwait(false);
        Raise(run, ingest.Warning, ingest.CandidatesChanged);
        return run.Status == MorningRunStatus.Ingested
            ? Result.Ok()
            : Result.Fail(Messages.MorningResultUnreadable);
    }

    public async Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
    {
        MorningRun? run = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.MorningRunNotFound);
            // この IsTerminal() 確認と、下の Status への書き込みは同じコールバックの中に
            // 置いたままにする(分けない)。AiJobService.FinishByHandAsync と同じ形。
            // 別々の _gate.RunAsync に分けると、その間に OnHookLineAsync が取り込みを終わらせて
            // Ingested にできてしまい、ここが無条件に Cancelled で上書きしてしまう。
            if (current.Status.IsTerminal()) return Result.Fail(Messages.MorningRunAlreadyFinished);
            run = current;
            // 仕事が終わったわけではないので取り込まない。端末も殺さない(仕様 §12)。
            current.Status = MorningRunStatus.Cancelled;
            current.EndedAt = _clock.UtcNow;
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return result;

        _events.StopFollowing(run!.Id);
        _turns.TryRemove(run.Id, out _);
        Raise(run, warning, candidatesChanged: false);
        return Result.Ok();
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var run = await _gate.RunAsync(() => _runs.GetUnfinishedRunAsync(ct), ct).ConfigureAwait(false);
        if (run is null || run.JobFolder.Length == 0) return;

        // 掛け直せなければ「閉じる能力」だけを諦め、追従(events.jsonl)は続ける(仕様 §7)。
        // 開始時刻を照合するのは launcher 側の仕事で、ここは材料を渡すだけ。
        var descriptor = MorningRunDescriptor.TryParse(
            _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName));
        if (descriptor is { ProcessId: > 0 })
        {
            _launcher.TryReattach(run.Id, descriptor.ProcessId, descriptor.ProcessStartedAt);
        }

        Follow(run.Id, run.JobFolder, run.ProcessedLines);
    }

    /// <summary>
    /// 端末の終了を受けた後始末。イベントは void で来るので、直近の 1 本をここに残して
    /// テストが待てるようにする(MorningPlanViewModel.PendingLoad と同じ手)。
    /// </summary>
    public Task PendingTerminalExit { get; private set; } = Task.CompletedTask;

    private void OnOwnedSessionExited(object? sender, int runId)
        => PendingTerminalExit = OnOwnedSessionExitedAsync(runId);

    /// <summary>
    /// 端末が先に死んだ(人が × で閉じた・claude が落ちた)。追跡中なら Failed にして降りる。
    /// 終端の実行はそのまま(閉じたのがこちらの CloseOwned なら、そもそもここへ来ない)。
    /// </summary>
    private async Task OnOwnedSessionExitedAsync(int runId)
    {
        MorningRun? run = null;
        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            run = current;
            current.Status = MorningRunStatus.Failed;
            current.ErrorMessage = Messages.MorningTerminalClosed;
            current.EndedAt = _clock.UtcNow;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        Raise(run, warning, candidatesChanged: false);
    }

    private Task<Result<MorningRun>> FindActiveRunAsync(int runId, CancellationToken ct)
        => _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return Result.Fail<MorningRun>(Messages.MorningRunNotFound);
            if (run.Status.IsTerminal()) return Result.Fail<MorningRun>(Messages.MorningRunAlreadyFinished);
            return Result.Ok(run);
        }, ct);

    /// <summary>ゲートの外から呼ぶ。終了状態にして追従を降りる。戻り値はバナー向けの警告。</summary>
    private async Task<string?> FinishAsync(MorningRun run, MorningRunStatus status, string? error)
    {
        var warning = await _gate.RunAsync(async () =>
        {
            run.Status = status;
            run.EndedAt = _clock.UtcNow;
            if (error is not null) run.ErrorMessage = error;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        return warning;
    }

    // ---------- 仕分け ----------

    public async Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
    {
        var title = decision.Title.Trim();
        if (title.Length == 0) return Result.Fail<TaskItem>(Messages.TitleRequired);

        var found = await FindQueuedCandidateAsync(decision.CandidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail<TaskItem>(found.Error!);
        var candidate = found.Value!;

        // ここからゲートの外。IBoardService は同じゲートを取るので、中から呼ぶとデッドロックする。
        var project = await ResolveProjectAsync(decision.ProjectName, ct).ConfigureAwait(false);
        if (!project.IsSuccess) return Result.Fail<TaskItem>(project.Error!);

        var created = await _boardService.CreateTaskAsync(decision.ColumnId, title, ct).ConfigureAwait(false);
        if (!created.IsSuccess) return Result.Fail<TaskItem>(created.Error!);
        var task = created.Value!;
        var createdWarnings = new List<string>(created.Warnings);

        var updated = await _boardService.UpdateTaskAsync(
            new TaskUpdate(task.Id, title, CandidateNote.Format(candidate), project.Value, decision.DueDate), ct)
            .ConfigureAwait(false);
        if (!updated.IsSuccess)
        {
            // タスクは作れたが仕上げ(説明・プロジェクト・期限)の書き込みに失敗した。中途半端な
            // タスクを残すと、候補は Pending のままなので人がもう一度登録し直したときに
            // 二重にタスクができてしまう。ゲートは呼び出しごとに取り直すので、この削除も
            // ゲートの外から呼ぶ(Create/Update と同じ)。削除自体が失敗しても、人に見せるべきは
            // 元の失敗理由なので、そちらを返す(取り消しは最善努力でしかない)。
            await _boardService.DeleteTaskAsync(task.Id, ct).ConfigureAwait(false);
            return Result.Fail<TaskItem>(updated.Error!);
        }

        var warning = await DecideAsync(candidate, TriageStatus.Registered, task.Id,
            HistoryKind.CandidateRegistered, ct).ConfigureAwait(false);

        if (warning is not null) createdWarnings.Add(warning);
        return Result.Ok(task, createdWarnings);
    }

    public async Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
    {
        var found = await FindQueuedCandidateAsync(candidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);
        var candidate = found.Value!;

        var target = await _gate.RunAsync(async () =>
        {
            var task = await _boards.GetTaskAsync(targetTaskId, ct).ConfigureAwait(false);
            return task is null || task.IsDeleted ? null : task;
        }, ct).ConfigureAwait(false);
        if (target is null) return Result.Fail(Messages.TaskNotFound);

        var note = CandidateNote.Format(candidate);
        var description = target.Description.TrimEnd().Length == 0
            ? note
            : target.Description.TrimEnd() + "\n\n" + note;
        // 候補側にだけ期限があるときだけ入れる。既にある期限は上書きしない(仕様 §10)。
        var due = target.DueDate ?? candidate.SuggestedDueDate;

        var updated = await _boardService.UpdateTaskAsync(
            new TaskUpdate(target.Id, target.Title, description, target.ProjectId, due), ct).ConfigureAwait(false);
        if (!updated.IsSuccess) return updated;

        var warning = await DecideAsync(candidate, TriageStatus.Merged, target.Id,
            HistoryKind.CandidateMerged, ct).ConfigureAwait(false);
        return warning is null ? Result.Ok() : Result.Ok(warning);
    }

    public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        => DecideOnlyAsync(candidateId, TriageStatus.Later, ct);

    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        => DecideOnlyAsync(candidateId, TriageStatus.Rejected, ct);

    /// <summary>「あとで」「却下」は候補の状態を変えるだけ。タスクは作らないし履歴も残さない。</summary>
    private async Task<Result> DecideOnlyAsync(int candidateId, TriageStatus status, CancellationToken ct)
    {
        var found = await FindQueuedCandidateAsync(candidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);

        var warning = await DecideAsync(found.Value!, status, resultTaskId: null, kind: null, ct).ConfigureAwait(false);
        return warning is null ? Result.Ok() : Result.Ok(warning);
    }

    /// <summary>
    /// キューに乗っている候補(Pending、または他の実行から持ち越された Later)を探す。
    /// Later はこのキューの「再提示」経路であって決着済み状態ではないので、ここでも受け付ける
    /// (仕様 §9)。そうしないと「あとで」を選んだ候補が二度と仕分けできなくなる。
    /// </summary>
    private Task<Result<TriageCandidate>> FindQueuedCandidateAsync(int candidateId, CancellationToken ct)
        => _gate.RunAsync(async () =>
        {
            var candidate = await _runs.GetCandidateAsync(candidateId, ct).ConfigureAwait(false);
            if (candidate is null) return Result.Fail<TriageCandidate>(Messages.CandidateNotFound);
            if (candidate.Status != TriageStatus.Pending && candidate.Status != TriageStatus.Later)
                return Result.Fail<TriageCandidate>(Messages.CandidateAlreadyDecided);
            return Result.Ok(candidate);
        }, ct);

    /// <summary>ゲートの中で候補を片づけ、必要なら履歴を 1 件残す。戻り値はバナー向けの警告。</summary>
    private async Task<string?> DecideAsync(
        TriageCandidate candidate, TriageStatus status, int? resultTaskId, HistoryKind? kind, CancellationToken ct)
    {
        var warning = await _gate.RunAsync(async () =>
        {
            candidate.Status = status;
            candidate.ResultTaskId = resultTaskId;
            candidate.DecidedAt = _clock.UtcNow;
            if (kind is HistoryKind historyKind && resultTaskId is int taskId)
            {
                _history.Add(new HistoryEntry
                {
                    TaskId = taskId, At = _clock.UtcNow, Kind = historyKind,
                    Detail = TriageHistoryDetail.Serialize(
                        new TriageHistoryDetail(candidate.Source, candidate.ExternalId)),
                });
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await RaiseCandidatesChangedAsync(candidate.MorningRunId, warning, ct).ConfigureAwait(false);
        return warning;
    }

    /// <summary>候補が動いたことだけを知らせる。実行そのものの状態は変わらない。</summary>
    private async Task RaiseCandidatesChangedAsync(int runId, string? warning, CancellationToken ct)
    {
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId, ct), ct).ConfigureAwait(false);
        if (run is null) return;
        Raise(run, warning, candidatesChanged: true);
    }

    /// <summary>
    /// 名前でプロジェクトを引き、無ければ作る。空白だけなら「プロジェクト無し」。
    /// ゲートの外から呼ぶこと(IBoardService を使う)。
    /// </summary>
    private async Task<Result<int?>> ResolveProjectAsync(string name, CancellationToken ct)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return Result.Ok<int?>(null);

        var projects = await _boardService.GetProjectsAsync(ct).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return Result.Ok<int?>(existing.Id);

        var created = await _boardService.CreateProjectAsync(wanted, ct).ConfigureAwait(false);
        return created.IsSuccess ? Result.Ok<int?>(created.Value!.Id) : Result.Fail<int?>(created.Error!);
    }

    // ---------- 一括 ----------

    /// <summary>
    /// 既存の 4 アクションを順に呼ぶだけ（仕様 §5）。各アクションが呼び出しごとにゲートを取るので、
    /// ここ自身はゲートを取らない（取ると中の IBoardService でデッドロックする）。
    /// 1 件失敗しても止めず、見送り理由を積んで次へ進む。全件見送りでも Result は成功。
    /// </summary>
    public Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default)
        => RunBulkAsync(runId, candidate => candidate.SuggestedAction switch
        {
            TriageAction.Register => RegisterBySuggestionAsync(candidate, registerColumnId, ct),
            TriageAction.Merge => candidate.SuggestedMergeTaskId is int target
                ? MergeAsync(candidate.Id, target, ct)
                : Task.FromResult(Result.Fail(Messages.MergeTargetMissing)),
            TriageAction.Later => PostponeAsync(candidate.Id, ct),
            TriageAction.Reject => RejectAsync(candidate.Id, ct),
            _ => Task.FromResult(Result.Fail(Messages.CandidateAlreadyDecided)),
        }, ct);

    public Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default)
        => RunBulkAsync(runId, candidate => PostponeAsync(candidate.Id, ct), ct);

    private async Task<Result> RegisterBySuggestionAsync(TriageCandidate candidate, int columnId, CancellationToken ct)
        => await RegisterAsync(new CandidateDecision(
            candidate.Id, candidate.Title, candidate.SuggestedDueDate, candidate.SuggestedProject, columnId), ct)
            .ConfigureAwait(false);

    private async Task<Result<BulkOutcome>> RunBulkAsync(
        int runId, Func<TriageCandidate, Task<Result>> action, CancellationToken ct)
    {
        var queue = await GetQueueAsync(runId, ct).ConfigureAwait(false);
        var applied = 0;
        var skipped = new List<string>();
        var warnings = new List<string>();
        foreach (var candidate in queue)
        {
            var result = await action(candidate).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                applied++;
                warnings.AddRange(result.Warnings);
            }
            else
            {
                skipped.Add(string.Format(Messages.BulkSkippedFormat, candidate.Title, result.Error));
            }
        }
        return Result.Ok(new BulkOutcome(applied, skipped), warnings);
    }

    // ---------- 補助 ----------

    /// <summary>ゲートの外から呼ぶ。実行を終了状態にして通知する。</summary>
    private async Task<Result<MorningRun>> FailAsync(MorningRun run, string error)
    {
        await FinishAsync(run, MorningRunStatus.Failed, error).ConfigureAwait(false);
        Raise(run, null, candidatesChanged: false);
        return Result.Fail<MorningRun>(error);
    }

    /// <summary>追従スレッドからの保存失敗で実行を殺さない。理由は警告として UI へ回す。</summary>
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

    private void Raise(MorningRun run, string? warning, bool candidatesChanged)
        => RunChanged?.Invoke(this, new MorningRunChangedEventArgs(
            new MorningRunSnapshot(run.Id, run.Date, run.Status, TurnCountOf(run.Id), run.ErrorMessage, run.JobFolder),
            warning, candidatesChanged));
}
