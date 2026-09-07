using System.Text.Json;

namespace MoTask.Core.Model;

/// <summary>HistoryKind.CandidateRegistered / CandidateMerged の Detail。</summary>
public sealed record TriageHistoryDetail(string Source, string ExternalId)
{
    public static string Serialize(TriageHistoryDetail detail) => JsonSerializer.Serialize(detail);

    /// <summary>空・壊れた JSON は null（1 行の壊れた履歴で呼び出し元を落とさない）。</summary>
    public static TriageHistoryDetail? Deserialize(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        try
        {
            return JsonSerializer.Deserialize<TriageHistoryDetail>(detail);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
