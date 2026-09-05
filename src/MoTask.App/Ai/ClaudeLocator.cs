using System.IO;

namespace MoTask.App.Ai;

/// <summary>claude 実行ファイルの所在。設定があればそれだけを信じ、無ければ PATH を探す（仕様 §10, §11）。</summary>
public static class ClaudeLocator
{
    private static readonly string[] Candidates = { "claude.exe", "claude" };

    public static string? Find(string? configuredPath, string? pathVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim();
            return File.Exists(configured) ? configured : null;
        }

        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in Candidates)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, name);
                }
                catch (ArgumentException)
                {
                    continue; // PATH に不正な文字が混ざっていても探索を続ける
                }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
