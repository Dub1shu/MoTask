using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly MoTaskDbContext _db;

    public HistoryRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(HistoryEntry entry) => _db.History.Add(entry);

    public async Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default)
        => await _db.History.AsNoTracking()
            .Where(h => h.TaskId == taskId)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .ToListAsync(ct);
}
