using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoTask.Core.Model;

/// <summary>HistoryKind.AiJobStarted / AiJobFinished の Detail。Started のとき Status は null。</summary>
public sealed record AiJobHistoryDetail(AiJobKind Kind, AiJobStatus? Status)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AiJobHistoryDetail detail) => JsonSerializer.Serialize(detail, Options);

    /// <summary>空・壊れた JSON は null（1 行の壊れた履歴で呼び出し元を落とさない）。</summary>
    public static AiJobHistoryDetail? Deserialize(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiJobHistoryDetail>(detail, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
