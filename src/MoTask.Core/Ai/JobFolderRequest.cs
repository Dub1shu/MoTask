namespace MoTask.Core.Ai;

/// <summary>
/// ジョブフォルダを作るのに要る材料。親フォルダは実装が設定から決める。
/// Category / OutputDirectoryName の既定は AI 遂行の現状と同じなので、既存の呼び出しは変わらない。
/// </summary>
public sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction)
{
    /// <summary>既定ワークフォルダ直下のどこに置くか（jobs / morning）。</summary>
    public string Category { get; init; } = JobFolderPaths.JobsDirectoryName;

    /// <summary>Create が併せて作る出力フォルダ（artifacts / result）。</summary>
    public string OutputDirectoryName { get; init; } = JobFolderPaths.ArtifactsDirectoryName;
}
