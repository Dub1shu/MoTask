using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MoTask.App.Ai.BoardTools;
using MoTask.App.Ai.MorningTools;

namespace MoTask.App.Ai;

/// <summary>
/// アプリ内に 1 つだけ立てる HTTP MCP サーバ。127.0.0.1 の空きポートへバインドする。
/// HttpListener は 127.0.0.1 への非管理者バインドが可能なことを本機で確認済み（urlacl の登録も
/// `http://+:port/` への変更も不要）。
/// 認証はアプリの起動ごとに 1 つ発行する board トークンだけで、endpoint.json 経由でブリッジへ渡す。
/// 提供するのは <see cref="BoardToolHost"/> の board ツール 6 本と
/// <see cref="MorningToolHost"/> の morning ツール 4 本。
///
/// リクエストにサーバ側のタイムアウトは設けない。ツールの実処理（DB 操作）は短く、詰まったときは
/// 呼び出し側（ブリッジ / Claude Code）が打ち切るのが筋なので、ここで勝手に切ると
/// 「書き込みは通ったのに応答だけ落ちた」という一番始末の悪い状態を作りかねない。
///
/// Dispose 時の契約: HttpListener は Stop()/Close() を呼ぶと、書き込み中の応答であっても
/// ObjectDisposedException で打ち切ってしまう（本機で実測済み）。そのため Dispose() は、
/// 進行中のリクエスト処理（応答を書き込む途中のものを含む）が完了するのを待ってから listener を止める。
/// </summary>
public sealed class MoTaskMcpServer : IDisposable
{
    /// <summary>Dispose 時に進行中のリクエストを待つ上限。後始末が万一詰まった場合に
    /// アプリの終了処理を無限に止めないための保険。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Drain 完了後、listener.Stop()/Close() を呼ぶまでの猶予。http.sys 側の後始末を
    /// 追い越して直前の応答を巻き添えにしないための待機（Dispose() 参照）。</summary>
    private static readonly TimeSpan PostDrainGrace = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<McpTool> _tools;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<HttpListenerContext, Task> _inFlight = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _stopping;
    private int _disposed;
    private string? _boardToken;

    public MoTaskMcpServer(BoardToolHost boardTools, MorningToolHost morningTools)
        => _tools = boardTools.Tools.Concat(morningTools.Tools).ToList();

    public Uri? McpUrl { get; private set; }

    /// <summary>board ツール専用トークン。endpoint.json に書いてブリッジへ渡す。Start 後に有効。</summary>
    public string BoardToken => _boardToken
        ?? throw new InvalidOperationException("MoTaskMcpServer が起動していません（Start を先に呼ぶこと）");

    public void Start()
    {
        if (McpUrl is not null) return;
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        McpUrl = new Uri(prefix + "mcp");
        _boardToken = NewToken();
        _ = Task.Run(AcceptLoopAsync);
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

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
            // HandleAsync の内部処理は最初の await まで同期的に進みうるため、"task を得てから登録"
            // では間に合わないことがある（登録が処理の開始に間に合わず、Dispose が競り勝つ余地が生まれる）。
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

            if (!IsAuthorized(request.Headers["Authorization"]))
            {
                await WriteAsync(response, 401, null).ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // 以降、応答を書き終えるまでこのメソッドは完了しない（早期リターンや書き込みの投げっぱなしをしない）。
            var result = await McpProtocol.HandleAsync(body, _tools, _shutdown.Token).ConfigureAwait(false);
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

    private bool IsAuthorized(string? authorization)
    {
        const string prefix = "Bearer ";
        if (_boardToken is null) return false;
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return string.Equals(authorization[prefix.Length..].Trim(), _boardToken, StringComparison.Ordinal);
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

        // 新規受付はここで止めるが、既に受け付けたリクエスト（応答を書き込んでいる途中のものを含む）は
        // 生かしたまま、下の Drain で書き終わるのを待つ。listener.Stop()/Close() を先に呼ぶと、
        // 書き込み中の応答であっても即座に切断されてしまう（本機で実測済み）。
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
    /// DrainTimeout はあくまで後始末が詰まった場合の保険。</summary>
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
