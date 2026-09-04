namespace MoTask.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>ローカル日付。期限の「今日」「今週」「期限切れ」の判定に使う。</summary>
    DateOnly Today { get; }
}
