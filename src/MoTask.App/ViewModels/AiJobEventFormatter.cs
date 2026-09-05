using System.Globalization;
using System.Text.Json;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

public sealed record AiLogLine(string Time, string Text, bool IsError);

/// <summary>
/// AiJobEvent の Payload（フックの 1 行、または作り替え前に保存された stream-json の 1 行）を 1 行の表示に落とす。
/// 壊れた Payload でも例外にせず、フォールバック文言で出す（HistoryFormatter と同じ流儀）。
/// </summary>
public static class AiJobEventFormatter
{
    private const int MaxResultChars = 120;

    public static AiLogLine Format(AiJobEvent e, TimeZoneInfo? timeZone = null)
    {
        var time = HistoryFormatter.Timestamp(e.At, timeZone);
        var (text, isError) = Body(e);
        return new AiLogLine(time, text, isError);
    }

    /// <summary>
    /// いま出せる「結果」。対話なので確定した最終回答は無い。直近の Stop フックが持つ
    /// last_assistant_message を出す（作り替え前に保存された result 行も拾う）。
    /// </summary>
    public static string? ResultText(IEnumerable<AiJobEvent> events)
    {
        var list = events as IReadOnlyList<AiJobEvent> ?? events.ToList();
        var stop = list.LastOrDefault(e => e.Kind == AiJobEventKind.TurnEnded);
        if (stop is not null)
        {
            using var stopped = TryParse(stop.Payload);
            var message = stopped is null ? null : ReadString(stopped.RootElement, "last_assistant_message");
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }

        var result = list.LastOrDefault(e => e.Kind == AiJobEventKind.Result);
        if (result is null) return null;
        using var doc = TryParse(result.Payload);
        return doc is null ? null : ReadString(doc.RootElement, "result");
    }

    private static (string Text, bool IsError) Body(AiJobEvent e)
    {
        using var doc = TryParse(e.Payload);
        if (doc is null)
        {
            return e.Kind switch
            {
                AiJobEventKind.Error => (string.Format(Strings.AiLogErrorFormat, e.Payload), true),
                _ => (Strings.AiLogUnparsed, false),
            };
        }
        var root = doc.RootElement;
        return e.Kind switch
        {
            AiJobEventKind.AssistantText => (AssistantText(root), false),
            AiJobEventKind.ToolUse => (ToolUse(root, e.ToolName), false),
            AiJobEventKind.ToolResult => ToolResult(root),
            AiJobEventKind.Error => (string.Format(Strings.AiLogErrorFormat, ReadString(root, "message") ?? e.Payload), true),
            AiJobEventKind.Result => Result(root),
            AiJobEventKind.SessionStarted => (string.Format(Strings.AiLogSessionStartedFormat, ReadString(root, "source") ?? "?"), false),
            AiJobEventKind.SessionEnded => (string.Format(Strings.AiLogSessionEndedFormat, ReadString(root, "reason") ?? "?"), false),
            AiJobEventKind.TurnEnded => (TurnEnded(root), false),
            _ => System(root),
        };
    }

    private static string AssistantText(JsonElement root)
    {
        var texts = ContentBlocks(root)
            .Where(b => ReadString(b, "type") == "text")
            .Select(b => ReadString(b, "text"))
            .Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", texts);
    }

    private static string ToolUse(JsonElement root, string? toolName)
    {
        var name = toolName ?? ReadString(root, "tool_name") ?? "?";
        var summary = ToolInput(root, toolName) is { } input ? ArgumentSummary(name, input) : null;
        return summary is null
            ? string.Format(Strings.AiLogToolUseNoArg, name)
            : string.Format(Strings.AiLogToolUseFormat, name, summary);
    }

    /// <summary>
    /// フックの PostToolUse は root.tool_input。作り替え前に保存された stream-json の行は
    /// message.content[].input なので、そちらへも落ちる。
    /// </summary>
    private static JsonElement? ToolInput(JsonElement root, string? toolName)
    {
        if (root.TryGetProperty("tool_input", out var hookInput) && hookInput.ValueKind == JsonValueKind.Object) return hookInput;
        var block = FindToolUse(root, toolName);
        return block is { } b && b.TryGetProperty("input", out var input) ? input : null;
    }

    private static string? ArgumentSummary(string toolName, JsonElement input) => toolName switch
    {
        "Bash" => ReadString(input, "command"),
        "Write" or "Edit" or "Read" or "NotebookEdit" => ReadString(input, "file_path"),
        "Glob" or "Grep" => ReadString(input, "pattern"),
        "WebSearch" => ReadString(input, "query"),
        "WebFetch" => ReadString(input, "url"),
        _ => null,
    };

    private static (string, bool) ToolResult(JsonElement root)
    {
        var block = ContentBlocks(root).FirstOrDefault(b => ReadString(b, "type") == "tool_result");
        var isError = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        var text = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("content", out var content) ? ContentText(content) : "";
        var firstLine = FirstLine(text);
        if (firstLine.Length == 0) firstLine = Strings.AiLogToolResultEmpty;
        return (string.Format(isError ? Strings.AiLogToolResultErrorFormat : Strings.AiLogToolResultOkFormat, firstLine), isError);
    }

    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(b => ReadString(b, "text")).Where(t => !string.IsNullOrEmpty(t))),
        _ => "",
    };

    private static string TurnEnded(JsonElement root)
    {
        var message = FirstLine(ReadString(root, "last_assistant_message") ?? "");
        return message.Length == 0 ? Strings.AiLogTurnEnded : string.Format(Strings.AiLogTurnEndedFormat, message);
    }

    private static (string, bool) Result(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        if (isError) return (string.Format(Strings.AiLogResultErrorFormat, ReadString(root, "result") ?? ""), true);
        // ValueKind == Number は整数として GetInt32/GetDecimal できることまでは保証しない（小数、
        // int32/decimal の範囲外の値など）。TryGet* で失敗を吸収し、欠落時と同じ既定値にフォールバックする
        // （TryGetInt32/TryGetDecimal は ValueKind が Number でない場合に InvalidOperationException を
        // 投げるため、ValueKind チェックは残す）。
        var turns = root.TryGetProperty("num_turns", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var ti) ? ti : 0;
        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var ci) ? ci : 0m;
        return (string.Format(Strings.AiLogResultFormat, turns, cost.ToString("0.000", CultureInfo.InvariantCulture)), false);
    }

    private static (string, bool) System(JsonElement root)
    {
        var type = ReadString(root, "type");
        var subtype = ReadString(root, "subtype");
        if (type == "system" && subtype == "init") return (string.Format(Strings.AiLogSessionStartFormat, ReadString(root, "model") ?? "?"), false);
        if (type == "system" && subtype == "permission_denied") return (string.Format(Strings.AiLogCliDeniedFormat, ReadString(root, "tool_name") ?? "?"), true);
        if (type == "rate_limit_event")
        {
            var status = root.TryGetProperty("rate_limit_info", out var info) ? ReadString(info, "status") : null;
            return (string.Format(Strings.AiLogRateLimitFormat, status ?? "?"), false);
        }
        return (Strings.AiLogSystem, false);
    }

    // ---- 補助 ----

    private static JsonDocument? TryParse(string payload)
    {
        try
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return Enumerable.Empty<JsonElement>();
        }
        return content.EnumerateArray();
    }

    private static JsonElement? FindToolUse(JsonElement root, string? toolName)
    {
        foreach (var block in ContentBlocks(root))
        {
            if (ReadString(block, "type") != "tool_use") continue;
            if (toolName is null || ReadString(block, "name") == toolName) return block;
        }
        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length <= MaxResultChars ? line : line[..MaxResultChars] + "…";
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
