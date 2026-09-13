using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>ファイルは作らない。作れと言われた要求と、書けと言われた job.json を記録するだけ。</summary>
public sealed class FakeJobFolder : IJobFolder
{
    public Result<string>? CreateFailure { get; set; }
    public List<JobFolderRequest> Created { get; } = new();
    public List<JobDescriptor> Descriptors { get; } = new();
    public List<string> Artifacts { get; } = new();
    /// <summary>ReadTail が返す行。テストが直接積む。</summary>
    public List<string> Lines { get; } = new();

    /// <summary>WriteText / Put で置かれたもの。キーは「ルート|相対パス」。</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Result? WriteFailure { get; set; }
    /// <summary>設定すると、WriteFailure はこの相対パスへの書き込みだけに効く（他は成功する）。</summary>
    public string? FailWritesTo { get; set; }

    public string ResolveRoot(JobFolderRequest request)
        => $@"C:\work\{request.Category}\{request.JobId:0000}-{request.TaskTitle}";

    public Result<string> Create(JobFolderRequest request)
    {
        Created.Add(request);
        if (CreateFailure is { } failure) return failure;
        return Result.Ok(ResolveRoot(request));
    }

    public void WriteJobJson(string root, JobDescriptor descriptor) => Descriptors.Add(descriptor);

    public IReadOnlyList<string> ListArtifacts(string root) => Artifacts;

    public IReadOnlyList<string> ReadTail(string root, int lines)
    {
        if (lines <= 0) return Array.Empty<string>();
        return Lines.Count <= lines ? Lines.ToList() : Lines.Skip(Lines.Count - lines).ToList();
    }

    public Result WriteText(string root, string relativePath, string content)
    {
        if (WriteFailure is { } failure &&
            (FailWritesTo is null || string.Equals(FailWritesTo, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return failure;
        }
        Files[Key(root, relativePath)] = content;
        return Result.Ok();
    }

    public string? ReadText(string root, string relativePath)
        => Files.TryGetValue(Key(root, relativePath), out var content) ? content : null;

    /// <summary>テストが Claude の書いたファイルを置く。</summary>
    public void Put(string root, string relativePath, string content)
        => Files[Key(root, relativePath)] = content;

    private static string Key(string root, string relativePath)
        => root + "|" + relativePath.Replace('/', '\\');
}
