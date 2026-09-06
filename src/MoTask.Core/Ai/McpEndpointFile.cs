using System.Text.Json;

namespace MoTask.Core.Ai;

/// <summary>アプリが立てた MCP サーバの在り処。ブリッジはこれを読んで接続する（仕様 §5）。</summary>
public sealed record McpEndpoint(string Url, string Token, int Pid);

/// <summary>
/// endpoint.json の読み書き。アプリは MCP サーバを立てた直後に書き、終了時に消す。
/// 保護は %LOCALAPPDATA% のユーザー ACL に任せる（DB 本体と同じ扱い）。
/// </summary>
public static class McpEndpointFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(string path, McpEndpoint endpoint)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(endpoint, Options));
    }

    /// <summary>読めない・壊れている・項目が欠けているときは null。呼び手はアプリ未起動として扱う。</summary>
    public static McpEndpoint? TryRead(string path)
    {
        try
        {
            var endpoint = JsonSerializer.Deserialize<McpEndpoint>(File.ReadAllText(path), Options);
            if (endpoint is null) return null;
            if (string.IsNullOrWhiteSpace(endpoint.Url) || string.IsNullOrWhiteSpace(endpoint.Token)) return null;
            return endpoint.Pid <= 0 ? null : endpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentNullException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても実害は無い。残っても pid の生存確認で弾かれる。
        }
    }
}
