namespace MoTask.Core.Ai;

/// <summary>
/// ジョブフォルダを作るのに要る材料。親フォルダは実装が設定から決める。
/// Category / OutputDirectoryName の既定は AI 遂行の現状と同じなので、既存の呼び出しは変わらない。
/// </summary>
public sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction)
{
    /// <summary>既定ワークフォルダ直下のどこに置くか（jobs / planning）。</summary>
    public string Category { get; init; } = JobFolderPaths.JobsDirectoryName;

    /// <summary>
    /// Create が併せて作る出力フォルダ。null でも空文字でも作らない（計画づくりは成果をファイルに出さない）。
    /// </summary>
    public string? OutputDirectoryName { get; init; } = JobFolderPaths.ArtifactsDirectoryName;

    /// <summary>
    /// mcp.json を併せて書くか（仕様 §5.4）。計画づくりだけが true。
    /// AI 遂行は利用者の手動登録に任せるので既定は false。
    /// </summary>
    public bool WithMcpConfig { get; init; }
}
