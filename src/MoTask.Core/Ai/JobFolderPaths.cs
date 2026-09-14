using System.Text;

namespace MoTask.Core.Ai;

/// <summary>
/// ジョブフォルダのレイアウト（仕様 §6）。パスを組み立てるだけで、ファイルには触らない。
/// 実体を作るのは App の JobFolder。
/// </summary>
public sealed record JobFolderPaths(string Root)
{
    /// <summary>既定ワークフォルダ直下の、ジョブフォルダを集める場所。</summary>
    public const string JobsDirectoryName = "jobs";

    /// <summary>朝の実行のフォルダを集める場所（仕様 §6）。</summary>
    public const string MorningDirectoryName = "morning";

    /// <summary>AI 遂行の成果物。Create が併せて作る。</summary>
    public const string ArtifactsDirectoryName = "artifacts";

    /// <summary>朝の実行の成果物。Create が併せて作る。</summary>
    public const string ResultDirectoryName = "result";

    public const string JobJsonName = "job.json";
    public const string InstructionMarkdownName = "instruction.md";
    public const string HooksJsonName = "hooks.json";
    public const string EventsJsonlName = "events.jsonl";
    public const string BoardJsonName = "board.json";
    public const string RunJsonName = "run.json";

    /// <summary>--mcp-config に渡す MCP サーバ登録（仕様 §5.4）。朝の実行だけが持つ。</summary>
    public const string McpJsonName = "mcp.json";

    /// <summary>ルートからの相対パス。IJobFolder.WriteText / ReadText に渡す。</summary>
    public static readonly string CandidatesRelativePath = Path.Combine(ResultDirectoryName, "candidates.jsonl");

    public static readonly string PlanRelativePath = Path.Combine(ResultDirectoryName, "plan.json");

    /// <summary>フォルダ名に残す長さの上限。パス全体が 260 文字に近づかないようにする。</summary>
    private const int MaxSlugLength = 40;

    public string JobJson => Path.Combine(Root, JobJsonName);
    public string InstructionMarkdown => Path.Combine(Root, InstructionMarkdownName);
    public string HooksJson => Path.Combine(Root, HooksJsonName);
    public string EventsJsonl => Path.Combine(Root, EventsJsonlName);
    public string ArtifactsDirectory => Path.Combine(Root, ArtifactsDirectoryName);
    public string ResultDirectory => Path.Combine(Root, ResultDirectoryName);
    public string BoardJson => Path.Combine(Root, BoardJsonName);
    public string RunJson => Path.Combine(Root, RunJsonName);
    public string McpJson => Path.Combine(Root, McpJsonName);
    public string CandidatesJsonl => Path.Combine(Root, CandidatesRelativePath);
    public string PlanJson => Path.Combine(Root, PlanRelativePath);

    public static JobFolderPaths For(string root) => new(root);

    /// <summary>Id を頭に置くので、同じ題名のタスクでも衝突しない。</summary>
    public static string FolderName(int jobId, string taskTitle)
        => $"{jobId:0000}-{Slug(taskTitle)}";

    /// <summary>
    /// タスク名をフォルダ名に使える形へ。日本語はそのまま残し、使えない文字と空白は - に潰す。
    /// 何も残らなければ "task"（人が見て分かる必要はあるが、識別は先頭の Id が担う）。
    /// </summary>
    public static string Slug(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        for (var i = 0; i < title.Length; i++)
        {
            var c = title[i];
            var replace = char.IsWhiteSpace(c) || char.IsControl(c) || Array.IndexOf(invalid, c) >= 0;
            var next = replace ? '-' : c;
            // 区切りの連続は 1 つにまとめる
            if (next == '-' && builder.Length > 0 && builder[^1] == '-') continue;
            if (next == '-' && builder.Length == 0) continue;
            // サロゲートペアの上位だけを残すと、フォルダ名の中に孤立したサロゲートが残る。
            // 下位が続くなら 2 つとも入れるか、入らないならここで打ち切る。
            if (char.IsHighSurrogate(next) && i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]))
            {
                if (builder.Length + 2 > MaxSlugLength) break;
                builder.Append(next);
                builder.Append(title[i + 1]);
                i++;
                if (builder.Length >= MaxSlugLength) break;
                continue;
            }
            builder.Append(next);
            if (builder.Length >= MaxSlugLength) break;
        }

        // 末尾の - と . は Windows がフォルダ名から黙って落とすので、こちらで落としておく
        var slug = builder.ToString().TrimEnd('-', '.').TrimStart('-', '.');
        return slug.Length == 0 ? "task" : slug;
    }
}
