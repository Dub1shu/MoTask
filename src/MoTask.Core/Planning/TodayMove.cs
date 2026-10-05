using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// 計画の「今日中」から今日中の列へ移すものを選ぶ（仕様 2026-10-05-today-column §5）。純関数。
/// PlanningService（実際に移す）と PlanViewModel（ボタンを押せるか）が同じ規則を使うためにここに置く。
/// </summary>
public static class TodayMove
{
    /// <summary>移し先。役割が Today の列のうち Order が最小のもの。名前では探さない（改名されうる）。</summary>
    public static Column? TargetOf(Board? board)
        => board?.Columns.Where(c => c.Role == ColumnRole.Today).OrderBy(c => c.Order).FirstOrDefault();

    /// <summary>
    /// 移すタスク。今日中グループの実タスクのうち未着手の列にあるものを、計画の並び順で。
    /// 進行中・確認待ちのものを戻したり、仕分け前の候補を動かしたりはしない。
    /// </summary>
    public static IReadOnlyList<int> TasksToMove(ResolvedPlan plan)
        => plan.Groups.FirstOrDefault(g => g.Key == PlanGroupKey.Today)?.Rows
               .OfType<TaskRow>()
               .Where(r => r.ColumnRole == ColumnRole.Backlog)
               .Select(r => r.TaskId)
               .ToList()
           ?? (IReadOnlyList<int>)Array.Empty<int>();
}
