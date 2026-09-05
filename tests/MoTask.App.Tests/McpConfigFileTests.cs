using System.IO;
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class McpConfigFileTests
{
    [Fact]
    public void Write_ProducesHttpServerEntry_WithBearerHeader_AndDeleteRemovesIt()
    {
        var path = McpConfigFile.Write(42, new Uri("http://127.0.0.1:5123/mcp"), "TOKEN123");
        try
        {
            File.Exists(path).Should().BeTrue();
            Path.GetFileName(path).Should().StartWith("mcp-42-");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var server = doc.RootElement.GetProperty("mcpServers").GetProperty("motask");
            server.GetProperty("type").GetString().Should().Be("http");
            server.GetProperty("url").GetString().Should().Be("http://127.0.0.1:5123/mcp");
            server.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer TOKEN123");
        }
        finally
        {
            McpConfigFile.Delete(path);
        }
        File.Exists(path).Should().BeFalse();
        McpConfigFile.Delete(path); // 二度目は何も起きない
    }
}
