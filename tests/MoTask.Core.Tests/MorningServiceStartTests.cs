using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 朝の実行の開始（仕様 §6・§12）。フォルダを先に作り、パスが確定してから DB に保存する。
/// </summary>
public class MorningServiceStartTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly Column _active;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceStartTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        _active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedColumn("完了", ColumnRole.Done);
        _store.SeedTask(_active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    [Fact]
    public async Task Start_PutsTheFolderUnderMorning_NamedByRunNumberAndDate()
    {
        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeTrue(started.Error);
        _folder.Created.Should().ContainSingle();
        _folder.Created[0].Category.Should().Be(JobFolderPaths.MorningDirectoryName);
        _folder.Created[0].OutputDirectoryName.Should().Be(JobFolderPaths.ResultDirectoryName);
        _folder.Created[0].TaskTitle.Should().Be("2026-09-07");
        started.Value!.JobFolder.Should().Be(@"C:\work\morning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_RunsInsideTheJobFolderItself()
    {
        await _service.StartAsync();

        var request = _launcher.Requests.Should().ContainSingle().Subject;
        request.WorkingDirectory.Should().Be(request.JobFolder, "朝の実行はソースツリーに用が無い（仕様 §6）");
        request.Resume.Should().BeFalse();
    }

    [Fact]
    public async Task Start_SavesTheRunOnlyAfterTheFolderIsKnown()
    {
        var run = (await _service.StartAsync()).Value!;

        run.Status.Should().Be(MorningRunStatus.Pending);
        run.JobFolder.Should().NotBeEmpty("JobFolder が空のまま Pending で残る窓を作らない（仕様 §12）");
        run.Instruction.Should().Contain(run.JobFolder, "指示文には出力先の実パスが入る");
        run.SessionId.Should().NotBeEmpty();
        run.StartedAt.Should().Be(_clock.UtcNow);
        _store.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_WritesBoardJsonWithTheUnfinishedTasks()
    {
        var run = (await _service.StartAsync()).Value!;

        var board = _folder.ReadText(run.JobFolder, JobFolderPaths.BoardJsonName);
        board.Should().NotBeNull();
        var root = JsonDocument.Parse(board!).RootElement;
        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()).Should().Equal("Q4企画書の内容を確定する");
    }

    [Fact]
    public async Task Start_WritesRunJsonWithTheLaunchCommand()
    {
        var run = (await _service.StartAsync()).Value!;

        var text = _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName);
        text.Should().NotBeNull("DB が壊れてもフォルダだけで何の実行か分かるようにする（仕様 §6）");
        var root = JsonDocument.Parse(text!).RootElement;
        root.GetProperty("runId").GetInt32().Should().Be(run.Id);
        root.GetProperty("launchCommand").GetString().Should().StartWith("wt.exe ");
    }

    [Fact]
    public async Task Start_FollowsTheEventsFileFromTheTop()
    {
        var run = (await _service.StartAsync()).Value!;

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(0);
        _events.EventsPathOf(run.Id).Should().Be(JobFolderPaths.For(run.JobFolder).EventsJsonl);
    }

    [Fact]
    public async Task Start_RefusesASecondRunWhileOneIsUnfinished()
    {
        await _service.StartAsync();

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeFalse("二重起動の防止（仕様 §12）");
        second.Error.Should().Be(Messages.MorningRunAlreadyRunning);
        _store.Runs.Should().ContainSingle();
        _folder.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_IsAllowedAgainOnceTheLastRunIsFinished()
    {
        var first = (await _service.StartAsync()).Value!;
        first.Status = MorningRunStatus.Ingested;

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.JobFolder.Should().Be(@"C:\work\morning\0002-2026-09-07", "連番は実行の件数で決まる");
    }

    [Fact]
    public async Task Start_StopsBeforeDoingAnything_WhenClaudeIsMissing()
    {
        _launcher.Availability = Result.Fail(Messages.ClaudeNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Runs.Should().BeEmpty("黙って走らせず、開始時点で止める（仕様 §12）");
        _folder.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_StopsBeforeSaving_WhenTheHooksExeIsMissing()
    {
        _folder.CreateFailure = Result.Fail<string>(Messages.HooksExecutableNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.HooksExecutableNotFound);
        _store.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_MarksTheRunFailed_WhenTheTerminalWillNotOpen()
    {
        _launcher.LaunchFailure = Result.Fail("端末を起動できませんでした");

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        var run = _store.Runs.Should().ContainSingle().Subject;
        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be("端末を起動できませんでした");
        run.JobFolder.Should().NotBeEmpty("失敗した実行でもフォルダは開ける");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Start_UsesTheConfiguredInstructionTemplate()
    {
        _settings.Settings = AiSettings.Default() with { MorningInstruction = "私の方針" };

        var run = (await _service.StartAsync()).Value!;

        run.Instruction.Should().StartWith("私の方針");
        _folder.Created[0].Instruction.Should().Be(run.Instruction);
    }

    [Fact]
    public async Task Start_RaisesRunChangedOnce()
    {
        var run = (await _service.StartAsync()).Value!;

        _changes.Should().ContainSingle();
        _changes[0].Run.RunId.Should().Be(run.Id);
        _changes[0].Run.Status.Should().Be(MorningRunStatus.Pending);
        _changes[0].CandidatesChanged.Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentRun_IsTheLatestOne()
    {
        (await _service.GetCurrentRunAsync()).Should().BeNull("まだ一度も走らせていない");

        var run = (await _service.StartAsync()).Value!;

        (await _service.GetCurrentRunAsync())!.Id.Should().Be(run.Id);
    }
}
