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
    /// <summary>
    /// src/MoTask.Mcp/IAppHost.cs の SystemAppHost.ExeOverrideVariable と同じ名前(仕様 §3)。
    /// MoTask.Mcp はビルド順だけの依存で参照しないので、名前は文字列で合わせる。
    /// これが無いと、ブリッジは mcp\ フォルダの隣にある既定の "MoTask.exe" を探しに行き、
    /// mcp\ の下にコピーされたブリッジからは見つからない。
    /// </summary>
    private const string AppExeOverrideVariable = "MOTASK_APP_EXE";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 人が開いて読めるように、バックスラッシュや日本語を \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string mcpExecutable, string appExecutable)
    {
        var servers = new Dictionary<string, object>
        {
            ["motask"] = new
            {
                command = mcpExecutable,
                args = Array.Empty<string>(),
                env = new Dictionary<string, string> { [AppExeOverrideVariable] = appExecutable },
            },
        };
        return JsonSerializer.Serialize(new { mcpServers = servers }, Options);
    }
}
