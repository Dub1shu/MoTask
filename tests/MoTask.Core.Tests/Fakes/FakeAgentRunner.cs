using System.Collections.Concurrent;
using MoTask.Core;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.Core.Tests.Fakes;

/// <summary>
/// RunAsync は Complete / Fail が呼ばれるまで返らない。その間にテストが EmitAsync でイベントを流し、
/// AskPermissionAsync で承認要求を差し込む。ct の取り消しは本物と同じく OperationCanceledException で返る。
/// </summary>
public sealed class FakeAgentRunner : IAgentRunner
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentRunOutcome>> _pending = new();
    // (jobId, 何回目の起動か) → その起動のリクエスト。再開すると同じジョブで 2 回目の RunAsync が来る。
    private readonly ConcurrentDictionary<(int JobId, int Nth), TaskCompletionSource<AgentRunRequest>> _started = new();
    private readonly ConcurrentDictionary<int, int> _runCounts = new();

    public Result Availability { get; set; } = Result.Ok();
    public List<AgentRunRequest> Requests { get; } = new();

    public Result CheckAvailable() => Availability;

    public Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AgentRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Requests) Requests.Add(request);
        _pending[request.JobId] = tcs;
        ct.Register(() => tcs.TrySetCanceled(ct));
        var nth = _runCounts.AddOrUpdate(request.JobId, 1, (_, n) => n + 1);
        Started(request.JobId, nth).TrySetResult(request);
        return tcs.Task;
    }

    /// <summary>
    /// サービスが Task.Run で起動するので、リクエストが届くまで待ってから操作する。
    /// nth は同じジョブの何回目の起動か（再開は 2）。
    /// </summary>
    public Task<AgentRunRequest> WaitForRunAsync(int jobId, int nth = 1)
        => Started(jobId, nth).Task.WaitAsync(TimeSpan.FromSeconds(5));

    public bool IsRunning(int jobId) => _pending.TryGetValue(jobId, out var tcs) && !tcs.Task.IsCompleted;
    public bool WasCancelled(int jobId) => _pending.TryGetValue(jobId, out var tcs) && tcs.Task.IsCanceled;

    public Task EmitAsync(int jobId, AgentEvent ev) => RequestFor(jobId).OnEvent(ev);

    public Task<PermissionDecision> AskPermissionAsync(int jobId, PermissionRequest request, CancellationToken ct = default)
        => RequestFor(jobId).OnPermissionRequest(request, ct);

    public void Complete(int jobId, AgentRunOutcome outcome) => _pending[jobId].TrySetResult(outcome);
    public void Fail(int jobId, Exception ex) => _pending[jobId].TrySetException(ex);

    public static AgentRunOutcome Success(int turns = 2, decimal cost = 0.05m, string text = "完了")
        => new(0, new AgentResultInfo(false, turns, cost, text), null);

    // 補間の区切りは $ の数だけの波かっこ。JSON の末尾が }} で終わるので、$$ だと
    // それが補間の終わりと読まれて CS9007 になる。$$$ にして {{{...}}} で補間する。
    public static AgentEvent Text(string text = "考えています")
        => new(AiJobEventKind.AssistantText, null, $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{text}}}"}]}}""");

    public static AgentEvent ToolUse(string tool = "Bash")
        => new(AiJobEventKind.ToolUse, tool, $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{tool}}}","input":{}}]}}""");

    public static AgentEvent ToolResult()
        => new(AiJobEventKind.ToolResult, null, """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""");

    private AgentRunRequest RequestFor(int jobId)
    {
        lock (Requests) return Requests.Last(r => r.JobId == jobId);
    }

    private TaskCompletionSource<AgentRunRequest> Started(int jobId, int nth)
        => _started.GetOrAdd((jobId, nth), _ => new TaskCompletionSource<AgentRunRequest>(TaskCreationOptions.RunContinuationsAsynchronously));
}
