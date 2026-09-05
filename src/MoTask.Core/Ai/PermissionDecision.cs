namespace MoTask.Core.Ai;

public enum PermissionBehavior
{
    Allow,
    Deny,
}

/// <summary>承認ツールへ返す決定。Deny のときの Message はモデルに見える文言。</summary>
public sealed record PermissionDecision(PermissionBehavior Behavior, string? Message = null)
{
    public static PermissionDecision Allow() => new(PermissionBehavior.Allow);
    public static PermissionDecision Deny(string message) => new(PermissionBehavior.Deny, message);
    public bool IsAllowed => Behavior == PermissionBehavior.Allow;
}
