using System.Globalization;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>「9/4 8:40 未着手 → 進行中」の形に整形する。</summary>
public static class HistoryFormatter
{
    public static string Format(HistoryEntry entry, Func<int, string> columnName, TimeZoneInfo? timeZone = null)
    {
        var utc = DateTime.SpecifyKind(entry.At, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? TimeZoneInfo.Local);
        var stamp = local.ToString(Strings.HistoryTimestampFormat, CultureInfo.InvariantCulture);

        var body = entry.Kind switch
        {
            HistoryKind.Created => string.Format(Strings.HistoryCreatedFormat, Name(entry.ToColumnId)),
            HistoryKind.Moved => string.Format(Strings.HistoryMovedFormat, Name(entry.FromColumnId), Name(entry.ToColumnId)),
            HistoryKind.Edited => string.Format(Strings.HistoryEditedFormat,
                string.Join(Strings.HistoryFieldJoiner, HistoryDetail.Deserialize(entry.Detail).Keys.Select(FieldName))),
            HistoryKind.Deleted => Strings.HistoryDeleted,
            HistoryKind.Restored => Strings.HistoryRestored,
            _ => Strings.HistoryUnknown,
        };
        return $"{stamp} {body}";

        string Name(int? id) => id is int i ? columnName(i) : Strings.UnknownColumn;
    }

    public static string FieldName(string key) => key switch
    {
        "Title" => Strings.FieldTitle,
        "Description" => Strings.FieldDescription,
        "Project" => Strings.FieldProject,
        "DueDate" => Strings.FieldDueDate,
        "Labels" => Strings.FieldLabels,
        _ => key,
    };
}
