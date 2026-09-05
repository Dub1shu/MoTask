using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>UI が読む用の値のコピー。エンティティを UI スレッドに渡さない。TurnCount は実行中の概算（AssistantText + ToolUse の数）、完了後は num_turns。</summary>
public sealed record AiJobSnapshot(
    int JobId, int TaskId, AiJobKind Kind, AiJobStatus Status, int TurnCount,
    decimal? TotalCostUsd, string? ErrorMessage, string WorkingDirectory);

/// <summary>NewEvent はイベント追記のときだけ。Warning は完了時の列移動に関する注意（Review 列が無い等）や保存失敗。</summary>
public sealed class AiJobChangedEventArgs : EventArgs
{
    public AiJobChangedEventArgs(AiJobSnapshot job, AiJobEvent? newEvent, string? warning)
    {
        Job = job;
        NewEvent = newEvent;
        Warning = warning;
    }

    public AiJobSnapshot Job { get; }
    public AiJobEvent? NewEvent { get; }
    public string? Warning { get; }
}
