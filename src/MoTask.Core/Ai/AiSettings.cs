namespace MoTask.Core.Ai;

/// <summary>
/// 仕様 §11「設定」。端末で人が claude を回すので、同時実行の上限も最大ターン数も MoTask は持たない。
/// </summary>
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    string? ClaudeExecutablePath,
    string? Model,
    string PermissionMode,
    string? TerminalCommandTemplate,
    // 計画づくりの指示文（仕様 §6）。null なら PlanningInstruction.DefaultTemplate。
    // XML doc コメントはパラメータリストの中に置けない（CS1587）ので行コメントにする。
    string? PlanningInstruction = null)
{
    /// <summary>--permission-mode の既定。既定で止まらず走り、危険な操作は端末で人に聞かれる（仕様 §3）。</summary>
    public const string DefaultPermissionMode = "auto";

    /// <summary>CLI が受け付ける値（仕様 §4.3）。この 6 つ以外は渡さない。</summary>
    public static readonly IReadOnlyList<string> PermissionModes =
        new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan" };

    public static string DefaultWorkingDirectoryPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MoTask");

    public static AiSettings Default() => new(DefaultWorkingDirectoryPath, null, null, DefaultPermissionMode, null);
}
