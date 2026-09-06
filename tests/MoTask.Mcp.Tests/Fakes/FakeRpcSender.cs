using MoTask.Core.Ai;
using MoTask.Mcp;

namespace MoTask.Mcp.Tests.Fakes;

public sealed class FakeRpcSender : IRpcSender
{
    /// <summary>この URL への送信だけが繋がる。null なら全部繋がらない。</summary>
    public string? ReachableUrl { get; set; }

    /// <summary>繋がったときに返す応答を決める。既定は 200 で空の結果。</summary>
    public Func<McpEndpoint, string, RpcResponse> Respond { get; set; }
        = (_, _) => new RpcResponse(200, """{"jsonrpc":"2.0","id":0,"result":{}}""");

    public List<(McpEndpoint Endpoint, string Body)> Sent { get; } = new();

    public Task<RpcResponse?> SendAsync(McpEndpoint endpoint, string body, CancellationToken ct)
    {
        Sent.Add((endpoint, body));
        if (ReachableUrl is null || endpoint.Url != ReachableUrl) return Task.FromResult<RpcResponse?>(null);
        return Task.FromResult<RpcResponse?>(Respond(endpoint, body));
    }
}
