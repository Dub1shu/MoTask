using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

public sealed record McpResponse(int StatusCode, string? Body);

/// <summary>
/// MCP（Streamable HTTP）の JSON-RPC 部分。HTTP や認証は ApprovalMcpServer が持つ。
/// 承認ツール `approve` の入出力形は仕様 §4.1（Claude Code 2.1.260 で実機確認）。
/// </summary>
public static class McpProtocol
{
    public const string DefaultProtocolVersion = "2025-06-18";
    public const string ToolName = "approve";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<McpResponse> HandleAsync(
        string body,
        Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> approve,
        CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new McpResponse(400, Error(null, -32700, "Parse error"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new McpResponse(400, Error(null, -32600, "Invalid Request"));

            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            // id 無しは通知。応答本文は無し（Streamable HTTP では 202 Accepted）。
            if (!hasId) return new McpResponse(202, null);
            var id = idElement.Clone();
            root.TryGetProperty("params", out var parameters);

            switch (method)
            {
                case "initialize":
                {
                    var requested = parameters.ValueKind == JsonValueKind.Object
                                    && parameters.TryGetProperty("protocolVersion", out var v)
                                    && v.ValueKind == JsonValueKind.String
                        ? v.GetString()
                        : null;
                    return Ok(id, new
                    {
                        protocolVersion = requested ?? DefaultProtocolVersion,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = ApprovalMcpServer.ServerName, version = "1.0.0" },
                    });
                }
                case "ping":
                    return Ok(id, new { });
                case "tools/list":
                    return Ok(id, new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = ToolName,
                                description = "MoTask asks the user whether the tool call may run.",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        tool_name = new { type = "string" },
                                        input = new { type = "object" },
                                        tool_use_id = new { type = "string" },
                                    },
                                    required = new[] { "tool_name", "input" },
                                },
                            },
                        },
                    });
                case "tools/call":
                    return await CallAsync(id, parameters, approve, ct).ConfigureAwait(false);
                default:
                    return new McpResponse(200, Error(id, -32601, "Method not found"));
            }
        }
    }

    private static async Task<McpResponse> CallAsync(
        JsonElement id, JsonElement parameters,
        Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> approve, CancellationToken ct)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var name) || name.GetString() != ToolName)
        {
            return new McpResponse(200, Error(id, -32602, "Unknown tool"));
        }
        if (!parameters.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("tool_name", out var toolName) || toolName.ValueKind != JsonValueKind.String
            || !args.TryGetProperty("input", out var input))
        {
            return new McpResponse(200, Error(id, -32602, "Invalid params"));
        }
        var toolUseId = args.TryGetProperty("tool_use_id", out var tu) && tu.ValueKind == JsonValueKind.String ? tu.GetString() : null;

        var decision = await approve(new PermissionRequest(toolName.GetString()!, input.GetRawText(), toolUseId), ct).ConfigureAwait(false);

        var text = decision.IsAllowed
            ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = input.Clone() }, Options)
            : JsonSerializer.Serialize(new { behavior = "deny", message = decision.Message ?? "" }, Options);
        return Ok(id, new { content = new[] { new { type = "text", text } }, isError = false });
    }

    private static McpResponse Ok(JsonElement id, object result)
        => new(200, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Options));

    private static string Error(JsonElement? id, int code, string message)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, Options);
}
