using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class TaskAiPanelViewModelTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly IAiJobService _ai = Substitute.For<IAiJobService>();
    private readonly List<AiJob> _jobs = new();
    private readonly List<AiJobEvent> _events = new();
    private readonly List<string> _opened = new();
    private readonly BoardViewModel _vm;

    public TaskAiPanelViewModelTests()
    {
        var board = TestBoards.Sample();
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>()));

        _ai.GetUnfinishedJobsAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<AiJob>>(_jobs.Where(j => !j.Status.IsTerminal()).ToList()));
        _ai.GetJobsForTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<AiJob>>(_jobs.Where(j => j.TaskId == ci.Arg<int>()).OrderByDescending(j => j.Id).ToList()));
        _ai.GetEventsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<AiJobEvent>>(_events.Where(e => e.JobId == ci.Arg<int>()).OrderBy(e => e.Seq).ToList()));
        _ai.StartJobAsync(Arg.Any<int>(), Arg.Any<AiJobKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var job = new AiJob { Id = _jobs.Count + 1, TaskId = ci.Arg<int>(), Kind = ci.Arg<AiJobKind>(), Status = AiJobStatus.Running, Instruction = ci.Arg<string>(), WorkingDirectory = @"C:\w" };
            _jobs.Add(job);
            return Task.FromResult(Result.Ok(job));
        });
        _ai.StopJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _ai.ResumeJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));

        _vm = new BoardViewModel(_service, new TestClock(), _ai) { OpenPath = _opened.Add };
    }

    private async Task<TaskAiPanelViewModel> OpenAsync(int taskId = 10)
    {
        await _vm.LoadAsync();
        var card = _vm.Columns.SelectMany(c => c.AllCards).Single(c => c.Id == taskId);
        _vm.SelectCard(card);
        var ai = _vm.Detail!.Ai;
        await ai.PendingLoad;
        return ai;
    }

    private void RaiseChanged(AiJobSnapshot job, AiJobEvent? newEvent = null, string? warning = null)
        => _ai.JobChanged += Raise.EventWith(_ai, new AiJobChangedEventArgs(job, newEvent, warning));

    private static AiJobSnapshot Running(int jobId, int taskId = 10, int turns = 1, AiJobKind kind = AiJobKind.Execute)
        => new(jobId, taskId, kind, AiJobStatus.Running, turns, null, null, @"C:\w");

    [Fact]
    public async Task WithoutJobs_CanStart_AndHasNothingToShow()
    {
        var ai = await OpenAsync();

        ai.HasJob.Should().BeFalse();
        ai.CanStart.Should().BeTrue();
        ai.IsComposing.Should().BeFalse();
        ai.Log.Should().BeEmpty();
        ai.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task BeginResearch_ComposesDefaultInstruction_FromTitleAndDescription()
    {
        var ai = await OpenAsync();

        ai.BeginResearchCommand.Execute(null);

        ai.IsComposing.Should().BeTrue();
        ai.ComposingKind.Should().Be(AiJobKind.Research);
        ai.ComposingTitle.Should().Be(Strings.AiComposeResearch);
        ai.Instruction.Should().Contain("請求先情報を更新する");
    }

    [Fact]
    public async Task ConfirmStart_CallsService_ReloadsPanel_AndSetsCardBadge()
    {
        var ai = await OpenAsync();
        ai.BeginExecuteCommand.Execute(null);
        ai.Instruction = "請求先を最新にしてください";

        await ai.ConfirmStartCommand.ExecuteAsync(null);

        await _ai.Received(1).StartJobAsync(10, AiJobKind.Execute, "請求先を最新にしてください", Arg.Any<CancellationToken>());
        ai.IsComposing.Should().BeFalse();
        ai.HasJob.Should().BeTrue();
        ai.IsActive.Should().BeTrue();
        ai.CanStart.Should().BeFalse();
        ai.StatusText.Should().Be(Strings.AiStatusExecuting);
        _vm.Detail!.Card.HasAiBadge.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmStart_WhenServiceRejects_ShowsBanner_AndStaysComposing()
    {
        _ai.StartJobAsync(Arg.Any<int>(), Arg.Any<AiJobKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail<AiJob>(Messages.ClaudeNotFound)));
        var ai = await OpenAsync();
        ai.BeginExecuteCommand.Execute(null);

        await ai.ConfirmStartCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.ClaudeNotFound);
        ai.IsComposing.Should().BeTrue();
        ai.HasJob.Should().BeFalse();
    }

    [Fact]
    public async Task CancelCompose_ClosesTheEditor()
    {
        var ai = await OpenAsync();
        ai.BeginExecuteCommand.Execute(null);
        ai.CancelComposeCommand.Execute(null);
        ai.IsComposing.Should().BeFalse();
    }

    [Fact]
    public async Task JobChanged_ForThisTask_AppendsLog_AndArtifacts_AndUpdatesBadge()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        var ai = await OpenAsync();
        var write = new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.ToolUse, ToolName = "Write",
            Payload = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\w\\report.md","content":"x"}}]}}""",
        };

        RaiseChanged(Running(1, turns: 2), write);

        ai.Log.Should().ContainSingle().Which.Text.Should().Be(string.Format(Strings.AiLogToolUseFormat, "Write", @"C:\w\report.md"));
        ai.Artifacts.Should().ContainSingle().Which.Path.Should().Be(@"C:\w\report.md");
        ai.Artifacts[0].FileName.Should().Be("report.md");
        _vm.Detail!.Card.AiBadgeText.Should().Be(string.Format(Strings.AiBadgeTurnsFormat, Strings.AiStatusExecuting, 2));
    }

    [Fact]
    public async Task JobChanged_ForAnotherTask_IsIgnoredByThePanel_ButBadgesThatCard()
    {
        var ai = await OpenAsync();

        RaiseChanged(Running(7, taskId: 11, turns: 4, kind: AiJobKind.Research));

        ai.HasJob.Should().BeFalse();
        ai.Log.Should().BeEmpty();
        var other = _vm.Columns.SelectMany(c => c.AllCards).Single(c => c.Id == 11);
        other.AiBadgeText.Should().Be(string.Format(Strings.AiBadgeTurnsFormat, Strings.AiStatusResearching, 4));
    }

    [Fact]
    public async Task JobChanged_AwaitingApproval_FlagsTheCard()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        var ai = await OpenAsync();

        RaiseChanged(new AiJobSnapshot(1, 10, AiJobKind.Execute, AiJobStatus.AwaitingApproval, 1, null, null, @"C:\w"));

        _vm.Detail!.Card.IsAwaitingApproval.Should().BeTrue();
        ai.StatusText.Should().Be(Strings.AiStatusAwaiting);
    }

    [Fact]
    public async Task JobChanged_Succeeded_ShowsCostAndResult_ClearsBadge_AndAllowsANewJob()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        _events.Add(new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.Result,
            Payload = """{"type":"result","is_error":false,"num_turns":3,"total_cost_usd":0.25,"result":"レポートを書きました"}""",
        });
        var ai = await OpenAsync();
        _jobs[0].Status = AiJobStatus.Succeeded;
        _jobs[0].NumTurns = 3;
        _jobs[0].TotalCostUsd = 0.25m;

        RaiseChanged(new AiJobSnapshot(1, 10, AiJobKind.Execute, AiJobStatus.Succeeded, 3, 0.25m, null, @"C:\w"));
        await ai.PendingLoad;

        ai.StatusText.Should().Be(Strings.AiStatusSucceeded);
        ai.CostText.Should().Be(string.Format(Strings.AiCostFormat, 0.25m, 3));
        ai.ResultText.Should().Be("レポートを書きました");
        ai.IsActive.Should().BeFalse();
        ai.CanStart.Should().BeTrue();
        _vm.Detail!.Card.HasAiBadge.Should().BeFalse();
    }

    [Fact]
    public async Task JobChanged_Failed_ShowsErrorMessage()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        var ai = await OpenAsync();
        _jobs[0].Status = AiJobStatus.Failed;
        _jobs[0].ErrorMessage = "だめでした";

        RaiseChanged(new AiJobSnapshot(1, 10, AiJobKind.Execute, AiJobStatus.Failed, 1, null, "だめでした", @"C:\w"));
        await ai.PendingLoad;

        ai.StatusText.Should().Be(Strings.AiStatusFailed);
        ai.ErrorMessage.Should().Be("だめでした");
    }

    [Fact]
    public async Task JobChanged_WithWarning_ShowsBanner()
    {
        var ai = await OpenAsync();
        RaiseChanged(Running(1), warning: Messages.NoReviewColumn);
        _vm.BannerMessage.Should().Be(Messages.NoReviewColumn);
    }

    [Fact]
    public async Task Stop_CallsService()
    {
        _jobs.Add(new AiJob { Id = 5, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        var ai = await OpenAsync();

        ai.IsActive.Should().BeTrue();
        await ai.StopCommand.ExecuteAsync(null);

        await _ai.Received(1).StopJobAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Suspended_OffersResume_AndResumeCallsService()
    {
        _jobs.Add(new AiJob { Id = 5, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Suspended, WorkingDirectory = @"C:\w" });
        var ai = await OpenAsync();

        ai.IsSuspended.Should().BeTrue();
        ai.CanStart.Should().BeFalse("中断中は再開か、まず停止");
        ai.StatusText.Should().Be(Strings.AiStatusSuspended);
        await ai.ResumeCommand.ExecuteAsync(null);

        await _ai.Received(1).ResumeJobAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenArtifact_AndOpenWorkingDirectory_UseOpenPath()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Succeeded, WorkingDirectory = @"C:\w" });
        _events.Add(new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.ToolUse, ToolName = "Write",
            Payload = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\w\\report.md","content":"x"}}]}}""",
        });
        var ai = await OpenAsync();

        ai.OpenArtifactCommand.Execute(ai.Artifacts.Single());
        ai.OpenWorkingDirectoryCommand.Execute(null);

        _opened.Should().Equal(@"C:\w\report.md", @"C:\w");
    }

    [Fact]
    public async Task Load_RestoresBadgesFromUnfinishedJobs()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 11, Kind = AiJobKind.Research, Status = AiJobStatus.Suspended, WorkingDirectory = @"C:\w" });
        _jobs.Add(new AiJob { Id = 2, TaskId = 12, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w" });
        _ai.TurnCountOf(2).Returns(6);

        await _vm.LoadAsync();

        var cards = _vm.Columns.SelectMany(c => c.AllCards).ToDictionary(c => c.Id);
        cards[11].AiBadgeText.Should().Be(Strings.AiStatusSuspended);
        cards[12].AiBadgeText.Should().Be(string.Format(Strings.AiBadgeTurnsFormat, Strings.AiStatusExecuting, 6));
        cards[10].HasAiBadge.Should().BeFalse();
    }

    [Fact]
    public async Task DeletedTask_CannotStart()
    {
        var board = TestBoards.WithDeletedCard();
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(board)));
        var ai = await OpenAsync(11);
        ai.CanStart.Should().BeFalse();
    }
}
