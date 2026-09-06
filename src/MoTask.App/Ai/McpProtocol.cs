using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.App.Ai;

public sealed record McpResponse(int StatusCode, string? Body);

/// <summary>
/// MCP（Streamable HTTP）の JSON-RPC 部分。HTTP や認証は MoTaskMcpServer が持つ。
/// ツールは配列で受け取り、tools/list はその定義を並べ、tools/call は名前で引く。
/// </summary>
public static class McpProtocol
{
    public const string DefaultProtocolVersion = "2025-06-18";
    public const string ServerName = "motask";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>引数が省略されたツール呼び出しに渡す空オブジェクト。</summary>
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public static async Task<McpResponse> HandleAsync(string body, IReadOnlyList<McpTool> tools, CancellationToken ct)
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
                        serverInfo = new { name = ServerName, version = "1.0.0" },
                    });
                }
                case "ping":
                    return Ok(id, new { });
                case "tools/list":
                    return Ok(id, new
                    {
                        tools = tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }).ToArray(),
                    });
                case "tools/call":
                    return await CallAsync(id, parameters, tools, ct).ConfigureAwait(false);
                default:
                    return new McpResponse(200, Error(id, -32601, "Method not found"));
            }
        }
    }

    private static async Task<McpResponse> CallAsync(
        JsonElement id, JsonElement parameters, IReadOnlyList<McpTool> tools, CancellationToken ct)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return new McpResponse(200, Error(id, -32602, "Invalid params"));
        }

        var name = nameElement.GetString();
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return new McpResponse(200, Error(id, -32602, "Unknown tool"));

        var arguments = parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            ? args
            : EmptyArguments;

        McpToolResult result;
        try
        {
            result = await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            // ツールが「引数の形が不正」を表すために投げる例外。スキーマの知識はツール側に残すので、
            // ここでは型だけを見て -32602 に落とす。
            return new McpResponse(200, Error(id, -32602, "Invalid params"));
        }
        catch (Exception)
        {
            // 想定外の例外は仕様 §8 のとおり -32603。例外の内容は呼び出し側へ漏らさない（現状、記録先は未実装）。
            return new McpResponse(200, Error(id, -32603, "Internal error"));
        }

        return Ok(id, new
        {
            content = new[] { new { type = "text", text = result.Text } },
            isError = result.IsError,
        });
    }

    private static McpResponse Ok(JsonElement id, object result)
        => new(200, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Options));

    private static string Error(JsonElement? id, int code, string message)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, Options);
}
