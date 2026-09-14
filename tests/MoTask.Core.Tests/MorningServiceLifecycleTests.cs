using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 追従とライフサイクル(仕様 §6・§7)。成果は MCP から届く(MorningServiceMcpTests)ので、
/// ここは events.jsonl の追従・掛け直し・端末の終了まわりを固定する。
/// </summary>
public class MorningServiceLifecycleTests
{
    private const string Plan =
        """{"date":"2026-09-07","groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceLifecycleTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    private async Task<MorningRun> StartAsync()
    {
        var started = await _service.StartAsync();
        started.IsSuccess.Should().BeTrue(started.Error);
        return started.Value!;
    }

    private void PutRunJson(MorningRun run, int processId)
        => _folder.Put(run.JobFolder, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
            new MorningRunDescriptor(
                run.Id, run.Date, run.SessionId, run.JobFolder, "cmd.exe /c claude",
                new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc),
                ProcessId: processId,
                ProcessStartedAt: new DateTime(2026, 9, 7, 6, 0, 1, DateTimeKind.Utc))));

    [Fact]
    public async Task Recover_ReattachesToTheTerminalUsingRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().ContainSingle().Which.Should()
            .Be((run.Id, 31337, new DateTime(2026, 9, 7, 6, 0, 1, DateTimeKind.Utc)));
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>掛け直せた実行は、その後の完了でちゃんと閉じられる(仕様 §7)。</summary>
    [Fact]
    public async Task Recover_ThenComplete_StillClosesTheTerminal()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);
        await _service.RecoverOnStartupAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteAsync(run.Id);

        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>掛け直せなくても追従は続ける。諦めるのは「閉じる能力」だけ(仕様 §7)。</summary>
    [Fact]
    public async Task Recover_KeepsFollowingEvenWhenItCannotReattach()
    {
        _launcher.ReattachSucceeds = false;
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>pid の無い(この実装より前に作られた)run.json では掛け直しを試みない。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWithoutAProcessId()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, processId: 0);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>run.json そのものが無くても、追従だけは今までどおり再開する。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWhenThereIsNoRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    [Fact]
    public async Task SessionStart_MovesTheRunToRunning()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        run.Status.Should().Be(MorningRunStatus.Running);
        run.ProcessedLines.Should().Be(1);
    }

    [Fact]
    public async Task Stop_LeavesTheRunAlone_UntilCompleteArrives()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Running, "Stop は何度でも来る");
        _store.Candidates.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
        _service.TurnCountOf(run.Id).Should().Be(1);
        _launcher.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_RefusesARunThatIsAlreadyFinished()
    {
        var run = await StartAsync();
        await _service.StopTrackingAsync(run.Id);

        var again = await _service.CompleteAsync(run.Id);

        again.IsSuccess.Should().BeFalse();
        again.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, run.Id));
    }

    [Fact]
    public async Task StopTracking_CancelsWithoutFinishingTheRun()
    {
        var run = await StartAsync();

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeTrue(stopped.Error);
        run.Status.Should().Be(MorningRunStatus.Cancelled);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty("追跡をやめただけ。端末は殺さないし取り込みもしない");
        _events.IsFollowing(run.Id).Should().BeFalse();
        _launcher.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task LinesArrivingAfterTheRunFinished_AreDropped()
    {
        var run = await StartAsync();
        await _service.StopTrackingAsync(run.Id);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        run.Status.Should().Be(MorningRunStatus.Cancelled);
    }

    [Fact]
    public async Task Recover_ResumesFollowingFromTheProcessedLineCount()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        run.ProcessedLines = 12;

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(12);
    }

    [Fact]
    public async Task Recover_IgnoresFinishedRuns()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task AProblemFollowingTheFile_RaisesAWarningWithoutChangingTheStatus()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.ProblemAsync(run.Id, "イベントログを追えなくなりました");

        run.Status.Should().Be(MorningRunStatus.Running);
        _changes.Last().Warning.Should().Be("イベントログを追えなくなりました");
    }

    [Fact]
    public async Task GetLogTail_ReadsTheEndOfTheEventsFile()
    {
        var run = await StartAsync();
        _folder.Lines.AddRange(new[] { "1", "2", "3", "4" });

        var tail = await _service.GetLogTailAsync(run.Id, 3);

        tail.Should().Equal("2", "3", "4");
    }

    [Fact]
    public async Task Stop_LeavesTheTerminalOpen_WhileTheResultIsNotThereYet()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().BeEmpty("Stop は何度でも来る。揃うまでは閉じない");
    }

    /// <summary>「追跡をやめる」は端末を殺さない。既存の約束をここでは守る(仕様 §7)。</summary>
    [Fact]
    public async Task StopTracking_DoesNotCloseTheTerminal()
    {
        var run = await StartAsync();

        await _service.StopTrackingAsync(run.Id);

        run.Status.Should().Be(MorningRunStatus.Cancelled);
        _launcher.Closed.Should().BeEmpty();
    }

    /// <summary>
    /// 人が × で閉じた・claude が落ちた。所有しているからこそ気づける(仕様 §7)。
    /// 気づかないと events.jsonl を延々ポーリングし続けて実行が宙に浮く。
    /// </summary>
    [Fact]
    public async Task TheTerminalDyingFirst_FailsTheRunAndStopsFollowing()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningTerminalClosed);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _events.IsFollowing(run.Id).Should().BeFalse();
        _changes.Last().Run.Status.Should().Be(MorningRunStatus.Failed);
    }

    /// <summary>
    /// 起こした端末が LaunchOwned の戻り値より先に死んでも、追従は残さない。
    /// 追従を起動より後に掛けると、終わった実行に誰も止めないポーラーが付く(仕様 §7)。
    /// </summary>
    [Fact]
    public async Task TheTerminalDyingDuringTheLaunch_LeavesNoPollerBehind()
    {
        _launcher.ExitsDuringLaunch = true;

        var run = await StartAsync();
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningTerminalClosed);
        _events.IsFollowing(run.Id).Should().BeFalse("起動より先に追従を掛けていれば止められる");
    }

    /// <summary>閉じたのはこちらなので、取り込み済みの実行を Failed で上書きしない。</summary>
    [Fact]
    public async Task TheTerminalDyingAfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteAsync(run.Id);

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.ErrorMessage.Should().BeNull();
    }

    /// <summary>知らない runId のイベントで落ちない。</summary>
    [Fact]
    public async Task AnExitForARunWeDoNotKnow_IsIgnored()
    {
        _launcher.RaiseExited(9999);

        await _service.PendingTerminalExit;

        _store.Runs.Should().BeEmpty();
    }
}
