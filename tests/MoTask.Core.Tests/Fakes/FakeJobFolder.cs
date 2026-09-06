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

    public Result<string> Create(JobFolderRequest request)
    {
        Created.Add(request);
        if (CreateFailure is { } failure) return failure;
        return Result.Ok($@"C:\work\jobs\{request.JobId:0000}-{request.TaskTitle}");
    }

    public void WriteJobJson(string root, JobDescriptor descriptor) => Descriptors.Add(descriptor);

    public IReadOnlyList<string> ListArtifacts(string root) => Artifacts;

    public IReadOnlyList<string> ReadTail(string root, int lines)
    {
        if (lines <= 0) return Array.Empty<string>();
        return Lines.Count <= lines ? Lines.ToList() : Lines.Skip(Lines.Count - lines).ToList();
    }
}
