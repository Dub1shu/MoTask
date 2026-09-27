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

    /// <summary>
    /// events.jsonl の末尾 lines 行を古い順で返す。空行は飛ばす。
    /// フォルダやファイルが無ければ空。読めなくても投げない（表示が空になるだけ）。
    /// </summary>
    IReadOnlyList<string> ReadTail(string root, int lines);

    /// <summary>
    /// フォルダを作らずにルートのパスだけ決める。指示文がフォルダ内のパスを含む計画づくりで、
    /// Create に渡す前に知る必要がある。
    /// </summary>
    string ResolveRoot(JobFolderRequest request);

    /// <summary>ルートからの相対パスにテキストを書く（途中のフォルダは作る）。</summary>
    Result WriteText(string root, string relativePath, string content);

    /// <summary>
    /// ルートからの相対パスを読む。フォルダやファイルが無ければ null。
    /// 読めなくても投げない（Claude がまだ書いていないだけのことが多い）。
    /// </summary>
    string? ReadText(string root, string relativePath);
}
