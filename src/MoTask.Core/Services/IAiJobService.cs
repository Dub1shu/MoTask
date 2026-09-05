using MoTask.Core.Model;

namespace MoTask.Core.Services;

public interface IAiJobService
{
    /// <summary>状態・イベントの変化。ワーカースレッドから上がるので、UI 側で Dispatcher へ載せ替える。</summary>
    event EventHandler<AiJobChangedEventArgs>? JobChanged;

    /// <summary>検証して Running のジョブを作り、子プロセスを起こす。上限超過・cwd 不在・claude 不在は Fail。</summary>
    Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default);

    Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default);
    /// <summary>Running / AwaitingApproval / Suspended。カードのバッジ初期化に使う。</summary>
    Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default);
    /// <summary>実行中ジョブの概算ターン数。実行中でなければ 0。</summary>
    int TurnCountOf(int jobId);
}
