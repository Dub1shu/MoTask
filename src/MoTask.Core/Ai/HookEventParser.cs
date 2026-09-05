using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>
/// events.jsonl の 1 行 → HookEvent。CLI との唯一の形の依存点なので、純粋関数にして
/// 実機で採取した fixture でテストから固定する（仕様 §5, §13）。
/// 知らないフックも壊れた行も捨てず、System として原文を残す（仕様 §12）。
/// </summary>
public static class HookEventParser
{
    public static HookEvent Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return System(line);

            return ReadString(doc.RootElement, "hook_event_name") switch
            {
                "SessionStart" => new HookEvent(AiJobEventKind.SessionStarted, null, line),
                "PostToolUse" => new HookEvent(AiJobEventKind.ToolUse, ReadString(doc.RootElement, "tool_name"), line),
                "Stop" => new HookEvent(AiJobEventKind.TurnEnded, null, line),
                "SessionEnd" => new HookEvent(AiJobEventKind.SessionEnded, null, line),
                _ => System(line),
            };
        }
        catch (JsonException)
        {
            return System(line);
        }
    }

    private static HookEvent System(string line) => new(AiJobEventKind.System, null, line);

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
