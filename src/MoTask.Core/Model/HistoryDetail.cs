using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Model;

public sealed record FieldChange(string? From, string? To);

public static class HistoryDetail
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IReadOnlyDictionary<string, FieldChange> changes)
        => JsonSerializer.Serialize(changes, Options);

    public static IReadOnlyDictionary<string, FieldChange> Deserialize(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return new Dictionary<string, FieldChange>();
        }
        return JsonSerializer.Deserialize<Dictionary<string, FieldChange>>(detail, Options)
               ?? new Dictionary<string, FieldChange>();
    }
}
