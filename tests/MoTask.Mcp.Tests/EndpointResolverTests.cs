using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Ai;
using MoTask.Mcp;
using MoTask.Mcp.Tests.Fakes;
using Xunit;

namespace MoTask.Mcp.Tests;

public class EndpointResolverTests
{
    private const string Url = "http://127.0.0.1:52341/mcp";

    private readonly FakeAppHost _host = new();
    private readonly FakeRpcSender _sender = new();

    private static McpEndpoint Endpoint(int pid = 4242) => new(Url, "TOKEN", pid);

    private EndpointResolver Resolver()
        => new(_host, _sender, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(250));

    private void MakeHealthy()
    {
        _host.Endpoints[0] = Endpoint();
        _host.RunningPids.Add(4242);
        _sender.ReachableUrl = Url;
    }

    [Fact]
    public async Task Healthy_UsesTheFileWithoutLaunchingTheApp()
    {
        MakeHealthy();

        var endpoint = await Resolver().ResolveAsync(CancellationToken.None);

        endpoint.Should().Be(Endpoint());
        _host.LaunchCount.Should().Be(0);
        _sender.Sent.Should().ContainSingle().Which.Body.Should().Contain("\"ping\"");
    }

    [Fact]
    public async Task MissingFile_LaunchesTheApp_ThenUsesWhatItWrites()
    {
        _host.Endpoints.Clear();
        _host.Endpoints.Add(null);            // 1 回目の読み取り: まだ無い
        _host.Endpoints.Add(Endpoint());      // ポーリング後: アプリが書いた
        _sender.ReachableUrl = Url;

        var endpoint = await Resolver().ResolveAsync(CancellationToken.None);

        endpoint.Should().Be(Endpoint());
        _host.LaunchCount.Should().Be(1);
        _host.Delays.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StaleFile_DeadPid_LaunchesTheApp()
    {
        _host.Endpoints[0] = Endpoint(pid: 9999);   // pid のプロセスは生きていない
        _sender.ReachableUrl = Url;

        await Resolver().ResolveAsync(CancellationToken.None);

        _host.LaunchCount.Should().Be(1);
    }

    [Fact]
    public async Task LivePid_ButUnreachable_LaunchesTheApp()
    {
        _host.Endpoints[0] = Endpoint();
        _host.RunningPids.Add(4242);
        _sender.ReachableUrl = null;                // 接続できない

        var act = async () => await Resolver().ResolveAsync(CancellationToken.None);

        await act.Should().ThrowAsync<McpBridgeException>();
        _host.LaunchCount.Should().Be(1);
    }

    [Fact]
    public async Task Unauthorized_IsNotTreatedAsConnected()
    {
        MakeHealthy();
        _sender.Respond = (_, _) => new RpcResponse(401, null);

        var act = async () => await Resolver().ResolveAsync(CancellationToken.None);

        await act.Should().ThrowAsync<McpBridgeException>()
            .WithMessage(Messages.McpAppStartTimeout);
    }

    [Fact]
    public async Task Timeout_ReportsThatTheAppShouldBeChecked()
    {
        _host.Endpoints[0] = null;

        var act = async () => await Resolver().ResolveAsync(CancellationToken.None);

        await act.Should().ThrowAsync<McpBridgeException>().WithMessage(Messages.McpAppStartTimeout);
        _host.Delays.Should().HaveCount(4);   // 1000ms / 250ms
    }

    [Fact]
    public async Task LaunchFailure_SurfacesThatMessage()
    {
        _host.Endpoints[0] = null;
        _host.LaunchFailure = new McpBridgeException("MoTask の実行ファイルが見つかりません: C:\\x\\MoTask.exe");

        var act = async () => await Resolver().ResolveAsync(CancellationToken.None);

        await act.Should().ThrowAsync<McpBridgeException>().WithMessage("*MoTask.exe*");
    }

    [Fact]
    public async Task EachResolve_StartsOver()
    {
        MakeHealthy();
        var resolver = Resolver();

        await resolver.ResolveAsync(CancellationToken.None);
        await resolver.ResolveAsync(CancellationToken.None);

        // 状態を引きずらない（プロセスは常駐しない）ので、毎回ファイルを読み直す
        _sender.Sent.Should().HaveCount(2);
    }
}
