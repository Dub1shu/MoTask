using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class JobFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly StubSettingsStore _settings;
    private readonly JobFolder _folder;
    private readonly string _hooksExe;

    public JobFolderTests()
    {
        Directory.CreateDirectory(_root);
        _hooksExe = Path.Combine(_root, "MoTask.Hooks.exe");
        File.WriteAllText(_hooksExe, "");
        _settings = new StubSettingsStore(AiSettings.Default() with { DefaultWorkingDirectory = _root });
        _folder = new JobFolder(_settings) { HooksExecutable = _hooksExe };
    }

    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }

    [Fact]
    public void Create_LaysOutTheFolderUnderTheDefaultWorkFolder()
    {
        var created = _folder.Create(new JobFolderRequest(42, "請求書の突合", "やること"));

        created.IsSuccess.Should().BeTrue();
        var paths = JobFolderPaths.For(created.Value!);
        paths.Root.Should().Be(Path.Combine(_root, "jobs", "0042-請求書の突合"));
        Directory.Exists(paths.ArtifactsDirectory).Should().BeTrue();
        File.Exists(paths.HooksJson).Should().BeTrue();
        File.ReadAllText(paths.InstructionMarkdown, new UTF8Encoding(false)).Should().Be("やること");
    }

    [Fact]
    public void Create_PointsTheHooksAtThisJobsEventLog()
    {
        var root = _folder.Create(new JobFolderRequest(1, "t", "i")).Value!;

        using var doc = JsonDocument.Parse(File.ReadAllText(JobFolderPaths.For(root).HooksJson));
        doc.RootElement.GetProperty("hooks").GetProperty("SessionEnd")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString()
            .Should().Contain(JobFolderPaths.For(root).EventsJsonl);
    }

    [Fact]
    public void Create_IsIdempotent_SoReopeningDoesNotFail()
    {
        _folder.Create(new JobFolderRequest(7, "t", "一回目")).IsSuccess.Should().BeTrue();

        var again = _folder.Create(new JobFolderRequest(7, "t", "二回目"));

        again.IsSuccess.Should().BeTrue();
        File.ReadAllText(JobFolderPaths.For(again.Value!).InstructionMarkdown).Should().Be("二回目");
    }

    [Fact]
    public void Create_FailsWhenTheHookExeIsMissing()
    {
        var folder = new JobFolder(_settings) { HooksExecutable = Path.Combine(_root, "no-such.exe") };

        var created = folder.Create(new JobFolderRequest(1, "t", "i"));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(MoTask.Core.Messages.HooksExecutableNotFound);
    }

    [Fact]
    public void WriteJobJson_RecordsWhatTheFolderIsFor()
    {
        var root = _folder.Create(new JobFolderRequest(3, "見積り", "i")).Value!;
        var session = Guid.NewGuid();

        _folder.WriteJobJson(root, new JobDescriptor(3, session, AiJobKind.Execute, @"D:\work\sample",
            "wt.exe -d ...", new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc)));

        using var doc = JsonDocument.Parse(File.ReadAllText(JobFolderPaths.For(root).JobJson));
        doc.RootElement.GetProperty("JobId").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("SessionId").GetString().Should().Be(session.ToString("D"));
        doc.RootElement.GetProperty("Kind").GetString().Should().Be("Execute");
        doc.RootElement.GetProperty("WorkingDirectory").GetString().Should().Be(@"D:\work\sample");
        doc.RootElement.GetProperty("LaunchCommand").GetString().Should().Be("wt.exe -d ...");
    }

    [Fact]
    public void ListArtifacts_ReturnsEveryRealFile_InNameOrder()
    {
        var root = _folder.Create(new JobFolderRequest(5, "t", "i")).Value!;
        var artifacts = JobFolderPaths.For(root).ArtifactsDirectory;
        File.WriteAllText(Path.Combine(artifacts, "b.md"), "b");
        // Write / Edit を通らないファイル（リダイレクトで作ったもの）も見えること
        File.WriteAllText(Path.Combine(artifacts, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(artifacts, "sub"));
        File.WriteAllText(Path.Combine(artifacts, "sub", "c.csv"), "c");

        var listed = _folder.ListArtifacts(root);

        listed.Select(Path.GetFileName).Should().Equal("a.txt", "b.md", "c.csv");
        listed.Should().OnlyContain(p => Path.IsPathRooted(p));
    }

    [Fact]
    public void ListArtifacts_IsEmptyWhenTheFolderIsGone()
    {
        _folder.ListArtifacts(Path.Combine(_root, "no-such-job")).Should().BeEmpty();
    }

    [Fact]
    public void ReadTail_ReturnsEmptyWhenThereIsNoFolder()
    {
        _folder.ReadTail(Path.Combine(_root, "no-such-job"), 3).Should().BeEmpty();
    }

    [Fact]
    public void ReadTail_ReturnsEveryLineWhenThereAreFewerThanAsked()
    {
        var root = WriteEvents("a\nb\n");

        _folder.ReadTail(root, 3).Should().Equal("a", "b");
    }

    [Fact]
    public void ReadTail_ReturnsTheLastLinesInOrder()
    {
        var root = WriteEvents("1\n2\n3\n4\n5\n");

        _folder.ReadTail(root, 3).Should().Equal("3", "4", "5");
    }

    [Fact]
    public void ReadTail_KeepsALastLineThatHasNoNewline()
    {
        var root = WriteEvents("1\n2\n3");

        _folder.ReadTail(root, 2).Should().Equal("2", "3");
    }

    [Fact]
    public void ReadTail_SkipsBlankLines()
    {
        var root = WriteEvents("1\n\n2\n\n");

        _folder.ReadTail(root, 3).Should().Equal("1", "2");
    }

    [Fact]
    public void ReadTail_KeepsALongLineIntact()
    {
        var line = new string('x', 40_000);
        var root = WriteEvents("short\n" + line + "\n");

        _folder.ReadTail(root, 1).Should().Equal(line);
    }

    [Fact]
    public void ReadTail_ReturnsEmptyWhenAskedForNoLines()
    {
        var root = WriteEvents("1\n2\n");

        _folder.ReadTail(root, 0).Should().BeEmpty();
    }

    /// <summary>events.jsonl だけを持つジョブフォルダを作り、その root を返す。</summary>
    private string WriteEvents(string content)
    {
        var root = Path.Combine(_root, "0001-tail");
        Directory.CreateDirectory(root);
        File.WriteAllText(JobFolderPaths.For(root).EventsJsonl, content, new UTF8Encoding(false));
        return root;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
