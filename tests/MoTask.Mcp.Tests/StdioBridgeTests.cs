using System.Text.Json;
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Ai;
using MoTask.Mcp;
using MoTask.Mcp.Tests.Fakes;
using Xunit;

namespace MoTask.Mcp.Tests;

public class StdioBridgeTests
{
    private const string Url = "http://127.0.0.1:52341/mcp";

    private readonly FakeEndpointResolver _resolver = new(new McpEndpoint(Url, "TOKEN", 4242));
    private readonly FakeRpcSender _sender = new() { ReachableUrl = Url };
    private readonly StringWriter _log = new();

    private async Task<string[]> RunAsync(params string[] lines)
    {
        var output = new StringWriter();
        var bridge = new StdioBridge(_resolver, _sender, _log);
        await bridge.RunAsync(new StringReader(string.Join("\n", lines)), output, CancellationToken.None);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public async Task ForwardsTheRequest_AndWritesTheResponseOnOneLine()
    {
        _sender.Respond = (_, _) => new RpcResponse(200, """{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""");

        var written = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        written.Should().ContainSingle().Which.Should().Be("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""");
        _sender.Sent.Should().ContainSingle().Which.Body.Should().Contain("tools/list");
    }

    [Fact]
    public async Task Notification_WritesNothing()
    {
        _sender.Respond = (_, _) => new RpcResponse(202, null);

        var written = await RunAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        written.Should().BeEmpty();
    }

    [Fact]
    public async Task BlankLines_AreSkipped()
    {
        _sender.Respond = (_, _) => new RpcResponse(200, """{"jsonrpc":"2.0","id":1,"result":{}}""");

        var written = await RunAsync("", "   ", """{"jsonrpc":"2.0","id":1,"method":"ping"}""");

        written.Should().ContainSingle();
        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task SeveralMessages_AreForwardedInOrder()
    {
        _sender.Respond = (_, body) => new RpcResponse(200, body.Contains("\"id\":1") ? """{"id":1}""" : """{"id":2}""");

        var written = await RunAsync(
            """{"jsonrpc":"2.0","id":1,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        written.Should().Equal("""{"id":1}""", """{"id":2}""");
    }

    [Fact]
    public async Task Unauthorized_ReResolvesAndRetriesOnce()
    {
        var attempts = 0;
        _sender.Respond = (_, _) => ++attempts == 1
            ? new RpcResponse(401, null)
            : new RpcResponse(200, """{"jsonrpc":"2.0","id":1,"result":{}}""");

        var written = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");

        written.Should().ContainSingle();
        _resolver.Calls.Should().Be(2);
        _sender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task StillUnauthorized_ReturnsAJsonRpcError()
    {
        _sender.Respond = (_, _) => new RpcResponse(401, null);

        var written = await RunAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        using var doc = JsonDocument.Parse(written.Single());
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(7);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32603);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be(Messages.McpUnauthorized);
    }

    [Fact]
    public async Task ResolutionFailure_ReturnsAJsonRpcErrorAndLogsToStderr()
    {
        _resolver.Failure = new McpBridgeException("MoTask の実行ファイルが見つかりません: C:\\x\\MoTask.exe");

        var written = await RunAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_board"}}""");

        using var doc = JsonDocument.Parse(written.Single());
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("MoTask.exe");
        _log.ToString().Should().Contain("MoTask.exe");
    }

    [Fact]
    public async Task ResolutionFailure_OnANotification_WritesNothingToStdout()
    {
        _resolver.Failure = new McpBridgeException("起動できません");

        var written = await RunAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        written.Should().BeEmpty();
        _log.ToString().Should().Contain("起動できません");
    }

    [Fact]
    public async Task ConnectionLost_ReturnsAJsonRpcError()
    {
        _sender.ReachableUrl = null;   // 送信中に切れた

        var written = await RunAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");

        using var doc = JsonDocument.Parse(written.Single());
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(4);
        // 起動待ちのタイムアウトとは別の状況なので、専用の文言になる。
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Be(Messages.McpConnectionLost);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsAJsonRpcErrorAndKeepsRunning()
    {
        _sender.Respond = (_, body) => body.Contains("\"id\":8")
            ? throw new InvalidOperationException("An invalid request URI was provided")
            : new RpcResponse(200, """{"jsonrpc":"2.0","id":9,"result":{}}""");

        var written = await RunAsync(
            """{"jsonrpc":"2.0","id":8,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        // (a) 落ちた行の JSON-RPC エラーが 1 行だけ出て、(b) 次の行の処理は続く。
        written.Should().HaveCount(2);
        using var doc = JsonDocument.Parse(written[0]);
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(8);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32603);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Be(Messages.McpUnexpectedFailure);
        written[1].Should().Be("""{"jsonrpc":"2.0","id":9,"result":{}}""");

        // (c) 例外の詳細は stderr にだけ出す（stdout の本文には入れない）。
        _log.ToString().Should().Contain("An invalid request URI was provided")
            .And.Contain(nameof(InvalidOperationException));
        written[0].Should().NotContain("An invalid request URI was provided");
    }

    [Fact]
    public async Task StringIds_ArePreservedVerbatim()
    {
        _resolver.Failure = new McpBridgeException("だめ");

        var written = await RunAsync("""{"jsonrpc":"2.0","id":"abc","method":"ping"}""");

        JsonDocument.Parse(written.Single()).RootElement.GetProperty("id").GetString().Should().Be("abc");
    }

    [Fact]
    public async Task UnparsableLine_IsStillForwarded_SoTheAppReturnsTheParseError()
    {
        _sender.Respond = (_, _) => new RpcResponse(400, """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""");

        var written = await RunAsync("{ broken");

        written.Should().ContainSingle().Which.Should().Contain("-32700");
    }

    [Fact]
    public async Task ErrorMessages_AreEscapedProperly()
    {
        _resolver.Failure = new McpBridgeException("""パス "C:\x" が見つかりません""");

        var written = await RunAsync("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");

        JsonDocument.Parse(written.Single()).RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Be("""パス "C:\x" が見つかりません""");
    }
}
