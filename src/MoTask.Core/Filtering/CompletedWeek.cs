using MoTask.Core.Model;

namespace MoTask.Core.Filtering;

/// <summary>
/// 完了列に出すのは今週（月曜始まり、ローカル時刻）に完了した分だけで、それより前はアーカイブで見る。
/// 週の区切りは期限の「今週」と同じ <see cref="TaskFilter.WeekOf"/> を使う。
/// </summary>
public static class CompletedWeek
{
    /// <summary>
    /// DB は UTC で持つので現地の日付へ直す。EF Core から読んだ値は Kind が Unspecified なので、
    /// UTC と決めてから変換する（HistoryFormatter.Timestamp と同じ扱い）。
    /// </summary>
    public static DateOnly CompletedOn(DateTime completedAtUtc, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc), timeZone ?? TimeZoneInfo.Local);
        return DateOnly.FromDateTime(local);
    }

    /// <summary>
    /// 今週より前に完了したか。CompletedAt が無ければ false。完了列で null は本来ありえないが、
    /// 見えなくなるより安全な方に倒してボードに残す。列の役割はここでは見ない（完了列を出ると
    /// CompletedAt は null に戻るので、他の列のタスクは結果として false になる）。
    /// </summary>
    public static bool IsArchived(TaskItem task, DateOnly today, TimeZoneInfo? timeZone = null)
        => task.CompletedAt is DateTime completed
           && CompletedOn(completed, timeZone) < TaskFilter.WeekOf(today).Start;
}
