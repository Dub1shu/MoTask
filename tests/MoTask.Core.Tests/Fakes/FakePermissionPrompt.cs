using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>Enqueue した答えがあれば即返す。無ければ Answer が呼ばれるか ct が取り消されるまで待つ。</summary>
public sealed class FakePermissionPrompt : IPermissionPrompt
{
    private readonly Queue<HumanDecision> _answers = new();
    private TaskCompletionSource<HumanDecision>? _pending;

    public List<PermissionPromptContext> Asked { get; } = new();

    /// <summary>次の AskAsync でこれを投げる（ダイアログ側の失敗の再現）。1 回で消費する。</summary>
    public Exception? ThrowOnAsk { get; set; }

    public void Enqueue(HumanDecision decision) => _answers.Enqueue(decision);

    public async Task<HumanDecision> AskAsync(PermissionPromptContext context, CancellationToken ct)
    {
        Asked.Add(context);
        if (ThrowOnAsk is { } failure)
        {
            ThrowOnAsk = null;
            throw failure;
        }
        if (_answers.Count > 0) return _answers.Dequeue();

        var tcs = new TaskCompletionSource<HumanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    public bool IsWaiting => _pending is { Task.IsCompleted: false };

    public void Answer(HumanDecision decision) => _pending!.TrySetResult(decision);

    /// <summary>ダイアログが出るまで待つ（サービスはワーカースレッドで進む）。</summary>
    public async Task WaitUntilAskedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!IsWaiting && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!IsWaiting) throw new TimeoutException("承認ダイアログが呼ばれませんでした");
    }
}
