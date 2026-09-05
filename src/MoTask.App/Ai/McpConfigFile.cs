using System.IO;
using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>
/// ジョブごとの --mcp-config 一時ファイル。トークンを含むのでジョブ終了時に必ず消す（仕様 §9）。
/// 置き場所は %TEMP%（Windows では利用者ごとの AppData\Local\Temp）なので、他の利用者からは読めない。
/// </summary>
public static class McpConfigFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Write(int jobId, Uri mcpUrl, string token)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MoTask");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"mcp-{jobId}-{Guid.NewGuid():N}.json");
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [ApprovalMcpServer.ServerName] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = mcpUrl.ToString(),
                    ["headers"] = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくてもジョブの結果には関係ない。次回起動時にも残るがトークンは既に無効。
        }
    }
}
