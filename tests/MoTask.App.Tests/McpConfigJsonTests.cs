using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>--mcp-config に渡す 1 ファイル（仕様 §5.4）。HooksJson と同じ構え。</summary>
public class McpConfigJsonTests
{
    [Fact]
    public void Build_RegistersTheBridgeUnderTheNameMotask()
    {
        var json = McpConfigJson.Build(@"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe");

        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("motask");
        server.GetProperty("command").GetString().Should().Be(@"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe");
        server.GetProperty("args").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Build_KeepsBackslashesReadable()
    {
        // 人が開いて読めるように \uXXXX にしない（HooksJson と同じ理由）
        McpConfigJson.Build(@"C:\x\MoTask.Mcp.exe").Should().Contain(@"C:\\x\\MoTask.Mcp.exe");
    }
}
