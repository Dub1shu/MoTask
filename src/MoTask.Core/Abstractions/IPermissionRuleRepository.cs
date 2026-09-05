using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

public interface IPermissionRuleRepository
{
    /// <summary>CreatedAt 昇順。</summary>
    Task<IReadOnlyList<AiPermissionRule>> GetAllAsync(CancellationToken ct = default);
    Task<AiPermissionRule?> GetAsync(int ruleId, CancellationToken ct = default);
    void Add(AiPermissionRule rule);
    void Remove(AiPermissionRule rule);
}
