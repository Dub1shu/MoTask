using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>Project スコープが Global に優先し、同一スコープ内では Deny が Allow に優先する。</summary>
public sealed class PermissionPolicy : IPermissionPolicy
{
    public PolicyVerdict Evaluate(int? projectId, IReadOnlyList<AiPermissionRule> rules, PermissionRequest request)
    {
        var matching = rules.Where(r => PermissionPattern.Matches(r, request)).ToList();

        if (projectId is int pid)
        {
            var project = matching.Where(r => r.Scope == RuleScope.Project && r.ProjectId == pid).ToList();
            if (Decide(project) is PolicyVerdict verdict) return verdict;
        }

        var global = matching.Where(r => r.Scope == RuleScope.Global).ToList();
        return Decide(global) ?? PolicyVerdict.AskHuman;
    }

    private static PolicyVerdict? Decide(List<AiPermissionRule> scoped)
    {
        if (scoped.Any(r => r.Decision == RuleDecision.Deny)) return PolicyVerdict.Deny;
        if (scoped.Any(r => r.Decision == RuleDecision.Allow)) return PolicyVerdict.Allow;
        return null;
    }
}
