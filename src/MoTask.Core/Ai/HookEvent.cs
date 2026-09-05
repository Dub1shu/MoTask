using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>フックの 1 行から取り出した、表示と状態遷移に要る最小限。Payload は原文のまま。</summary>
public sealed record HookEvent(AiJobEventKind Kind, string? ToolName, string Payload);
