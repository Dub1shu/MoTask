namespace MoTask.Core.Ai;

/// <summary>
/// 承認ツール（--permission-prompt-tool）に来た 1 件の要求。仕様 §4.1。
/// InputJson は "input" オブジェクトをそのまま JSON 文字列にしたもの。
/// </summary>
public sealed record PermissionRequest(string ToolName, string InputJson, string? ToolUseId);
