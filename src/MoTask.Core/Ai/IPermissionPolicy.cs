using MoTask.Core.Model;

namespace MoTask.Core.Ai;

public enum PolicyVerdict
{
    Allow,
    Deny,
    AskHuman,
}

/// <summary>記憶したルールだけで決められるかを判定する。AskHuman のときだけ App がダイアログを出す（仕様 §5）。</summary>
public interface IPermissionPolicy
{
    PolicyVerdict Evaluate(int? projectId, IReadOnlyList<AiPermissionRule> rules, PermissionRequest request);
}
