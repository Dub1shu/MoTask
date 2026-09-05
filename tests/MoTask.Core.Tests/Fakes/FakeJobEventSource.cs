using System.Collections.Concurrent;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>テストが events.jsonl の代わりに行を流し込む。</summary>
public sealed class FakeJobEventSource : IJobEventSource
{
    private readonly ConcurrentDictionary<int, JobEventSubscription> _subscriptions = new();

    /// <summary>掛けられた購読の全履歴（StopFollowing しても消えない）。</summary>
    public List<JobEventSubscription> Subscriptions { get; } = new();
    public List<int> Stopped { get; } = new();

    public bool IsFollowing(int jobId) => _subscriptions.ContainsKey(jobId);
    public int SkipLinesOf(int jobId) => _subscriptions[jobId].SkipLines;
    public string EventsPathOf(int jobId) => _subscriptions[jobId].EventsPath;

    public void Follow(JobEventSubscription subscription)
    {
        _subscriptions[subscription.JobId] = subscription;
        Subscriptions.Add(subscription);
    }

    public void StopFollowing(int jobId)
    {
        _subscriptions.TryRemove(jobId, out _);
        Stopped.Add(jobId);
    }

    /// <summary>
    /// フックが 1 行書いたことにする。追従を切った後でも呼べる（実機では追従スレッドが
    /// 止まる前に読み終えた行が遅れて届きうるので、サービス側の終了ジョブの門番を試せる）。
    /// </summary>
    public Task EmitAsync(int jobId, string line) => Latest(jobId).OnLine(line);

    public Task ProblemAsync(int jobId, string message) => Latest(jobId).OnProblem(message);

    private JobEventSubscription Latest(int jobId) => Subscriptions.Last(s => s.JobId == jobId);

    // ---- よく使う行 ----

    public static string SessionStart(string source = "startup")
        => $$"""{"hook_event_name":"SessionStart","source":"{{source}}"}""";

    // 末尾が }} で終わるので、補間の閉じ括弧と読まれないように $ を 3 つにする
    public static string PostToolUse(string tool = "Bash")
        => $$$"""{"hook_event_name":"PostToolUse","tool_name":"{{{tool}}}","tool_input":{"command":"dir"}}""";

    public static string Stop(string message = "できました")
        => $$"""{"hook_event_name":"Stop","last_assistant_message":"{{message}}"}""";

    public static string SessionEnd(string reason = "exit")
        => $$"""{"hook_event_name":"SessionEnd","reason":"{{reason}}"}""";
}
