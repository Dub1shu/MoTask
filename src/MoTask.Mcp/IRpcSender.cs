using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MoTask.Core.Ai;

namespace MoTask.Mcp;

/// <summary>HTTP の応答。接続そのものが失敗した場合は SendAsync が null を返す。</summary>
public sealed record RpcResponse(int Status, string? Body);

public interface IRpcSender
{
    /// <summary>解決済み endpoint へ JSON-RPC を 1 件 POST する。接続できなければ null。</summary>
    Task<RpcResponse?> SendAsync(McpEndpoint endpoint, string body, CancellationToken ct);
}

public sealed class HttpRpcSender : IRpcSender, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<RpcResponse?> SendAsync(McpEndpoint endpoint, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new RpcResponse((int)response.StatusCode, text.Length == 0 ? null : text);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // アプリが落ちている・まだ立っていない。呼び手が再解決する。
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
