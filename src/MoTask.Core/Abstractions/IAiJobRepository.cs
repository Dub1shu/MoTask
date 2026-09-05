using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

/// <summary>読み取りは追跡された同一インスタンスを返す（IBoardRepository と同じ契約）。</summary>
public interface IAiJobRepository
{
    void Add(AiJob job);
    Task<AiJob?> GetAsync(int jobId, CancellationToken ct = default);
    /// <summary>新しい順（Id 降順）。</summary>
    Task<IReadOnlyList<AiJob>> GetForTaskAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<AiJob>> GetByStatusAsync(IReadOnlyCollection<AiJobStatus> statuses, CancellationToken ct = default);

    void AddEvent(AiJobEvent entry);
    /// <summary>Seq 昇順。</summary>
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default);
}
