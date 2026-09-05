namespace MoTask.Core.Abstractions;

/// <summary>
/// DbContext は同時に 1 操作しか受け付けない。BoardService と AiJobService は同じ singleton の
/// DbContext を使うので、両者が同じインスタンスのこのゲートを通してユースケースを直列化する。
/// ゲートの中から別サービスの（同じゲートを取る）操作を呼ぶとデッドロックするので、
/// AiJobService は完了時の列移動をゲートの外から呼ぶ。
/// </summary>
public sealed class OperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(Func<Task> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
