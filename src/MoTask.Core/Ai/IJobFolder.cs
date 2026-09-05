namespace MoTask.Core.Ai;

/// <summary>ジョブフォルダの実体（App の JobFolder）。</summary>
public interface IJobFolder
{
    /// <summary>
    /// フォルダを作り、instruction.md と hooks.json を書く。戻り値はフォルダの絶対パス。
    /// job.json は起動コマンドが決まってから WriteJobJson で書く。
    /// </summary>
    Result<string> Create(JobFolderRequest request);

    /// <summary>job.json を書く。書けなくてもジョブは続けるので、失敗は握り潰す。</summary>
    void WriteJobJson(string root, JobDescriptor descriptor);

    /// <summary>artifacts/ の実ファイル（絶対パス、名前順）。フォルダが無ければ空。</summary>
    IReadOnlyList<string> ListArtifacts(string root);
}
