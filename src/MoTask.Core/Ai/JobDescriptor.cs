using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>job.json の中身。MoTask の DB が壊れても、フォルダだけで何のジョブか分かるように残す（仕様 §6）。</summary>
public sealed record JobDescriptor(
    int JobId,
    Guid SessionId,
    AiJobKind Kind,
    string WorkingDirectory,
    string LaunchCommand,
    DateTime StartedAt);
