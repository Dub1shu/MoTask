using System.Text.Json;
using MoTask.Core.Ai;

namespace MoTask.Data;

/// <summary>
/// settings.json。項目が欠けていても既定で埋め、壊れていても既定を返す(設定ファイルの事故で起動を止めない)。
/// 保存は都度ファイル全体を書く。
/// </summary>
public sealed class JsonAiSettingsStore : IAiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonAiSettingsStore(string path)
    {
        _path = path;
    }

    public AiSettings Load()
    {
        var defaults = AiSettings.Default();
        if (!File.Exists(_path)) return defaults;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), Options);
            if (dto is null) return defaults;
            return new AiSettings(
                string.IsNullOrWhiteSpace(dto.DefaultWorkingDirectory) ? defaults.DefaultWorkingDirectory : dto.DefaultWorkingDirectory,
                dto.MaxConcurrentJobs is > 0 ? dto.MaxConcurrentJobs.Value : defaults.MaxConcurrentJobs,
                string.IsNullOrWhiteSpace(dto.ClaudeExecutablePath) ? null : dto.ClaudeExecutablePath,
                string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model,
                dto.MaxTurns is > 0 ? dto.MaxTurns.Value : defaults.MaxTurns);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return defaults;
        }
    }

    public void Save(AiSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new Dto
        {
            DefaultWorkingDirectory = settings.DefaultWorkingDirectory,
            MaxConcurrentJobs = settings.MaxConcurrentJobs,
            ClaudeExecutablePath = settings.ClaudeExecutablePath,
            Model = settings.Model,
            MaxTurns = settings.MaxTurns,
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, Options));
    }

    private sealed class Dto
    {
        public string? DefaultWorkingDirectory { get; set; }
        public int? MaxConcurrentJobs { get; set; }
        public string? ClaudeExecutablePath { get; set; }
        public string? Model { get; set; }
        public int? MaxTurns { get; set; }
    }
}
