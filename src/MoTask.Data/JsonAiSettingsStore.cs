using System.Text.Json;
using MoTask.Core.Ai;

namespace MoTask.Data;

/// <summary>
/// settings.json。項目が欠けていても既定で埋め、壊れていても既定を返す(設定ファイルの事故で起動を止めない)。
/// 保存は同じフォルダの一時ファイルに書いてから置き換える(クラッシュや電源断で本体を破損させない)。
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
                string.IsNullOrWhiteSpace(dto.ClaudeExecutablePath) ? null : dto.ClaudeExecutablePath,
                string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model,
                // 知らない値が入っていたら既定へ。CLI に弾かれる値を渡さない。
                dto.PermissionMode is { Length: > 0 } mode && AiSettings.PermissionModes.Contains(mode)
                    ? mode
                    : defaults.PermissionMode,
                string.IsNullOrWhiteSpace(dto.TerminalCommandTemplate) ? null : dto.TerminalCommandTemplate);
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
            ClaudeExecutablePath = settings.ClaudeExecutablePath,
            Model = settings.Model,
            PermissionMode = settings.PermissionMode,
            TerminalCommandTemplate = settings.TerminalCommandTemplate,
        };

        // 一時ファイルに書いてから置き換える。書き込みが途中で失敗しても settings.json は元のまま。
        var tmpPath = _path + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(dto, Options));
            if (File.Exists(_path))
            {
                File.Replace(tmpPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmpPath, _path);
            }
        }
        finally
        {
            // 成功時は既にリネーム済みで存在しない。失敗時のゴミだけ片付ける。
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    /// <summary>知らないキー（廃止した MaxConcurrentJobs / MaxTurns など）は黙って捨てられる。</summary>
    private sealed class Dto
    {
        public string? DefaultWorkingDirectory { get; set; }
        public string? ClaudeExecutablePath { get; set; }
        public string? Model { get; set; }
        public string? PermissionMode { get; set; }
        public string? TerminalCommandTemplate { get; set; }
    }
}
