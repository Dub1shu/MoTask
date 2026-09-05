using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// `--permission-prompt-tool mcp__motask__approve` から呼ばれる HTTP MCP サーバ（仕様 §9）。
/// アプリ起動時に 127.0.0.1 の空きポートへ 1 つだけ立てる。ジョブごとにランダムな Bearer トークンを
/// 発行し、トークンでジョブ（承認ハンドラ）を特定する。要求はタイムアウトさせない。
/// HttpListener は 127.0.0.1 への非管理者バインドが可能なことを本機で確認済み。
///
/// Dispose 時の注意（Task 5 レビュー由来の契約）: Core は承認ハンドラの完了（決定が出たこと）までしか
/// 観測できず、その決定が HTTP 応答として CLI に実際に届いたかまでは分からない。HttpListener は
/// Stop()/Close() を呼ぶと、書き込み中の応答であっても ObjectDisposedException で打ち切ってしまう
/// （本機で実測済み）。そのため Dispose() は、進行中のリクエスト処理（決定を得て応答を書き込む途中の
/// ものを含む）が完了するのを待ってから listener を止める。
/// </summary>
public sealed class ApprovalMcpServer : IDisposable
{
    public const string ServerName = "motask";
    public const string ToolName = McpProtocol.ToolName;
    public const string PermissionPromptTool = $"mcp__{ServerName}__{McpProtocol.ToolName}";

    /// <summary>Dispose 時に進行中のリクエストを待つ上限。承認そのものへのタイムアウトではなく、
    /// 後始末が万一詰まった場合にアプリの終了処理を無限に止めないための保険。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Drain 完了後、listener.Stop()/Close() を呼ぶまでの猶予。http.sys 側の後始末を
    /// 追い越して直前の応答を巻き添えにしないための待機（Dispose() 参照）。</summary>
    private static readonly TimeSpan PostDrainGrace = TimeSpan.FromMilliseconds(200);

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Func<PermissionRequest, CancellationToken, Task<PermissionDecision>>> _handlers = new();
    private readonly ConcurrentDictionary<HttpListenerContext, Task> _inFlight = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _stopping;
    private int _disposed;

    public Uri? McpUrl { get; private set; }

    public void Start()
    {
        if (McpUrl is not null) return;
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        McpUrl = new Uri(prefix + "mcp");
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>ハンドラを登録してトークンを返す。ジョブ終了時に Unregister すること。</summary>
    public string Register(Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> handler)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _handlers[token] = handler;
        return token;
    }

    public void Unregister(string token) => _handlers.TryRemove(token, out _);

    /// <summary>HttpListener はポート 0 を受け付けないので、TCP で空きポートを一度取って返す。</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (_stopping || !_listener.IsListening) return;
                continue;
            }

            // Drain（Dispose 参照）が待つべき対象を、実際の処理を始める前に登録する。
            // handler がすぐ決定を返す場合、HandleAsync の内部処理はここで await せずに同期的に
            // handler まで進みうるため、"task を得てから登録" では間に合わないことがある
            // （登録が handler 呼び出しに間に合わず、Dispose が競り勝つ余地が生まれる）。
            var tracker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight[context] = tracker.Task;
            _ = ProcessAsync(context, tracker);
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, TaskCompletionSource tracker)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        finally
        {
            tracker.TrySetResult();
            _inFlight.TryRemove(context, out _);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.Url?.AbsolutePath != "/mcp")
            {
                await WriteAsync(response, 404, null).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                await WriteAsync(response, 405, null).ConfigureAwait(false);
                return;
            }

            var handler = ResolveHandler(request.Headers["Authorization"]);
            if (handler is null)
            {
                await WriteAsync(response, 401, null).ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // ここで得られる result は、承認済みなら人の決定そのもの。以降、応答を書き終えるまで
            // このメソッドは完了しない（早期リターンや書き込みの投げっぱなしをしない）。
            var result = await McpProtocol.HandleAsync(body, handler, _shutdown.Token).ConfigureAwait(false);
            await WriteAsync(response, result.StatusCode, result.Body).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                await WriteAsync(response, 500, null).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 応答すら書けないなら諦める（クライアント切断など）
            }
        }
    }

    private Func<PermissionRequest, CancellationToken, Task<PermissionDecision>>? ResolveHandler(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return _handlers.TryGetValue(authorization[prefix.Length..].Trim(), out var handler) ? handler : null;
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string? body)
    {
        response.StatusCode = status;
        if (body is null)
        {
            response.ContentLength64 = 0;
            response.Close();
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // 新規受付はここで止めるが、既に受け付けたリクエスト（決定を得て応答を書き込んでいる途中の
        // ものを含む）は生かしたまま、下の Drain で書き終わるのを待つ。listener.Stop()/Close() を
        // 先に呼ぶと、書き込み中の応答であっても即座に切断されてしまう（本機で実測済み）。
        _stopping = true;
        Drain();

        // HttpListener（http.sys）は、managed 側の response.Close() が返った直後であっても、
        // カーネル側でその要求の後始末が完了しきっていないことがある。その状態で listener.Stop()/
        // Close() を呼ぶと、ついさっき書き終えたはずの応答ごと接続を強制切断してしまう場合がある
        // （本機で実測: 遅延無しではほぼ確実に再現し、数十 ms の猶予で確実に解消した）。
        // Dispose はアプリ終了時に一度だけ呼ばれる想定なので、ここで小さな猶予を置いても実害はない。
        Thread.Sleep(PostDrainGrace);

        _shutdown.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>進行中のリクエスト処理（応答の書き込みを含む）が終わるのを待つ。
    /// DrainTimeout はあくまで後始末が詰まった場合の保険であり、承認待ち自体を打ち切るものではない
    /// （設計上、Dispose が呼ばれる時点で保留中の承認ハンドラは Core 側で既に完了している）。</summary>
    private void Drain()
    {
        var deadline = DateTime.UtcNow + DrainTimeout;
        while (true)
        {
            var pending = _inFlight.Values.ToArray();
            if (pending.Length == 0) return;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            try
            {
                Task.WaitAll(pending, remaining);
            }
            catch
            {
                // 個々のリクエスト処理側で例外は握り潰して 500 を返すため、ここでの例外は無視してよい。
            }
        }
    }
}
