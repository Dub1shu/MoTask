using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.App.Ai.BoardTools;

/// <summary>board ツールの返り値の形（仕様 §6.1〜§6.3）。Dictionary で組んでから 1 度だけ直列化する。</summary>
public static class BoardJson
{
    /// <summary>一覧に載せる本文の長さ。全文は get_task で取る。</summary>
    public const int DescriptionLimit = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        // Claude が読む JSON なので、日本語を \uXXXX に潰さない。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Dictionary<string, object?> BoardShape(
        Board board, IReadOnlyList<Project> projects, IReadOnlyList<Label> labels)
        => new()
        {
            ["columns"] = board.Columns.OrderBy(c => c.Order).Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["role"] = c.Role.ToString(),
                ["wipLimit"] = c.WipLimit,
                ["taskCount"] = c.ActiveCount,
                ["overWip"] = c.IsOverWip,
            }).ToArray(),
            // アーカイブ済みも archived: true を付けて返す（既存タスクに付いたままのことがある）
            ["projects"] = projects.Select(p => new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["name"] = p.Name, ["archived"] = p.Archived,
            }).ToArray(),
            ["labels"] = labels.Select(l => new Dictionary<string, object?>
            {
                ["id"] = l.Id, ["name"] = l.Name, ["color"] = l.Color, ["archived"] = l.Archived,
            }).ToArray(),
        };

    public static Dictionary<string, object?> TaskSummary(
        TaskItem task, Column column, IReadOnlyDictionary<int, string> projectNames)
        => new()
        {
            ["id"] = task.Id,
            ["title"] = task.Title,
            ["column"] = column.Name,
            ["project"] = task.ProjectId is int pid && projectNames.TryGetValue(pid, out var name) ? name : null,
            ["labels"] = task.Labels.Select(l => l.Name).ToArray(),
            ["dueDate"] = Date(task.DueDate),
            ["completedAt"] = Timestamp(task.CompletedAt),
            ["description"] = Truncate(task.Description, DescriptionLimit),
        };

    public static Dictionary<string, object?> TaskDetail(
        TaskItem task, Column column, IReadOnlyDictionary<int, string> projectNames)
    {
        var detail = TaskSummary(task, column, projectNames);
        detail["description"] = task.Description;
        detail["createdAt"] = Timestamp(task.CreatedAt);
        detail["updatedAt"] = Timestamp(task.UpdatedAt);
        detail["position"] = task.Position;
        return detail;
    }

    /// <summary>WIP 超過などの警告を同じ JSON に同梱する（仕様 §6「共通の約束」）。</summary>
    public static string Serialize(Dictionary<string, object?> payload, IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0) payload["warnings"] = warnings.ToArray();
        return JsonSerializer.Serialize(payload, Options);
    }

    public static string? Date(DateOnly? value)
        => value is DateOnly d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>DB から戻る DateTime は Kind が Unspecified のことがあるので UTC と決め打って表記を揃える。</summary>
    public static string? Timestamp(DateTime? value)
        => value is DateTime t
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)
            : null;

    public static string Truncate(string text, int limit)
    {
        if (text.Length <= limit) return text;
        // サロゲートペアの途中で切ると片割れだけが残って壊れるので、1 つ手前へ寄せる。
        var cut = limit > 0 && char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
        return text[..cut] + "…";
    }
}
