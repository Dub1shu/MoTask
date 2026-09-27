using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using MoTask.Core.Services;
using MoTask.App.Tests.Fakes;
using Xunit;

namespace MoTask.App.Tests;

public class PlanViewModelTests
{
    private readonly FakePlanningService _service = new();
    private readonly FakeBoardService _boards = new();
    private readonly PlanViewModel _vm;
    private readonly List<string> _opened = new();

    public PlanViewModelTests()
    {
        // 未着手(1) / 進行中(2) / 完了(3)。完了列は登録先に出さない。
        _boards.Board = TestBoards.Sample();
        _boards.Projects = new[] { TestBoards.ProjectA() };
        _vm = new PlanViewModel(_service, _boards) { OpenPath = _opened.Add };
    }

    [Fact]
    public async Task Load_OffersEveryColumnExceptDone()
    {
        await _vm.LoadAsync();

        _vm.Triage.ColumnChoices.Select(c => c.Id).Should().Equal(1, 2);
        _vm.Triage.EditColumnId.Should().Be(1, "既定は先頭の列");
    }

    private TriageCandidate Candidate(int id = 1, TriageAction suggested = TriageAction.Register)
        => new()
        {
            Id = id, PlanningRunId = 1, ExternalId = $"outlook:{id:000}", Source = "Outlook",
            From = "山本さん", Title = "請求先情報を更新する", Evidence = "「9月8日までに」",
            Link = "https://outlook.office.com/x", Reasoning = "依頼が明確",
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
            SuggestedAction = suggested, SuggestedMergeTaskId = suggested == TriageAction.Merge ? 12 : null,
        };

    private PlanningRun IngestedRun() => new()
    {
        Id = 1, Date = new DateOnly(2026, 9, 7), Status = PlanningRunStatus.Ingested,
        JobFolder = @"C:\work\planning\0001-2026-09-07", PlanJson = "{\"groups\":[]}",
    };

    /// <summary>TestBoards.Sample のタスク 10 / 12 と、候補 outlook:001 を指す計画。</summary>
    private PlanningRun IngestedRunWithPlan(string firstThing = "{\"taskId\":10,\"reason\":\"期限が一番近い\"}",
        string today = "{\"taskId\":10},{\"externalId\":\"outlook:001\"},{\"taskId\":12}")
    {
        var run = IngestedRun();
        run.PlanJson = $"{{\"date\":\"2026-09-07\",\"firstThing\":{firstThing},\"groups\":[{{\"key\":\"today\",\"items\":[{today}]}}]}}";
        return run;
    }

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
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));

        await _vm.LoadAsync();

        _vm.Candidates.Should().HaveCount(2);
        _vm.Selected!.CandidateId.Should().Be(1);
        _vm.Triage.EditTitle.Should().Be("請求先情報を更新する", "人が編集してから登録できる");
        // 期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）
        _vm.Triage.EditDueDate.Should().Be(new DateTime(2026, 9, 8));
        _vm.Triage.EditProjectName.Should().Be("顧客A");
        _vm.Triage.PositionText.Should().Be("1 / 2");
        _vm.CanStart.Should().BeFalse("片づけ終わるまでは次の実行を始めない");
    }

    [Fact]
    public async Task Load_WithAnIngestedRunAndNoCandidates_SaysSo()
    {
        _service.Current = IngestedRun();

        await _vm.LoadAsync();

        _vm.HasNoCandidates.Should().BeTrue("候補 0 件は失敗ではない（仕様 §11）");
        _vm.CanStart.Should().BeTrue();
        _vm.HasNoPlanYet.Should().BeFalse(
            "取り込み済みなら候補が 0 件でもこの計画は存在する（仕様 §4）。" +
            "『今日の計画はまだありません』と『候補はありませんでした』を同時に出さない");
    }

    [Fact]
    public async Task Load_WithAFailedRun_ShowsTheReasonAndTheFolder()
    {
        _service.Current = new PlanningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = PlanningRunStatus.Failed,
            ErrorMessage = "読み取れませんでした", JobFolder = @"C:\work\planning\0001-2026-09-07",
        };

        await _vm.LoadAsync();

        _vm.IsFailed.Should().BeTrue();
        _vm.ErrorMessage.Should().Be("読み取れませんでした");
        _vm.OpenJobFolderCommand.Execute(null);
        _opened.Should().Equal(@"C:\work\planning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_ShowsTheError_WhenTheServiceRefuses()
    {
        _service.StartResult = Result.Fail<PlanningRun>("claude が見つかりません");

        await _vm.LoadAsync();
        await _vm.StartCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("claude が見つかりません");
        _service.Calls.Should().Equal("Start");
    }

    [Fact]
    public async Task Register_PassesTheEditedValues_AndMovesToTheNextCandidate()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.Triage.EditTitle = "書き換えた題名";
        _vm.Triage.EditDueDate = new DateTime(2026, 9, 10);
        _vm.Triage.EditProjectName = "別プロジェクト";
        _vm.Triage.EditColumnId = 2;

        await _vm.Triage.RegisterCommand.ExecuteAsync(null);

        _service.LastDecision.Should().Be(new CandidateDecision(1, "書き換えた題名",
            new DateOnly(2026, 9, 10), "別プロジェクト", 2));
        _vm.Candidates.Should().ContainSingle();
        _vm.Selected!.CandidateId.Should().Be(2);
        _vm.Triage.PositionText.Should().Be("1 / 1");
    }

    [Fact]
    public async Task Register_KeepsTheCandidate_WhenTheServiceFails()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();
        _service.RegisterResult = Result.Fail<TaskItem>("列が見つかりません");

        await _vm.Triage.RegisterCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("列が見つかりません");
        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Merge_PreselectsTheSuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate(suggested: TriageAction.Merge));
        await _vm.LoadAsync();

        _vm.Selected!.IsMergeSuggested.Should().BeTrue("候補キューの『統合が推奨』バッジ");
        _vm.Triage.EditMergeTargetId.Should().Be(12, "推薦された統合先が盤面にあるので初期選択");
        await _vm.Triage.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:12");
    }

    [Fact]
    public async Task Merge_LetsThePersonChooseATarget_WhenNothingWasSuggested()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.Triage.CanMerge.Should().BeFalse("推薦が無ければ未選択から始まる");
        _vm.Triage.MergeTargets.Select(t => t.Id).Should().Equal(new[] { 10, 11, 12 }, "完了列以外の未削除タスク");
        _vm.Triage.EditMergeTargetId = 11;
        await _vm.Triage.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:11");
    }

    [Theory]
    [InlineData("Postpone")]
    [InlineData("Reject")]
    public async Task PostponeAndReject_TakeTheCandidateOutOfTheQueue(string call)
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        if (call == "Postpone") await _vm.Triage.PostponeCommand.ExecuteAsync(null);
        else await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain(call);
        _vm.Candidates.Should().BeEmpty();
        _vm.Selected.Should().BeNull();
        _vm.IsPlanReady.Should().BeTrue();
        _vm.HasNoCandidates.Should().BeFalse("候補はあった。案内文は 0 件の日だけ");
    }

    [Fact]
    public async Task OpenLink_OpensTheCandidateLink()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.Triage.OpenLinkCommand.Execute(null);

        _opened.Should().Equal("https://outlook.office.com/x");
    }

    [Fact]
    public async Task Load_WithNoRun_MentionsThePreviousOne()
    {
        await _vm.LoadAsync();
        _vm.LastRunText.Should().BeEmpty("一度も走らせていなければ『前回』は無い");

        _service.Current = new PlanningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 5), Status = PlanningRunStatus.Ingested,
        };
        await _vm.LoadAsync();

        _vm.LastRunText.Should().Be(string.Format(Strings.PlanLastRunFormat, "9/5"));
    }

    [Fact]
    public async Task RunChanged_ForARunningRun_SwitchesToTheProgressView()
    {
        await _vm.LoadAsync();

        _service.Raise(new PlanningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = PlanningRunStatus.Running,
        });

        _vm.IsRunning.Should().BeTrue();
        _vm.CanStart.Should().BeFalse();
        _vm.CanControl.Should().BeTrue("『完了にする』『追跡をやめる』を出す");
        _vm.ProgressText.Should().Be(string.Format(Strings.PlanTurnsFormat, 0),
            "仕様 §11『実行中』はターン数を出す");
    }

    /// <summary>
    /// finding I3。仕様 §7: 右カラム(候補キュー＋計画)は実行前・実行中・失敗のときは空にする。
    /// HasNoPlanYet は取り込み済みなら候補 0 件でも false になるだけの値で、実行中はそれだけでは
    /// 右カラムを隠せないので、別の HasPlanView で見せる/隠すを決める。
    /// </summary>
    [Fact]
    public async Task HasPlanView_IsFalseWhileRunning_AndTrueOnceIngested()
    {
        await _vm.LoadAsync();

        _service.Raise(new PlanningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = PlanningRunStatus.Running,
        });
        _vm.HasPlanView.Should().BeFalse("実行中は右カラムを空にする（仕様 §7）");

        _service.Current = IngestedRun();
        await _vm.LoadAsync();
        _vm.HasPlanView.Should().BeTrue("取り込み済みなら右カラムを出す");
    }

    /// <summary>
    /// Raise は発火時点のスナップショットを積む。届くのは非同期(SynchronizationContext.Post)なので、
    /// ゲートが Ingested を確定させた後に、それより前の Running スナップショットが遅れて届くことが
    /// ある。ビューモデルがそれを鵜呑みにして「実行中」へ戻ってしまうと、DbContext が追跡する
    /// このエンティティへ Running を書き戻すことになり(共有 DbContext なので)次の SaveChangesAsync
    /// で本当に Running が永続化されてしまう(仕様の finding 3)。ここでは、そのエンティティ自体が
    /// 書き換えられていないことまで確かめる。
    /// </summary>
    [Fact]
    public async Task RunChanged_WithAStaleSnapshot_DoesNotReviveOrMutateTheFinishedRun()
    {
        var run = IngestedRun();
        _service.Current = run;
        await _vm.LoadAsync();

        _service.Raise(new PlanningRun { Id = run.Id, Date = run.Date, Status = PlanningRunStatus.Running });

        _vm.IsRunning.Should().BeFalse("届いたのはもう終わった実行より前の古いスナップショット");
        _vm.CanStart.Should().BeTrue();
        run.Status.Should().Be(PlanningRunStatus.Ingested,
            "ビューモデルはリポジトリが返したエンティティを書き換えない(共有 DbContext を汚さない)");
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
        _service.Candidates.Add(Candidate());

        _service.Raise(run, candidates: true);
        await _vm.PendingLoad;

        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task ErrorMessage_ClearsOnTheNextSuccessfulAction()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();
        _service.RegisterResult = Result.Fail<TaskItem>("列が見つかりません");
        await _vm.Triage.RegisterCommand.ExecuteAsync(null);
        _vm.ErrorMessage.Should().Be("列が見つかりません");

        await _vm.Triage.PostponeCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().BeNull("片づいたのだから古いエラーを出し続けない");
    }

    [Fact]
    public async Task WarningMessage_ClearsOnTheNextSuccessfulDecision()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();
        _service.Raise(IngestedRun(), warning: "5 件のうち 1 件は読み取れませんでした");
        _vm.WarningMessage.Should().Be("5 件のうち 1 件は読み取れませんでした");

        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.WarningMessage.Should().BeNull("片づいたのだから古い警告を出し続けない");
    }

    /// <summary>
    /// PendingLoad は本番の誰も待っていない Task なので、RefreshAsync の中の例外を放っておくと
    /// 誰にも観測されない例外になり、候補キューが古いまま黙って固まる。MainWindow.OnLoaded が
    /// 起動失敗をバナーへ回すのと同じように、ここも WarningMessage へ回すこと。
    /// </summary>
    [Fact]
    public async Task Refresh_SurfacesAFailureThroughTheWarningBanner_InsteadOfThrowing()
    {
        _service.Current = IngestedRun();
        await _vm.LoadAsync();
        _service.FailNextGetCurrentRun = new InvalidOperationException("接続できません");

        await _vm.CompleteCommand.ExecuteAsync(null);

        _vm.WarningMessage.Should().Be(
            string.Format(Strings.PlanRefreshFailedFormat, "接続できません"));
    }

    [Fact]
    public async Task LeftPanel_IsTheTriagePanel_WhileCandidatesRemain()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.LeftPanel.Should().BeSameAs(_vm.Triage);
        _vm.Triage.Selected.Should().BeSameAs(_vm.Selected);
    }

    [Fact]
    public async Task LeftPanel_IsEmpty_BeforeTheFirstRun()
    {
        await _vm.LoadAsync();

        _vm.LeftPanel.Should().BeNull("実行前・実行中・失敗は上部バーが案内する");
    }

    [Fact]
    public async Task Load_WithAnIngestedRunAndNoQueue_ShowsTheFirstThingAndTheGroups()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();

        _vm.IsPlanReady.Should().BeTrue();
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing, "候補が無ければ左は『最初にやる1件』（ワイヤー 4b）");
        _vm.FirstThing.HasFirstThing.Should().BeTrue();
        _vm.FirstThing.Title.Should().Be("請求先情報を更新する");
        _vm.FirstThing.Reason.Should().Be("期限が一番近い");
        _vm.FirstThing.IsFallback.Should().BeFalse();
        _vm.Sections.Select(s => s.Key).Should().Equal(
            PlanGroupKey.Today, PlanGroupKey.IfTime, PlanGroupKey.AiReady, PlanGroupKey.Waiting);
        _vm.Sections[0].Rows.Select(r => r.TaskId).Should().Equal(
            new int?[] { 10, 12 }, "候補 outlook:001 は取り込まれていないので落ちる");
        _vm.Sections[0].Rows[0].Caption.Should().Be("顧客A対応 / " + string.Format(Strings.CardDueFormat, 9, 8));
        _vm.Sections[0].CountText.Should().Be(string.Format(Strings.PlanGroupCountFormat, 2));
        _vm.Sections[1].IsEmpty.Should().BeTrue();
        _vm.StatusLine.Should().Be(string.Format(Strings.PlanTriageDoneFormat, 0, 0, 0, 0, 0));
        _vm.DateHeading.Should().Be(string.Format(Strings.PlanDateHeadingFormat, "9月7日（月）"));
    }

    [Fact]
    public async Task Load_WithCandidates_ShowsTheProvisionalPlan()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.IsTriaging.Should().BeTrue();
        _vm.StatusLine.Should().Be(string.Format(Strings.PlanTriagingFormat, 1));
        _vm.FirstThingHeadingText.Should().Be(Strings.PlanFirstThingHeading + Strings.PlanFirstThingProvisional);
        var row = _vm.Sections[0].Rows.Should().HaveCount(3).And.Subject.ElementAt(1);
        row.CandidateId.Should().Be(1);
        row.BadgeText.Should().Be(Strings.PlanRowPendingCandidate);
        _vm.Sections[0].CountText.Should().Be(string.Format(Strings.PlanGroupCountWithCandidatesFormat, 2, 1));
        _vm.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Register_TurnsTheCandidateRowIntoATaskRow_AndSwitchesTheLeftPanel()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.RegisterResult = Result.Ok(new TaskItem { Id = 11 });
        await _vm.LoadAsync();

        await _vm.Triage.RegisterCommand.ExecuteAsync(null);

        var row = _vm.Sections[0].Rows.Should().HaveCount(3).And.Subject.ElementAt(1);
        row.TaskId.Should().Be(11, "登録した候補の行はその場で実タスクに解決する（親仕様 §8）");
        row.BadgeText.Should().Be(Strings.PlanRowNew);
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing, "最後の 1 件を片づけたので切り替わる");
        _vm.PendingCount.Should().Be(0);
        _vm.HasNoCandidates.Should().BeFalse("候補はあった。『候補はありませんでした』は 0 件の日だけ");
    }

    [Fact]
    public async Task Reject_RemovesTheRowFromThePlan()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.Sections[0].Rows.Select(r => r.TaskId).Should().Equal(10, 12);
        _vm.StatusLine.Should().Be(string.Format(Strings.PlanTriageDoneFormat, 1, 0, 0, 1, 0));
    }

    [Fact]
    public async Task FirstThing_FallsBackToTheFirstTodayRow_WhenItPointedAtARejectedCandidate()
    {
        _service.Current = IngestedRunWithPlan(
            firstThing: "{\"externalId\":\"outlook:001\",\"reason\":\"この実行の依頼\"}",
            today: "{\"externalId\":\"outlook:001\"},{\"taskId\":12}");
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.FirstThing.Title.Should().Be("週次レポートを作成する");
        _vm.FirstThing.IsFallback.Should().BeTrue();
        _vm.FirstThingHeadingText.Should().Be(Strings.PlanFirstThingHeading + Strings.PlanFirstThingProvisional,
            "繰り下げたら状態 2 でも（暫定）を付ける");
    }

    [Fact]
    public async Task FirstThing_SaysSo_WhenNothingIsLeft()
    {
        _service.Current = IngestedRunWithPlan(firstThing: "{\"taskId\":999,\"reason\":\"消えた\"}", today: "");
        await _vm.LoadAsync();

        _vm.FirstThing.HasFirstThing.Should().BeFalse();
        _vm.FirstThing.OpenOnBoardCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task RunChanged_ToIngestedWithoutCandidates_StillLoadsThePlan()
    {
        await _vm.LoadAsync();
        var run = IngestedRunWithPlan();
        _service.Current = run;

        _service.Raise(new PlanningRun { Id = run.Id, Date = run.Date, Status = PlanningRunStatus.Running });
        _service.Raise(run, candidates: false);
        await _vm.PendingLoad;

        _vm.Sections[0].Rows.Should().NotBeEmpty("候補 0 件の日でも計画はある（親仕様 §8）");
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing);
    }

    [Fact]
    public async Task OpeningATaskRow_AsksTheWindowToShowItOnTheBoard()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();
        var navigated = new List<int>();
        _vm.NavigateToTask += (_, id) => navigated.Add(id);

        _vm.Sections[0].Rows[1].OpenCommand.Execute(null);
        _vm.FirstThing.OpenOnBoardCommand.Execute(null);

        navigated.Should().Equal(12, 10);
    }

    [Fact]
    public async Task OpeningACandidateRow_SelectsThatCandidate()
    {
        _service.Current = IngestedRunWithPlan(today: "{\"externalId\":\"outlook:002\"},{\"externalId\":\"outlook:001\"}");
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.Selected!.CandidateId.Should().Be(1);

        _vm.Sections[0].Rows[0].OpenCommand.Execute(null);

        _vm.Selected!.CandidateId.Should().Be(2);
        _vm.Triage.Selected!.CandidateId.Should().Be(2);
    }

    [Fact]
    public async Task Load_SurvivesABoardFailure_WithAnEmptyPlanAndAWarning()
    {
        _boards.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("接続できません"));
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.WarningMessage.Should().Be("接続できません");
        _vm.Sections[0].Rows.Should().ContainSingle().Which.CandidateId.Should().Be(1, "候補行は盤面が無くても解決できる");
        _vm.LeftPanel.Should().BeSameAs(_vm.Triage, "仕分けは盤面が無くても動く（仕様 §8）");
    }

    [Fact]
    public void CandidateItem_MapsTheSuggestionToABadge()
    {
        new CandidateItemViewModel(Candidate(suggested: TriageAction.Merge)).SuggestionText.Should().Be(Strings.PlanSuggestMerge);
        new CandidateItemViewModel(Candidate(suggested: TriageAction.Reject)).SuggestionText.Should().Be(Strings.PlanSuggestReject);
    }

    [Fact]
    public async Task ApplySuggestions_DoesNothing_WhenThePersonSaysNo()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();
        var asked = new List<string>();
        _vm.Confirm = message => { asked.Add(message); return false; };

        await _vm.ApplySuggestionsCommand.ExecuteAsync(null);

        asked.Should().ContainSingle().Which.Should().Be(string.Format(Strings.PlanApplyConfirmFormat, 1, "未着手"),
            "登録先の列は完了以外の先頭で、文言に明記する");
        _service.Calls.Should().NotContain(c => c.StartsWith("ApplySuggestions"));
        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplySuggestions_AppliesIntoTheFirstColumn_AndReportsTheOutcome()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2, TriageAction.Reject));
        _service.BulkResult = Result.Ok(new BulkOutcome(1, new[] { "候補 2: 列が見つかりません" }));
        await _vm.LoadAsync();
        _vm.Confirm = _ => true;

        await _vm.ApplySuggestionsCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("ApplySuggestions:1");
        _vm.WarningMessage.Should().Be(string.Format(Strings.PlanBulkResultFormat, 1, 1, "候補 2: 列が見つかりません"));
        _vm.Candidates.Should().BeEmpty("偽サービスが全件を決着させたのでキューは読み直しで空になる");
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing);
    }

    [Fact]
    public async Task PostponeAll_NeedsNoConfirmation()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.BulkResult = Result.Ok(new BulkOutcome(1, Array.Empty<string>()));
        await _vm.LoadAsync();
        _vm.Confirm = _ => throw new InvalidOperationException("確認は出さない");

        await _vm.PostponeAllCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("PostponeAll");
        _vm.WarningMessage.Should().Be(string.Format(Strings.PlanBulkAppliedFormat, 1));
        _vm.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkCommands_AreDisabled_WhenNothingIsQueued()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();

        _vm.ApplySuggestionsCommand.CanExecute(null).Should().BeFalse();
        _vm.PostponeAllCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task PendingBadge_FollowsTheQueue()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.PendingCount.Should().Be(2);
        _vm.HasPendingCandidates.Should().BeTrue();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);
        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.PendingCount.Should().Be(0);
        _vm.HasPendingCandidates.Should().BeFalse("0 のときはバッジを出さない");
    }

    /// <summary>
    /// finding C1 の回帰試験。偽サービスの RejectAsync が候補を書き換えて RunChanged
    /// (candidatesChanged: true) を上げると、OnRunChanged→RefreshAsync→ReloadQueueAsync
    /// (R)が、RejectCommand 自身の後始末である AfterDecisionAsync→ReloadQueueAsync(D)より前に
    /// (仕分け経路の中から再入して)動く。テストでは SynchronizationContext.Current が null なので
    /// Post はインラインで実行される(それでよい。まず問い合わせてから Clear+Add をまとめて行う
    /// 順序と、世代番号による古い読み直しの打ち切りが、仕分け経路から再入されたときにも
    /// 崩れないことを確かめる)。
    /// </summary>
    [Fact]
    public async Task Reject_DoesNotDuplicateTheQueue_WhenTheServiceAlsoRaisesRunChanged()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);
        await _vm.PendingLoad;

        _vm.Candidates.Should().ContainSingle();
        _vm.PendingCount.Should().Be(1);
        _vm.Triage.PositionText.Should().Be("1 / 1");
    }
}
