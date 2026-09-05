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

    /// <summary>フォルダ名に残す長さの上限。パス全体が 260 文字に近づかないようにする。</summary>
    private const int MaxSlugLength = 40;

    public string JobJson => Path.Combine(Root, "job.json");
    public string InstructionMarkdown => Path.Combine(Root, "instruction.md");
    public string HooksJson => Path.Combine(Root, "hooks.json");
    public string EventsJsonl => Path.Combine(Root, "events.jsonl");
    public string ArtifactsDirectory => Path.Combine(Root, "artifacts");

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
