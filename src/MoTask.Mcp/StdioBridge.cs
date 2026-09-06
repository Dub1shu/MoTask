using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core;

namespace MoTask.Mcp;

/// <summary>
/// stdio ⇄ HTTP の中継（仕様 §5.1）。stdin の 1 行が JSON-RPC の 1 件。
/// stdout には JSON-RPC 以外を絶対に書かない。ログ・診断はすべて log（stderr）へ。
/// </summary>
public sealed class StdioBridge
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IEndpointResolver _resolver;
    private readonly IRpcSender _sender;
    private readonly TextWriter _log;

    public StdioBridge(IEndpointResolver resolver, IRpcSender sender, TextWriter log)
    {
        _resolver = resolver;
        _sender = sender;
        _log = log;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            if (line.Trim().Length == 0) continue;

            var response = await ExchangeAsync(line, ct).ConfigureAwait(false);
            if (response is null) continue;   // 通知、または id の無い要求の失敗

            await output.WriteLineAsync(response).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> ExchangeAsync(string line, CancellationToken ct)
    {
        try
        {
            var endpoint = await _resolver.ResolveAsync(ct).ConfigureAwait(false);
            var response = await _sender.SendAsync(endpoint, line, ct).ConfigureAwait(false);

            if (response is { Status: 401 })
            {
                // トークンが古い。endpoint.json を読み直して 1 度だけ再試行する（仕様 §8）。
                _log.WriteLine("motask: 401 を受けたので endpoint を読み直して再試行します");
                endpoint = await _resolver.ResolveAsync(ct).ConfigureAwait(false);
                response = await _sender.SendAsync(endpoint, line, ct).ConfigureAwait(false);
                if (response is { Status: 401 }) throw new McpBridgeException(Messages.McpUnauthorized);
            }

            // 接続が途中で切れた（アプリ終了など）。この 1 件はエラーにし、次の呼び出しで再解決する。
            if (response is null) throw new McpBridgeException(Messages.McpConnectionLost);
            if (response.Body is not null) return response.Body;
            if (response.Status == 202) return null;

            throw new McpBridgeException(string.Format(
                CultureInfo.CurrentCulture, Messages.McpUnexpectedStatusFormat, response.Status));
        }
        catch (McpBridgeException ex)
        {
            _log.WriteLine("motask: " + ex.Message);
            return ErrorFor(line, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 想定外の失敗はこの 1 件のツールエラーにとどめる（ブリッジのプロセスごと落とさない）。
            // 例外の詳細は stderr にだけ出し、stdout には resx の日本語だけを返す。
            _log.WriteLine("motask: 想定外の失敗: " + ex);
            return ErrorFor(line, Messages.McpUnexpectedFailure);
        }
    }

    /// <summary>id のある要求にだけ JSON-RPC エラーを返す。通知には何も返さない。</summary>
    private static string? ErrorFor(string line, string message)
    {
        var id = RawId(line);
        if (id is null) return null;
        var text = JsonSerializer.Serialize(message, Options);
        return $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32603,"message":{{{text}}}}}""";
    }

    /// <summary>id を生の JSON のまま取り出す（数値でも文字列でもそのまま返す）。</summary>
    private static string? RawId(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("id", out var id)) return null;
            return id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : id.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
