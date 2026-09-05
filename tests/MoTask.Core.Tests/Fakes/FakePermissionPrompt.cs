using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>
/// Enqueue した答えがあれば即返す。無ければ Answer が呼ばれるか ct が取り消されるまで待つ。
/// CLI は複数のツールを同時に呼ぶので、待ち行列は複数件を同時に保持する（Answer は古い方から答える）。
/// </summary>
public sealed class FakePermissionPrompt : IPermissionPrompt
{
    private readonly object _sync = new();
    private readonly Queue<HumanDecision> _answers = new();
    private readonly List<TaskCompletionSource<HumanDecision>> _pending = new();

    public List<PermissionPromptContext> Asked { get; } = new();

    /// <summary>次の AskAsync でこれを投げる（ダイアログ側の失敗の再現）。1 回で消費する。</summary>
    public Exception? ThrowOnAsk { get; set; }

    public void Enqueue(HumanDecision decision) => _answers.Enqueue(decision);

    public async Task<HumanDecision> AskAsync(PermissionPromptContext context, CancellationToken ct)
    {
        TaskCompletionSource<HumanDecision> tcs;
        lock (_sync)
        {
            Asked.Add(context);
            if (ThrowOnAsk is { } failure)
            {
                ThrowOnAsk = null;
                throw failure;
            }
            if (_answers.Count > 0) return _answers.Dequeue();

            tcs = new TaskCompletionSource<HumanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(tcs);
        }

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_sync) _pending.Remove(tcs);
        }
    }

    /// <summary>まだ答えを待っているダイアログの数。</summary>
    public int WaitingCount
    {
        get { lock (_sync) return _pending.Count(p => !p.Task.IsCompleted); }
    }

    public bool IsWaiting => WaitingCount > 0;

    /// <summary>いちばん古い待ちに答える。</summary>
    public void Answer(HumanDecision decision)
    {
        TaskCompletionSource<HumanDecision> tcs;
        lock (_sync) tcs = _pending.First(p => !p.Task.IsCompleted);
        tcs.TrySetResult(decision);
    }

    /// <summary>ダイアログが count 件出るまで待つ（サービスはワーカースレッドで進む）。</summary>
    public async Task WaitUntilAskedAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (WaitingCount < count && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (WaitingCount < count) throw new TimeoutException($"承認ダイアログが {count} 件呼ばれませんでした");
    }
}
