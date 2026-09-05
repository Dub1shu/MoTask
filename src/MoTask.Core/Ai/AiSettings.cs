namespace MoTask.Core.Ai;

/// <summary>
/// 仕様 §11「設定」。MaxConcurrentJobs / MaxTurns はターミナル実行では使わないので Task 10 で落とす。
/// </summary>
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    int MaxConcurrentJobs,
    string? ClaudeExecutablePath,
    string? Model,
    int MaxTurns,
    string PermissionMode,
    string? TerminalCommandTemplate)
{
    public const int DefaultMaxConcurrentJobs = 3;
    public const int DefaultMaxTurns = 50;

    /// <summary>--permission-mode の既定。既定で止まらず走り、危険な操作は端末で人に聞かれる（仕様 §3）。</summary>
    public const string DefaultPermissionMode = "auto";

    /// <summary>CLI が受け付ける値（仕様 §4.3）。この 6 つ以外は渡さない。</summary>
    public static readonly IReadOnlyList<string> PermissionModes =
        new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan" };

    public static string DefaultWorkingDirectoryPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MoTask");

    public static AiSettings Default() => new(
        DefaultWorkingDirectoryPath, DefaultMaxConcurrentJobs, null, null, DefaultMaxTurns,
        DefaultPermissionMode, null);
}
