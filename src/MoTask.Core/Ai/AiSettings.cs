namespace MoTask.Core.Ai;

/// <summary>仕様 §10「設定」。許可ルールは DB（AiPermissionRule）にあるのでここには含めない。</summary>
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    int MaxConcurrentJobs,
    string? ClaudeExecutablePath,
    string? Model,
    int MaxTurns)
{
    public const int DefaultMaxConcurrentJobs = 3;
    public const int DefaultMaxTurns = 50;

    public static string DefaultWorkingDirectoryPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MoTask");

    public static AiSettings Default() => new(DefaultWorkingDirectoryPath, DefaultMaxConcurrentJobs, null, null, DefaultMaxTurns);
}
