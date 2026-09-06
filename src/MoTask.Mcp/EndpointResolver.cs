using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Mcp;

public interface IEndpointResolver
{
    /// <summary>繋がる endpoint を返す。用意できなければ McpBridgeException。</summary>
    Task<McpEndpoint> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// 仕様 §5.2 の分岐。ファイルが無い／pid のプロセスが居ない／接続できないなら MoTask を起動して待つ。
/// プロセスは常駐しないので状態は持ち越さず、呼ばれるたびに最初から試す。
/// </summary>
public sealed class EndpointResolver : IEndpointResolver
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>疎通確認。トークンが合っていれば 200 が返る。</summary>
    public const string PingBody = """{"jsonrpc":"2.0","id":0,"method":"ping"}""";

    private readonly IAppHost _host;
    private readonly IRpcSender _sender;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public EndpointResolver(IAppHost host, IRpcSender sender, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        _host = host;
        _sender = sender;
        _timeout = timeout ?? DefaultTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public async Task<McpEndpoint> ResolveAsync(CancellationToken ct)
    {
        var existing = _host.ReadEndpoint();
        if (existing is not null && _host.IsRunning(existing.Pid)
            && await CanConnectAsync(existing, ct).ConfigureAwait(false))
        {
            return existing;
        }

        // 起動待ちの間にアプリが DB 修復ダイアログで止まることがある。その場合はタイムアウトになる。
        _host.LaunchApp();

        for (var waited = TimeSpan.Zero; waited < _timeout; waited += _pollInterval)
        {
            await _host.DelayAsync(_pollInterval, ct).ConfigureAwait(false);
            var endpoint = _host.ReadEndpoint();
            if (endpoint is not null && await CanConnectAsync(endpoint, ct).ConfigureAwait(false)) return endpoint;
        }

        throw new McpBridgeException(Messages.McpAppStartTimeout);
    }

    private async Task<bool> CanConnectAsync(McpEndpoint endpoint, CancellationToken ct)
        => await _sender.SendAsync(endpoint, PingBody, ct).ConfigureAwait(false) is { Status: 200 };
}
