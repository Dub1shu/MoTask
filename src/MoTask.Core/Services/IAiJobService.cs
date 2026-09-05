using MoTask.Core.Model;

namespace MoTask.Core.Services;

public interface IAiJobService
{
    /// <summary>状態・イベントの変化。追従スレッドから上がるので、UI 側で Dispatcher へ載せ替える。</summary>
    event EventHandler<AiJobChangedEventArgs>? JobChanged;

    /// <summary>
    /// ジョブフォルダを作り、端末を開いて手放す。返るジョブは Pending
    /// （SessionStart フックが届いて初めて Running になる）。
    /// </summary>
    Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default);

    Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default);
    /// <summary>Pending / Running / WaitingForInput。カードのバッジ初期化と起動時の追いつきに使う。</summary>
    Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default);
    /// <summary>ジョブフォルダの artifacts/ にある実ファイル（仕様 §6）。</summary>
    Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default);
    /// <summary>Stop フックを数えたターン数。追跡していなければ 0。</summary>
    int TurnCountOf(int jobId);

    /// <summary>--resume で端末を開き直す。状態は変えない（SessionStart フックが Running に戻す）。</summary>
    Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// 端末を × で閉じられて SessionEnd が来なかったジョブを、人の手で閉じる。
    /// SessionEnd を受け取ったのと同じ扱い（Succeeded にして確認待ち列へ）。
    /// </summary>
    Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>追跡をやめて Cancelled にする。端末のプロセスは殺さない（仕様 §8）。</summary>
    Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default);

    /// <summary>未完了ジョブの events.jsonl を、記録済みの行数から読み直して追いつく。起動時に呼ぶ。</summary>
    Task RecoverOnStartupAsync(CancellationToken ct = default);
}
