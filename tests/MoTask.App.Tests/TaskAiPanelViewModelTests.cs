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
    private const string Folder = @"C:\w\jobs\0001-t";

    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly IAiJobService _ai = Substitute.For<IAiJobService>();
    private readonly List<AiJob> _jobs = new();
    private readonly List<AiJobEvent> _events = new();
    private readonly List<string> _artifacts = new();
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
        _ai.GetEventsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<AiJobEvent>>(_events.Where(e => e.JobId == ci.ArgAt<int>(0)).OrderBy(e => e.Seq).ToList()));
        _ai.GetArtifactsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<string>>(_artifacts.ToList()));
        _ai.StartJobAsync(Arg.Any<int>(), Arg.Any<AiJobKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var job = new AiJob { Id = _jobs.Count + 1, TaskId = ci.Arg<int>(), Kind = ci.Arg<AiJobKind>(), Status = AiJobStatus.Running, Instruction = ci.Arg<string>(), WorkingDirectory = @"C:\w", JobFolder = Folder };
            _jobs.Add(job);
            return Task.FromResult(Result.Ok(job));
        });
        _ai.CompleteJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _ai.StopTrackingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _ai.ReopenTerminalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));

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

    private static AiJobSnapshot Snapshot(int jobId, AiJobStatus status, int taskId = 10, int turns = 1,
        AiJobKind kind = AiJobKind.Execute, string? error = null)
        => new(jobId, taskId, kind, status, turns, error, @"C:\w", Folder);

    private static AiJobSnapshot Running(int jobId, int taskId = 10, int turns = 1, AiJobKind kind = AiJobKind.Execute)
        => Snapshot(jobId, AiJobStatus.Running, taskId, turns, kind);

    private static AiJob Job(int id, AiJobStatus status, int taskId = 10)
        => new() { Id = id, TaskId = taskId, Kind = AiJobKind.Execute, Status = status, WorkingDirectory = @"C:\w", JobFolder = Folder };

    [Fact]
    public async Task WithoutJobs_CanStart_AndHasNothingToShow()
    {
        var ai = await OpenAsync();

        ai.HasJob.Should().BeFalse();
        ai.CanStart.Should().BeTrue();
        ai.CanControl.Should().BeFalse();
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
        ai.CanControl.Should().BeTrue();
        ai.CanStart.Should().BeFalse();
        ai.StatusText.Should().Be(Strings.AiStatusExecuting);
        ai.JobFolder.Should().Be(Folder);
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
    public async Task JobChanged_ForThisTask_AppendsLog_AndUpdatesBadge()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        var ai = await OpenAsync();
        var toolUse = new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.ToolUse, ToolName = "Write",
            Payload = """{"hook_event_name":"PostToolUse","tool_name":"Write","tool_input":{"file_path":"C:\\w\\jobs\\0001-t\\artifacts\\report.md"}}""",
        };

        RaiseChanged(Running(1, turns: 2), toolUse);

        ai.Log.Should().ContainSingle().Which.Text
            .Should().Be(string.Format(Strings.AiLogToolUseFormat, "Write", @"C:\w\jobs\0001-t\artifacts\report.md"));
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
    public async Task JobChanged_WaitingForInput_ShowsTheInputBadge_AndKeepsTheControls()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        var ai = await OpenAsync();

        RaiseChanged(Snapshot(1, AiJobStatus.WaitingForInput));

        ai.IsWaitingForInput.Should().BeTrue();
        ai.CanControl.Should().BeTrue("入力待ちでもまだ追跡している");
        ai.CanStart.Should().BeFalse();
        ai.StatusText.Should().Be(Strings.AiStatusWaitingForInput);
        _vm.Detail!.Card.IsWaitingForInput.Should().BeTrue();
    }

    [Fact]
    public async Task JobChanged_TurnEnded_ShowsTheLastAssistantMessageAsTheResult()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        var ai = await OpenAsync();
        var stop = new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.TurnEnded,
            Payload = """{"hook_event_name":"Stop","last_assistant_message":"レポートを書きました"}""",
        };

        RaiseChanged(Snapshot(1, AiJobStatus.WaitingForInput), stop);

        ai.ResultText.Should().Be("レポートを書きました");
    }

    [Fact]
    public async Task JobChanged_Succeeded_ClearsBadge_AndAllowsANewJob()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        _events.Add(new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.TurnEnded,
            Payload = """{"hook_event_name":"Stop","last_assistant_message":"レポートを書きました"}""",
        });
        var ai = await OpenAsync();
        _jobs[0].Status = AiJobStatus.Succeeded;
        _jobs[0].NumTurns = 3;

        RaiseChanged(Snapshot(1, AiJobStatus.Succeeded, turns: 3));
        await ai.PendingLoad;

        ai.StatusText.Should().Be(Strings.AiStatusSucceeded);
        ai.ResultText.Should().Be("レポートを書きました");
        ai.IsActive.Should().BeFalse();
        ai.CanControl.Should().BeFalse();
        ai.CanStart.Should().BeTrue();
        _vm.Detail!.Card.HasAiBadge.Should().BeFalse();
    }

    /// <summary>
    /// AiJobService は JobChanged を上げる前に BoardService でタスクを確認待ちへ動かす。
    /// SyncCardsFromModel の VM 再利用は列ごとなので、先にカード VM を移し替えないと移動先が
    /// 別インスタンスを作り、開いている詳細パネルのカードが孤児になって選択も外れる。
    /// </summary>
    [Fact]
    public async Task JobChanged_Succeeded_KeepsTheCardViewModel_WhenTheTaskMovedToReview()
    {
        var board = TestBoards.Sample();
        var review = new Column { Id = 4, BoardId = 1, Name = "確認待ち", Order = 3, Role = ColumnRole.Review };
        board.Columns.Add(review);
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(board)));
        _jobs.Add(Job(1, AiJobStatus.Running));
        var ai = await OpenAsync();
        var card = _vm.Detail!.Card;

        TestBoards.Move(board.Columns[0], review, 10, int.MaxValue);
        _jobs[0].Status = AiJobStatus.Succeeded;

        RaiseChanged(Snapshot(1, AiJobStatus.Succeeded, turns: 3));
        await ai.PendingLoad;

        var reviewColumn = _vm.Columns.Single(c => c.Id == 4);
        reviewColumn.AllCards.Should().ContainSingle().Which.Should().BeSameAs(card, "カード VM の同一性が保たれる");
        reviewColumn.Cards.Should().ContainSingle().Which.Should().BeSameAs(card);
        _vm.Columns.Single(c => c.Id == 1).AllCards.Select(c => c.Id).Should().Equal(11);
        reviewColumn.SelectedCard.Should().BeSameAs(card, "移動後も選択が残る");
        _vm.SelectedCard.Should().BeSameAs(card);
        _vm.Detail!.Card.Should().BeSameAs(card);
    }

    /// <summary>
    /// LoadAsync は Log.Clear() → await → Log.Add() なので、重なると 2 倍に増える。
    /// 実際にこうなる: StartJobAsync が同期的に JobChanged を上げるため、ConfirmStartAsync が
    /// まだ待っている間に OnJobChanged 経由の読み込みが始まり、その後 ConfirmStartAsync も読み込む。
    /// substitute は同期完了するので、照会をゲートで止めて本当に重ねる。
    /// </summary>
    [Fact]
    public async Task OverlappingLoads_DoNotDoubleTheLog()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        _events.Add(new AiJobEvent
        {
            JobId = 1, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.SessionStarted,
            Payload = """{"hook_event_name":"SessionStart","source":"startup"}""",
        });
        _artifacts.Add(@"C:\w\jobs\0001-t\artifacts\report.md");
        var ai = await OpenAsync();

        var gate = new TaskCompletionSource();
        async Task<IReadOnlyList<AiJobEvent>> Gated(int jobId)
        {
            await gate.Task;
            return _events.Where(e => e.JobId == jobId).OrderBy(e => e.Seq).ToList();
        }
        _ai.GetEventsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci => Gated(ci.ArgAt<int>(0)));

        var first = ai.LoadAsync();
        var second = ai.LoadAsync();
        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        ai.Log.Should().ContainSingle("重なった読み込みでログが二重にならない");
        ai.Artifacts.Should().ContainSingle("成果物も二重にならない");
    }

    [Fact]
    public async Task JobChanged_Failed_ShowsErrorMessage()
    {
        _jobs.Add(Job(1, AiJobStatus.Running));
        var ai = await OpenAsync();
        _jobs[0].Status = AiJobStatus.Failed;
        _jobs[0].ErrorMessage = "だめでした";

        RaiseChanged(Snapshot(1, AiJobStatus.Failed, error: "だめでした"));
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
    public async Task Complete_CallsService_AndReloads()
    {
        _jobs.Add(Job(5, AiJobStatus.WaitingForInput));
        var ai = await OpenAsync();

        ai.CanControl.Should().BeTrue();
        await ai.CompleteCommand.ExecuteAsync(null);

        await _ai.Received(1).CompleteJobAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopTracking_CallsService()
    {
        _jobs.Add(Job(5, AiJobStatus.Running));
        var ai = await OpenAsync();

        await ai.StopTrackingCommand.ExecuteAsync(null);

        await _ai.Received(1).StopTrackingAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReopenTerminal_CallsService()
    {
        _jobs.Add(Job(5, AiJobStatus.WaitingForInput));
        var ai = await OpenAsync();

        await ai.ReopenTerminalCommand.ExecuteAsync(null);

        await _ai.Received(1).ReopenTerminalAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Artifacts_ComeFromTheService_NotFromTheEventLog()
    {
        _jobs.Add(Job(1, AiJobStatus.Succeeded));
        _artifacts.Add(@"C:\w\jobs\0001-t\artifacts\report.md");
        var ai = await OpenAsync();

        await _ai.Received(1).GetArtifactsAsync(1, Arg.Any<CancellationToken>());
        ai.Artifacts.Should().ContainSingle().Which.Path.Should().Be(@"C:\w\jobs\0001-t\artifacts\report.md");
        ai.Artifacts[0].FileName.Should().Be("report.md");
    }

    [Fact]
    public async Task OpenArtifact_WorkingDirectory_AndJobFolder_UseOpenPath()
    {
        _jobs.Add(Job(1, AiJobStatus.Succeeded));
        _artifacts.Add(@"C:\w\jobs\0001-t\artifacts\report.md");
        var ai = await OpenAsync();

        ai.OpenArtifactCommand.Execute(ai.Artifacts.Single());
        ai.OpenWorkingDirectoryCommand.Execute(null);
        ai.OpenJobFolderCommand.Execute(null);

        _opened.Should().Equal(@"C:\w\jobs\0001-t\artifacts\report.md", @"C:\w", Folder);
    }

    [Fact]
    public async Task Load_RestoresBadgesFromUnfinishedJobs()
    {
        _jobs.Add(new AiJob { Id = 1, TaskId = 11, Kind = AiJobKind.Research, Status = AiJobStatus.WaitingForInput, WorkingDirectory = @"C:\w", JobFolder = Folder });
        _jobs.Add(new AiJob { Id = 2, TaskId = 12, Kind = AiJobKind.Execute, Status = AiJobStatus.Running, WorkingDirectory = @"C:\w", JobFolder = Folder });
        _ai.TurnCountOf(2).Returns(6);

        await _vm.LoadAsync();

        var cards = _vm.Columns.SelectMany(c => c.AllCards).ToDictionary(c => c.Id);
        cards[11].AiBadgeText.Should().Be(Strings.AiStatusWaitingForInput);
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

    /// <summary>ジョブが 1 件走っている状態のパネル。既存の OpenAsync と同じ手順で開く。</summary>
    private async Task<TaskAiPanelViewModel> PanelWithRunningJobAsync()
    {
        _jobs.Add(new AiJob
        {
            Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running,
            WorkingDirectory = @"C:\w", JobFolder = @"C:\w\jobs\0001-a",
        });
        return await OpenAsync();
    }

    [Fact]
    public async Task Panel_OffersTheControlsWhileTheJobIsTracked()
    {
        var panel = await PanelWithRunningJobAsync();

        panel.CanControl.Should().BeTrue();
        panel.CanStart.Should().BeFalse("追跡中は新しい依頼を受けない");
        panel.JobFolder.Should().Be(@"C:\w\jobs\0001-a");
    }

    [Fact]
    public async Task OpenJobFolder_OpensTheFolderOfTheCurrentJob()
    {
        var panel = await PanelWithRunningJobAsync();

        panel.OpenJobFolderCommand.Execute(null);

        _opened.Should().ContainSingle().Which.Should().Be(@"C:\w\jobs\0001-a");
    }

    [Fact]
    public async Task Panel_ListsTheArtifactsTheServiceReports()
    {
        _ai.GetArtifactsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { @"C:\w\jobs\0001-a\artifacts\report.md" }));

        var panel = await PanelWithRunningJobAsync();

        panel.Artifacts.Should().ContainSingle().Which.FileName.Should().Be("report.md");
    }

    private static AiJobEvent Event(int seq, AiJobEventKind kind, string toolName)
        => new() { JobId = 1, Seq = seq, At = DateTime.UtcNow, Kind = kind, ToolName = toolName, Payload = "" };

    [Fact]
    public async Task Panel_ShowsOnlyTheLastThreeLogLines()
    {
        _ai.GetEventsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AiJobEvent[]
            {
                Event(1, AiJobEventKind.ToolUse, "Read"),
                Event(2, AiJobEventKind.ToolUse, "Grep"),
                Event(3, AiJobEventKind.ToolUse, "Bash"),
                Event(4, AiJobEventKind.ToolUse, "Write"),
                Event(5, AiJobEventKind.ToolUse, "Edit"),
            });

        var panel = await PanelWithRunningJobAsync();

        panel.Log.Should().HaveCount(3);
    }
}
