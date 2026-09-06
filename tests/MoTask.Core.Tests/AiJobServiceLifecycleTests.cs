using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// ターミナル実行のライフサイクル（仕様 §8）。
/// Pending → SessionStart → Running ⇄ Stop → WaitingForInput → SessionEnd → Succeeded。
/// </summary>
public class AiJobServiceLifecycleTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly AiJobService _service;
    private readonly Column _active;
    private readonly Column _review;
    private readonly TaskItem _task;
    private readonly List<AiJobChangedEventArgs> _changes = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public AiJobServiceLifecycleTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active);
        _review = _store.SeedColumn("確認待ち", ColumnRole.Review);
        _task = _store.SeedTask(_active, "見積り");
        // cwd の解決が既定ワークフォルダを実際に作るので、ホームではなく一時フォルダを指す
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new AiJobService(_store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.JobChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private async Task<AiJob> StartAsync()
    {
        var started = await _service.StartJobAsync(_task.Id, AiJobKind.Execute, "やること");
        started.IsSuccess.Should().BeTrue(started.Error);
        return started.Value!;
    }

    [Fact]
    public async Task Start_LeavesTheJobPendingUntilTheSessionActuallyStarts()
    {
        var job = await StartAsync();

        job.Status.Should().Be(AiJobStatus.Pending);
        job.SessionId.Should().NotBeEmpty();
        job.JobFolder.Should().Be($@"C:\work\jobs\{job.Id:0000}-見積り");
        _launcher.Launched.Should().ContainSingle();
        _events.IsFollowing(job.Id).Should().BeTrue();
        _events.EventsPathOf(job.Id).Should().Be(JobFolderPaths.For(job.JobFolder).EventsJsonl);
    }

    [Fact]
    public async Task Start_WritesJobJsonWithTheLaunchCommand()
    {
        var job = await StartAsync();

        _folder.Descriptors.Should().ContainSingle()
            .Which.LaunchCommand.Should().StartWith("wt.exe ");
        _folder.Descriptors[0].SessionId.Should().Be(job.SessionId);
    }

    [Fact]
    public async Task Start_RecordsTheHistoryEntry()
    {
        await StartAsync();

        _store.History.Should().ContainSingle(h => h.Kind == HistoryKind.AiJobStarted);
    }

    [Fact]
    public async Task SessionStart_MovesTheJobToRunning()
    {
        var job = await StartAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        job.Status.Should().Be(AiJobStatus.Running);
        _changes.Last().NewEvent!.Kind.Should().Be(AiJobEventKind.SessionStarted);
    }

    [Fact]
    public async Task Stop_MovesTheJobToWaitingForInput_AndCountsTheTurn()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        job.Status.Should().Be(AiJobStatus.WaitingForInput);
        job.NumTurns.Should().Be(1);
    }

    [Fact]
    public async Task ToolUse_AfterStop_GoesBackToRunning()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse("Read"));

        job.Status.Should().Be(AiJobStatus.Running);
        _changes.Last().NewEvent!.ToolName.Should().Be("Read");
    }

    [Fact]
    public async Task SessionEnd_SucceedsTheJob_MovesTheTask_AndStopsFollowing()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionEnd());

        job.Status.Should().Be(AiJobStatus.Succeeded);
        job.EndedAt.Should().NotBeNull();
        _task.ColumnId.Should().Be(_review.Id);
        _events.Stopped.Should().Contain(job.Id);
        _store.History.Should().ContainSingle(h => h.Kind == HistoryKind.AiJobFinished);
    }

    [Fact]
    public async Task Events_AreNumberedInOrder_AndKeepTheirRawLine()
    {
        var job = await StartAsync();
        var line = FakeJobEventSource.SessionStart();

        await _events.EmitAsync(job.Id, line);
        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse());

        var raised = _changes.Where(c => c.NewEvent is not null).ToList();
        raised.Select(c => c.NewEvent!.Seq).Should().Equal(1, 2);
        raised[0].NewEvent!.Payload.Should().Be(line);
    }

    [Fact]
    public async Task BrokenLines_AreKeptAsSystem_AndDoNotChangeTheStatus()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(job.Id, "これは JSON ではない");

        job.Status.Should().Be(AiJobStatus.Running);
        _changes.Last().NewEvent!.Kind.Should().Be(AiJobEventKind.System);
        _changes.Last().NewEvent!.Payload.Should().Be("これは JSON ではない");
    }

    [Fact]
    public async Task Complete_TreatsTheJobAsIfSessionEndArrived()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        var completed = await _service.CompleteJobAsync(job.Id);

        completed.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Succeeded);
        _task.ColumnId.Should().Be(_review.Id);
        _events.Stopped.Should().Contain(job.Id);
    }

    [Fact]
    public async Task StopTracking_CancelsTheJob_WithoutMovingTheTask()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        var stopped = await _service.StopTrackingAsync(job.Id);

        stopped.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Cancelled);
        _task.ColumnId.Should().Be(_active.Id, "追跡をやめただけで、仕事が終わったわけではない");
        _events.Stopped.Should().Contain(job.Id);
    }

    [Fact]
    public async Task LinesArrivingAfterTheJobFinished_AreIgnored()
    {
        var job = await StartAsync();
        await _service.StopTrackingAsync(job.Id);
        var before = _changes.Count;

        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        _changes.Should().HaveCount(before);
        job.Status.Should().Be(AiJobStatus.Cancelled);
    }

    [Fact]
    public async Task Complete_FailsForAJobThatAlreadyFinished()
    {
        var job = await StartAsync();
        await _service.CompleteJobAsync(job.Id);

        (await _service.CompleteJobAsync(job.Id)).Error.Should().Be(Messages.AiJobAlreadyFinished);
    }

    [Fact]
    public async Task ReopenTerminal_ResumesTheSameSession_WithoutChangingTheStatus()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        var reopened = await _service.ReopenTerminalAsync(job.Id);

        reopened.IsSuccess.Should().BeTrue();
        _launcher.Requests.Last().Resume.Should().BeTrue();
        _launcher.Requests.Last().SessionId.Should().Be(job.SessionId);
        job.Status.Should().Be(AiJobStatus.WaitingForInput);
        _events.SkipLinesOf(job.Id).Should().Be(2, "既に取り込んだ行は読み直さない");
    }

    [Fact]
    public async Task EventLogGone_RaisesAWarning_ButLeavesTheJobAlone()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        lock (_changes) _changes.Clear();

        await _events.ProblemAsync(job.Id, "イベントログを追えなくなりました: x");

        job.Status.Should().Be(AiJobStatus.Running);
        lock (_changes) _changes.Should().ContainSingle().Which.Warning.Should().Contain("追えなくなりました");
    }

    [Fact]
    public async Task Recover_PicksUpWhereTheEventLogWasLeft()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());
        // アプリを閉じ直したことにする
        _events.StopFollowing(job.Id);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(job.Id).Should().BeTrue();
        _events.SkipLinesOf(job.Id).Should().Be(2);
    }

    [Fact]
    public async Task Recover_FinishesAJobWhoseSessionEndArrivedWhileTheAppWasClosed()
    {
        var job = await StartAsync();
        _events.StopFollowing(job.Id);
        await _service.RecoverOnStartupAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionEnd());

        job.Status.Should().Be(AiJobStatus.Succeeded);
        _task.ColumnId.Should().Be(_review.Id);
    }

    [Fact]
    public async Task GetUnfinishedJobs_ReturnsPendingRunningAndWaiting()
    {
        var job = await StartAsync();

        (await _service.GetUnfinishedJobsAsync()).Should().ContainSingle().Which.Id.Should().Be(job.Id);

        await _service.StopTrackingAsync(job.Id);
        (await _service.GetUnfinishedJobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetArtifacts_ListsWhatIsInTheJobFolder()
    {
        var job = await StartAsync();
        _folder.Artifacts.Add($@"{job.JobFolder}\artifacts\report.md");

        (await _service.GetArtifactsAsync(job.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task GetEvents_ReadsTheTailOfTheEventsFile()
    {
        var job = await StartAsync();
        _folder.Lines.Add(FakeJobEventSource.SessionStart());
        _folder.Lines.Add(FakeJobEventSource.PostToolUse("Read"));
        _folder.Lines.Add(FakeJobEventSource.PostToolUse("Bash"));
        _folder.Lines.Add(FakeJobEventSource.Stop("できました"));

        var events = await _service.GetEventsAsync(job.Id, 3);

        events.Should().HaveCount(3);
        events[0].Kind.Should().Be(AiJobEventKind.ToolUse);
        events[0].ToolName.Should().Be("Read");
        events[2].Kind.Should().Be(AiJobEventKind.TurnEnded);
        events.Select(e => e.Seq).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task GetEvents_ReturnsEmptyForAJobWithoutAFolder()
    {
        var job = await StartAsync();
        _store.Jobs.Single(j => j.Id == job.Id).JobFolder = "";

        (await _service.GetEventsAsync(job.Id, 3)).Should().BeEmpty();
    }

    /// <summary>結果（最終回答）は末尾 3 行の外にあることが多い。広く読めば届くことを固定する。</summary>
    [Fact]
    public async Task GetEvents_ReachesAnEarlierTurnWhenAskedForMoreLines()
    {
        var job = await StartAsync();
        _folder.Lines.Add(FakeJobEventSource.Stop("まとめました"));
        for (var i = 0; i < 5; i++) _folder.Lines.Add(FakeJobEventSource.PostToolUse("Read"));

        var forTheLog = await _service.GetEventsAsync(job.Id, 3);
        var forTheResult = await _service.GetEventsAsync(job.Id, 200);

        forTheLog.Should().OnlyContain(e => e.Kind == AiJobEventKind.ToolUse);
        forTheResult.Should().Contain(e => e.Kind == AiJobEventKind.TurnEnded);
    }

    /// <summary>フックの行には時刻が無い。読んだ時刻を実際の発生時刻として偽らないことを固定する。</summary>
    [Fact]
    public async Task GetEvents_DoNotStampAReadTime_TheHookLineCarriesNone()
    {
        var job = await StartAsync();
        _folder.Lines.Add(FakeJobEventSource.PostToolUse("Read"));

        var events = await _service.GetEventsAsync(job.Id, 1);

        events.Should().ContainSingle().Which.At.Should().Be(default);
    }

    [Fact]
    public async Task HookLine_AdvancesTheProcessedLineCount()
    {
        var job = await StartAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse("Read"));

        _store.Jobs.Single(j => j.Id == job.Id).ProcessedLines.Should().Be(2);
    }

    [Fact]
    public async Task Recover_SkipsTheLinesAlreadyTakenIn()
    {
        var job = await StartAsync();
        _store.Jobs.Single(j => j.Id == job.Id).ProcessedLines = 5;

        await _service.RecoverOnStartupAsync();

        _events.SkipLinesOf(job.Id).Should().Be(5);
    }
}
