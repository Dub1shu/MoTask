using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class AiJobServiceLifecycleTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly OperationGate _gate = new();
    private readonly FakeAgentRunner _runner = new();
    private readonly FakePermissionPrompt _prompt = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly AiJobService _service;
    private readonly Column _backlog;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    private static readonly PermissionRequest GitPush =
        new("Bash", """{"command":"git push origin main"}""", "toolu_1");

    public AiJobServiceLifecycleTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("確認待ち", ColumnRole.Review);
        _store.SeedColumn("完了", ColumnRole.Done);
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, _gate);
        _service = new AiJobService(_store, _store, _store, _store, _store, _clock, _gate, _runner,
            new PermissionPolicy(), _prompt, _settings, boardService);
    }

    private async Task<AiJob> StartAsync(string title = "a")
    {
        var task = _store.SeedTask(_backlog, title);
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        return job;
    }

    private Task<AiJobSnapshot> WaitForStatusAsync(AiJobStatus status)
    {
        var tcs = new TaskCompletionSource<AiJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.JobChanged += (_, e) => { if (e.Job.Status == status) tcs.TrySetResult(e.Job); };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---------- 停止 ----------

    [Fact]
    public async Task Stop_KillsTheProcess_AndMarksCancelled_WithHistory()
    {
        var job = await StartAsync();
        _clock.UtcNow = new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc);

        var result = await _service.StopJobAsync(job.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        _runner.WasCancelled(job.Id).Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Cancelled);
        job.EndedAt.Should().Be(_clock.UtcNow);
        _service.TurnCountOf(job.Id).Should().Be(0);
        AiJobHistoryDetail.Deserialize(_store.History.Last().Detail)!.Status.Should().Be(AiJobStatus.Cancelled);
        var task = _store.AllTasks.Single(t => t.Id == job.TaskId);
        task.ColumnId.Should().Be(_backlog.Id, "停止では列を動かさない");
    }

    [Fact]
    public async Task Stop_WhilePermissionIsPending_DeniesWithStoppedMessage()
    {
        var job = await StartAsync();
        var pending = _runner.AskPermissionAsync(job.Id, GitPush);
        await _prompt.WaitUntilAskedAsync();

        await _service.StopJobAsync(job.Id);

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        decision.IsAllowed.Should().BeFalse();
        decision.Message.Should().Be(Messages.StoppedByUser);
        job.Status.Should().Be(AiJobStatus.Cancelled);
    }

    [Fact]
    public async Task Stop_NotRunning_IsRejected()
    {
        (await _service.StopJobAsync(999)).Error.Should().Be(Messages.AiJobNotFound);

        var job = await StartAsync();
        var done = WaitForStatusAsync(AiJobStatus.Succeeded);
        _runner.Complete(job.Id, FakeAgentRunner.Success());
        await done;

        (await _service.StopJobAsync(job.Id)).Error.Should().Be(Messages.AiJobNotActive);
    }

    // ---------- アプリ終了 ----------

    [Fact]
    public async Task SuspendAll_DeniesPendingPrompts_KillsProcesses_AndMarksSuspended_WithoutFinishedHistory()
    {
        var a = await StartAsync("a");
        var b = await StartAsync("b");
        var pending = _runner.AskPermissionAsync(a.Id, GitPush);
        await _prompt.WaitUntilAskedAsync();
        var historyBefore = _store.History.Count;

        await _service.SuspendAllAsync();

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        decision.IsAllowed.Should().BeFalse();
        decision.Message.Should().Be(Messages.SuspendedByShutdown);
        _runner.WasCancelled(a.Id).Should().BeTrue();
        _runner.WasCancelled(b.Id).Should().BeTrue();
        a.Status.Should().Be(AiJobStatus.Suspended);
        b.Status.Should().Be(AiJobStatus.Suspended);
        a.EndedAt.Should().BeNull("中断は終了ではない");
        _store.History.Count.Should().Be(historyBefore, "Suspended では AiJobFinished を書かない");
        _service.TurnCountOf(a.Id).Should().Be(0);
    }

    [Fact]
    public async Task SuspendAll_WithNothingRunning_IsANoOp()
    {
        await _service.SuspendAllAsync();
        _store.Jobs.Should().BeEmpty();
    }

    // ---------- 並行する承認要求（CLI は複数のツールを同時に呼ぶ） ----------

    [Fact]
    public async Task SuspendAll_WithTwoPermissionsInFlight_DeniesEveryOne()
    {
        var job = await StartAsync();
        var first = _runner.AskPermissionAsync(job.Id, GitPush);
        var second = _runner.AskPermissionAsync(job.Id, GitPush with { ToolUseId = "toolu_2" });
        await _prompt.WaitUntilAskedAsync(2);

        await _service.SuspendAllAsync();

        foreach (var pending in new[] { first, second })
        {
            var decision = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            decision.IsAllowed.Should().BeFalse();
            decision.Message.Should().Be(Messages.SuspendedByShutdown);
        }
        _prompt.WaitingCount.Should().Be(0, "保留中の承認は 1 件も残さない");
        _runner.WasCancelled(job.Id).Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Suspended);
    }

    /// <summary>
    /// 承認の deny を返し切ってからプロセスを殺す、という順序そのものを見る。ダイアログ側の取り消しを
    /// テストが握って（HoldCancellation）、deny と kill の採番の前後を比べるので、待ち時間に依存しない。
    /// </summary>
    [Fact]
    public async Task SuspendAll_AnswersEveryPendingPermission_BeforeKillingTheProcess()
    {
        var job = await StartAsync();
        _prompt.StampCancellation = _runner.NextOrder;
        _prompt.HoldCancellation = true;
        var first = _runner.AskPermissionAsync(job.Id, GitPush);
        var second = _runner.AskPermissionAsync(job.Id, GitPush with { ToolUseId = "toolu_2" });
        await _prompt.WaitUntilAskedAsync(2);

        var suspend = _service.SuspendAllAsync();
        await _prompt.WaitUntilHeldAsync(2);
        _runner.KillOrderOf(job.Id).Should().Be(0, "保留中の承認を握っている間はまだ殺さない");

        _prompt.ReleaseCancellations();
        await suspend.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        _prompt.CancelOrders.Should().HaveCount(2);
        _prompt.CancelOrders.Max().Should().BeLessThan(
            _runner.KillOrderOf(job.Id), "保留中の承認すべてに決定を返してからプロセスを殺す");
        job.Status.Should().Be(AiJobStatus.Suspended);
    }

    /// <summary>
    /// deny を受け取った CLI が次のツールを呼ぶことがある。畳み始めたあとの要求で状態やイベントを
    /// 書くと、確定済みの Cancelled / Suspended を追い越して終了済みのジョブが Running に蘇る。
    /// </summary>
    [Fact]
    public async Task PermissionArrivingAfterTeardownStarted_IsDeniedWithoutTouchingTheJob()
    {
        var job = await StartAsync();
        _prompt.HoldCancellation = true;
        var first = _runner.AskPermissionAsync(job.Id, GitPush);
        await _prompt.WaitUntilAskedAsync();

        var suspend = _service.SuspendAllAsync();
        await _prompt.WaitUntilHeldAsync();
        var eventsBefore = (await _service.GetEventsAsync(job.Id)).Count;

        var late = await _runner.AskPermissionAsync(job.Id, GitPush with { ToolUseId = "toolu_late" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        late.IsAllowed.Should().BeFalse();
        late.Message.Should().Be(Messages.SuspendedByShutdown);
        job.Status.Should().Be(AiJobStatus.AwaitingApproval, "遅れて来た要求は状態を書き換えない");
        (await _service.GetEventsAsync(job.Id)).Count.Should().Be(eventsBefore, "イベントも増やさない");
        _prompt.Asked.Should().ContainSingle("畳み始めたあとはダイアログを出さない");

        _prompt.ReleaseCancellations();
        await suspend.WaitAsync(TimeSpan.FromSeconds(5));
        (await first.WaitAsync(TimeSpan.FromSeconds(5))).Message.Should().Be(Messages.SuspendedByShutdown);
        job.Status.Should().Be(AiJobStatus.Suspended);
        job.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task TwoPermissionsInFlight_ReturnToRunning_OnlyWhenTheLastOneIsDecided()
    {
        var job = await StartAsync();
        var first = _runner.AskPermissionAsync(job.Id, GitPush);
        var second = _runner.AskPermissionAsync(job.Id, GitPush with { ToolUseId = "toolu_2" });
        await _prompt.WaitUntilAskedAsync(2);

        _prompt.Answer(new HumanDecision(RuleDecision.Allow, Remember: false, RuleScope.Global));
        var settled = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        (await settled).IsAllowed.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.AwaitingApproval, "もう 1 件が承認待ちのまま残っている");

        _prompt.Answer(new HumanDecision(RuleDecision.Allow, Remember: false, RuleScope.Global));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        job.Status.Should().Be(AiJobStatus.Running);
    }

    // ---------- 再開 ----------

    [Fact]
    public async Task Resume_LaunchesWithResumeFlag_AndContinuesSeq()
    {
        var job = await StartAsync();
        await _runner.EmitAsync(job.Id, FakeAgentRunner.Text());
        await _runner.EmitAsync(job.Id, FakeAgentRunner.ToolUse());
        await _service.SuspendAllAsync();
        job.Status.Should().Be(AiJobStatus.Suspended);

        var result = await _service.ResumeJobAsync(job.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        job.Status.Should().Be(AiJobStatus.Running);
        var request = await _runner.WaitForRunAsync(job.Id, nth: 2);
        _runner.Requests.Count(r => r.JobId == job.Id).Should().Be(2);
        request.Resume.Should().BeTrue();
        request.SessionId.Should().Be(job.SessionId);
        request.Prompt.Should().Be(Messages.ResumeInstruction);
        request.WorkingDirectory.Should().Be(job.WorkingDirectory);

        await _runner.EmitAsync(job.Id, FakeAgentRunner.ToolResult());
        (await _service.GetEventsAsync(job.Id)).Select(e => e.Seq).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Resume_ThenFailure_UsesResumeFailedMessage()
    {
        var job = await StartAsync();
        await _service.SuspendAllAsync();
        (await _service.ResumeJobAsync(job.Id)).IsSuccess.Should().BeTrue();
        await _runner.WaitForRunAsync(job.Id, nth: 2);
        var failed = WaitForStatusAsync(AiJobStatus.Failed);

        _runner.Complete(job.Id, new AgentRunOutcome(1, null, "No conversation found with session ID"));
        await failed;

        job.ErrorMessage.Should().Be(string.Format(Messages.ResumeFailedFormat, "No conversation found with session ID"));
    }

    [Fact]
    public async Task Resume_NotSuspended_IsRejected()
    {
        var job = await StartAsync();
        (await _service.ResumeJobAsync(job.Id)).Error.Should().Be(Messages.AiJobNotSuspended);
        (await _service.ResumeJobAsync(999)).Error.Should().Be(Messages.AiJobNotFound);
    }

    [Fact]
    public async Task Resume_ChecksAvailabilityAndLimit()
    {
        var job = await StartAsync();
        await _service.SuspendAllAsync();

        _runner.Availability = Result.Fail(Messages.ClaudeNotFound);
        (await _service.ResumeJobAsync(job.Id)).Error.Should().Be(Messages.ClaudeNotFound);
        _runner.Availability = Result.Ok();

        _settings.Settings = _settings.Settings with { MaxConcurrentJobs = 1 };
        await StartAsync("other");
        (await _service.ResumeJobAsync(job.Id)).Error.Should().Be(string.Format(Messages.ConcurrencyLimitFormat, 1, 1));
        job.Status.Should().Be(AiJobStatus.Suspended);
    }

    /// <summary>
    /// 再開したジョブは実行中の一覧（_running）へ戻る。戻っていないと「1 タスクに実行中のジョブは 1 つ」が
    /// 中断→再開をまたいだ瞬間だけ破れる。
    /// </summary>
    [Fact]
    public async Task Resume_PutsTheJobBack_SoTheOneActiveJobPerTaskGuardStillHolds()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        await _service.SuspendAllAsync();

        (await _service.ResumeJobAsync(job.Id)).IsSuccess.Should().BeTrue();
        await _runner.WaitForRunAsync(job.Id, nth: 2);

        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "もう一度"))
            .Error.Should().Be(Messages.TaskAlreadyHasActiveJob);
    }

    [Fact]
    public async Task Resume_DeletedTask_IsRejected()
    {
        var job = await StartAsync();
        await _service.SuspendAllAsync();
        _store.AllTasks.Single(t => t.Id == job.TaskId).DeletedAt = _clock.UtcNow;

        (await _service.ResumeJobAsync(job.Id)).Error.Should().Be(Messages.TaskDeletedCannotRunAi);
        job.Status.Should().Be(AiJobStatus.Suspended);
    }

    [Fact]
    public async Task Resume_WhenTheTaskAlreadyHasAnotherActiveJob_IsRejected()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        await _service.SuspendAllAsync();

        // 中断中のジョブは Active ではないので、同じタスクで別のジョブを始められる
        var other = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "別口")).Value!;
        await _runner.WaitForRunAsync(other.Id);

        (await _service.ResumeJobAsync(job.Id)).Error.Should().Be(Messages.TaskAlreadyHasActiveJob);
        job.Status.Should().Be(AiJobStatus.Suspended);
    }

    // ---------- 起動時復旧 ----------

    [Fact]
    public async Task RecoverOnStartup_MarksOrphanedActiveJobsSuspended()
    {
        var task = _store.SeedTask(_backlog, "a");
        var running = new AiJob { TaskId = task.Id, Status = AiJobStatus.Running, SessionId = Guid.NewGuid() };
        var awaiting = new AiJob { TaskId = task.Id, Status = AiJobStatus.AwaitingApproval, SessionId = Guid.NewGuid() };
        var done = new AiJob { TaskId = task.Id, Status = AiJobStatus.Succeeded, SessionId = Guid.NewGuid() };
        _store.Add(running);
        _store.Add(awaiting);
        _store.Add(done);

        await _service.RecoverOnStartupAsync();

        running.Status.Should().Be(AiJobStatus.Suspended);
        awaiting.Status.Should().Be(AiJobStatus.Suspended);
        done.Status.Should().Be(AiJobStatus.Succeeded);
        _store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task RecoverOnStartup_WithNothingToRecover_DoesNotSave()
    {
        await _service.RecoverOnStartupAsync();
        _store.SaveCount.Should().Be(0);
    }

    // ---------- ルール管理 ----------

    [Fact]
    public async Task Rules_ListAndDelete()
    {
        _store.Add(new AiPermissionRule { ToolName = "Bash", Pattern = "git push", Decision = RuleDecision.Allow, CreatedAt = _clock.UtcNow });
        var rule = _store.Rules.Single();

        (await _service.GetPermissionRulesAsync()).Should().ContainSingle();
        (await _service.DeletePermissionRuleAsync(rule.Id)).IsSuccess.Should().BeTrue();
        (await _service.GetPermissionRulesAsync()).Should().BeEmpty();
        (await _service.DeletePermissionRuleAsync(rule.Id)).Error.Should().Be(Messages.PermissionRuleNotFound);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
