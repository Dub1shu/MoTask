using System.Diagnostics;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 承認ハンドラの出入りを数える門（ClaudeCodeRunner が ApprovalMcpServer へ登録する実体）。
/// RunAsync が返ったあとに Core のコールバックが呼ばれると、確定済みのジョブが Running に蘇る。
/// ApprovalMcpServer.Unregister は辞書からの TryRemove でしかなく、「トークンで解決済みだが
/// まだハンドラを呼んでいない」要求を止められないので、ここで閉じて数える。
/// Close と入場カウントを同じロックの中で行うため、閉じたあとにカウントが増える隙間は無い。
/// </summary>
public sealed class PermissionGate
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

    /// <summary>閉じたあとの要求は内側（Core のコールバック）へ通さず、その場で拒否を返す。</summary>
    public Task<PermissionDecision> InvokeAsync(PermissionRequest request, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_closed) return Task.FromResult(PermissionDecision.Deny(Messages.StoppedByUser));
            if (_active++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return InvokeCoreAsync(request, ct);
    }

    /// <summary>何度呼んでもよい。</summary>
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
