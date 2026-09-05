using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>
/// Enqueue した答えがあれば即返す。無ければ Answer が呼ばれるか ct が取り消されるまで待つ。
/// CLI は複数のツールを同時に呼ぶので、待ち行列は複数件を同時に保持する（Answer は古い方から答える）。
/// HoldCancellation を立てると、取り消しを受けても ReleaseCancellations まで deny を返さずに握る。
/// 「保留中の承認へ deny を返し切ってからプロセスを殺す」順序を、待ち時間に頼らず検証するための仕掛け。
/// </summary>
public sealed class FakePermissionPrompt : IPermissionPrompt
{
    private readonly object _sync = new();
    private readonly Queue<HumanDecision> _answers = new();
    private readonly List<TaskCompletionSource<HumanDecision>> _pending = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    public List<PermissionPromptContext> Asked { get; } = new();

    /// <summary>次の AskAsync でこれを投げる（ダイアログ側の失敗の再現）。1 回で消費する。</summary>
    public Exception? ThrowOnAsk { get; set; }

    /// <summary>true なら ct の取り消しで即 deny せず、ReleaseCancellations まで握る。</summary>
    public bool HoldCancellation { get; set; }

    /// <summary>取り消しを deny に変える直前に採番する（FakeAgentRunner.NextOrder を挿す）。</summary>
    public Func<int>? StampCancellation { get; set; }

    /// <summary>StampCancellation で採番した、deny に変えた順番。</summary>
    public List<int> CancelOrders { get; } = new();

    public void Enqueue(HumanDecision decision) => _answers.Enqueue(decision);

    /// <summary>握っている取り消しをまとめて deny に変える。</summary>
    public void ReleaseCancellations() => _release.TrySetResult();

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

        // ct.Register のコールバックは Cancel() の中で同期的に走るので、ここでブロックしてはいけない。
        // 握るときは解放を待つ継続を仕掛けるだけにする。
        using var registration = ct.Register(() =>
        {
            if (!HoldCancellation)
            {
                CancelOne(tcs, ct);
                return;
            }
            Interlocked.Increment(ref _held);
            _release.Task.ContinueWith(_ => CancelOne(tcs, ct), TaskScheduler.Default);
        });
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_sync) _pending.Remove(tcs);
        }
    }

    private void CancelOne(TaskCompletionSource<HumanDecision> tcs, CancellationToken ct)
    {
        if (StampCancellation is { } stamp)
        {
            var order = stamp();
            lock (_sync) CancelOrders.Add(order);
        }
        tcs.TrySetCanceled(ct);
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
    public Task WaitUntilAskedAsync(int count = 1)
        => WaitUntilAsync(() => WaitingCount >= count, $"承認ダイアログが {count} 件呼ばれませんでした");

    /// <summary>取り消しを count 件握るまで待つ（＝畳み始めたことの目印）。</summary>
    public Task WaitUntilHeldAsync(int count = 1)
        => WaitUntilAsync(() => Volatile.Read(ref _held) >= count, $"取り消しが {count} 件届きませんでした");

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!condition()) throw new TimeoutException(message);
    }
}
