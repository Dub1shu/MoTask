using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Morning;

/// <summary>
/// run.json の中身（仕様 §6）。job.json 相当で、DB が壊れてもフォルダだけで何の実行か分かるように残す。
/// </summary>
public sealed record MorningRunDescriptor(
    int RunId,
    DateOnly Date,
    Guid SessionId,
    string JobFolder,
    string LaunchCommand,
    DateTime StartedAt)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(MorningRunDescriptor descriptor)
        => JsonSerializer.Serialize(descriptor, Options);
}
