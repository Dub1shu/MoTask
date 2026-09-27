using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class PlanningRepository : IPlanningRepository
{
    private static readonly PlanningRunStatus[] Unfinished = { PlanningRunStatus.Pending, PlanningRunStatus.Running };

    private readonly MoTaskDbContext _db;

    public PlanningRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(PlanningRun run) => _db.PlanningRuns.Add(run);

    public Task<PlanningRun?> GetRunAsync(int runId, CancellationToken ct = default)
        => _db.PlanningRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    public Task<PlanningRun?> GetUnfinishedRunAsync(CancellationToken ct = default)
        // 値変換(enum → 文字列)付きの列に対する Contains は EF Core 8 以降で IN 句に翻訳される
        => _db.PlanningRuns.Where(r => Unfinished.Contains(r.Status))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<PlanningRun?> GetLatestRunAsync(CancellationToken ct = default)
        => _db.PlanningRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<int> CountRunsAsync(CancellationToken ct = default) => _db.PlanningRuns.CountAsync(ct);

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
            .Where(c => (c.PlanningRunId == runId && c.Status == TriageStatus.Pending)
                        || (c.PlanningRunId != runId && c.Status == TriageStatus.Later))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => await _db.TriageCandidates
            .Where(c => c.PlanningRunId == runId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
}
