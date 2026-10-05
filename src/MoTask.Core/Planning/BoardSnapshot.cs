using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// planning_get_context が返す盤面のスナップショット（仕様 §6）。統合先の推薦と計画作成には現在の盤面が要るが、
/// SQLite を直接読ませず MoTask がスナップショットを書く。
/// </summary>
public static class BoardSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 人が開いて読めるように、日本語やスラッシュを \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(
        Board board,
        DateOnly date,
        IReadOnlyDictionary<int, string> projectNames,
        IReadOnlyCollection<int> taskIdsWithActiveAiJob,
        IReadOnlyList<Label> labels)
    {
        var ordered = board.Columns.OrderBy(c => c.Order).ToList();
        var columns = ordered.Select(c => new ColumnDto(c.Id, c.Name, c.Role.ToString())).ToList();

        var tasks = new List<TaskDto>();
        foreach (var column in ordered)
        {
            // 未完了タスクだけを渡す（仕様 §6）。論理削除済みも含めない（仕様 §7）。
            if (column.Role == ColumnRole.Done) continue;
            foreach (var task in column.Tasks.Where(t => t.DeletedAt is null).OrderBy(t => t.Position))
            {
                tasks.Add(new TaskDto(
                    task.Id,
                    task.Title,
                    column.Id,
                    column.Role.ToString(),
                    task.ProjectId is int id && projectNames.TryGetValue(id, out var name) ? name : null,
                    task.DueDate?.ToString("yyyy-MM-dd"),
                    task.Labels.Select(l => l.Name).ToList(),
                    DateTime.SpecifyKind(task.UpdatedAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    taskIdsWithActiveAiJob.Contains(task.Id)));
            }
        }

        var labelDtos = labels.Where(l => !l.Archived).Select(l => new LabelDto(l.Id, l.Name)).ToList();

        return JsonSerializer.Serialize(
            new SnapshotDto(date.ToString("yyyy-MM-dd"), columns, tasks, labelDtos), Options);
    }

    private sealed record SnapshotDto(
        string Date, IReadOnlyList<ColumnDto> Columns, IReadOnlyList<TaskDto> Tasks, IReadOnlyList<LabelDto> Labels);

    /// <summary>候補にラベルを推薦するときに選べるもの（アーカイブ済みは入れない）。</summary>
    private sealed record LabelDto(int Id, string Name);

    private sealed record ColumnDto(int Id, string Name, string Role);

    private sealed record TaskDto(
        int Id, string Title, int ColumnId, string ColumnRole, string? Project,
        string? Due, IReadOnlyList<string> Labels, string UpdatedAt, bool HasActiveAiJob);
}
