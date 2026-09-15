using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// ジョブフォルダの実体（仕様 §6）。cwd はここではなくプロジェクトの作業フォルダなので、
/// このフォルダは --add-dir で読み書きを許す。
/// </summary>
public sealed class JobFolder : IJobFolder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JobJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAiSettingsStore _settings;

    public JobFolder(IAiSettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>MoTask.App のビルドが hooks\ へ運ぶ exe。テストは差し替える。</summary>
    internal string HooksExecutable { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "hooks", "MoTask.Hooks.exe");

    /// <summary>MoTask.App のビルドが mcp\ へ運ぶ exe（仕様 §5.4）。テストは差し替える。</summary>
    internal string McpExecutable { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "mcp", "MoTask.Mcp.exe");

    public string ResolveRoot(JobFolderRequest request)
        => Path.Combine(
            _settings.Load().DefaultWorkingDirectory,
            request.Category,
            JobFolderPaths.FolderName(request.JobId, request.TaskTitle));

    public Result<string> Create(JobFolderRequest request)
    {
        // フックが無いと端末は動くが盤面が一切追従しない。黙って走らせず、開始時に止める。
        if (!File.Exists(HooksExecutable)) return Result.Fail<string>(Messages.HooksExecutableNotFound);

        // ブリッジが無いと朝の実行は成果を渡す先を失う。黙って走らせず、開始時に止める。
        if (request.WithMcpConfig && !File.Exists(McpExecutable))
        {
            return Result.Fail<string>(Messages.McpExecutableNotFound);
        }

        var root = ResolveRoot(request);
        var paths = JobFolderPaths.For(root);
        try
        {
            // 既にあっても作り直さない（--resume で開き直すときに同じフォルダへ戻る）
            Directory.CreateDirectory(request.OutputDirectoryName is { Length: > 0 } output
                ? Path.Combine(root, output)
                : root);
            File.WriteAllText(paths.InstructionMarkdown, request.Instruction, Utf8);
            File.WriteAllText(paths.HooksJson, HooksJson.Build(HooksExecutable, paths.EventsJsonl), Utf8);
            if (request.WithMcpConfig)
            {
                File.WriteAllText(paths.McpJson, McpConfigJson.Build(McpExecutable), Utf8);
            }
            return Result.Ok(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail<string>(string.Format(Messages.JobFolderFailedFormat, root, ex.Message));
        }
    }

    public Result WriteText(string root, string relativePath, string content)
    {
        try
        {
            var path = Path.Combine(root, relativePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, content, Utf8);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail(string.Format(Messages.JobFolderFailedFormat, root, ex.Message));
        }
    }

    public string? ReadText(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var path = Path.Combine(root, relativePath);
            if (!File.Exists(path)) return null;
            // Claude が書いている最中でも読めるように共有を広く取る（ReadTail と同じ理由）。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 読めないだけ。呼び出し側は「まだ書かれていない」と同じに扱う。
            return null;
        }
    }

    public void WriteJobJson(string root, JobDescriptor descriptor)
    {
        try
        {
            File.WriteAllText(JobFolderPaths.For(root).JobJson,
                JsonSerializer.Serialize(descriptor, JobJsonOptions), Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // job.json は DB が壊れたときの保険。書けなくてもジョブは続ける。
        }
    }

    public IReadOnlyList<string> ListArtifacts(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Array.Empty<string>();
        var artifacts = JobFolderPaths.For(root).ArtifactsDirectory;
        try
        {
            if (!Directory.Exists(artifacts)) return Array.Empty<string>();
            return Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一覧が出ないだけで、ジョブの状態には関係しない
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> ReadTail(string root, int lines)
    {
        if (string.IsNullOrWhiteSpace(root) || lines <= 0) return Array.Empty<string>();
        var path = JobFolderPaths.For(root).EventsJsonl;
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            // フックが追記中でも読めるように共有を広く取る。ファイルは小さいので
            // 先頭から読んで末尾 lines 行だけ残す（末尾からの逆読みは行の境目を跨ぐと厄介）。
            var tail = new Queue<string>(lines);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Utf8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (tail.Count == lines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return tail.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めないだけで、ジョブの状態には関係しない
            return Array.Empty<string>();
        }
    }
}
