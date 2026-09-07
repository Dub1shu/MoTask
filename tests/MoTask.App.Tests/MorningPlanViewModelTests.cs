using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class MorningPlanViewModelTests
{
    /// <summary>呼ばれた操作を記録するだけの偽サービス。</summary>
    private sealed class FakeMorningService : IMorningService
    {
        public event EventHandler<MorningRunChangedEventArgs>? RunChanged;

        public MorningRun? Current { get; set; }
        public List<TriageCandidate> Queue { get; } = new();
        public List<string> Calls { get; } = new();
        public Result<MorningRun> StartResult { get; set; } = Result.Ok(new MorningRun());
        public Result<TaskItem> RegisterResult { get; set; } = Result.Ok(new TaskItem { Id = 1 });
        public CandidateDecision? LastDecision { get; private set; }

        public void Raise(MorningRun run, string? warning = null, bool candidates = false)
            => RunChanged?.Invoke(this, new MorningRunChangedEventArgs(
                new MorningRunSnapshot(run.Id, run.Date, run.Status, 0, run.ErrorMessage, run.JobFolder),
                warning, candidates));

        public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TriageCandidate>>(Queue.ToList());

        public Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public int TurnCountOf(int runId) => 0;

        public Task<Result<MorningRun>> StartAsync(CancellationToken ct = default)
        {
            Calls.Add("Start");
            if (StartResult.IsSuccess) Current = StartResult.Value;
            return Task.FromResult(StartResult);
        }

        public Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
        {
            Calls.Add("Complete");
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
        {
            Calls.Add("StopTracking");
            return Task.FromResult(Result.Ok());
        }

        public Task RecoverOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
        {
            Calls.Add("Register");
            LastDecision = decision;
            if (RegisterResult.IsSuccess) Queue.RemoveAll(c => c.Id == decision.CandidateId);
            return Task.FromResult(RegisterResult);
        }

        public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
        {
            Calls.Add($"Merge:{targetTaskId}");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Postpone");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Reject");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }
    }

    private readonly FakeMorningService _service = new();
    private readonly IBoardService _boards = Substitute.For<IBoardService>();
    private readonly MorningPlanViewModel _vm;
    private readonly List<string> _opened = new();

    public MorningPlanViewModelTests()
    {
        // 未着手(1) / 進行中(2) / 完了(3)。完了列は登録先に出さない。
        _boards.GetBoardAsync().Returns(Task.FromResult(Result.Ok(TestBoards.Sample())));
        _vm = new MorningPlanViewModel(_service, _boards) { OpenPath = _opened.Add };
    }

    [Fact]
    public async Task Load_OffersEveryColumnExceptDone()
    {
        await _vm.LoadAsync();

        _vm.ColumnChoices.Select(c => c.Id).Should().Equal(1, 2);
        _vm.EditColumnId.Should().Be(1, "既定は先頭の列");
    }

    private TriageCandidate Candidate(int id = 1, TriageAction suggested = TriageAction.Register)
        => new()
        {
            Id = id, MorningRunId = 1, ExternalId = $"outlook:{id:000}", Source = "Outlook",
            From = "山本さん", Title = "請求先情報を更新する", Evidence = "「9月8日までに」",
            Link = "https://outlook.office.com/x", Reasoning = "依頼が明確",
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
            SuggestedAction = suggested, SuggestedMergeTaskId = suggested == TriageAction.Merge ? 12 : null,
        };

    private MorningRun IngestedRun() => new()
    {
        Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Ingested,
        JobFolder = @"C:\work\morning\0001-2026-09-07", PlanJson = "{\"groups\":[]}",
    };

    [Fact]
    public async Task Load_WithNoRun_OffersToStart()
    {
        await _vm.LoadAsync();

        _vm.CanStart.Should().BeTrue();
        _vm.IsRunning.Should().BeFalse();
        _vm.Candidates.Should().BeEmpty();
        _vm.HasNoCandidates.Should().BeFalse("まだ一度も走らせていないので『候補なし』ではない");
    }

    [Fact]
    public async Task Load_WithCandidates_SelectsTheFirstAndFillsTheEditor()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        _service.Queue.Add(Candidate(2));

        await _vm.LoadAsync();

        _vm.Candidates.Should().HaveCount(2);
        _vm.Selected!.CandidateId.Should().Be(1);
        _vm.EditTitle.Should().Be("請求先情報を更新する", "人が編集してから登録できる");
        // 期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）
        _vm.EditDueDate.Should().Be(new DateTime(2026, 9, 8));
        _vm.EditProjectName.Should().Be("顧客A");
        _vm.PositionText.Should().Be("1 / 2");
        _vm.CanStart.Should().BeFalse("片づけ終わるまでは次の実行を始めない");
    }

    [Fact]
    public async Task Load_WithAnIngestedRunAndNoCandidates_SaysSo()
    {
        _service.Current = IngestedRun();

        await _vm.LoadAsync();

        _vm.HasNoCandidates.Should().BeTrue("候補 0 件は失敗ではない（仕様 §11）");
        _vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task Load_WithAFailedRun_ShowsTheReasonAndTheFolder()
    {
        _service.Current = new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Failed,
            ErrorMessage = "読み取れませんでした", JobFolder = @"C:\work\morning\0001-2026-09-07",
        };

        await _vm.LoadAsync();

        _vm.IsFailed.Should().BeTrue();
        _vm.ErrorMessage.Should().Be("読み取れませんでした");
        _vm.OpenJobFolderCommand.Execute(null);
        _opened.Should().Equal(@"C:\work\morning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_ShowsTheError_WhenTheServiceRefuses()
    {
        _service.StartResult = Result.Fail<MorningRun>("claude が見つかりません");

        await _vm.LoadAsync();
        await _vm.StartCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("claude が見つかりません");
        _service.Calls.Should().Equal("Start");
    }

    [Fact]
    public async Task Register_PassesTheEditedValues_AndMovesToTheNextCandidate()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        _service.Queue.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.EditTitle = "書き換えた題名";
        _vm.EditDueDate = new DateTime(2026, 9, 10);
        _vm.EditProjectName = "別プロジェクト";
        _vm.EditColumnId = 2;

        await _vm.RegisterCommand.ExecuteAsync(null);

        _service.LastDecision.Should().Be(new CandidateDecision(1, "書き換えた題名",
            new DateOnly(2026, 9, 10), "別プロジェクト", 2));
        _vm.Candidates.Should().ContainSingle();
        _vm.Selected!.CandidateId.Should().Be(2);
        _vm.PositionText.Should().Be("1 / 1");
    }

    [Fact]
    public async Task Register_KeepsTheCandidate_WhenTheServiceFails()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();
        _service.RegisterResult = Result.Fail<TaskItem>("列が見つかりません");

        await _vm.RegisterCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("列が見つかりません");
        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Merge_UsesTheSuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate(suggested: TriageAction.Merge));
        await _vm.LoadAsync();

        _vm.Selected!.CanMerge.Should().BeTrue();
        await _vm.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:12");
    }

    [Fact]
    public async Task Merge_IsNotOfferedWithoutASuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        _vm.Selected!.CanMerge.Should().BeFalse("統合先が無ければ 2 本目の計画で選ばせる。今は出さない");
    }

    [Theory]
    [InlineData("Postpone")]
    [InlineData("Reject")]
    public async Task PostponeAndReject_TakeTheCandidateOutOfTheQueue(string call)
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        if (call == "Postpone") await _vm.PostponeCommand.ExecuteAsync(null);
        else await _vm.RejectCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain(call);
        _vm.Candidates.Should().BeEmpty();
        _vm.Selected.Should().BeNull();
        _vm.HasNoCandidates.Should().BeTrue();
    }

    [Fact]
    public async Task OpenLink_OpensTheCandidateLink()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        _vm.OpenLinkCommand.Execute(null);

        _opened.Should().Equal("https://outlook.office.com/x");
    }

    [Fact]
    public async Task Load_WithNoRun_MentionsThePreviousOne()
    {
        await _vm.LoadAsync();
        _vm.LastRunText.Should().BeEmpty("一度も走らせていなければ『前回』は無い");

        _service.Current = new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 5), Status = MorningRunStatus.Ingested,
        };
        await _vm.LoadAsync();

        _vm.LastRunText.Should().Be(string.Format(Strings.MorningLastRunFormat, "9/5"));
    }

    [Fact]
    public async Task RunChanged_ForARunningRun_SwitchesToTheProgressView()
    {
        await _vm.LoadAsync();

        _service.Raise(new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Running,
        });

        _vm.IsRunning.Should().BeTrue();
        _vm.CanStart.Should().BeFalse();
        _vm.CanControl.Should().BeTrue("『完了にする』『追跡をやめる』を出す");
        _vm.ProgressText.Should().Be(string.Format(Strings.MorningTurnsFormat, 0),
            "仕様 §11『実行中』はターン数を出す");
    }

    [Fact]
    public async Task RunChanged_ShowsTheWarningAboutDiscardedLines()
    {
        await _vm.LoadAsync();

        _service.Raise(IngestedRun(), warning: "5 件のうち 1 件は読み取れませんでした");

        _vm.WarningMessage.Should().Be("5 件のうち 1 件は読み取れませんでした");
    }

    [Fact]
    public async Task RunChanged_WithCandidates_ReloadsTheQueue()
    {
        await _vm.LoadAsync();
        var run = IngestedRun();
        _service.Current = run;
        _service.Queue.Add(Candidate());

        _service.Raise(run, candidates: true);
        await _vm.PendingLoad;

        _vm.Candidates.Should().ContainSingle();
    }
}
