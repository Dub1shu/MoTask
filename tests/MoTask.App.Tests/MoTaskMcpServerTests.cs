using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Ai.BoardTools;
using MoTask.App.Ai.MorningTools;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.App.Tests.Fakes;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// 実際に HTTP を立てて叩く。トークンは起動ごとの board トークン 1 本だけで、
/// 見えるのは board ツール 6 本と morning ツール 4 本（BoardToolHost と MorningToolHost を
/// 束ねたもの。仕様 §9）。宛先の絞り込みはツールごとの runId 引数が担う。
/// </summary>
public class MoTaskMcpServerTests : IDisposable
{
    private readonly FakeBoardService _board = new();
    private readonly MoTaskMcpServer _server;
    private readonly HttpClient _http = new();

    public MoTaskMcpServerTests()
    {
        _board.Board = TestBoards.Sample();
        _server = new MoTaskMcpServer(
            new BoardToolHost(_board, new TestClock()),
            new MorningToolHost(new FakeMorningService()));
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

    private const string GetBoardCall =
        """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"get_board","arguments":{}}}""";

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
        var (status, body) = await PostAsync(_server.BoardToken, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.260"}}}""");

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
        var (status, body) = await PostAsync(_server.BoardToken, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body.Should().BeNull();
    }

    [Fact]
    public async Task BoardToken_SeesTheBoardAndMorningTools()
    {
        var (_, body) = await PostAsync(_server.BoardToken, """{"jsonrpc":"2.0","id":11,"method":"tools/list"}""");

        var names = body!.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        names.Should().Equal(
            "get_board", "list_tasks", "get_task", "add_task", "update_task", "move_task",
            "morning_get_context", "morning_add_candidate", "morning_submit_plan", "morning_complete");
    }

    [Fact]
    public async Task ToolsCall_RunsTheToolAndReturnsItsJson()
    {
        var (status, body) = await PostAsync(_server.BoardToken, GetBoardCall);

        status.Should().Be(HttpStatusCode.OK);
        var result = body!.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("進行中");
    }

    [Fact]
    public async Task UnknownToken_Is401_AndTheToolNeverRuns()
    {
        var (status, _) = await PostAsync("NOPE", GetBoardCall);

        status.Should().Be(HttpStatusCode.Unauthorized);
        _board.GetBoardCalls.Should().Be(0);
    }

    [Fact]
    public async Task MissingToken_Is401_AndTheToolNeverRuns()
    {
        var (status, _) = await PostAsync(null, GetBoardCall);

        status.Should().Be(HttpStatusCode.Unauthorized);
        _board.GetBoardCalls.Should().Be(0);

        // 認証はメソッドで分岐する前に効く。ツール一覧も無トークンでは覗けない。
        var (listStatus, _) = await PostAsync(null, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        listStatus.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void BoardToken_IsA64DigitHexMintedAtStart()
    {
        _server.BoardToken.Should().MatchRegex("^[0-9A-F]{64}$");

        using var notStarted = new MoTaskMcpServer(
            new BoardToolHost(_board, new TestClock()),
            new MorningToolHost(new FakeMorningService()));
        notStarted.Invoking(s => s.BoardToken).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        var (status, body) = await PostAsync(_server.BoardToken, """{"jsonrpc":"2.0","id":10,"method":"resources/list"}""");

        status.Should().Be(HttpStatusCode.OK);
        body!.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        var (_, body) = await PostAsync(_server.BoardToken, """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"other","arguments":{}}}""");

        body!.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    [Fact]
    public async Task InvalidJson_ReturnsParseError()
    {
        var (status, body) = await PostAsync(_server.BoardToken, "{ nope");

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
    public async Task SlowTool_IsNotCutOffByAServerSideTimeout()
    {
        var gate = new TaskCompletionSource<Result<Board>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _board.OnGetBoard = () => gate.Task;

        var call = PostAsync(_server.BoardToken, GetBoardCall);
        await Task.Delay(300);
        call.IsCompleted.Should().BeFalse("サーバ側でリクエストを打ち切らない");

        gate.SetResult(Result.Ok(TestBoards.Sample()));
        (await call).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightResponseToBeWritten()
    {
        // HttpListener は Stop()/Close() で書き込み中の応答まで打ち切る。処理中に Dispose が
        // 割り込んでも、既に受理したリクエストの応答は書き終わることを固定する。
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _board.OnGetBoard = async () =>
        {
            started.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Result.Ok(TestBoards.Sample());
        };

        var call = PostAsync(_server.BoardToken, GetBoardCall);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 受理されツールが動き出してから競争を仕掛ける
        release.SetResult();
        _server.Dispose();

        var (status, body) = await call.WaitAsync(TimeSpan.FromSeconds(5));

        status.Should().Be(HttpStatusCode.OK);
        body!.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }
}
