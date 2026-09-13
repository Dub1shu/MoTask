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
/// 追従と取り込み(仕様 §6・§8)。Stop は何度でも来るので、揃った時点で 1 度だけ取り込む。
/// </summary>
public class MorningServiceIngestTests
{
    private const string Plan =
        """{"date":"2026-09-07","groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

    private const string TwoCandidates =
        """
        {"externalId":"outlook:001","source":"Outlook","title":"請求先情報を更新する","evidence":"「9月8日までに」","suggestedAction":"register"}
        {"externalId":"teams:002","source":"Teams","title":"数値を差し替える","evidence":"「速報値に」","suggestedAction":"merge","mergeTargetTaskId":45}
        """;

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceIngestTests()
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

    private void PutResult(MorningRun run, string? candidates, string? plan)
    {
        if (candidates is not null) _folder.Put(run.JobFolder, JobFolderPaths.CandidatesRelativePath, candidates);
        if (plan is not null) _folder.Put(run.JobFolder, JobFolderPaths.PlanRelativePath, plan);
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

    /// <summary>掛け直せた実行は、その後の取り込みでちゃんと閉じられる(仕様 §7)。</summary>
    [Fact]
    public async Task Recover_ThenIngest_StillClosesTheTerminal()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);
        await _service.RecoverOnStartupAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

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
    public async Task Stop_LeavesTheRunAlone_WhenResultIsNotThereYet()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Running, "Stop は何度でも来る");
        _store.Candidates.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
        _service.TurnCountOf(run.Id).Should().Be(1);
    }

    [Fact]
    public async Task Stop_IngestsOnceTheResultIsComplete()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.PlanJson.Should().Be(Plan);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Select(c => c.ExternalId).Should().Equal("outlook:001", "teams:002");
        _events.IsFollowing(run.Id).Should().BeFalse();
        _changes.Last().CandidatesChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Ingest_CopiesEveryFieldOntoTheCandidate()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        var merge = _store.Candidates.Single(c => c.ExternalId == "teams:002");
        merge.MorningRunId.Should().Be(run.Id);
        merge.Source.Should().Be("Teams");
        merge.Title.Should().Be("数値を差し替える");
        merge.Evidence.Should().Be("「速報値に」");
        merge.SuggestedAction.Should().Be(TriageAction.Merge);
        merge.SuggestedMergeTaskId.Should().Be(45);
        merge.Status.Should().Be(TriageStatus.Pending);
        merge.DecidedAt.Should().BeNull();
    }

    [Fact]
    public async Task Stop_TwiceIngestsOnlyOnce()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Should().HaveCount(2, "result が揃った時点で 1 度だけ取り込む(仕様 §14)");
    }

    [Fact]
    public async Task SessionEnd_AfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Ingested, "終わった実行を蘇らせない");
        _store.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task SessionEnd_WithoutAPlan_FailsTheRunWithAReason()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, plan: null);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningResultUnreadable);
        _store.Candidates.Should().BeEmpty("プランが読めない実行は取り込まない(仕様 §8)");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task SessionEnd_WithABrokenPlan_FailsTheRun()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, """{"groups":[{"key":"someday","items":[]}]}""");

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
    }

    [Fact]
    public async Task Ingest_SucceedsWithNoCandidates_WhenThePlanIsValid()
    {
        var run = await StartAsync();
        PutResult(run, "", Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Ingested,
            "コネクタ未認証や候補が無い朝は失敗ではない(仕様 §8・§14)");
        _store.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_ReportsHowManyLinesItThrewAway()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates + "\n{壊れた行\n{\"externalId\":\"x\",\"source\":\"S\",\"title\":\"根拠なし\",\"suggestedAction\":\"register\"}", Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Should().HaveCount(2);
        _changes.Last().Warning.Should()
            .Be(string.Format(Messages.MorningCandidatesDiscardedFormat, 4, 2), "黙って減らさない(仕様 §8)");
    }

    [Fact]
    public async Task Ingest_SkipsCandidatesAlreadyDecidedInAnEarlierRun()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:001", TriageStatus.Rejected);
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Where(c => c.MorningRunId == run.Id).Select(c => c.ExternalId)
            .Should().Equal(new[] { "teams:002" }, "却下した候補は翌朝また出てきても取り込まない(仕様 §9)");
    }

    [Fact]
    public async Task Complete_IngestsForARunWhoseTerminalWasClosed()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        PutResult(run, TwoCandidates, Plan);

        var completed = await _service.CompleteAsync(run.Id);

        completed.IsSuccess.Should().BeTrue(completed.Error);
        run.Status.Should().Be(MorningRunStatus.Ingested);
        _store.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task Complete_FailsTheRun_WhenThereIsNothingToIngest()
    {
        var run = await StartAsync();

        var completed = await _service.CompleteAsync(run.Id);

        completed.IsSuccess.Should().BeFalse();
        completed.Error.Should().Be(Messages.MorningResultUnreadable);
        run.Status.Should().Be(MorningRunStatus.Failed);
    }

    [Fact]
    public async Task Complete_RefusesARunThatIsAlreadyFinished()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        var again = await _service.CompleteAsync(run.Id);

        again.IsSuccess.Should().BeFalse();
        again.Error.Should().Be(Messages.MorningRunAlreadyFinished);
    }

    [Fact]
    public async Task StopTracking_CancelsWithoutIngesting()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeTrue(stopped.Error);
        run.Status.Should().Be(MorningRunStatus.Cancelled);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty("追跡をやめただけ。端末は殺さないし取り込みもしない");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    // すでに取り込みを終えた実行に対する StopTrackingAsync の拒否経路を確認する。
    // 「確認」と「書き込み」を1回の _gate.RunAsync にまとめたこと自体(競合窓を無くしたこと)は、
    // ここでは検証していない。その窓は StopTrackingAsync の2回のゲート取得の間にあったが、
    // 修正で1回にまとめたことで窓そのものが構造的に無くなっており、テストで再現しようとすると
    // 本番コードにテスト専用の待ち合わせフックを足すことになる。それは避け、ここでは
    // 「順番どおり呼んだときに正しく拒否される」という、実際に確認できる性質だけを固定する。
    [Fact]
    public async Task StopTracking_RefusesARunThatAlreadyFinishedIngesting()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        run.Status.Should().Be(MorningRunStatus.Ingested, "前提: 取り込みが先に終わっている");

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeFalse();
        stopped.Error.Should().Be(Messages.MorningRunAlreadyFinished);
        run.Status.Should().Be(MorningRunStatus.Ingested, "終了済みの実行を Cancelled で上書きしない");
        _store.Candidates.Should().HaveCount(2, "取り込んだ候補は追跡解除で消えない");
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

    /// <summary>仕事が終わったら窓も畳む(仕様 §7)。</summary>
    [Fact]
    public async Task Stop_ClosesTheTerminalOnceTheResultIsIngested()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }

    [Fact]
    public async Task Stop_LeavesTheTerminalOpen_WhileTheResultIsNotThereYet()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().BeEmpty("Stop は何度でも来る。揃うまでは閉じない");
    }

    /// <summary>読めないまま終わった実行も後始末する。窓だけ残しても人は困る(仕様 §7)。</summary>
    [Fact]
    public async Task SessionEnd_WithoutAPlan_ClosesTheTerminalToo()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>人の「完了にする」はその場で閉じる。待つべき Stop が来る保証が無い(仕様 §7)。</summary>
    [Fact]
    public async Task Complete_ClosesTheTerminal()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _service.CompleteAsync(run.Id);

        _launcher.Closed.Should().Equal(run.Id);
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

    /// <summary>閉じたのはこちらなので、取り込み済みの実行を Failed で上書きしない。</summary>
    [Fact]
    public async Task TheTerminalDyingAfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

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
