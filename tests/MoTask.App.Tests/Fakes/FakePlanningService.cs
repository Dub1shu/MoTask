using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

/// <summary>MergeAsync に渡された引数の記録。</summary>
public sealed record MergeCall(int CandidateId, int TargetTaskId);

/// <summary>AddCandidateAsync に渡された引数の記録。</summary>
public sealed record AddCandidateCall(int RunId, CandidateInput Input);

/// <summary>SubmitPlanAsync に渡された引数の記録。</summary>
public sealed record SubmitPlanCall(int RunId, string PlanJson);

/// <summary>CompleteRunAsync に渡された引数の記録。closeNow がそのまま渡るかを見るのに使う。</summary>
public sealed record CompleteRunCall(int RunId, bool CloseNow);

/// <summary>
/// 呼ばれた操作を記録する偽サービス。候補は全状態を 1 つのリストに持ち、キューは実リポジトリと
/// 同じ規則（この実行の Pending ＋ 他の実行の Later）で計算する。4 アクションは実サービスと同じく
/// Status / ResultTaskId を書き換える（そうしないと「登録した行が実タスクに変わる」を試せない）。
/// </summary>
public sealed class FakePlanningService : IPlanningService
{
    public event EventHandler<PlanningRunChangedEventArgs>? RunChanged;
    public event EventHandler? BoardChanged;

    /// <summary>BoardChanged を発火させる。</summary>
    public void RaiseBoardChanged() => BoardChanged?.Invoke(this, EventArgs.Empty);

    public PlanningRun? Current { get; set; }
    public List<TriageCandidate> Candidates { get; } = new();
    public List<string> Calls { get; } = new();
    public Result<PlanningRun> StartResult { get; set; } = Result.Ok(new PlanningRun());
    public Result<TaskItem> RegisterResult { get; set; } = Result.Ok(new TaskItem { Id = 1 });
    public CandidateDecision? LastDecision { get; private set; }

    /// <summary>RefreshAsync が例外を握りつぶさずバナーへ回すことを確かめるためのフック。</summary>
    public Exception? FailNextGetCurrentRun { get; set; }

    /// <summary>RegisterAsync に渡された引数を呼ばれた順に。</summary>
    public List<CandidateDecision> RegisterCalls { get; } = new();

    /// <summary>MergeAsync に渡された引数を呼ばれた順に。</summary>
    public List<MergeCall> MergeCalls { get; } = new();

    /// <summary>PostponeAsync に渡された candidateId を呼ばれた順に。</summary>
    public List<int> PostponeCalls { get; } = new();

    /// <summary>RejectAsync に渡された candidateId を呼ばれた順に。</summary>
    public List<int> RejectCalls { get; } = new();

    /// <summary>Reject の応答を差し替える口。TriagePanelViewModelTests が待たせるのに使う。</summary>
    public Func<int, Task<Result>>? OnReject { get; set; }

    public void Raise(PlanningRun run, string? warning = null, bool candidates = false)
        => RunChanged?.Invoke(this, new PlanningRunChangedEventArgs(
            new PlanningRunSnapshot(run.Id, run.Date, run.Status, 0, run.ErrorMessage, run.JobFolder),
            warning, candidates));

    public Task<PlanningRun?> GetCurrentRunAsync(CancellationToken ct = default)
    {
        if (FailNextGetCurrentRun is { } ex)
        {
            FailNextGetCurrentRun = null;
            throw ex;
        }
        return Task.FromResult(Current);
    }

    public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TriageCandidate>>(Candidates
            .Where(c => (c.PlanningRunId == runId && c.Status == TriageStatus.Pending)
                        || (c.PlanningRunId != runId && c.Status == TriageStatus.Later))
            .OrderBy(c => c.Id).ToList());

    public Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TriageCandidate>>(
            Candidates.Where(c => c.PlanningRunId == runId).OrderBy(c => c.Id).ToList());

    public Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public int TurnCountOf(int runId) => 0;

    public Task<Result<PlanningRun>> StartAsync(CancellationToken ct = default)
    {
        Calls.Add("Start");
        if (StartResult.IsSuccess) Current = StartResult.Value;
        return Task.FromResult(StartResult);
    }

    public Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
    {
        Calls.Add("Complete");
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
    {
        Calls.Add("StopTracking");
        return Task.FromResult(Result.Ok());
    }

    public Task RecoverOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 実サービス(PlanningService.DecideAsync)と同じく、候補を書き換えたら RunChanged
    /// (candidatesChanged: true) を上げる。これが無いと C1(仕分けのたびにキューが二重になる)を
    /// 偽サービスで再現できない(finding I2)。
    /// </summary>
    private void Decide(int candidateId, TriageStatus status, int? resultTaskId = null)
    {
        var candidate = Candidates.Single(c => c.Id == candidateId);
        candidate.Status = status;
        candidate.ResultTaskId = resultTaskId;
        if (Current is not null) Raise(Current, warning: null, candidates: true);
    }

    public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
    {
        Calls.Add("Register");
        RegisterCalls.Add(decision);
        LastDecision = decision;
        if (RegisterResult.IsSuccess) Decide(decision.CandidateId, TriageStatus.Registered, RegisterResult.Value!.Id);
        return Task.FromResult(RegisterResult);
    }

    public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
    {
        Calls.Add($"Merge:{targetTaskId}");
        MergeCalls.Add(new MergeCall(candidateId, targetTaskId));
        Decide(candidateId, TriageStatus.Merged, targetTaskId);
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
    {
        Calls.Add("Postpone");
        PostponeCalls.Add(candidateId);
        Decide(candidateId, TriageStatus.Later);
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
    {
        Calls.Add("Reject");
        RejectCalls.Add(candidateId);
        if (OnReject is { } on) return on(candidateId);
        Decide(candidateId, TriageStatus.Rejected);
        return Task.FromResult(Result.Ok());
    }

    public Result<BulkOutcome> BulkResult { get; set; } = Result.Ok(new BulkOutcome(0, Array.Empty<string>()));

    public Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default)
    {
        Calls.Add($"ApplySuggestions:{registerColumnId}");
        foreach (var candidate in Candidates.Where(c => c.Status == TriageStatus.Pending).ToList())
        {
            Decide(candidate.Id, candidate.SuggestedAction switch
            {
                TriageAction.Register => TriageStatus.Registered,
                TriageAction.Merge => TriageStatus.Merged,
                TriageAction.Later => TriageStatus.Later,
                _ => TriageStatus.Rejected,
            });
        }
        return Task.FromResult(BulkResult);
    }

    // ---- MCP 経由の受け口（仕様 §6）----

    /// <summary>GetContextAsync が返す盤面。宛先違いを試すテストは Result.Fail に差し替える。</summary>
    public Result<string> ContextResult { get; set; } = Result.Ok("{}");

    /// <summary>AddCandidateAsync が返す結果。</summary>
    public Result<CandidateOutcome> AddCandidateResult { get; set; } = Result.Ok(new CandidateOutcome(true, null, 1, 1));

    /// <summary>SubmitPlanAsync が返す結果。</summary>
    public Result<PlanningOutcome> SubmitPlanResult { get; set; } = Result.Ok(new PlanningOutcome(true, null));

    /// <summary>GetContextAsync に渡された runId を呼ばれた順に。</summary>
    public List<int> GetContextCalls { get; } = new();

    /// <summary>AddCandidateAsync に渡された引数を呼ばれた順に。</summary>
    public List<AddCandidateCall> AddCandidateCalls { get; } = new();

    /// <summary>SubmitPlanAsync に渡された引数を呼ばれた順に。</summary>
    public List<SubmitPlanCall> SubmitPlanCalls { get; } = new();

    public Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default)
    {
        Calls.Add("GetContext");
        GetContextCalls.Add(runId);
        return Task.FromResult(ContextResult);
    }

    public Task<Result<CandidateOutcome>> AddCandidateAsync(
        int runId, CandidateInput input, CancellationToken ct = default)
    {
        Calls.Add("AddCandidate");
        AddCandidateCalls.Add(new AddCandidateCall(runId, input));
        return Task.FromResult(AddCandidateResult);
    }

    public Task<Result<PlanningOutcome>> SubmitPlanAsync(
        int runId, string planJson, CancellationToken ct = default)
    {
        Calls.Add("SubmitPlan");
        SubmitPlanCalls.Add(new SubmitPlanCall(runId, planJson));
        return Task.FromResult(SubmitPlanResult);
    }

    public Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default)
    {
        Calls.Add("PostponeAll");
        foreach (var candidate in Candidates.Where(c => c.Status == TriageStatus.Pending).ToList())
            Decide(candidate.Id, TriageStatus.Later);
        return Task.FromResult(BulkResult);
    }

    /// <summary>CompleteRunAsync が返す結果。</summary>
    public Result<PlanningOutcome> CompleteRunResult { get; set; } = Result.Ok(new PlanningOutcome(true, null));

    /// <summary>CompleteRunAsync に渡された引数を呼ばれた順に。</summary>
    public List<CompleteRunCall> CompleteRunCalls { get; } = new();

    public Task<Result<PlanningOutcome>> CompleteRunAsync(
        int runId, bool closeNow, CancellationToken ct = default)
    {
        Calls.Add("CompleteRun");
        CompleteRunCalls.Add(new CompleteRunCall(runId, closeNow));
        return Task.FromResult(CompleteRunResult);
    }
}
