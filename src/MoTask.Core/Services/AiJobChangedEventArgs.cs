using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// UI が読む用の値のコピー。エンティティを UI スレッドに渡さない。
/// 費用は持たない（フックに来ないので、盤面に出す手立てが無い。仕様 §9）。
/// </summary>
public sealed record AiJobSnapshot(
    int JobId, int TaskId, AiJobKind Kind, AiJobStatus Status, int TurnCount,
    string? ErrorMessage, string WorkingDirectory, string JobFolder);

/// <summary>NewEvent はイベント追記のときだけ。Warning は保存失敗・列移動の注意・イベントログの消失。</summary>
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
