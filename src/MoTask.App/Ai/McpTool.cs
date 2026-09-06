using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>ツール 1 回分の結果。Text はそのままテキストコンテンツとして返る。</summary>
public sealed record McpToolResult(string Text, bool IsError)
{
    public static McpToolResult Ok(string text) => new(text, false);

    /// <summary>MCP のツールエラー（isError: true）。本文には利用者向けの日本語を入れる。</summary>
    public static McpToolResult Error(string message) => new(message, true);
}

/// <summary>
/// サーバに載せるツール 1 本。InputSchema は JSON Schema をそのまま表す匿名オブジェクトでよい。
/// InvokeAsync に渡る JsonElement は呼び出しの間だけ有効なので、保持するならクローンすること。
/// </summary>
public sealed record McpTool(
    string Name,
    string Description,
    object InputSchema,
    Func<JsonElement, CancellationToken, Task<McpToolResult>> InvokeAsync);
