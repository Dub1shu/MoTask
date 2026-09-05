namespace MoTask.Core.Model;

/// <summary>「今後も許可／拒否」の記憶。Pattern は、Bash の場合は先頭 2 トークン、Write/Edit の場合はディレクトリの絶対パス、ツール全体を許可する場合は null。</summary>
public sealed class AiPermissionRule
{
    public int Id { get; set; }
    public RuleScope Scope { get; set; }
    /// <summary>Scope が Project のときだけ。</summary>
    public int? ProjectId { get; set; }
    public string ToolName { get; set; } = "";
    /// <summary>null は「そのツール全部」。Bash は先頭 2 トークン、Write / Edit はディレクトリの絶対パス。</summary>
    public string? Pattern { get; set; }
    public RuleDecision Decision { get; set; }
    public DateTime CreatedAt { get; set; }
}
