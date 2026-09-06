using MoTask.Core.Ai;
using MoTask.Mcp;

namespace MoTask.Mcp.Tests.Fakes;

/// <summary>ファイル・プロセス・時間を全部差し替えて、EndpointResolver を純関数として試験する。</summary>
public sealed class FakeAppHost : IAppHost
{
    /// <summary>ReadEndpoint が順に返す値。尽きたら最後の値を返し続ける。</summary>
    public List<McpEndpoint?> Endpoints { get; } = new() { null };

    public HashSet<int> RunningPids { get; } = new();
    public int LaunchCount { get; private set; }
    public McpBridgeException? LaunchFailure { get; set; }
    public List<TimeSpan> Delays { get; } = new();

    private int _reads;

    public McpEndpoint? ReadEndpoint()
    {
        var index = Math.Min(_reads, Endpoints.Count - 1);
        _reads++;
        return Endpoints[index];
    }

    public bool IsRunning(int pid) => RunningPids.Contains(pid);

    public void LaunchApp()
    {
        LaunchCount++;
        if (LaunchFailure is not null) throw LaunchFailure;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}
