using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>
/// 1 回のプロセス起動。Resume が true なら --resume SessionId で続きから。
/// OnPermissionRequest は承認ツールから呼ばれ、決定が返るまで CLI 側は待つ。
/// OnEvent は stream-json の 1 行ごとに呼ばれる（順序どおり、直列）。
/// </summary>
public sealed record AgentRunRequest(
    int JobId,
    Guid SessionId,
    AiJobKind Kind,
    string Prompt,
    string WorkingDirectory,
    bool Resume,
    Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> OnPermissionRequest,
    Func<AgentEvent, Task> OnEvent);
