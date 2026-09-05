namespace MoTask.Core.Model;

public enum AiJobEventKind
{
    AssistantText = 0,
    ToolUse = 1,
    ToolResult = 2,
    PermissionAsked = 3,
    PermissionDecided = 4,
    Error = 5,
    Result = 6,
    /// <summary>system/init、rate_limit_event、CLI 側の permission_denied、パースできない行。捨てずに残すための受け皿。</summary>
    System = 7,
}
