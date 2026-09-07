using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class MorningRepository : IMorningRepository
{
    private static readonly MorningRunStatus[] Unfinished = { MorningRunStatus.Pending, MorningRunStatus.Running };

    private readonly MoTaskDbContext _db;

    public MorningRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(MorningRun run) => _db.MorningRuns.Add(run);

    public Task<MorningRun?> GetRunAsync(int runId, CancellationToken ct = default)
        => _db.MorningRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    public Task<MorningRun?> GetUnfinishedRunAsync(CancellationToken ct = default)
        // 値変換(enum → 文字列)付きの列に対する Contains は EF Core 8 以降で IN 句に翻訳される
        => _db.MorningRuns.Where(r => Unfinished.Contains(r.Status))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<MorningRun?> GetLatestRunAsync(CancellationToken ct = default)
        => _db.MorningRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<int> CountRunsAsync(CancellationToken ct = default) => _db.MorningRuns.CountAsync(ct);

    public void AddCandidate(TriageCandidate candidate) => _db.TriageCandidates.Add(candidate);

    public Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default)
        => _db.TriageCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct);

    public async Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(
        IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        if (externalIds.Count == 0) return Array.Empty<string>();
        var wanted = externalIds.ToList();
        return await _db.TriageCandidates
            .Where(c => wanted.Contains(c.ExternalId))
            .Select(c => c.ExternalId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => await _db.TriageCandidates
            .Where(c => (c.MorningRunId == runId && c.Status == TriageStatus.Pending)
                        || (c.MorningRunId != runId && c.Status == TriageStatus.Later))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
}
