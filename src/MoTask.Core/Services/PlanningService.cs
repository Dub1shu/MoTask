using System.Collections.Concurrent;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Planning;

namespace MoTask.Core.Services;

/// <summary>
/// 計画づくりのライフサイクル(仕様 §6・§10)。AiJobService と同じ構えだが、終了時の副作用が違う
/// (あちらはタスクを確認待ちへ動かし、こちらは候補と計画を取り込む)ので共通化しない。
/// DB は BoardService / AiJobService と共有の OperationGate で直列化する。
/// <b>ゲートの中から IBoardService を呼ぶとデッドロックする</b>ので、登録・統合はゲートの外で呼ぶ。
/// </summary>
public sealed class PlanningService : IPlanningService
{
    private readonly IPlanningRepository _runs;
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

    /// <summary>
    /// 閉じるのを予約した実行(仕様 §7)。planning_complete の直後に殺すと Claude の最後の
    /// 一言が切れるので、次に来る Stop まで待つ。
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _closePending = new();

    public event EventHandler<PlanningRunChangedEventArgs>? RunChanged;
    public event EventHandler? BoardChanged;

    public PlanningService(
        IPlanningRepository runs, IBoardRepository boards, IAiJobRepository jobs, IHistoryRepository history,
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

        // 所有した端末が先に死んだら気づけるようにする(仕様 §7)。PlanningService も
        // ISessionLauncher もアプリに 1 つずつの singleton なので、外すことはしない。
        _launcher.OwnedSessionExited += OnOwnedSessionExited;
    }

    // ---------- 照会 ----------

    public Task<PlanningRun?> GetCurrentRunAsync(CancellationToken ct = default)
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

    private sealed record Prepared(int RunNumber);

    /// <summary>
    /// 盤面のスナップショット。ゲートの中から呼ぶこと(リポジトリを 4 つ引く)。
    /// planning_get_context が返す形はこれ 1 つ(仕様 §4)。盤面が無ければ null。
    /// </summary>
    private async Task<string?> BuildSnapshotAsync(DateOnly date, CancellationToken ct)
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return null;

        var projects = await _boards.GetProjectsAsync(ct).ConfigureAwait(false);
        var busy = await _jobs.GetByStatusAsync(
            new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct)
            .ConfigureAwait(false);
        var labels = await _boards.GetLabelsAsync(ct).ConfigureAwait(false);

        return BoardSnapshot.Build(
            board, date,
            projects.ToDictionary(p => p.Id, p => p.Name),
            busy.Select(j => j.TaskId).ToHashSet(),
            labels);
    }

    public async Task<Result<PlanningRun>> StartAsync(CancellationToken ct = default)
    {
        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<PlanningRun>(available.Error!);

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings.Load();
            var date = _clock.Today;

            var prepared = await _gate.RunAsync(async () =>
            {
                // 二重起動の防止(仕様 §12)。追跡中かどうかは DB で数える。
                var unfinished = await _runs.GetUnfinishedRunAsync(ct).ConfigureAwait(false);
                if (unfinished is not null) return Result.Fail<Prepared>(Messages.PlanningRunAlreadyRunning);

                // 盤面は planning_get_context が返すので、ここでは「在るか」だけを見る（仕様 §4）
                var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
                if (board is null) return Result.Fail<Prepared>(Messages.BoardNotFound);

                // フォルダ名の連番。DB の採番を待たずに決まる（親仕様 §12）。
                var runNumber = await _runs.CountRunsAsync(ct).ConfigureAwait(false) + 1;
                return Result.Ok(new Prepared(runNumber));
            }, ct).ConfigureAwait(false);
            if (!prepared.IsSuccess) return Result.Fail<PlanningRun>(prepared.Error!);

            // ここから先はファイル操作と端末の起動なので、ゲートの外でやる。
            var request = new JobFolderRequest(prepared.Value!.RunNumber, date.ToString("yyyy-MM-dd"), "")
            {
                Category = JobFolderPaths.PlanningDirectoryName,
                OutputDirectoryName = null,
                WithMcpConfig = true,
            };
            // 指示文は runId（DB の採番）を含むので、行を保存してからでないと組み立てられない
            // （仕様 §8）。フォルダと hooks.json / mcp.json だけを先に作り、instruction.md は後で書く。
            var root = _folder.ResolveRoot(request);
            var created = _folder.Create(request);
            if (!created.IsSuccess) return Result.Fail<PlanningRun>(created.Error!);

            var sessionId = Guid.NewGuid();
            // cwd はジョブフォルダ自身。計画づくりはソースツリーに用が無い(仕様 §6)。
            // 成果はファイルではなく MCP で渡すので、起動プロンプトに出力先を出さない(仕様 §5.4)。
            var command = _launcher.BuildCommand(new SessionLaunchRequest(
                sessionId, root, root, Resume: false,
                OutputDirectoryName: null, CloseOnExit: true,
                McpConfigPath: JobFolderPaths.For(root).McpJson));
            if (!command.IsSuccess) return Result.Fail<PlanningRun>(command.Error!);

            // フォルダとコマンドが確定してから DB に書く。
            // AiJobService の「JobFolder が空のまま Pending で残る」窓をこちらでは作らない(仕様 §12)。
            var now = _clock.UtcNow;
            var run = new PlanningRun
            {
                Date = date, Status = PlanningRunStatus.Pending, SessionId = sessionId,
                Instruction = "", JobFolder = root, StartedAt = now,
            };
            var saved = await _gate.RunAsync(async () =>
            {
                try
                {
                    _runs.Add(run);
                    // Id が要るのでいったん保存し、採番してから指示文を作って書き戻す（仕様 §8）。
                    // ゲートの取得は 1 回のままで、SaveChanges が 2 回走るだけ。
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    run.Instruction = PlanningInstruction.Build(settings.PlanningInstruction, date, run.Id);
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Result.Ok();
                }
                catch (PersistenceException ex)
                {
                    var error = $"{Messages.SaveFailed}: {ex.Message}";
                    // EfUnitOfWork.SaveChangesAsync は例外を投げる前に ChangeTracker.Clear()
                    // する(次の GetBoard が DB を読み直せるように)ので、この時点で run は
                    // Detach 済み。run.Status を書き換えて SaveChanges しても追跡対象がゼロ
                    // で何も保存されない。読み直せば追跡された同一インスタンスが返る
                    // (IPlanningRepository の契約)ので、それを倒す。1 回目の保存自体が
                    // 落ちていて何も commit されていなければ null(何もしない、で正しい)。
                    // ここは既にゲートの中なので FailAsync(ゲートを取り直す)は呼べない。
                    // ベストエフォートで倒すだけにし、後始末自体が落ちても元のエラーを
                    // 黙って優先する。
                    try
                    {
                        var persisted = await _runs.GetUnfinishedRunAsync(ct).ConfigureAwait(false);
                        if (persisted is not null)
                        {
                            persisted.Status = PlanningRunStatus.Failed;
                            persisted.ErrorMessage = error;
                            persisted.EndedAt = _clock.UtcNow;
                            await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                        // 後始末も失敗。GetUnfinishedRunAsync は PersistenceException に包まない
                        // ので、DB ロックなどで落ちると別種の例外になりうる。ここは後始末なので、
                        // 何が起きても握りつぶし、元の保存エラー(error)を優先して返す。行は
                        // Pending のまま残るが、これ以上リトライしない。
                    }
                    return Result.Fail(error);
                }
            }, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result.Fail<PlanningRun>(saved.Error!);

            var wroteInstruction = _folder.WriteText(
                root, JobFolderPaths.InstructionMarkdownName, run.Instruction);
            if (!wroteInstruction.IsSuccess)
            {
                return await FailAsync(run, wroteInstruction.Error!).ConfigureAwait(false);
            }

            // 追従は起動より先に掛ける。起こした端末が即死すると OwnedSessionExited は
            // LaunchOwned が戻る前にも届きうるので、後から掛けると「終わった実行に追従を
            // 掛け直す」ことになり、誰も止めないポーラーが残る（仕様 §7）。
            // 起動に失敗した場合は FailAsync → FinishAsync が StopFollowing まで面倒を見る。
            _turns[run.Id] = 0;
            Follow(run.Id, root, skipLines: 0);

            // 計画づくりは MoTask が所有する。完了時に窓を閉じるには Process ハンドルが要る（仕様 §5.3）。
            var owned = _launcher.LaunchOwned(run.Id, command.Value!);
            if (!owned.IsSuccess) return await FailAsync(run, owned.Error!).ConfigureAwait(false);

            // 掛け直しの材料（pid と開始時刻）は起動できてからでないと書けないので、run.json は
            // 起動の後に書く。書けなくても実行そのものは続ける（端末はもう走っている）が、
            // 再起動後に掛け直せなくなるので警告として人に見せる（仕様 §7）。
            var wroteRun = _folder.WriteText(root, JobFolderPaths.RunJsonName, PlanningRunDescriptor.Serialize(
                new PlanningRunDescriptor(run.Id, date, sessionId, root, command.Value!.Display, now,
                    owned.Value!.ProcessId, owned.Value!.StartedAt)));

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

        // 取り込んだ実行は終端なので普段は行を捨てるが、閉じる予約がある間だけはターンの
        // 終わりを見る。Claude が最後の一言を言い終えてから窓を消すため(仕様 §7)。
        if (_closePending.ContainsKey(runId)
            && parsed.Kind is AiJobEventKind.TurnEnded or AiJobEventKind.SessionEnded)
        {
            CloseIfPending(runId);
            return;
        }

        PlanningRun? run = null;
        var abandoned = false;

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
                    current.Status = PlanningRunStatus.Running;
                    break;
                case AiJobEventKind.TurnEnded:
                    // Stop のたびに result/ を見に行くのはやめた。成果は MCP で届く(仕様 §4)。
                    current.Status = PlanningRunStatus.Running;
                    _turns.AddOrUpdate(runId, 1, (_, turns) => turns + 1);
                    break;
                case AiJobEventKind.SessionEnded:
                    // planning_complete が来ないまま終わった(仕様 §7)
                    current.Status = PlanningRunStatus.Failed;
                    current.ErrorMessage = Messages.PlanningCompleteMissing;
                    current.EndedAt = _clock.UtcNow;
                    abandoned = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        if (abandoned)
        {
            _events.StopFollowing(run.Id);
            _turns.TryRemove(run.Id, out _);
            _launcher.CloseOwned(run.Id);
        }
        Raise(run, warning, candidatesChanged: false);
    }

    /// <summary>events.jsonl が消えた／作り直された(仕様 §12)。状態は変えず、注意だけ出す。</summary>
    private async Task OnProblemAsync(int runId, string message)
    {
        _events.StopFollowing(runId);
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId)).ConfigureAwait(false);
        if (run is null) return;
        Raise(run, message, candidatesChanged: false);
    }

    // ---------- MCP 経由の受け口（仕様 §6） ----------

    public Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<string>(runId);

            var snapshot = await BuildSnapshotAsync(run.Date, ct).ConfigureAwait(false);
            return snapshot is null ? Result.Fail<string>(Messages.BoardNotFound) : Result.Ok(snapshot);
        }, ct);

    public async Task<Result<CandidateOutcome>> AddCandidateAsync(
        int runId, CandidateInput input, CancellationToken ct = default)
    {
        // 形の検証はゲートの外(純関数なので DB を待たせない)
        var validated = CandidateValidator.Validate(input);

        PlanningRun? accepted = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<CandidateOutcome>(runId);

            var mine = await _runs.GetCandidatesOfRunAsync(runId, ct).ConfigureAwait(false);
            var total = mine.Count;
            if (!validated.IsSuccess) return Refused(validated.Error!, total);

            var record = validated.Value!;
            if (record.SuggestedAction == TriageAction.Merge)
            {
                var target = await _boards.GetTaskAsync(record.MergeTargetTaskId!.Value, ct).ConfigureAwait(false);
                if (target is null || target.IsDeleted) return Refused(Messages.CandidateMergeTargetMissing, total);
            }

            // 推薦ラベルは既存のものだけ（AI にラベルを作らせない）。名前は id に直して持つ。
            var labelIds = new List<int>();
            if (input.SuggestedLabels is { Count: > 0 } names)
            {
                var labels = await _boards.GetLabelsAsync(ct).ConfigureAwait(false);
                var resolved = SuggestedLabels.Resolve(names, labels);
                if (!resolved.IsSuccess) return Refused(resolved.Error!, total);
                labelIds = resolved.Value!;
            }

            // 却下・登録済みの ExternalId は次の実行でまた出てきても積まない(親仕様 §9)。
            // 現行は黙って捨てていたが、ここでは理由を返す(仕様 §6)。
            var known = await _runs.GetKnownExternalIdsAsync(new[] { record.ExternalId }, ct).ConfigureAwait(false);
            if (known.Count > 0)
            {
                var inThisRun = mine.Any(c => string.Equals(c.ExternalId, record.ExternalId, StringComparison.Ordinal));
                return Refused(
                    inThisRun ? Messages.CandidateAlreadyInThisRun : Messages.CandidateAlreadyDecidedElsewhere, total);
            }

            var candidate = new TriageCandidate
            {
                PlanningRunId = run.Id,
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
                SuggestedLabelIds = labelIds,
                Status = TriageStatus.Pending,
            };
            _runs.AddCandidate(candidate);
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            accepted = run;
            // 保存できていなければ積めていない。Accepted:true を返すと Claude は積まれたと
            // 信じて次へ進み、候補は黙って消える。ツールエラーにはせず、理由を返して
            // 呼び直せるようにする(Finding 2)。
            return warning is null
                ? Result.Ok(new CandidateOutcome(true, null, candidate.Id, total + 1))
                : Refused(warning, total);
        }, ct).ConfigureAwait(false);

        // 受理したときだけ知らせる。候補キューが 1 件ずつ増える(仕様 §6)。
        if (accepted is not null) Raise(accepted, warning, candidatesChanged: true);
        return result;
    }

    public async Task<Result<PlanningOutcome>> SubmitPlanAsync(
        int runId, string planJson, CancellationToken ct = default)
    {
        var validated = PlanValidator.Validate(planJson);

        PlanningRun? saved = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<PlanningOutcome>(runId);
            // 受理しなかった計画で、前に受理したものを上書きしない
            if (!validated.IsSuccess) return Result.Ok(new PlanningOutcome(false, validated.Error!));

            run.PlanJson = validated.Value!;
            run.Status = PlanningRunStatus.Running;
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            saved = run;
            return Result.Ok(new PlanningOutcome(true, null));
        }, ct).ConfigureAwait(false);

        if (saved is not null) Raise(saved, warning, candidatesChanged: false);
        return result;
    }

    /// <summary>宛先違い。利用者の普段使いの Claude Code が誤って動かす事故を防ぐ(仕様 §6)。</summary>
    private static Result<T> NotRunning<T>(int runId)
        => Result.Fail<T>(string.Format(Messages.PlanningRunNotRunningFormat, runId));

    /// <summary>受け取ったが積まなかった。ツールエラーではない(仕様 §3)。</summary>
    private static Result<CandidateOutcome> Refused(string reason, int total)
        => Result.Ok(new CandidateOutcome(false, reason, 0, total));

    // ---------- 人の操作 ----------

    public async Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
    {
        // 人の「完了にする」は、端末が詰まっているか既に閉じられている場面で押される。
        // 待つべき Stop が来る保証が無いのでその場で閉じる(仕様 §7)。
        var result = await CompleteRunAsync(runId, closeNow: true, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return Result.Fail(result.Error!);
        // result.Value.Reason は Claude 向け(MCP ツール名を含む)。人には出さない。
        // CompleteRunAsync が Accepted:false を返す理由は今のところ「計画未提出」の
        // 1 種類だけ(他の拒否は NotRunning = Result.Fail でここまで来ない)。理由が増えたら
        // ここも作り直すこと。
        return result.Value!.Accepted ? Result.Ok() : Result.Fail(Messages.PlanningCompleteWithoutPlan);
    }

    public async Task<Result<PlanningOutcome>> CompleteRunAsync(
        int runId, bool closeNow, CancellationToken ct = default)
    {
        PlanningRun? finished = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<PlanningOutcome>(runId);
            // 計画の無い実行は終わらせない(仕様 §6)。候補 0 件は失敗ではない(親仕様 §8)。
            if (run.PlanJson is not { Length: > 0 })
            {
                return Result.Ok(new PlanningOutcome(false, Messages.PlanNotSubmitted));
            }

            run.Status = PlanningRunStatus.Ingested;
            run.EndedAt = _clock.UtcNow;
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            finished = run;
            return Result.Ok(new PlanningOutcome(true, null));
        }, ct).ConfigureAwait(false);

        if (finished is null) return result;

        _turns.TryRemove(finished.Id, out _);
        if (closeNow)
        {
            _events.StopFollowing(finished.Id);
            _launcher.CloseOwned(finished.Id);
        }
        else
        {
            // ツール結果を返した直後に殺すと Claude の最後の一言が切れる。追従は予約が
            // 解けるまで続ける(Stop を受け取る必要がある)。
            ReserveClose(finished.Id);
        }
        Raise(finished, warning, candidatesChanged: false);
        return result;
    }

    public async Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
    {
        PlanningRun? run = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.PlanningRunNotFound);
            // この IsTerminal() 確認と、下の Status への書き込みは同じコールバックの中に
            // 置いたままにする(分けない)。AiJobService.FinishByHandAsync と同じ形。
            // 別々の _gate.RunAsync に分けると、その間に OnHookLineAsync が取り込みを終わらせて
            // Ingested にできてしまい、ここが無条件に Cancelled で上書きしてしまう。
            if (current.Status.IsTerminal()) return Result.Fail(Messages.PlanningRunAlreadyFinished);
            run = current;
            // 仕事が終わったわけではないので取り込まない。端末も殺さない(仕様 §12)。
            current.Status = PlanningRunStatus.Cancelled;
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

        // 追従は掛け直しより先に。掛け直した先が既に死んでいれば OwnedSessionExited は
        // TryReattach が戻る前にも届きうるので、後から掛けると終わった実行にポーラーが残る
        // (StartAsync と同じ理由・仕様 §7)。
        Follow(run.Id, run.JobFolder, run.ProcessedLines);

        // 掛け直せなければ「閉じる能力」だけを諦め、追従(events.jsonl)は続ける(仕様 §7)。
        // 開始時刻を照合するのは launcher 側の仕事で、ここは材料を渡すだけ。
        var descriptor = PlanningRunDescriptor.TryParse(
            _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName));
        if (descriptor is { ProcessId: > 0 })
        {
            _launcher.TryReattach(run.Id, descriptor.ProcessId, descriptor.ProcessStartedAt);
        }
    }

    /// <summary>
    /// 予約から Stop が来ないまま閉じるまでの保険(仕様 §7)。既定 60 秒。テストだけが短くする。
    /// </summary>
    public TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>保険の待ち合わせ。イベントと同じくテストが待てるように直近の 1 本を残す。</summary>
    public Task PendingClose { get; private set; } = Task.CompletedTask;

    private void ReserveClose(int runId)
    {
        _closePending[runId] = 0;
        PendingClose = CloseAfterGraceAsync(runId);
    }

    private async Task CloseAfterGraceAsync(int runId)
    {
        await Task.Delay(CloseGrace).ConfigureAwait(false);
        CloseIfPending(runId);
    }

    /// <summary>予約が残っていれば閉じる。Stop と保険のどちらが先でも 1 度しか閉じない。</summary>
    private void CloseIfPending(int runId)
    {
        if (!_closePending.TryRemove(runId, out _)) return;
        _events.StopFollowing(runId);
        _launcher.CloseOwned(runId);
    }

    /// <summary>
    /// 端末の終了を受けた後始末。イベントは void で来るので、直近の 1 本をここに残して
    /// テストが待てるようにする(PlanViewModel.PendingLoad と同じ手)。
    /// </summary>
    public Task PendingTerminalExit { get; private set; } = Task.CompletedTask;

    private void OnOwnedSessionExited(object? sender, int runId)
        => PendingTerminalExit = OnOwnedSessionExitedAsync(runId);

    /// <summary>
    /// 端末が先に死んだ(人が × で閉じた・claude が落ちた)。成果はもう result/ を経由しないので、
    /// 救い出す当てはない。終わっていない実行は Failed にして降りる。
    /// 終端の実行はそのまま(閉じたのがこちらの CloseOwned なら、そもそもここへ来ない)。
    /// </summary>
    private async Task OnOwnedSessionExitedAsync(int runId)
    {
        PlanningRun? run = null;
        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            // 知らない runId・終わった実行は黙って降りる(終端の実行を蘇らせない)。
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            run = current;
            current.Status = PlanningRunStatus.Failed;
            current.ErrorMessage = Messages.PlanningTerminalClosed;
            current.EndedAt = _clock.UtcNow;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null)
        {
            // 終端の実行(closePending中も含む)。端末はもう自分で死んでいるので、予約が残っていれば
            // 捨てて追従も降りる。予約だけ捨てて StopFollowing を呼ばないと、closeNow:false は
            // 「追従は予約が解けるまで続ける」設計なので、この経路だけ誰も止めない follower が
            // アプリ終了まで残る(B1)。捨てないと最大 CloseGrace 秒後に死んだ端末へも
            // 無意味な CloseOwned を打つ。
            if (_closePending.TryRemove(runId, out _)) _events.StopFollowing(runId);
            return;
        }
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        Raise(run, warning, candidatesChanged: false);
    }

    /// <summary>ゲートの外から呼ぶ。終了状態にして追従を降りる。戻り値はバナー向けの警告。</summary>
    private async Task<string?> FinishAsync(PlanningRun run, PlanningRunStatus status, string? error)
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

        var labeled = await AttachLabelsAsync(task.Id, decision.LabelIds, ct).ConfigureAwait(false);
        if (!labeled.IsSuccess)
        {
            // 仕上げの書き込みが失敗したときと同じ理由で、作ったタスクを残さない。
            await _boardService.DeleteTaskAsync(task.Id, ct).ConfigureAwait(false);
            return Result.Fail<TaskItem>(labeled.Error!);
        }

        BoardChanged?.Invoke(this, EventArgs.Empty);

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
        BoardChanged?.Invoke(this, EventArgs.Empty);

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

        await RaiseCandidatesChangedAsync(candidate.PlanningRunId, warning, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 登録したタスクにラベルを付ける。アーカイブ済みや見つからない id は黙って落とす(登録欄では
    /// もともと選べないもの)。付けるものが無ければ何もしない(履歴を増やさない)。
    /// ゲートの外から呼ぶこと(IBoardService を使う)。
    /// </summary>
    private async Task<Result> AttachLabelsAsync(int taskId, IReadOnlyList<int>? labelIds, CancellationToken ct)
    {
        if (labelIds is not { Count: > 0 }) return Result.Ok();

        var labels = await _boardService.GetLabelsAsync(ct).ConfigureAwait(false);
        var active = labels.Where(l => !l.Archived).Select(l => l.Id).ToHashSet();
        var keep = labelIds.Where(active.Contains).Distinct().ToList();
        if (keep.Count == 0) return Result.Ok();

        return await _boardService.SetTaskLabelsAsync(taskId, keep, ct).ConfigureAwait(false);
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
            candidate.Id, candidate.Title, candidate.SuggestedDueDate, candidate.SuggestedProject, columnId,
            candidate.SuggestedLabelIds), ct)
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
    private async Task<Result<PlanningRun>> FailAsync(PlanningRun run, string error)
    {
        await FinishAsync(run, PlanningRunStatus.Failed, error).ConfigureAwait(false);
        Raise(run, null, candidatesChanged: false);
        return Result.Fail<PlanningRun>(error);
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

    private void Raise(PlanningRun run, string? warning, bool candidatesChanged)
        => RunChanged?.Invoke(this, new PlanningRunChangedEventArgs(
            new PlanningRunSnapshot(run.Id, run.Date, run.Status, TurnCountOf(run.Id), run.ErrorMessage, run.JobFolder),
            warning, candidatesChanged));
}
