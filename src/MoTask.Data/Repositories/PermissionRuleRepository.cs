using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class PermissionRuleRepository : IPermissionRuleRepository
{
    private readonly MoTaskDbContext _db;

    public PermissionRuleRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AiPermissionRule>> GetAllAsync(CancellationToken ct = default)
        => await _db.AiPermissionRules.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);

    public Task<AiPermissionRule?> GetAsync(int ruleId, CancellationToken ct = default)
        => _db.AiPermissionRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);

    public void Add(AiPermissionRule rule) => _db.AiPermissionRules.Add(rule);

    public void Remove(AiPermissionRule rule) => _db.AiPermissionRules.Remove(rule);
}
