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
        var json = McpConfigJson.Build(
            @"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe", @"C:\Program Files\MoTask\MoTask.exe");

        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("motask");
        server.GetProperty("command").GetString().Should().Be(@"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe");
        server.GetProperty("args").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// ブリッジは mcp\ の下にいるので、既定の exe 探索（AppContext.BaseDirectory 直下）では
    /// MoTask.exe に届かない。MOTASK_APP_EXE（SystemAppHost.ExeOverrideVariable と同じ名前）に
    /// 絶対パスを渡すことで、MoTask を閉じたあとも呼び直せるようにする（Finding 1）。
    /// </summary>
    [Fact]
    public void Build_PassesTheAppExeThroughTheOverrideEnvironmentVariable()
    {
        var json = McpConfigJson.Build(
            @"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe", @"C:\Program Files\MoTask\MoTask.exe");

        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("motask");
        server.GetProperty("env").GetProperty("MOTASK_APP_EXE").GetString()
            .Should().Be(@"C:\Program Files\MoTask\MoTask.exe");
    }

    [Fact]
    public void Build_KeepsBackslashesReadable()
    {
        // 人が開いて読めるように \uXXXX にしない（HooksJson と同じ理由）
        McpConfigJson.Build(@"C:\x\MoTask.Mcp.exe", @"C:\x\MoTask.exe").Should().Contain(@"C:\\x\\MoTask.exe");
    }
}
