using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// 保存済み PlanJson の中身を読む唯一の場所（仕様 §4）。純関数。
/// 各項目が taskId か externalId のどちらかを指す、という契約の要（親仕様 §8）をここで画面の行に落とす。
/// 候補を登録すればその行が実タスクに解決し、却下・あとでにすれば行が消える。
/// </summary>
public static class PlanResolver
{
    /// <summary>JSON の key と enum の対応。PlanValidator.PlanGroupKeys と同じ並び。</summary>
    private static readonly (string Json, PlanGroupKey Key)[] Keys =
    {
        ("today", PlanGroupKey.Today),
        ("ifTime", PlanGroupKey.IfTime),
        ("aiReady", PlanGroupKey.AiReady),
        ("waiting", PlanGroupKey.Waiting),
    };

    public static ResolvedPlan Resolve(
        string planJson,
        IReadOnlyList<TriageCandidate> candidates,
        Board? board,
        Func<int?, string?> projectName)
    {
        var summary = TriageSummary.Of(candidates);
        if (string.IsNullOrWhiteSpace(planJson)) return ResolvedPlan.Empty(summary);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(planJson);
        }
        catch (JsonException)
        {
            // PlanValidator が検証済みのはずだが、DB の中身を信用しきらない
            return ResolvedPlan.Empty(summary);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ResolvedPlan.Empty(summary);
            var root = doc.RootElement;
            var index = new Index(candidates, board, projectName);

            var seenTasks = new HashSet<int>();
            var seenCandidates = new HashSet<int>();
            var groups = new List<PlanGroup>(Keys.Length);
            foreach (var (json, key) in Keys)
            {
                var rows = new List<PlanRow>();
                foreach (var item in ItemsOf(root, json))
                {
                    var row = index.Resolve(item);
                    var isNew = row switch
                    {
                        TaskRow task => seenTasks.Add(task.TaskId),
                        CandidateRow candidate => seenCandidates.Add(candidate.CandidateId),
                        _ => false,
                    };
                    if (isNew) rows.Add(row!);
                }
                groups.Add(new PlanGroup(key, rows));
            }

            PlanRow? firstThing = null;
            var reason = "";
            if (root.TryGetProperty("firstThing", out var first) && first.ValueKind == JsonValueKind.Object)
            {
                firstThing = index.Resolve(first);
                if (firstThing is not null) reason = Text(first, "reason");
            }
            var fallback = firstThing is null;
            if (fallback) firstThing = groups[0].Rows.FirstOrDefault();

            return new ResolvedPlan(firstThing, reason, fallback, groups, summary);
        }
    }

    /// <summary>groups[] から key の一致する要素の items[] を返す。無ければ空。</summary>
    private static IEnumerable<JsonElement> ItemsOf(JsonElement root, string key)
    {
        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || Text(group, "key") != key) continue;
            if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray()) yield return item;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static bool TryInt(JsonElement element, string name, out int number)
    {
        number = 0;
        return element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out number);
    }

    /// <summary>盤面と候補の引き当て。論理削除済みのタスクは最初から入れない。</summary>
    private sealed class Index
    {
        private readonly Dictionary<int, (TaskItem Task, Column Column)> _tasks = new();
        private readonly Dictionary<string, TriageCandidate> _byExternalId;
        private readonly Func<int?, string?> _projectName;

        public Index(IReadOnlyList<TriageCandidate> candidates, Board? board, Func<int?, string?> projectName)
        {
            _projectName = projectName;
            _byExternalId = new Dictionary<string, TriageCandidate>(StringComparer.Ordinal);
            foreach (var candidate in candidates) _byExternalId.TryAdd(candidate.ExternalId, candidate);
            if (board is null) return;
            foreach (var column in board.Columns)
            foreach (var task in column.Tasks)
            {
                if (!task.IsDeleted) _tasks[task.Id] = (task, column);
            }
        }

        public PlanRow? Resolve(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            // 両方持っていたら taskId を優先する（契約は「どちらか一方」だが、両方来ても落とさない）
            if (TryInt(item, "taskId", out var taskId)) return TaskRowFor(taskId, TaskRowOrigin.None);

            var externalId = Text(item, "externalId");
            if (externalId.Length == 0 || !_byExternalId.TryGetValue(externalId, out var candidate)) return null;
            return candidate.Status switch
            {
                TriageStatus.Pending or TriageStatus.Later => new CandidateRow(
                    candidate.Id, candidate.Title, candidate.Source, candidate.SuggestedDueDate, candidate.SuggestedProject),
                TriageStatus.Registered when candidate.ResultTaskId is int registered
                    => TaskRowFor(registered, TaskRowOrigin.RegisteredThisRun),
                TriageStatus.Merged when candidate.ResultTaskId is int merged
                    => TaskRowFor(merged, TaskRowOrigin.MergedThisRun),
                _ => null, // Rejected、または ResultTaskId の無い決着済み
            };
        }

        private TaskRow? TaskRowFor(int taskId, TaskRowOrigin origin)
        {
            if (!_tasks.TryGetValue(taskId, out var found)) return null;
            var (task, column) = found;
            return new TaskRow(
                task.Id, task.Title, _projectName(task.ProjectId), task.DueDate,
                column.Name, column.Role == ColumnRole.Done, origin);
        }
    }
}
