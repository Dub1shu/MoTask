using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>
/// --mcp-config に渡す 1 ファイル（仕様 §5.4）。ブリッジ（MoTask.Mcp.exe）を motask という名前で
/// 登録するだけで、--strict-mcp-config は渡さないので利用者のコネクタはそのまま生きる。
/// HooksJson と同じ構え。
/// </summary>
public static class McpConfigJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 人が開いて読めるように、バックスラッシュや日本語を \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string mcpExecutable)
    {
        var servers = new Dictionary<string, object>
        {
            ["motask"] = new { command = mcpExecutable, args = Array.Empty<string>() },
        };
        return JsonSerializer.Serialize(new { mcpServers = servers }, Options);
    }
}
