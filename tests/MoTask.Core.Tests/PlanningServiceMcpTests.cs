using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// MCP 経由の受け口（仕様 §6）。「不備」は通常の結果で理由を返し、「宛先違い」だけが Fail になる。
/// </summary>
public class PlanningServiceMcpTests
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
    private readonly TaskItem _existing;

    public PlanningServiceMcpTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _existing = _store.SeedTask(active, "Q4企画書の内容を確定する");
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

    private static CandidateInput Candidate(
        string externalId = "outlook:001", string evidence = "「9月8日までに」",
        string action = "register", int? mergeTarget = null)
        => new(externalId, "Outlook", "請求先情報を更新する", evidence,
            From: "山本さん", Link: "", Reasoning: "依頼が明確", ReceivedAt: null,
            SuggestedDueDate: null, SuggestedProject: "", SuggestedAction: action,
            MergeTargetTaskId: mergeTarget);

    // ---- planning_get_context ----

    [Fact]
    public async Task GetContext_ReturnsTheDateAndTheUnfinishedTasks()
    {
        var run = await StartAsync();

        var context = await _service.GetContextAsync(run.Id);

        context.IsSuccess.Should().BeTrue(context.Error);
        var root = JsonDocument.Parse(context.Value!).RootElement;
        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()).Should().Equal("Q4企画書の内容を確定する");
    }

    [Fact]
    public async Task GetContext_ListsTheLabels()
    {
        _store.SeedLabel("経理");
        var run = await StartAsync();

        var context = await _service.GetContextAsync(run.Id);

        JsonDocument.Parse(context.Value!).RootElement.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetProperty("name").GetString()).Should().Equal("経理");
    }

    /// <summary>盤面が無い(論理的にありえないはずだが)なら、Claude に取り違えさせず Fail にする。</summary>
    [Fact]
    public async Task GetContext_FailsWhenThereIsNoBoard()
    {
        var run = await StartAsync();
        _store.NoBoard = true;

        var context = await _service.GetContextAsync(run.Id);

        context.IsSuccess.Should().BeFalse();
        context.Error.Should().Be(Messages.BoardNotFound);
    }

    /// <summary>
    /// 利用者が普段使っている Claude Code も同じ MCP サーバに繋がる。宛先違いはツールエラーにして、
    /// そちらが誤って計画づくりを動かす事故を防ぐ（仕様 §6）。
    /// </summary>
    [Fact]
    public async Task GetContext_FailsForARunIdThatIsNotRunning()
    {
        await StartAsync();

        var context = await _service.GetContextAsync(9999);

        context.IsSuccess.Should().BeFalse();
        context.Error.Should().Be(string.Format(Messages.PlanningRunNotRunningFormat, 9999));
    }

    // ---- planning_add_candidate ----

    [Fact]
    public async Task AddCandidate_AcceptsOneAndTellsHowManyAreStacked()
    {
        var run = await StartAsync();

        var first = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        var second = await _service.AddCandidateAsync(run.Id, Candidate("teams:002"));

        first.Value!.Accepted.Should().BeTrue();
        first.Value.Total.Should().Be(1);
        second.Value!.Total.Should().Be(2);
        _store.Candidates.Select(c => c.ExternalId).Should().Equal("outlook:001", "teams:002");
        _store.Candidates[0].Id.Should().Be(first.Value.CandidateId);
        _store.Candidates[0].Status.Should().Be(TriageStatus.Pending);
    }

    /// <summary>受理のたびに知らせる。候補が画面に 1 件ずつ現れる（仕様 §6）。</summary>
    [Fact]
    public async Task AddCandidate_RaisesRunChangedForEachAcceptedCandidate()
    {
        var run = await StartAsync();
        _changes.Clear();

        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        await _service.AddCandidateAsync(run.Id, Candidate("teams:002"));

        _changes.Where(c => c.CandidatesChanged).Should().HaveCount(2);
    }

    [Fact]
    public async Task AddCandidate_CopiesEveryFieldOntoTheCandidate()
    {
        var run = await StartAsync();

        await _service.AddCandidateAsync(run.Id, Candidate(action: "merge", mergeTarget: _existing.Id));

        var candidate = _store.Candidates.Should().ContainSingle().Subject;
        candidate.PlanningRunId.Should().Be(run.Id);
        candidate.Source.Should().Be("Outlook");
        candidate.Evidence.Should().Be("「9月8日までに」");
        candidate.SuggestedAction.Should().Be(TriageAction.Merge);
        candidate.SuggestedMergeTaskId.Should().Be(_existing.Id);
        candidate.DecidedAt.Should().BeNull();
    }

    /// <summary>不備はツールエラーにしない。Claude は 1 件諦めて次へ進めばよい（仕様 §3）。</summary>
    [Fact]
    public async Task AddCandidate_RefusesWithoutEvidence_AsAnOrdinaryResult()
    {
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate(evidence: ""));

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateEvidenceRequired);
        _store.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AddCandidate_RefusesAMergeTargetThatIsNotOnTheBoard()
    {
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate(action: "merge", mergeTarget: 9999));

        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateMergeTargetMissing);
    }

    /// <summary>現行は取り込み時に黙って捨てていた。ここでは理由を返す（仕様 §6）。</summary>
    [Fact]
    public async Task AddCandidate_RefusesAnExternalIdDecidedInAnEarlierRun()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), PlanningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:001", TriageStatus.Rejected);
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateAlreadyDecidedElsewhere);
    }

    [Fact]
    public async Task AddCandidate_RefusesTheSameExternalIdTwiceInOneRun()
    {
        var run = await StartAsync();
        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        var again = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        again.Value!.Accepted.Should().BeFalse();
        again.Value.Reason.Should().Be(Messages.CandidateAlreadyInThisRun);
        _store.Candidates.Should().ContainSingle();
    }

    /// <summary>
    /// 保存に失敗したのに Accepted:true を返すと、Claude は積まれたと信じて次へ進み、
    /// 候補は黙って消える。保存できていなければ理由付きで断り、呼び直す機会を与える(Finding 2)。
    /// </summary>
    [Fact]
    public async Task AddCandidate_RefusesWhenTheSaveFails_SoClaudeCanRetry()
    {
        var run = await StartAsync();
        _store.FailNextSave = true;

        var result = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse("保存できていないのに積まれたと Claude に信じさせない");
        result.Value.Reason.Should().StartWith(Messages.SaveFailed);
        result.Value.Total.Should().Be(0, "保存できていない候補は数えない");
    }

    [Fact]
    public async Task AddCandidate_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.AddCandidateAsync(9999, Candidate());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.PlanningRunNotRunningFormat, 9999));
    }

    // ---- planning_submit_plan ----

    [Fact]
    public async Task SubmitPlan_SavesThePlanVerbatim()
    {
        var run = await StartAsync();

        var result = await _service.SubmitPlanAsync(run.Id, Plan);

        result.Value!.Accepted.Should().BeTrue();
        run.PlanJson.Should().Be(Plan);
        run.Status.Should().Be(PlanningRunStatus.Running, "提出は実行の終わりではない");
    }

    [Fact]
    public async Task SubmitPlan_KeepsTheLastAcceptedOne()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        var better = """{"groups":[{"key":"ifTime","items":[{"taskId":45}]}]}""";

        await _service.SubmitPlanAsync(run.Id, better);

        run.PlanJson.Should().Be(better, "何度でも呼べて、最後に受理されたものが残る（仕様 §6）");
    }

    [Fact]
    public async Task SubmitPlan_RefusesABadGroupKeyWithoutLosingTheOldPlan()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var result = await _service.SubmitPlanAsync(
            run.Id, """{"groups":[{"key":"someday","items":[]}]}""");

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(string.Format(Messages.PlanGroupKeyInvalidFormat, 0, "someday"));
        run.PlanJson.Should().Be(Plan, "受理しなかった計画で上書きしない");
    }

    [Fact]
    public async Task SubmitPlan_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.SubmitPlanAsync(9999, Plan);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.PlanningRunNotRunningFormat, 9999));
    }

    // ---- planning_complete ----

    [Fact]
    public async Task Complete_RefusesUntilAPlanHasBeenSubmitted()
    {
        var run = await StartAsync();

        var result = await _service.CompleteRunAsync(run.Id, closeNow: false);

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.PlanNotSubmitted);
        run.Status.Should().Be(PlanningRunStatus.Pending, "受理しなかっただけ。実行は動いたまま");
        _launcher.Closed.Should().BeEmpty();
    }

    /// <summary>候補 0 件の日は失敗ではない(親仕様 §8 の約束を引き継ぐ)。</summary>
    [Fact]
    public async Task Complete_AcceptsARunWithNoCandidates()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10); // 保険は今回は効かせない
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var result = await _service.CompleteRunAsync(run.Id, closeNow: false);

        result.Value!.Accepted.Should().BeTrue();
        run.Status.Should().Be(PlanningRunStatus.Ingested);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty();
    }

    /// <summary>
    /// ツール結果を返した直後に殺すと Claude の最後の一言が切れる。Stop はそのターンが
    /// 終わった合図なので、言い終えてから消す(仕様 §7)。
    /// </summary>
    [Fact]
    public async Task Complete_DoesNotCloseUntilTheNextStop()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10); // 保険は今回は効かせない
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteRunAsync(run.Id, closeNow: false);
        _launcher.Closed.Should().BeEmpty("まだ喋っている最中");
        _events.IsFollowing(run.Id).Should().BeTrue("Stop を受け取る必要があるので追従は続ける");

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().Equal(run.Id);
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_ClosesOnlyOnce_EvenIfMoreStopsArrive()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>Stop が来ないまま座り込んだときの保険(仕様 §7)。</summary>
    [Fact]
    public async Task Complete_ClosesAfterTheGraceEvenWithoutAStop()
    {
        _service.CloseGrace = TimeSpan.FromMilliseconds(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteRunAsync(run.Id, closeNow: false);
        await _service.PendingClose;

        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>人の「完了にする」は待つべき Stop が来る保証が無いのでその場で閉じる(仕様 §7)。</summary>
    [Fact]
    public async Task CompleteByHand_ClosesRightAway()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var done = await _service.CompleteAsync(run.Id);

        done.IsSuccess.Should().BeTrue(done.Error);
        run.Status.Should().Be(PlanningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteByHand_ReportsWhyItRefusedWithoutAPlan()
    {
        var run = await StartAsync();

        var done = await _service.CompleteAsync(run.Id);

        done.IsSuccess.Should().BeFalse();
        // 人向けの文言。Claude 向け(MCP ツール名入り)の Messages.PlanNotSubmitted とは別物。
        done.Error.Should().Be(Messages.PlanningCompleteWithoutPlan);
        run.Status.Should().Be(PlanningRunStatus.Pending);
    }

    /// <summary>候補も計画も MCP から来たものがそのまま画面へ回る。</summary>
    [Fact]
    public async Task AFullPlanning_EndsWithThePlanAndTheCandidatesInPlace()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(PlanningRunStatus.Ingested);
        run.PlanJson.Should().Be(Plan);
        (await _service.GetQueueAsync(run.Id)).Select(c => c.ExternalId).Should().Equal("outlook:001");
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>complete が来ないままセッションが終わった(仕様 §7)。</summary>
    [Fact]
    public async Task SessionEnd_WithoutComplete_FailsTheRunAndClosesTheTerminal()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(PlanningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.PlanningCompleteMissing);
        _events.IsFollowing(run.Id).Should().BeFalse();
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>complete の後に来た SessionEnd は、予約が残っていればそれを果たすだけ。</summary>
    [Fact]
    public async Task SessionEnd_AfterComplete_ClosesWithoutFailingTheRun()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(PlanningRunStatus.Ingested, "終わった実行を蘇らせない");
        _launcher.Closed.Should().Equal(run.Id);
    }

    [Fact]
    public async Task Complete_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.CompleteRunAsync(9999, closeNow: false);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.PlanningRunNotRunningFormat, 9999));
    }
}
