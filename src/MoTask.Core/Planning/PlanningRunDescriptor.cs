using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Planning;

/// <summary>
/// run.json の中身（親仕様 §6）。job.json 相当で、DB が壊れてもフォルダだけで何の実行か分かるように残す。
/// ProcessId / ProcessStartedAt は再起動後に端末へ掛け直すための材料（MCP 受け渡し仕様 §7）。
/// ProcessStartedAt は UTC。0 / default は「掛け直せる材料が無い」を意味する。
/// </summary>
public sealed record PlanningRunDescriptor(
    int RunId,
    DateOnly Date,
    Guid SessionId,
    string JobFolder,
    string LaunchCommand,
    DateTime StartedAt,
    int ProcessId = 0,
    DateTime ProcessStartedAt = default)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(PlanningRunDescriptor descriptor)
        => JsonSerializer.Serialize(descriptor, Options);

    /// <summary>読めない・壊れている・オブジェクトでないときは null（呼び手は掛け直しを諦める）。</summary>
    public static PlanningRunDescriptor? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PlanningRunDescriptor>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
