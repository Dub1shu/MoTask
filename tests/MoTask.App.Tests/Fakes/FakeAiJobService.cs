using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

/// <summary>StartJobAsync に渡された引数の記録。</summary>
public sealed record StartJobCall(int TaskId, AiJobKind Kind, string Instruction);

/// <summary>GetEventsAsync に渡された引数の記録。</summary>
public sealed record GetEventsCall(int JobId, int Lines);

/// <summary>
/// 端末は開かない。ジョブとイベントは 1 つのリストに持ち、照会は実サービスと同じ規則で絞る。
/// 既定では成功を返し、テストが変えたい所だけ On～ を挿す。
/// </summary>
public sealed class FakeAiJobService : IAiJobService
{
    public event EventHandler<AiJobChangedEventArgs>? JobChanged;

    /// <summary>テストが直接積むジョブ。GetJobsForTaskAsync と GetUnfinishedJobsAsync はここから絞る。</summary>
    public List<AiJob> Jobs { get; } = new();

    /// <summary>テストが直接積むイベント。GetEventsAsync はここから絞る。</summary>
    public List<AiJobEvent> Events { get; } = new();

    /// <summary>GetArtifactsAsync が返すもの。</summary>
    public List<string> Artifacts { get; } = new();

    /// <summary>TurnCountOf が返す値。無ければ 0。</summary>
    public Dictionary<int, int> TurnCounts { get; } = new();

    /// <summary>StartJobAsync に渡された引数を呼ばれた順に。</summary>
    public List<StartJobCall> StartJobCalls { get; } = new();

    /// <summary>GetEventsAsync に渡された引数を呼ばれた順に。</summary>
    public List<GetEventsCall> GetEventsCalls { get; } = new();

    /// <summary>GetArtifactsAsync に渡された jobId を呼ばれた順に。</summary>
    public List<int> GetArtifactsCalls { get; } = new();

    /// <summary>CompleteJobAsync に渡された jobId を呼ばれた順に。</summary>
    public List<int> CompleteJobCalls { get; } = new();

    /// <summary>StopTrackingAsync に渡された jobId を呼ばれた順に。</summary>
    public List<int> StopTrackingCalls { get; } = new();

    /// <summary>ReopenTerminalAsync に渡された jobId を呼ばれた順に。</summary>
    public List<int> ReopenTerminalCalls { get; } = new();

    /// <summary>RecoverOnStartupAsync が呼ばれた回数。</summary>
    public int RecoverOnStartupCalls { get; private set; }

    /// <summary>StartJobAsync の既定応答を差し替える。null なら Result.Ok(AiJob) を返す。</summary>
    public Func<StartJobCall, Task<Result<AiJob>>>? OnStartJob { get; set; }

    /// <summary>GetEventsAsync の既定応答を差し替える。null なら Events から絞ったものを返す。</summary>
    public Func<GetEventsCall, Task<IReadOnlyList<AiJobEvent>>>? OnGetEvents { get; set; }

    /// <summary>GetArtifactsAsync の既定応答を差し替える。null なら Artifacts を返す。</summary>
    public Func<int, Task<IReadOnlyList<string>>>? OnGetArtifacts { get; set; }

    /// <summary>CompleteJobAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnCompleteJob { get; set; }

    /// <summary>StopTrackingAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnStopTracking { get; set; }

    /// <summary>ReopenTerminalAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnReopenTerminal { get; set; }

    /// <summary>
    /// 実サービスと同じく、上げるのは AiJob そのものではなく AiJobSnapshot。
    /// TaskAiPanelViewModelTests の Snapshot(...) ヘルパーがこの型を作る。
    /// </summary>
    public void RaiseJobChanged(AiJobSnapshot job, AiJobEvent? newEvent = null, string? warning = null)
        => JobChanged?.Invoke(this, new AiJobChangedEventArgs(job, newEvent, warning));

    public Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default)
    {
        var call = new StartJobCall(taskId, kind, instruction);
        StartJobCalls.Add(call);
        return OnStartJob?.Invoke(call) ?? Task.FromResult(Result.Ok(new AiJob { TaskId = taskId, Kind = kind }));
    }

    public Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiJob>>(
            Jobs.Where(j => j.TaskId == taskId).OrderByDescending(j => j.Id).ToList());

    public Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, int lines, CancellationToken ct = default)
    {
        var call = new GetEventsCall(jobId, lines);
        GetEventsCalls.Add(call);
        return OnGetEvents?.Invoke(call)
               ?? Task.FromResult<IReadOnlyList<AiJobEvent>>(
                   Events.Where(e => e.JobId == jobId).OrderBy(e => e.Seq).ToList());
    }

    public Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiJob>>(Jobs.Where(j => !j.Status.IsTerminal()).ToList());

    public Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default)
    {
        GetArtifactsCalls.Add(jobId);
        return OnGetArtifacts?.Invoke(jobId) ?? Task.FromResult<IReadOnlyList<string>>(Artifacts.ToList());
    }

    public int TurnCountOf(int jobId) => TurnCounts.TryGetValue(jobId, out var count) ? count : 0;

    public Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default)
    {
        ReopenTerminalCalls.Add(jobId);
        return OnReopenTerminal?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default)
    {
        CompleteJobCalls.Add(jobId);
        return OnCompleteJob?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default)
    {
        StopTrackingCalls.Add(jobId);
        return OnStopTracking?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        RecoverOnStartupCalls++;
        return Task.CompletedTask;
    }
}
