using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>result イベントから抜き出した要約。</summary>
public sealed record AgentResultInfo(bool IsError, int? NumTurns, decimal? TotalCostUsd, string? ResultText);

/// <summary>ランナーが 1 行の stream-json から作るイベント。Payload は元の 1 行そのまま。</summary>
public sealed record AgentEvent(AiJobEventKind Kind, string? ToolName, string Payload, AgentResultInfo? Result = null);
