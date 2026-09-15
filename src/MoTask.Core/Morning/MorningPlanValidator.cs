using System.Text.Json;

namespace MoTask.Core.Morning;

/// <summary>
/// プラン 1 本の検証（仕様 §6）。妥当なら原文をそのまま返す（生 JSON を 1 カラムに持つ・親仕様 §9）ので、
/// MorningPlanResolver が読む文字列の形は今までと変わらない。
/// </summary>
public static class MorningPlanValidator
{
    /// <summary>groups[].key に許す 4 値（親仕様 §8）。</summary>
    public static readonly IReadOnlyList<string> PlanGroupKeys =
        new[] { "today", "ifTime", "aiReady", "waiting" };

    public static Result<string> Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Result.Fail<string>(Messages.PlanNotAnObject);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result.Fail<string>(Messages.PlanNotAnObject);

            if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return Result.Fail<string>(Messages.PlanGroupsInvalid);
            }

            var groupIndex = 0;
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    return Result.Fail<string>(string.Format(Messages.PlanGroupNotAnObjectFormat, groupIndex));
                }
                var key = Text(group, "key");
                if (!PlanGroupKeys.Contains(key))
                {
                    return Result.Fail<string>(string.Format(Messages.PlanGroupKeyInvalidFormat, groupIndex, key));
                }
                if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return Result.Fail<string>(string.Format(Messages.PlanItemsInvalidFormat, groupIndex));
                }

                var itemIndex = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (!HasIdentity(item))
                    {
                        return Result.Fail<string>(
                            string.Format(Messages.PlanItemNeedsIdFormat, groupIndex, itemIndex));
                    }
                    itemIndex++;
                }
                groupIndex++;
            }

            // firstThing は無くてもよい（最初の 1 件を決められない朝もある）。あるなら items と同じ形。
            if (root.TryGetProperty("firstThing", out var first)
                && first.ValueKind != JsonValueKind.Null
                && !HasIdentity(first))
            {
                return Result.Fail<string>(Messages.PlanFirstThingNeedsId);
            }

            return Result.Ok(json.Trim());
        }
        catch (JsonException)
        {
            return Result.Fail<string>(Messages.PlanNotAnObject);
        }
    }

    private static bool HasIdentity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty("taskId", out var taskId)
            && taskId.ValueKind == JsonValueKind.Number
            && taskId.TryGetInt32(out _))
        {
            return true;
        }
        return element.TryGetProperty("externalId", out var externalId)
               && externalId.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(externalId.GetString());
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
