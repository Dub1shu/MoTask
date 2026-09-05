using System.Text.Json;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.App.Ai;

/// <summary>
/// `claude --output-format stream-json --verbose` の 1 行を AgentEvent に写す。
/// 表示に要る最小限（Kind, ToolName, result の要約）だけ抜き、原文は Payload に必ず残す（仕様 §6）。
/// 新しいイベント型や壊れた行は System として残し、ジョブは止めない（仕様 §11）。
/// </summary>
public static class ClaudeCodeParser
{
    public static IReadOnlyList<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Array.Empty<AgentEvent>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new[] { new AgentEvent(AiJobEventKind.System, null, line) };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return System(line);
            var type = ReadString(root, "type");
            return type switch
            {
                "assistant" => FromContentBlocks(root, line, isAssistant: true),
                "user" => FromContentBlocks(root, line, isAssistant: false),
                "result" => new[] { new AgentEvent(AiJobEventKind.Result, null, line, ReadResult(root)) },
                _ => System(line),
            };
        }
    }

    private static IReadOnlyList<AgentEvent> System(string line)
        => new[] { new AgentEvent(AiJobEventKind.System, null, line) };

    private static IReadOnlyList<AgentEvent> FromContentBlocks(JsonElement root, string line, bool isAssistant)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return System(line);
        }

        var events = new List<AgentEvent>();
        foreach (var block in content.EnumerateArray())
        {
            var blockType = ReadString(block, "type");
            switch (blockType)
            {
                case "text" when isAssistant:
                    events.Add(new AgentEvent(AiJobEventKind.AssistantText, null, line));
                    break;
                case "tool_use" when isAssistant:
                    events.Add(new AgentEvent(AiJobEventKind.ToolUse, ReadString(block, "name"), line));
                    break;
                case "tool_result" when !isAssistant:
                    events.Add(new AgentEvent(AiJobEventKind.ToolResult, null, line));
                    break;
            }
        }
        return events.Count == 0 ? System(line) : events;
    }

    private static AgentResultInfo ReadResult(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        int? turns = root.TryGetProperty("num_turns", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : null;
        // decimal に収まらない値（1e40 など）で throw しない。1 行の異常でジョブ全体を落とさない。
        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            && c.TryGetDecimal(out var d)
            ? d
            : null;
        var text = ReadString(root, "result");
        if (string.IsNullOrEmpty(text) && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            text = string.Join(" / ", errors.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        }
        return new AgentResultInfo(isError, turns, cost, string.IsNullOrEmpty(text) ? null : text);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
