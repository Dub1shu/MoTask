using System.Globalization;
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// result/ の形を知る唯一の場所（仕様 §8）。HookEventParser が CLI 出力への依存を 1 点に
/// 閉じているのと同じ役割で、同じく純関数にして fixture から固定する。
/// 行単位で検証し、読めない行は捨てて残りを返す。1 行の JSON 崩れで朝を全滅させない。
/// </summary>
public static class MorningResultReader
{
    /// <summary>plan.json の groups[].key に許す 4 値（仕様 §8）。</summary>
    public static readonly IReadOnlyList<string> PlanGroupKeys =
        new[] { "today", "ifTime", "aiReady", "waiting" };

    public static MorningResult Read(string? candidatesJsonl, string? planJson)
    {
        var candidates = ReadCandidates(candidatesJsonl, out var discarded);
        return new MorningResult(candidates, discarded, ReadPlan(planJson));
    }

    private static IReadOnlyList<CandidateRecord> ReadCandidates(string? text, out int discarded)
    {
        discarded = 0;
        var records = new List<CandidateRecord>();
        if (string.IsNullOrWhiteSpace(text)) return records;

        // 同じファイルに同じ ExternalId が 2 度出てきたら 2 件目以降は捨てる。
        // DB の一意インデックスに任せると SaveChanges ごと落ちるので、ここで先に落とす。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue; // 空行は「読めなかった行」に数えない
            var record = Parse(trimmed);
            if (record is null || !seen.Add(record.ExternalId))
            {
                discarded++;
                continue;
            }
            records.Add(record);
        }
        return records;
    }

    private static CandidateRecord? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            var externalId = Text(root, "externalId");
            var source = Text(root, "source");
            var title = Text(root, "title");
            // 根拠の無い候補は人が判断できないので捨てる（仕様 §8）
            var evidence = Text(root, "evidence");
            if (externalId.Length == 0 || source.Length == 0 || title.Length == 0 || evidence.Length == 0) return null;

            if (!TryAction(Text(root, "suggestedAction"), out var action)) return null;
            var mergeTarget = Int(root, "mergeTargetTaskId");
            if (action == TriageAction.Merge && mergeTarget is null) return null;

            return new CandidateRecord(
                externalId, source, title, evidence,
                Text(root, "from"), Text(root, "link"), Text(root, "reasoning"),
                Instant(root, "receivedAt"), Date(root, "suggestedDueDate"),
                Text(root, "suggestedProject"), action, mergeTarget);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>妥当なら原文をそのまま返す（生 JSON を 1 カラムに持つ・仕様 §9）。壊れていれば空文字。</summary>
    private static string ReadPlan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array) return "";

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) return "";
                if (!PlanGroupKeys.Contains(Text(group, "key"))) return "";
                if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return "";
            }
            return text.Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static bool TryAction(string value, out TriageAction action)
    {
        switch (value)
        {
            case "register": action = TriageAction.Register; return true;
            case "merge": action = TriageAction.Merge; return true;
            case "later": action = TriageAction.Later; return true;
            case "reject": action = TriageAction.Reject; return true;
            default: action = TriageAction.Register; return false;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>オフセット付き ISO8601 を UTC へ寄せる。読めなければ null（行そのものは捨てない）。</summary>
    private static DateTime? Instant(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text.Length > 0
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.UtcDateTime
            : null;
    }

    private static DateOnly? Date(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text.Length > 0
               && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }
}
