using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class ApprovalMcpServerTests : IDisposable
{
    private readonly ApprovalMcpServer _server = new();
    private readonly HttpClient _http = new();

    public ApprovalMcpServerTests()
    {
        _server.Start();
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Body)> PostAsync(string? token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _server.McpUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonDocument.Parse(text));
    }

    private static string ApproveCall(int id, string toolName, string inputJson, string toolUseId = "toolu_1")
        => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"method":"tools/call","params":{"name":"approve","arguments":{"tool_name":"{{{{toolName}}}}","input":{{{{inputJson}}}},"tool_use_id":"{{{{toolUseId}}}}"},"_meta":{"claudecode/toolUseId":"{{{{toolUseId}}}}","progressToken":2}}}""";

    [Fact]
    public void Start_BindsLoopbackOnly()
    {
        _server.McpUrl.Should().NotBeNull();
        _server.McpUrl!.Host.Should().Be("127.0.0.1");
        _server.McpUrl.AbsolutePath.Should().Be("/mcp");
        _server.McpUrl.Port.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfo_AndEchoesProtocolVersion()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (status, body) = await PostAsync(token, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.260"}}}""");

        status.Should().Be(HttpStatusCode.OK);
        var result = body!.RootElement.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().Should().Be("2025-11-25");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("motask");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        body.RootElement.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task InitializedNotification_ReturnsAccepted_WithoutBody()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (status, body) = await PostAsync(token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body.Should().BeNull();
    }

    [Fact]
    public async Task ToolsList_ExposesApprove_WithInputSchema()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (_, body) = await PostAsync(token, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = body!.RootElement.GetProperty("result").GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("approve");
        var props = tools[0].GetProperty("inputSchema").GetProperty("properties");
        props.TryGetProperty("tool_name", out _).Should().BeTrue();
        props.TryGetProperty("input", out _).Should().BeTrue();
        props.TryGetProperty("tool_use_id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolsCall_Allow_ReturnsBehaviorAllow_WithUpdatedInput_AndPassesRequestToHandler()
    {
        PermissionRequest? received = null;
        var token = _server.Register((req, _) => { received = req; return Task.FromResult(PermissionDecision.Allow()); });

        var (status, body) = await PostAsync(token, ApproveCall(3, "Write", """{"file_path":"C:\\w\\a.md","content":"hi"}"""));

        status.Should().Be(HttpStatusCode.OK);
        received.Should().NotBeNull();
        received!.ToolName.Should().Be("Write");
        received.ToolUseId.Should().Be("toolu_1");
        using var input = JsonDocument.Parse(received.InputJson);
        input.RootElement.GetProperty("file_path").GetString().Should().Be(@"C:\w\a.md");

        var content = body!.RootElement.GetProperty("result").GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("text");
        using var decision = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
        decision.RootElement.GetProperty("behavior").GetString().Should().Be("allow");
        decision.RootElement.GetProperty("updatedInput").GetProperty("content").GetString().Should().Be("hi");
        body.RootElement.GetProperty("result").TryGetProperty("isError", out var isError).Should().BeTrue();
        isError.GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ToolsCall_Deny_ReturnsBehaviorDeny_WithMessage()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Deny("だめ")));

        var (_, body) = await PostAsync(token, ApproveCall(4, "Bash", """{"command":"rm -rf /"}"""));

        using var decision = JsonDocument.Parse(body!.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        decision.RootElement.GetProperty("behavior").GetString().Should().Be("deny");
        decision.RootElement.GetProperty("message").GetString().Should().Be("だめ");
        decision.RootElement.TryGetProperty("updatedInput", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Tokens_RouteToTheirOwnHandlers()
    {
        var hits = new List<string>();
        var tokenA = _server.Register((r, _) => { hits.Add("A:" + r.ToolName); return Task.FromResult(PermissionDecision.Allow()); });
        var tokenB = _server.Register((r, _) => { hits.Add("B:" + r.ToolName); return Task.FromResult(PermissionDecision.Allow()); });

        await PostAsync(tokenA, ApproveCall(5, "Read", "{}"));
        await PostAsync(tokenB, ApproveCall(6, "Glob", "{}"));

        hits.Should().Equal("A:Read", "B:Glob");
    }

    [Fact]
    public async Task MissingOrUnknownToken_IsUnauthorized_AndDoesNotReachHandlers()
    {
        var called = false;
        _server.Register((_, _) => { called = true; return Task.FromResult(PermissionDecision.Allow()); });

        (await PostAsync(null, ApproveCall(7, "Read", "{}"))).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync("not-a-token", ApproveCall(8, "Read", "{}"))).Status.Should().Be(HttpStatusCode.Unauthorized);
        called.Should().BeFalse();
    }

    [Fact]
    public async Task UnregisteredToken_IsUnauthorized()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));
        _server.Unregister(token);

        (await PostAsync(token, """{"jsonrpc":"2.0","id":9,"method":"ping"}""")).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (status, body) = await PostAsync(token, """{"jsonrpc":"2.0","id":10,"method":"resources/list"}""");

        status.Should().Be(HttpStatusCode.OK);
        body!.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (_, body) = await PostAsync(token, """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"other","arguments":{}}}""");

        body!.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    [Fact]
    public async Task InvalidJson_ReturnsParseError()
    {
        var token = _server.Register((_, _) => Task.FromResult(PermissionDecision.Allow()));

        var (status, body) = await PostAsync(token, "{ nope");

        status.Should().Be(HttpStatusCode.BadRequest);
        body!.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
    }

    [Fact]
    public async Task Get_IsMethodNotAllowed()
    {
        using var response = await _http.GetAsync(_server.McpUrl);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task HandlerWaitsAsLongAsItLikes_NoServerSideTimeout()
    {
        var gate = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _server.Register((_, _) => gate.Task);

        var call = PostAsync(token, ApproveCall(12, "Bash", """{"command":"git push"}"""));
        await Task.Delay(300);
        call.IsCompleted.Should().BeFalse("人が答えるまで応答しない");

        gate.SetResult(PermissionDecision.Allow());
        (await call).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightResponseToBeWritten_SoTheDecisionStillReachesTheClient()
    {
        // Task 5 のレビュー由来の契約: ハンドラが決定を返した直後に（Core 側の都合で）サーバが
        // 破棄されても、その応答は書き終わるまで握りつぶされてはならない。
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _server.Register(async (_, _) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return PermissionDecision.Allow();
        });

        var call = PostAsync(token, ApproveCall(13, "Read", "{}"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // リクエストが受理され、ハンドラが確実に呼ばれた後で競争を仕掛ける。
        release.SetResult();
        // 決定が確定した直後というタイミングを厳密に取る手段はないが、Dispose がここで割り込んでも
        // 応答の書き込みが打ち切られないことを確認する。
        _server.Dispose();

        var (status, body) = await call.WaitAsync(TimeSpan.FromSeconds(5));

        status.Should().Be(HttpStatusCode.OK);
        using var decision = JsonDocument.Parse(body!.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        decision.RootElement.GetProperty("behavior").GetString().Should().Be("allow");
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }
}
