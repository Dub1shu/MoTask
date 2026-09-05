using System.IO;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>開始時の検証と、端末が開くまでに失敗したときの畳み方（仕様 §12）。</summary>
public class AiJobServiceStartTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly AiJobService _service;
    private readonly Column _backlog;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public AiJobServiceStartTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("確認待ち", ColumnRole.Review);
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new AiJobService(_store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
    }

    private TaskItem Seed(string title = "a") => _store.SeedTask(_backlog, title);

    [Fact]
    public async Task Start_RejectsAnEmptyInstruction()
    {
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "   ");

        started.Error.Should().Be(Messages.InstructionRequired);
        _store.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_FailsWhenClaudeIsMissing()
    {
        _launcher.Availability = Result.Fail(Messages.ClaudeNotFound);
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        started.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Jobs.Should().BeEmpty("claude が無いならジョブの行も作らない");
    }

    [Fact]
    public async Task Start_FailsForAMissingTask()
    {
        (await _service.StartJobAsync(9999, AiJobKind.Execute, "やる")).Error.Should().Be(Messages.TaskNotFound);
    }

    [Fact]
    public async Task Start_FailsForADeletedTask()
    {
        var task = Seed();
        task.DeletedAt = _clock.UtcNow;

        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる"))
            .Error.Should().Be(Messages.TaskDeletedCannotRunAi);
    }

    [Fact]
    public async Task Start_RejectsASecondJobWhileOneIsStillTracked()
    {
        var task = Seed();
        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();

        var second = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        second.Error.Should().Be(Messages.TaskAlreadyHasActiveJob);
    }

    [Fact]
    public async Task Start_AllowsANewJobOnceTheOldOneFinished()
    {
        var task = Seed();
        var first = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _service.StopTrackingAsync(first.Id);

        var second = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        second.IsSuccess.Should().BeTrue(second.Error);
    }

    [Fact]
    public async Task Start_FailsWhenTheProjectWorkingDirectoryIsMissing()
    {
        var project = _store.SeedProject("p");
        project.WorkingDirectory = Path.Combine(_tempDir, "no-such-dir");
        var task = Seed();
        task.ProjectId = project.Id;

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Contain("プロジェクトの作業フォルダが見つかりません");
        _launcher.Launched.Should().BeEmpty("既定へ逃げずに開始しない");
    }

    [Fact]
    public async Task Start_UsesTheProjectWorkingDirectoryAsCwd()
    {
        var dir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(dir);
        var project = _store.SeedProject("p");
        project.WorkingDirectory = dir;
        var task = Seed();
        task.ProjectId = project.Id;

        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;

        job.WorkingDirectory.Should().Be(dir);
        _launcher.Requests.Single().WorkingDirectory.Should().Be(dir);
        _launcher.Requests.Single().JobFolder.Should().NotBe(dir, "ジョブフォルダは cwd とは別（仕様 §6）");
    }

    [Fact]
    public async Task Start_MarksTheJobFailedWhenTheJobFolderCannotBeCreated()
    {
        _folder.CreateFailure = Result.Fail<string>("ジョブフォルダを作成できません: x（権限がありません）");
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        var job = _store.Jobs.Should().ContainSingle().Subject;
        job.Status.Should().Be(AiJobStatus.Failed);
        job.ErrorMessage.Should().Be("ジョブフォルダを作成できません: x（権限がありません）");
        _events.Subscriptions.Should().BeEmpty();
        _store.History.Should().Contain(h => h.Kind == HistoryKind.AiJobFinished);
    }

    [Fact]
    public async Task Start_MarksTheJobFailedWhenTheTerminalWillNotOpen()
    {
        _launcher.LaunchFailure = Result.Fail("端末を起動できませんでした: wt.exe -d ...");
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        var job = _store.Jobs.Single();
        job.Status.Should().Be(AiJobStatus.Failed);
        job.ErrorMessage.Should().Contain("wt.exe", "何を実行しようとしたかが残る（仕様 §12）");
        _events.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_NeverLimitsHowManyJobsRunAtOnce()
    {
        // 同時実行の上限は廃止した。端末を開くのは人であって、アプリが数を絞る意味が無い（仕様 §8）。
        foreach (var i in Enumerable.Range(0, 5))
        {
            var task = Seed($"t{i}");
            (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();
        }

        _launcher.Launched.Should().HaveCount(5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
