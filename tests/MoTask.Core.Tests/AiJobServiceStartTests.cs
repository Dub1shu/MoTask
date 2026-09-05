using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class AiJobServiceStartTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly OperationGate _gate = new();
    private readonly FakeAgentRunner _runner = new();
    private readonly FakePermissionPrompt _prompt = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly AiJobService _service;
    private readonly Column _backlog;
    private readonly Column _review;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public AiJobServiceStartTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("進行中", ColumnRole.Active);
        _review = _store.SeedColumn("確認待ち", ColumnRole.Review);
        _store.SeedColumn("完了", ColumnRole.Done);
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = Path.Combine(_tempDir, "default") };
        var boardService = new BoardService(_store, _store, _store, _clock, _gate);
        _service = new AiJobService(_store, _store, _store, _store, _store, _clock, _gate, _runner,
            new PermissionPolicy(), _prompt, _settings, boardService);
    }

    private Task<AiJobSnapshot> WaitForStatusAsync(AiJobStatus status)
    {
        var tcs = new TaskCompletionSource<AiJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.JobChanged += (_, e) => { if (e.Job.Status == status) tcs.TrySetResult(e.Job); };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private Task<AiJobChangedEventArgs> WaitForWarningAsync()
    {
        var tcs = new TaskCompletionSource<AiJobChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.JobChanged += (_, e) => { if (e.Warning is not null) tcs.TrySetResult(e); };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---------- 開始 ----------

    [Fact]
    public async Task Start_CreatesRunningJob_WritesHistory_AndLaunchesRunner()
    {
        var task = _store.SeedTask(_backlog, "調べもの");
        _clock.UtcNow = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Research, "  最新の価格を調べてください  ");

        result.IsSuccess.Should().BeTrue(result.Error);
        var job = result.Value!;
        job.Status.Should().Be(AiJobStatus.Running);
        job.Kind.Should().Be(AiJobKind.Research);
        job.Instruction.Should().Be("最新の価格を調べてください");
        job.SessionId.Should().NotBe(Guid.Empty);
        job.StartedAt.Should().Be(_clock.UtcNow);
        job.WorkingDirectory.Should().Be(_settings.Settings.DefaultWorkingDirectory);
        Directory.Exists(job.WorkingDirectory).Should().BeTrue("既定ワークフォルダは初回のジョブ開始時に作る");
        _store.Jobs.Should().ContainSingle();
        _store.SaveCount.Should().Be(1);

        var history = _store.History.Single(h => h.TaskId == task.Id);
        history.Kind.Should().Be(HistoryKind.AiJobStarted);
        AiJobHistoryDetail.Deserialize(history.Detail).Should().Be(new AiJobHistoryDetail(AiJobKind.Research, null));

        var request = await _runner.WaitForRunAsync(job.Id);
        request.SessionId.Should().Be(job.SessionId);
        request.Kind.Should().Be(AiJobKind.Research);
        request.Prompt.Should().Be("最新の価格を調べてください");
        request.WorkingDirectory.Should().Be(job.WorkingDirectory);
        request.Resume.Should().BeFalse();
    }

    [Fact]
    public async Task Start_RaisesJobChanged_WithRunningSnapshot()
    {
        var task = _store.SeedTask(_backlog, "a");
        var running = WaitForStatusAsync(AiJobStatus.Running);

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        var snapshot = await running;
        snapshot.JobId.Should().Be(result.Value!.Id);
        snapshot.TaskId.Should().Be(task.Id);
        snapshot.Kind.Should().Be(AiJobKind.Execute);
        snapshot.TurnCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_EmptyInstruction_IsRejected()
    {
        var task = _store.SeedTask(_backlog, "a");
        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "   ");
        result.Error.Should().Be(Messages.InstructionRequired);
        _store.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenClaudeIsMissing_IsRejectedBeforeCreatingAJob()
    {
        _runner.Availability = Result.Fail(Messages.ClaudeNotFound);
        var task = _store.SeedTask(_backlog, "a");

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        result.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Jobs.Should().BeEmpty();
        _store.History.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_UnknownOrDeletedTask_IsRejected()
    {
        (await _service.StartJobAsync(999, AiJobKind.Execute, "やる")).Error.Should().Be(Messages.TaskNotFound);

        var task = _store.SeedTask(_backlog, "a");
        task.DeletedAt = _clock.UtcNow;
        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Error.Should().Be(Messages.TaskDeletedCannotRunAi);
    }

    [Fact]
    public async Task Start_UsesProjectWorkingDirectory_WhenItExists()
    {
        var dir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(dir);
        var project = _store.SeedProject("顧客A");
        project.WorkingDirectory = dir;
        var task = _store.SeedTask(_backlog, "a");
        task.ProjectId = project.Id;

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.WorkingDirectory.Should().Be(dir);
    }

    [Fact]
    public async Task Start_ProjectWorkingDirectoryMissing_IsRejected_WithoutFallingBackToDefault()
    {
        var missing = Path.Combine(_tempDir, "nope");
        var project = _store.SeedProject("顧客A");
        project.WorkingDirectory = missing;
        var task = _store.SeedTask(_backlog, "a");
        task.ProjectId = project.Id;

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        result.Error.Should().Be(string.Format(Messages.WorkingDirectoryMissingFormat, missing));
        _store.Jobs.Should().BeEmpty();
    }

    /// <summary>既定の作業フォルダを作れなかった場合は、プロジェクト設定を指す文言にならないこと。</summary>
    [Fact]
    public async Task Start_DefaultWorkingDirectoryCannotBeCreated_IsRejected_WithTheDefaultFolderMessage()
    {
        Directory.CreateDirectory(_tempDir);
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "");
        var fallback = Path.Combine(blocker, "MoTask");
        _settings.Settings = _settings.Settings with { DefaultWorkingDirectory = fallback };
        var task = _store.SeedTask(_backlog, "a");

        var result = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        result.Error.Should().Be(string.Format(Messages.DefaultWorkingDirectoryFailedFormat, fallback));
        _store.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_TaskWithActiveJob_IsRejected()
    {
        var task = _store.SeedTask(_backlog, "a");
        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();

        (await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる")).Error.Should().Be(Messages.TaskAlreadyHasActiveJob);
    }

    [Fact]
    public async Task Start_OverConcurrencyLimit_IsRejected_WithCounts()
    {
        _settings.Settings = _settings.Settings with { MaxConcurrentJobs = 1 };
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        (await _service.StartJobAsync(a.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();

        var result = await _service.StartJobAsync(b.Id, AiJobKind.Execute, "やる");

        result.Error.Should().Be(string.Format(Messages.ConcurrencyLimitFormat, 1, 1));
        _store.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_AfterCompletion_FreesTheSlot()
    {
        _settings.Settings = _settings.Settings with { MaxConcurrentJobs = 1 };
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        var first = (await _service.StartJobAsync(a.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(first.Id);
        var done = WaitForStatusAsync(AiJobStatus.Succeeded);
        _runner.Complete(first.Id, FakeAgentRunner.Success());
        await done;

        (await _service.StartJobAsync(b.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();
    }

    // ---------- イベント ----------

    [Fact]
    public async Task Events_ArePersistedInOrder_AndTurnCountCountsAssistantSteps()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var seen = new List<AiJobEvent>();
        _service.JobChanged += (_, e) => { if (e.NewEvent is not null) seen.Add(e.NewEvent); };
        _clock.UtcNow = new DateTime(2026, 9, 5, 9, 1, 0, DateTimeKind.Utc);

        await _runner.EmitAsync(job.Id, FakeAgentRunner.Text());
        await _runner.EmitAsync(job.Id, FakeAgentRunner.ToolUse("Bash"));
        await _runner.EmitAsync(job.Id, FakeAgentRunner.ToolResult());

        var events = await _service.GetEventsAsync(job.Id);
        events.Select(e => e.Seq).Should().Equal(1, 2, 3);
        events.Select(e => e.Kind).Should().Equal(AiJobEventKind.AssistantText, AiJobEventKind.ToolUse, AiJobEventKind.ToolResult);
        events[1].ToolName.Should().Be("Bash");
        events[0].Payload.Should().StartWith("{\"type\":\"assistant\"");
        events[0].At.Should().Be(_clock.UtcNow);
        seen.Should().HaveCount(3);
        _service.TurnCountOf(job.Id).Should().Be(2, "AssistantText と ToolUse を数える");
        _store.SaveCount.Should().Be(4, "開始 1 回 + イベント 3 回");
    }

    // ---------- 完了 ----------

    [Fact]
    public async Task Completion_Succeeded_MovesTaskToReview_StoresCost_AndWritesHistory()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var done = WaitForStatusAsync(AiJobStatus.Succeeded);
        _clock.UtcNow = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

        _runner.Complete(job.Id, FakeAgentRunner.Success(turns: 7, cost: 0.123m));
        var snapshot = await done;

        job.Status.Should().Be(AiJobStatus.Succeeded);
        job.EndedAt.Should().Be(_clock.UtcNow);
        job.NumTurns.Should().Be(7);
        job.TotalCostUsd.Should().Be(0.123m);
        job.ErrorMessage.Should().BeNull();
        snapshot.TurnCount.Should().Be(7);
        snapshot.TotalCostUsd.Should().Be(0.123m);
        task.ColumnId.Should().Be(_review.Id, "完了したタスクは Review ロールの列へ");
        _store.History.Select(h => h.Kind).Should().Equal(HistoryKind.AiJobStarted, HistoryKind.AiJobFinished, HistoryKind.Moved);
        AiJobHistoryDetail.Deserialize(_store.History[1].Detail)!.Status.Should().Be(AiJobStatus.Succeeded);
        _service.TurnCountOf(job.Id).Should().Be(0, "実行中の一覧から外れる");
    }

    [Fact]
    public async Task Completion_WithoutReviewColumn_SucceedsWithWarning_AndDoesNotMove()
    {
        _store.Board.Columns.Remove(_review);
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var warned = WaitForWarningAsync();

        _runner.Complete(job.Id, FakeAgentRunner.Success());
        var args = await warned;

        args.Job.Status.Should().Be(AiJobStatus.Succeeded);
        args.Warning.Should().Be(Messages.NoReviewColumn);
        task.ColumnId.Should().Be(_backlog.Id);
    }

    /// <summary>
    /// 完了時の保存に失敗しても、確認待ちへの移動は必ず試みる（??= だと短絡して移動が呼ばれない）。
    /// バナーは 1 本なので、先に立っている保存失敗の警告をそのまま出す。
    /// </summary>
    [Fact]
    public async Task Completion_WhenTheFinalSaveFails_StillMovesTaskToReview()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var warned = WaitForWarningAsync();
        _store.FailNextSave = true;

        _runner.Complete(job.Id, FakeAgentRunner.Success());
        var args = await warned;

        args.Job.Status.Should().Be(AiJobStatus.Succeeded);
        args.Warning.Should().StartWith(Messages.SaveFailed, "保存失敗の理由を見せる");
        task.ColumnId.Should().Be(_review.Id, "保存に失敗しても確認待ちへは動かす");
    }

    [Fact]
    public async Task Completion_IsError_BecomesFailed_WithResultText()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var failed = WaitForStatusAsync(AiJobStatus.Failed);

        _runner.Complete(job.Id, new AgentRunOutcome(0, new AgentResultInfo(true, 3, 0.01m, "Max turns reached"), null));
        await failed;

        job.Status.Should().Be(AiJobStatus.Failed);
        job.ErrorMessage.Should().Be(string.Format(Messages.AgentFailedFormat, "Max turns reached"));
        job.NumTurns.Should().Be(3);
        task.ColumnId.Should().Be(_backlog.Id, "失敗では動かさない");
        AiJobHistoryDetail.Deserialize(_store.History.Last().Detail)!.Status.Should().Be(AiJobStatus.Failed);
    }

    [Fact]
    public async Task Completion_NonZeroExitWithoutResult_UsesExitCodeMessage()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var failed = WaitForStatusAsync(AiJobStatus.Failed);

        _runner.Complete(job.Id, new AgentRunOutcome(1, null, "   "));
        await failed;

        job.ErrorMessage.Should().Be(string.Format(Messages.AgentFailedFormat, string.Format(Messages.AgentExitedWithCodeFormat, 1)));
    }

    [Fact]
    public async Task Completion_NonZeroExitWithStderr_UsesStderr()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var failed = WaitForStatusAsync(AiJobStatus.Failed);

        _runner.Complete(job.Id, new AgentRunOutcome(2, null, "Error: invalid API key"));
        await failed;

        job.ErrorMessage.Should().Be(string.Format(Messages.AgentFailedFormat, "Error: invalid API key"));
    }

    [Fact]
    public async Task Completion_RunnerThrows_BecomesFailed()
    {
        var task = _store.SeedTask(_backlog, "a");
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        var failed = WaitForStatusAsync(AiJobStatus.Failed);

        _runner.Fail(job.Id, new InvalidOperationException("spawn failed"));
        await failed;

        job.ErrorMessage.Should().Be(string.Format(Messages.AgentFailedFormat, "spawn failed"));
    }

    // ---------- 照会 ----------

    [Fact]
    public async Task Queries_ReturnJobsNewestFirst_AndUnfinishedOnes()
    {
        var task = _store.SeedTask(_backlog, "a");
        _store.Add(new AiJob { TaskId = task.Id, Kind = AiJobKind.Research, Status = AiJobStatus.Succeeded });
        _store.Add(new AiJob { TaskId = task.Id, Kind = AiJobKind.Execute, Status = AiJobStatus.Suspended });
        _store.Add(new AiJob { TaskId = 999, Kind = AiJobKind.Execute, Status = AiJobStatus.Failed });

        var forTask = await _service.GetJobsForTaskAsync(task.Id);
        forTask.Select(j => j.Kind).Should().Equal(AiJobKind.Execute, AiJobKind.Research);

        var unfinished = await _service.GetUnfinishedJobsAsync();
        unfinished.Should().ContainSingle().Which.Status.Should().Be(AiJobStatus.Suspended);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
