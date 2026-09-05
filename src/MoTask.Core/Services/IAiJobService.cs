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

    /// <summary>実行中のジョブを止めて Cancelled にする。保留中の承認は「MoTask で停止されました」で deny。</summary>
    Task<Result> StopJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>Suspended を --resume で続ける。claude 不在・上限超過は Fail。</summary>
    Task<Result> ResumeJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>アプリ終了時。保留中の承認へ deny を返し、子プロセスを畳み、Suspended にする。</summary>
    Task SuspendAllAsync();

    /// <summary>前回のクラッシュで Running / AwaitingApproval のまま残ったジョブを Suspended にする。起動時に呼ぶ。</summary>
    Task RecoverOnStartupAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AiPermissionRule>> GetPermissionRulesAsync(CancellationToken ct = default);
    Task<Result> DeletePermissionRuleAsync(int ruleId, CancellationToken ct = default);
}
