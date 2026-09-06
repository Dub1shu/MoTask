using System.Globalization;
using System.Text.Json;

namespace MoTask.App.Ai.BoardTools;

/// <summary>
/// tools/call の arguments を読む。省略（Has=false）と明示的な null（IsExplicitNull=true）を
/// 区別するのが肝で、update_task はこの違いで「保持」と「外す」を分ける（仕様 §6.5）。
/// Try* は「読めたか」を返す。読めなかった＝型が違う場合だけ false になり、省略と null は
/// true ＋ 値なしになる。
/// </summary>
public readonly struct BoardArgs
{
    private readonly JsonElement _element;
    private readonly bool _isObject;

    public BoardArgs(JsonElement arguments)
    {
        _element = arguments;
        _isObject = arguments.ValueKind == JsonValueKind.Object;
    }

    public bool Has(string name) => _isObject && _element.TryGetProperty(name, out _);

    public bool IsExplicitNull(string name)
        => _isObject && _element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Null;

    public string? String(string name)
        => _isObject && _element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public int? Int(string name)
        => _isObject && _element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i)
            ? i
            : null;

    public bool TryRef(string name, out McpRef? value)
    {
        value = null;
        if (!_isObject || !_element.TryGetProperty(name, out var v)) return true;
        return TryOne(v, out value);
    }

    /// <summary>id か名前の配列。単体で渡されたら 1 件として扱う。null / 空配列は「空にする」。</summary>
    public bool TryRefs(string name, out IReadOnlyList<McpRef>? value)
    {
        value = null;
        if (!_isObject || !_element.TryGetProperty(name, out var v)) return true;
        if (v.ValueKind == JsonValueKind.Null)
        {
            value = Array.Empty<McpRef>();
            return true;
        }
        if (v.ValueKind != JsonValueKind.Array)
        {
            if (!TryOne(v, out var single) || single is null) return false;
            value = new[] { single.Value };
            return true;
        }

        var list = new List<McpRef>();
        foreach (var item in v.EnumerateArray())
        {
            if (!TryOne(item, out var one) || one is null) return false;
            list.Add(one.Value);
        }
        value = list;
        return true;
    }

    /// <summary>YYYY-MM-DD だけを受け付ける。</summary>
    public bool TryDate(string name, out DateOnly? value)
    {
        value = null;
        if (!_isObject || !_element.TryGetProperty(name, out var v)) return true;
        if (v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind != JsonValueKind.String) return false;
        if (!DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryOne(JsonElement v, out McpRef? value)
    {
        value = null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Number when v.TryGetInt32(out var id):
                value = McpRef.FromId(id);
                return true;
            case JsonValueKind.String when v.GetString() is { } s:
                value = McpRef.FromName(s);
                return true;
            default:
                return false;
        }
    }
}
