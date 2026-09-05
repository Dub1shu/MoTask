using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// Claude Code CLI を子プロセスとして起動し、stdout の stream-json を 1 行ずつ AgentEvent に写す（仕様 §3, §8）。
/// 承認は ApprovalMcpServer に登録したハンドラ経由で AiJobService へ戻る。
/// ct の取り消しでプロセスツリーごと殺し、OperationCanceledException で抜ける。
/// </summary>
public sealed class ClaudeCodeRunner : IAgentRunner
{
    private const int StderrTailLength = 2000;

    /// <summary>承認ハンドラが決定を返しきるのを待つ上限。承認そのものへのタイムアウトではなく、
    /// 万一ハンドラが返らないときに停止操作を永久に止めないための保険。</summary>
    private static readonly TimeSpan ApprovalFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>決定が出てから子を殺すまでの猶予。決定は HTTP 応答として CLI へ書き戻される途中でありうる。</summary>
    private static readonly TimeSpan ApprovalFlushGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>子が終わったあと stderr を読み切るのを待つ上限（下の AwaitTailAsync 参照）。</summary>
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ApprovalMcpServer _server;
    private readonly IAiSettingsStore _settings;

    public ClaudeCodeRunner(ApprovalMcpServer server, IAiSettingsStore settings)
    {
        _server = server;
        _settings = settings;
    }

    public Result CheckAvailable()
        => ClaudeLocator.Find(_settings.Load().ClaudeExecutablePath) is null
            ? Result.Fail(Messages.ClaudeNotFound)
            : Result.Ok();

    public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var settings = _settings.Load();
        var exe = ClaudeLocator.Find(settings.ClaudeExecutablePath) ?? throw new InvalidOperationException(Messages.ClaudeNotFound);
        var mcpUrl = _server.McpUrl ?? throw new InvalidOperationException("ApprovalMcpServer が起動していません");

        var gate = new PermissionGate(request.OnPermissionRequest);
        var token = _server.Register(gate.InvokeAsync);
        var configPath = McpConfigFile.Write(request.JobId, mcpUrl, token);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in ClaudeCodeArguments.Build(request, settings, configPath)) psi.ArgumentList.Add(arg);
            // MoTask 自身が Claude Code のセッション内から起動されていても、子を入れ子セッション扱いにさせない
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.StandardInput.Close(); // -p に指示を渡しているので stdin は使わない

            // stdout を読み進める前に stderr の吸い出しを始める。片方を読まずに放置すると
            // パイプのバッファが埋まった時点で子が書き込みでブロックして進まなくなる。
            var stderr = ReadTailAsync(process.StandardError);

            // 取り消し・OnEvent の失敗・どちらから来ても子の畳み方は 1 つ。二重に走らせない。
            var stopSync = new object();
            Task? stopping = null;
            Task StopChildOnceAsync()
            {
                lock (stopSync) return stopping ??= StopChildAsync(process, gate);
            }

            AgentResultInfo? result = null;
            // 取り消しコールバックは Cancel() の呼び出し元を止めないよう、開始だけして返る。
            using var killOnCancel = ct.Register(() => _ = StopChildOnceAsync());
            try
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    foreach (var ev in ClaudeCodeParser.Parse(line))
                    {
                        if (ev.Result is not null) result = ev.Result;
                        // OnEvent は待つ。遅い購読者は取りこぼさず、子への背圧として効かせる。
                        await request.OnEvent(ev).ConfigureAwait(false);
                    }
                }
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                // 正常終了と取り消しが競った場合も取り消し扱いに揃える（出口を 1 本にする）。
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                // 取り消しでも OnEvent の失敗でも、子を野放しにしたまま抜けない。
                await StopChildOnceAsync().ConfigureAwait(false);
                throw;
            }

            return new AgentRunOutcome(process.ExitCode, result, await AwaitTailAsync(stderr).ConfigureAwait(false));
        }
        finally
        {
            // RunAsync が返ったあとに Core のコールバックが呼ばれると、確定済みのジョブが Running に
            // 蘇る。トークンを外して（新しい要求はもう解決されない）、門を閉じて（解決済みでまだ
            // 呼ばれていない要求も内側へ通さない）、走っているものを待ちきってから返る。
            _server.Unregister(token);
            gate.Close();
            await gate.WaitIdleAsync(null).ConfigureAwait(false);
            McpConfigFile.Delete(configPath);
        }
    }

    /// <summary>
    /// 承認の決定を書き戻しきるまで待ってから、プロセスツリーごと殺す。
    /// Core は「ハンドラが決定を返した」ところまでしか観測できず、その決定が HTTP 応答として
    /// CLI に届いたかまでは分からない。先に殺すと拒否が CLI に届かないまま消える。
    /// </summary>
    private static async Task StopChildAsync(Process process, PermissionGate gate)
    {
        await gate.WaitIdleAsync(ApprovalFlushTimeout).ConfigureAwait(false);
        if (gate.SinceLastDecision is { } elapsed && elapsed < ApprovalFlushGrace)
        {
            await Task.Delay(ApprovalFlushGrace - elapsed).ConfigureAwait(false);
        }
        TryKill(process);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            // 既に終わっている、権限が無い、あるいは正常終了と取り消しが競って Process が捨てられた後。
            // いずれも子は残らず、呼び出し側の状態遷移にも影響しない。
        }
    }

    private static async Task<string?> ReadTailAsync(StreamReader reader)
    {
        try
        {
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (text.Length == 0) return null;
            return text.Length <= StderrTailLength ? text : text[^StderrTailLength..];
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// stderr の末尾は失敗理由の補助でしかない。孫プロセスがハンドルを握ったまま生き残ると
    /// 子が終わっても読み切れないので、そこでジョブの完了を止めない。
    /// </summary>
    private static async Task<string?> AwaitTailAsync(Task<string?> stderr)
    {
        var finished = await Task.WhenAny(stderr, Task.Delay(StderrDrainTimeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, stderr) ? await stderr.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// 承認ハンドラの出入りを数える門。トークンの Unregister だけでは「解決済みだがまだ呼ばれて
    /// いない」要求を止められないので、閉じたあとに来たものは内側（Core）へ通さず即 deny で返す。
    /// </summary>
    private sealed class PermissionGate
    {
        private readonly Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> _inner;
        private readonly object _sync = new();
        private TaskCompletionSource? _idle;
        private int _active;
        private bool _closed;
        private long _lastDecisionTimestamp;

        public PermissionGate(Func<PermissionRequest, CancellationToken, Task<PermissionDecision>> inner) => _inner = inner;

        /// <summary>直近の決定からの経過時間。まだ 1 件も決まっていなければ null。</summary>
        public TimeSpan? SinceLastDecision
        {
            get
            {
                lock (_sync)
                {
                    return _lastDecisionTimestamp == 0 ? null : Stopwatch.GetElapsedTime(_lastDecisionTimestamp);
                }
            }
        }

        public Task<PermissionDecision> InvokeAsync(PermissionRequest request, CancellationToken ct)
        {
            lock (_sync)
            {
                if (_closed) return Task.FromResult(PermissionDecision.Deny(Messages.StoppedByUser));
                if (_active++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return InvokeCoreAsync(request, ct);
        }

        public void Close()
        {
            lock (_sync) _closed = true;
        }

        /// <summary>走っているハンドラが無くなるまで待つ。timeout が null なら待ちきる。</summary>
        public async Task WaitIdleAsync(TimeSpan? timeout)
        {
            Task idle;
            lock (_sync)
            {
                if (_active == 0) return;
                idle = _idle!.Task;
            }
            if (timeout is null) await idle.ConfigureAwait(false);
            else await Task.WhenAny(idle, Task.Delay(timeout.Value)).ConfigureAwait(false);
        }

        private async Task<PermissionDecision> InvokeCoreAsync(PermissionRequest request, CancellationToken ct)
        {
            try
            {
                return await _inner(request, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _lastDecisionTimestamp = Stopwatch.GetTimestamp();
                    if (--_active == 0)
                    {
                        _idle!.TrySetResult();
                        _idle = null;
                    }
                }
            }
        }
    }
}
