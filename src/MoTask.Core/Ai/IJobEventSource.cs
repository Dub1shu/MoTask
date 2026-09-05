namespace MoTask.Core.Ai;

/// <summary>events.jsonl を追う口（App の JobEventWatcher）。ファイルがまだ無くても待つ。</summary>
public interface IJobEventSource
{
    void Follow(JobEventSubscription subscription);

    /// <summary>知らない JobId でも何もせずに返る（完了処理から何度呼ばれてもよい）。</summary>
    void StopFollowing(int jobId);
}
