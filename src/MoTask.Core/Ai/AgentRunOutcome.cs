namespace MoTask.Core.Ai;

/// <summary>プロセス終了時の要約。StderrTail は失敗理由の補助（末尾 2000 文字程度）。</summary>
public sealed record AgentRunOutcome(int ExitCode, AgentResultInfo? Result, string? StderrTail);
