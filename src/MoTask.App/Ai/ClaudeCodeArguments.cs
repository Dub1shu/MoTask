using System.Globalization;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.App.Ai;

/// <summary>仕様 §8 の起動引数。ProcessStartInfo.ArgumentList に 1 要素ずつ渡す（引用符の心配をしない）。</summary>
public static class ClaudeCodeArguments
{
    public const string ResearchTools = "Read,Glob,Grep,WebSearch,WebFetch";
    public const string ExecuteTools = "default";

    public static IReadOnlyList<string> Build(AgentRunRequest request, AiSettings settings, string mcpConfigPath)
    {
        var args = new List<string>
        {
            "-p", request.Prompt,
            "--output-format", "stream-json",
            "--verbose",
        };

        // 再開は --session-id ではなく --resume。同じ ID を両方に渡さない。
        if (request.Resume) args.AddRange(new[] { "--resume", request.SessionId.ToString("D") });
        else args.AddRange(new[] { "--session-id", request.SessionId.ToString("D") });

        args.AddRange(new[]
        {
            // 利用者の settings.json の allowlist を MoTask のジョブに効かせない（仕様 §4.4）
            "--setting-sources", "",
            "--strict-mcp-config",
            "--mcp-config", mcpConfigPath,
            "--permission-prompt-tool", ApprovalMcpServer.PermissionPromptTool,
            "--permission-mode", "default",
            "--tools", request.Kind == AiJobKind.Research ? ResearchTools : ExecuteTools,
            "--max-turns", settings.MaxTurns.ToString(CultureInfo.InvariantCulture),
        });

        if (!string.IsNullOrWhiteSpace(settings.Model)) args.AddRange(new[] { "--model", settings.Model.Trim() });
        return args;
    }
}
