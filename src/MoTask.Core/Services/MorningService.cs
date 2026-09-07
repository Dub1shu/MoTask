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
    }

    // ---------- 照会 ----------

    public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetLatestRunAsync(ct), ct);

    public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetQueueAsync(runId, ct), ct);

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
            var command = _launcher.BuildCommand(new SessionLaunchRequest(sessionId, root, root, Resume: false));
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

            _folder.WriteText(root, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
                new MorningRunDescriptor(run.Id, date, sessionId, root, command.Value!.Display, now)));

            var launched = _launcher.Launch(command.Value!);
            if (!launched.IsSuccess) return await FailAsync(run, launched.Error!).ConfigureAwait(false);

            _turns[run.Id] = 0;
            Follow(run.Id, root, skipLines: 0);
            Raise(run, null, candidatesChanged: false);
            return Result.Ok(run);
        }
        finally
        {
            _startLock.Release();
        }
    }

    // ---------- 追従(Task 8 で埋める) ----------

    private void Follow(int runId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            runId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(runId, line),
            message => OnProblemAsync(runId, message)));
    }

    private Task OnHookLineAsync(int runId, string line) => Task.CompletedTask; // Task 8 で実装する

    private Task OnProblemAsync(int runId, string message) => Task.CompletedTask; // Task 8 で実装する

    public Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.MorningRunNotFound)); // Task 8 で実装する

    public Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.MorningRunNotFound)); // Task 8 で実装する

    public Task RecoverOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask; // Task 8 で実装する

    // ---------- 仕分け(Task 9 で埋める) ----------

    public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
        => Task.FromResult(Result.Fail<TaskItem>(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    // ---------- 補助 ----------

    /// <summary>ゲートの外から呼ぶ。実行を終了状態にして通知する。</summary>
    private async Task<Result<MorningRun>> FailAsync(MorningRun run, string error)
    {
        await _gate.RunAsync(async () =>
        {
            run.ErrorMessage = error;
            run.Status = MorningRunStatus.Failed;
            run.EndedAt = _clock.UtcNow;
            await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
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
