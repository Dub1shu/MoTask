using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class AiJobRepository : IAiJobRepository
{
    private readonly MoTaskDbContext _db;

    public AiJobRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(AiJob job) => _db.AiJobs.Add(job);

    public Task<AiJob?> GetAsync(int jobId, CancellationToken ct = default)
        => _db.AiJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);

    public async Task<IReadOnlyList<AiJob>> GetForTaskAsync(int taskId, CancellationToken ct = default)
        => await _db.AiJobs.Where(j => j.TaskId == taskId).OrderByDescending(j => j.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<AiJob>> GetByStatusAsync(IReadOnlyCollection<AiJobStatus> statuses, CancellationToken ct = default)
    {
        // 値変換(enum → 文字列)付きの列に対する Contains は EF Core 8 以降で IN 句に翻訳される
        var wanted = statuses.ToList();
        return await _db.AiJobs
            .Where(j => wanted.Contains(j.Status))
            .OrderBy(j => j.Id)
            .ToListAsync(ct);
    }
}
