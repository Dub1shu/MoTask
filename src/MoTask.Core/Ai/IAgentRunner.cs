namespace MoTask.Core.Ai;

/// <summary>エージェントループの実体（App の ClaudeCodeRunner）。Core はこの口しか知らない。</summary>
public interface IAgentRunner
{
    /// <summary>ジョブ開始前の事前確認。claude が見つからなければ Fail(Messages.ClaudeNotFound)。</summary>
    Result CheckAvailable();

    /// <summary>プロセスが終わるまで返らない。ct を取り消すとプロセスを殺して OperationCanceledException を投げる。</summary>
    Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken ct);
}
