using MoTask.Core.Ai;
using MoTask.Mcp;

namespace MoTask.Mcp.Tests.Fakes;

public sealed class FakeEndpointResolver : IEndpointResolver
{
    public FakeEndpointResolver(McpEndpoint endpoint) => Endpoint = endpoint;

    public McpEndpoint Endpoint { get; set; }
    public McpBridgeException? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<McpEndpoint> ResolveAsync(CancellationToken ct)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Endpoint) : Task.FromException<McpEndpoint>(Failure);
    }
}
