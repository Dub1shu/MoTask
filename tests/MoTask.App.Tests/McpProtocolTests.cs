using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class McpProtocolTests
{
    private static McpTool Echo(string name, bool isError = false) => new(
        name,
        $"{name} のテスト用",
        new { type = "object", properties = new { value = new { type = "string" } } },
        (args, _) => Task.FromResult(new McpToolResult(args.GetRawText(), isError)));

    private static async Task<JsonDocument> CallAsync(string body, params McpTool[] tools)
    {
        var response = await McpProtocol.HandleAsync(body, tools, CancellationToken.None);
        response.StatusCode.Should().Be(200);
        return JsonDocument.Parse(response.Body!);
    }

    [Fact]
    public async Task ToolsList_ListsEveryRegisteredTool_WithItsSchema()
    {
        using var doc = await CallAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Echo("alpha"), Echo("beta"));

        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Equal("alpha", "beta");
        tools[0].GetProperty("description").GetString().Should().Be("alpha のテスト用");
        tools[0].GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task ToolsCall_DispatchesByName_AndWrapsTheTextContent()
    {
        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"beta","arguments":{"value":"x"}}}""",
            Echo("alpha"), Echo("beta"));

        var result = doc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var content = result.GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Contain("\"value\"");
    }

    [Fact]
    public async Task ToolsCall_WithoutArguments_PassesAnEmptyObject()
    {
        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"alpha"}}""", Echo("alpha"));

        doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().Should().Be("{}");
    }

    [Fact]
    public async Task ToolsCall_ToolReportsFailure_SetsIsError()
    {
        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"bad","arguments":{}}}""",
            Echo("bad", isError: true));

        doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"missing","arguments":{}}}""",
            Echo("alpha"));

        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    [Fact]
    public async Task ToolsCall_ToolThrows_ReturnsInternalError()
    {
        var boom = new McpTool("boom", "投げる", new { type = "object" },
            (_, _) => throw new InvalidOperationException("壊れた"));

        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"boom","arguments":{}}}""", boom);

        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32603);
    }

    [Fact]
    public async Task ToolsCall_ToolThrowsArgumentException_ReturnsInvalidParams()
    {
        var badArgs = new McpTool("badArgs", "引数不正", new { type = "object" },
            (_, _) => throw new ArgumentException("引数の形が不正"));

        using var doc = await CallAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"badArgs","arguments":{}}}""", badArgs);

        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32602);
        error.GetProperty("message").GetString().Should().Be("Invalid params");
    }

    [Fact]
    public async Task Notification_StillReturnsAcceptedWithoutBody()
    {
        var response = await McpProtocol.HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", Array.Empty<McpTool>(), CancellationToken.None);

        response.StatusCode.Should().Be(202);
        response.Body.Should().BeNull();
    }

    [Fact]
    public async Task ParseError_Returns400()
    {
        var response = await McpProtocol.HandleAsync("{ broken", Array.Empty<McpTool>(), CancellationToken.None);

        response.StatusCode.Should().Be(400);
        JsonDocument.Parse(response.Body!).RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
    }
}
