using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>
/// --settings に渡すフック定義（仕様 §7）。4 イベントすべてで同じ exe を呼び、引数は追記先 1 つ。
/// --setting-sources を渡さないので、利用者の user / project / local 設定はそのまま効く（仕様 §4.1）。
/// </summary>
public static class HooksJson
{
    private static readonly string[] Events = { "SessionStart", "PostToolUse", "Stop", "SessionEnd" };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 人が開いて読めるように、バックスラッシュや日本語を \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string hooksExecutable, string eventsPath)
    {
        var command = new { type = "command", command = $"\"{hooksExecutable}\" \"{eventsPath}\"" };
        var matcher = new[] { new { hooks = new[] { command } } };
        var hooks = new Dictionary<string, object>();
        foreach (var name in Events) hooks[name] = matcher;
        return JsonSerializer.Serialize(new { hooks }, Options);
    }
}
