using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

public interface IHistoryRepository
{
    void Add(HistoryEntry entry);

    /// <summary>新しい順（At 降順、同時刻は Id 降順）。</summary>
    Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default);
}
