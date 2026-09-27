using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 追従とライフサイクル(仕様 §6・§7)。成果は MCP から届く(PlanningServiceMcpTests)ので、
/// ここは events.jsonl の追従・掛け直し・端末の終了まわりを固定する。
/// </summary>
public class PlanningServiceLifecycleTests
{
    private const string Plan =
        """{"date":"2026-09-07","groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly PlanningService _service;
    private readonly List<PlanningRunChangedEventArgs> _changes = new();

    public PlanningServiceLifecycleTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new PlanningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    private async Task<PlanningRun> StartAsync()
    {
        var started = await _service.StartAsync();
        started.IsSuccess.Should().BeTrue(started.Error);
        return started.Value!;
    }

    private void PutRunJson(PlanningRun run, int processId)
        => _folder.Put(run.JobFolder, JobFolderPaths.RunJsonName, PlanningRunDescriptor.Serialize(
            new PlanningRunDescriptor(
                run.Id, run.Date, run.SessionId, run.JobFolder, "cmd.exe /c claude",
                new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc),
                ProcessId: processId,
                ProcessStartedAt: new DateTime(2026, 9, 7, 6, 0, 1, DateTimeKind.Utc))));

    [Fact]
    public async Task Recover_ReattachesToTheTerminalUsingRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);
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
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);
        PutRunJson(run, 31337);
        await _service.RecoverOnStartupAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteAsync(run.Id);

        run.Status.Should().Be(PlanningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>掛け直せなくても追従は続ける。諦めるのは「閉じる能力」だけ(仕様 §7)。</summary>
    [Fact]
    public async Task Recover_KeepsFollowingEvenWhenItCannotReattach()
    {
        _launcher.ReattachSucceeds = false;
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);
        PutRunJson(run, 31337);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>pid の無い(この実装より前に作られた)run.json では掛け直しを試みない。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWithoutAProcessId()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);
        PutRunJson(run, processId: 0);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>run.json そのものが無くても、追従だけは今までどおり再開する。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWhenThereIsNoRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    [Fact]
    public async Task SessionStart_MovesTheRunToRunning()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        run.Status.Should().Be(PlanningRunStatus.Running);
        run.ProcessedLines.Should().Be(1);
    }

    [Fact]
    public async Task Stop_LeavesTheRunAlone_UntilCompleteArrives()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(PlanningRunStatus.Running, "Stop は何度でも来る");
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
        again.Error.Should().Be(string.Format(Messages.PlanningRunNotRunningFormat, run.Id));
    }

    /// <summary>
    /// 終わった実行への StopTracking は拒む。ここを緩めると、取り込みを終えた実行を
    /// レースで Cancelled に上書きしてしまう(PlanningService.StopTrackingAsync のガードのコメント参照)。
    /// </summary>
    [Fact]
    public async Task StopTracking_RefusesARunThatAlreadyFinishedIngesting()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteAsync(run.Id);

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeFalse();
        stopped.Error.Should().Be(Messages.PlanningRunAlreadyFinished);
        run.Status.Should().Be(PlanningRunStatus.Ingested, "既に終わった実行の状態を上書きしない");
    }

    [Fact]
    public async Task StopTracking_CancelsWithoutFinishingTheRun()
    {
        var run = await StartAsync();

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeTrue(stopped.Error);
        run.Status.Should().Be(PlanningRunStatus.Cancelled);
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

        run.Status.Should().Be(PlanningRunStatus.Cancelled);
    }

    /// <summary>
    /// 取り込み済み・閉じる予約も無い実行に、遅れて Stop や SessionEnd が届いても黙って
    /// 落ちる。既存の LinesArrivingAfterTheRunFinished_AreDropped は Cancelled ＋
    /// SessionStart の組み合わせしか見ていないので、Ingested ＋ Stop / SessionEnd を足す。
    /// </summary>
    [Theory]
    [MemberData(nameof(LateLines))]
    public async Task LinesArrivingAfterCompletionWithNoPendingClose_AreDropped(string line)
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteAsync(run.Id); // closeNow:true。予約は残らない

        await _events.EmitAsync(run.Id, line);

        run.Status.Should().Be(PlanningRunStatus.Ingested);
        run.ErrorMessage.Should().BeNull();
        // CompleteAsync の 1 回だけ。遅れた行で二重に閉じない
        _launcher.Closed.Should().Equal(run.Id);
    }

    public static IEnumerable<object[]> LateLines()
    {
        yield return new object[] { FakeJobEventSource.Stop() };
        yield return new object[] { FakeJobEventSource.SessionEnd() };
    }

    [Fact]
    public async Task Recover_ResumesFollowingFromTheProcessedLineCount()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), PlanningRunStatus.Running);
        run.ProcessedLines = 12;

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(12);
    }

    [Fact]
    public async Task Recover_IgnoresFinishedRuns()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 6), PlanningRunStatus.Ingested);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task AProblemFollowingTheFile_RaisesAWarningWithoutChangingTheStatus()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.ProblemAsync(run.Id, "イベントログを追えなくなりました");

        run.Status.Should().Be(PlanningRunStatus.Running);
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

        run.Status.Should().Be(PlanningRunStatus.Cancelled);
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

        run.Status.Should().Be(PlanningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.PlanningTerminalClosed);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _events.IsFollowing(run.Id).Should().BeFalse();
        _changes.Last().Run.Status.Should().Be(PlanningRunStatus.Failed);
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

        run.Status.Should().Be(PlanningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.PlanningTerminalClosed);
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

        run.Status.Should().Be(PlanningRunStatus.Ingested);
        run.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// planning_complete(closeNow:false) で閉じる予約が入った後、次の Stop より先に端末が
    /// 自分で死んだ(人が × で閉じた)。実行はもう終端なので蘇らせない。予約は捨てて、後から
    /// 保険のタイマーが来ても死んだ端末へ二重に CloseOwned を打たない(B1)。
    /// </summary>
    [Fact]
    public async Task TheTerminalDyingWhileACloseIsPending_DiscardsTheReservation()
    {
        _service.CloseGrace = TimeSpan.FromMilliseconds(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);
        var pendingClose = _service.PendingClose; // 保険のタイマー。予約を捨てた後に走っても何もしないはず

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;
        await pendingClose;

        run.Status.Should().Be(PlanningRunStatus.Ingested, "取り込み済みの実行を蘇らせない");
        run.ErrorMessage.Should().BeNull();
        _launcher.Closed.Should().BeEmpty("端末はもう自分で死んでいるので、無意味な CloseOwned を打たない");
        _events.IsFollowing(run.Id).Should().BeFalse("予約を捨てるなら追従も降りる。誰も止めないポーラーを残さない");
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
