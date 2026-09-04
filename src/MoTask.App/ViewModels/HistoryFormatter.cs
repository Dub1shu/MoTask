using System.Globalization;
using System.Text.Json;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>「9/4 8:40 未着手 → 進行中」の形に整形する。</summary>
public static class HistoryFormatter
{
    public static string Format(HistoryEntry entry, Func<int, string> columnName, TimeZoneInfo? timeZone = null)
    {
        var stamp = Timestamp(entry.At, timeZone);

        var body = entry.Kind switch
        {
            HistoryKind.Created => string.Format(Strings.HistoryCreatedFormat, Name(entry.ToColumnId)),
            HistoryKind.Moved => string.Format(Strings.HistoryMovedFormat, Name(entry.FromColumnId), Name(entry.ToColumnId)),
            HistoryKind.Edited => FormatEdited(entry.Detail),
            HistoryKind.Deleted => Strings.HistoryDeleted,
            HistoryKind.Restored => Strings.HistoryRestored,
            _ => Strings.HistoryUnknown,
        };
        return $"{stamp} {body}";

        string Name(int? id) => id is int i ? columnName(i) : Strings.UnknownColumn;
    }

    /// <summary>
    /// DB は UTC で持つので現地時刻へ直してから「9/4 8:40」の形にする。完了日時も同じ時計・
    /// 同じ書式で出すため、履歴と共有する。
    /// </summary>
    public static string Timestamp(DateTime utc, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone ?? TimeZoneInfo.Local);
        return local.ToString(Strings.HistoryTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 壊れた、あるいは将来のスキーマとずれた Detail は JsonException を投げうる（HistoryDetail.Deserialize）。
    /// 1件の壊れた履歴行のために呼び出し元（LoadHistoryAsync 経由の PendingSave）を fault させないよう、
    /// ここで受け止めて日本語のフォールバック文言を返す。
    /// </summary>
    private static string FormatEdited(string detail)
    {
        IReadOnlyDictionary<string, FieldChange> changes;
        try
        {
            changes = HistoryDetail.Deserialize(detail);
        }
        catch (JsonException)
        {
            return Strings.HistoryUnknown;
        }
        return string.Format(Strings.HistoryEditedFormat, string.Join(Strings.HistoryFieldJoiner, changes.Keys.Select(FieldName)));
    }

    public static string FieldName(string key) => key switch
    {
        "Title" => Strings.FieldTitle,
        "Description" => Strings.FieldDescription,
        "Project" => Strings.FieldProject,
        "DueDate" => Strings.FieldDueDate,
        "Labels" => Strings.FieldLabels,
        _ => Strings.FieldUnknown,
    };
}
