using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>仕様 §4 の解決ルール。1 ルール 1 テスト。</summary>
public class PlanResolverTests
{
    private readonly Board _board = new() { Id = 1, Name = "テスト" };
    private readonly Column _active = new() { Id = 2, Name = "今日中", Order = 1, Role = ColumnRole.Active };
    private readonly Column _done = new() { Id = 3, Name = "完了", Order = 2, Role = ColumnRole.Done };
    private readonly List<TriageCandidate> _candidates = new();

    public PlanResolverTests()
    {
        _board.Columns.Add(_active);
        _board.Columns.Add(_done);
        _active.Tasks.Add(new TaskItem { Id = 45, Title = "Q4企画書の内容を確定する", ColumnId = 2, ProjectId = 100, DueDate = new DateOnly(2026, 9, 8) });
        _active.Tasks.Add(new TaskItem { Id = 52, Title = "会場候補を3つに絞る", ColumnId = 2 });
        _active.Tasks.Add(new TaskItem { Id = 61, Title = "削除済み", ColumnId = 2, DeletedAt = new DateTime(2026, 9, 1) });
        _done.Tasks.Add(new TaskItem { Id = 70, Title = "請求書を承認", ColumnId = 3 });
    }

    private static string? ProjectName(int? id) => id == 100 ? "プロジェクトQ4" : null;

    private TriageCandidate Candidate(string externalId, TriageStatus status, int? resultTaskId = null)
    {
        var candidate = new TriageCandidate
        {
            Id = _candidates.Count + 1, PlanningRunId = 1, ExternalId = externalId, Source = "Outlook",
            Title = "請求先情報を更新する", Evidence = "「9月8日までに」", Status = status, ResultTaskId = resultTaskId,
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
        };
        _candidates.Add(candidate);
        return candidate;
    }

    private ResolvedPlan Resolve(string planJson) => PlanResolver.Resolve(planJson, _candidates, _board, ProjectName);

    private static string Plan(string todayItems, string firstThing = "null", string others = "")
        => $"{{\"date\":\"2026-09-07\",\"firstThing\":{firstThing},\"groups\":[{{\"key\":\"today\",\"items\":[{todayItems}]}}{others}]}}";

    [Fact]
    public void ATaskOnTheBoard_BecomesATaskRow()
    {
        var plan = Resolve(Plan("{\"taskId\":45}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<TaskRow>().Subject;
        row.TaskId.Should().Be(45);
        row.Title.Should().Be("Q4企画書の内容を確定する");
        row.ProjectName.Should().Be("プロジェクトQ4");
        row.DueDate.Should().Be(new DateOnly(2026, 9, 8));
        row.ColumnName.Should().Be("今日中");
        row.IsDone.Should().BeFalse();
        row.Origin.Should().Be(TaskRowOrigin.None);
    }

    [Fact]
    public void ATaskInTheDoneColumn_StaysWithADoneMark()
    {
        var plan = Resolve(Plan("{\"taskId\":70}"));

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<TaskRow>().Which.IsDone.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"taskId\":61}")]   // 論理削除済み
    [InlineData("{\"taskId\":999}")]  // 盤面に無い
    [InlineData("{}")]                 // どちらも持たない
    [InlineData("\"not an object\"")]
    public void AnUnresolvableTaskItem_IsDropped(string item)
    {
        var plan = Resolve(Plan(item));

        plan.Groups[0].Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TriageStatus.Pending)]
    [InlineData(TriageStatus.Later)]
    public void AnUndecidedCandidate_BecomesACandidateRow(TriageStatus status)
    {
        Candidate("outlook:001", status);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<CandidateRow>().Subject;
        row.CandidateId.Should().Be(1);
        row.Title.Should().Be("請求先情報を更新する");
        row.Source.Should().Be("Outlook");
        row.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        row.SuggestedProject.Should().Be("顧客A");
    }

    [Theory]
    [InlineData(TriageStatus.Registered, TaskRowOrigin.RegisteredThisRun)]
    [InlineData(TriageStatus.Merged, TaskRowOrigin.MergedThisRun)]
    public void ADecidedCandidate_ResolvesToItsResultTask(TriageStatus status, TaskRowOrigin origin)
    {
        Candidate("outlook:001", status, resultTaskId: 52);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<TaskRow>().Subject;
        row.TaskId.Should().Be(52);
        row.Origin.Should().Be(origin);
    }

    [Fact]
    public void ARegisteredCandidateWhoseTaskIsGone_IsDropped()
    {
        Candidate("outlook:001", TriageStatus.Registered, resultTaskId: 61);

        Resolve(Plan("{\"externalId\":\"outlook:001\"}")).Groups[0].Rows.Should().BeEmpty();
    }

    [Fact]
    public void ARejectedOrUnknownCandidate_IsDropped()
    {
        Candidate("outlook:001", TriageStatus.Rejected);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"},{\"externalId\":\"outlook:never-ingested\"}"));

        plan.Groups[0].Rows.Should().BeEmpty();
    }

    [Fact]
    public void AnItemWithBothIds_PrefersTheTaskId()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"taskId\":45,\"externalId\":\"outlook:001\"}"));

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(45);
    }

    [Fact]
    public void TheSameTask_AppearsOnlyInTheFirstGroupThatNamesIt()
    {
        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":45}",
            others: ",{\"key\":\"ifTime\",\"items\":[{\"taskId\":45},{\"taskId\":52}]}"));

        plan.Groups[0].Rows.Select(r => ((TaskRow)r).TaskId).Should().Equal(45);
        plan.Groups[1].Rows.Select(r => ((TaskRow)r).TaskId).Should().Equal(52);
    }

    [Fact]
    public void TheSameCandidate_AppearsOnlyOnce()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}",
            others: ",{\"key\":\"waiting\",\"items\":[{\"externalId\":\"outlook:001\"}]}"));

        plan.Groups[0].Rows.Should().ContainSingle();
        plan.Groups[3].Rows.Should().BeEmpty();
    }

    [Fact]
    public void FirstThing_ResolvesAndStillAppearsInItsGroup()
    {
        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":52}",
            firstThing: "{\"taskId\":45,\"reason\":\"送付前に部長の確認が必要\"}"));

        plan.FirstThing.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(45);
        plan.FirstThingReason.Should().Be("送付前に部長の確認が必要");
        plan.FirstThingIsFallback.Should().BeFalse();
        plan.Groups[0].Rows.Should().HaveCount(2, "最初にやる1件は重複排除に参加しない");
    }

    [Fact]
    public void FirstThing_FallsBackToTheFirstTodayRow_WhenItCannotBeResolved()
    {
        Candidate("outlook:001", TriageStatus.Rejected);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"},{\"taskId\":52}",
            firstThing: "{\"externalId\":\"outlook:001\",\"reason\":\"却下された\"}"));

        plan.FirstThing.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(52);
        plan.FirstThingIsFallback.Should().BeTrue();
        plan.FirstThingReason.Should().BeEmpty("繰り下げた行に元の理由は当てはまらない");
    }

    [Fact]
    public void FirstThing_IsNull_WhenTodayIsEmptyToo()
    {
        var plan = Resolve(Plan("", firstThing: "{\"taskId\":999,\"reason\":\"消えた\"}"));

        plan.FirstThing.Should().BeNull();
        plan.FirstThingIsFallback.Should().BeTrue();
    }

    [Fact]
    public void MissingGroups_AreFilledInAsEmpty_InTheFixedOrder()
    {
        var plan = Resolve(Plan("{\"taskId\":45}", others: ",{\"key\":\"waiting\",\"items\":[{\"taskId\":52}]}"));

        plan.Groups.Select(g => g.Key).Should().Equal(
            PlanGroupKey.Today, PlanGroupKey.IfTime, PlanGroupKey.AiReady, PlanGroupKey.Waiting);
        plan.Groups[1].Rows.Should().BeEmpty();
        plan.Groups[2].Rows.Should().BeEmpty();
        plan.Groups[3].Rows.Should().ContainSingle();
    }

    [Fact]
    public void Counts_SeparateTasksFromCandidates()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":52},{\"externalId\":\"outlook:001\"}"));

        plan.Groups[0].TaskCount.Should().Be(2);
        plan.Groups[0].CandidateCount.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    public void AnEmptyOrBrokenPlan_HasFourEmptyGroupsButStillASummary(string planJson)
    {
        Candidate("outlook:001", TriageStatus.Registered, resultTaskId: 45);

        var plan = Resolve(planJson);

        plan.Groups.Should().HaveCount(4);
        plan.Groups.Should().OnlyContain(g => g.Rows.Count == 0);
        plan.FirstThing.Should().BeNull();
        plan.Summary.Registered.Should().Be(1);
    }

    [Fact]
    public void Summary_CountsEveryStatusOfTheRun()
    {
        Candidate("a", TriageStatus.Registered, 45);
        Candidate("b", TriageStatus.Registered, 52);
        Candidate("c", TriageStatus.Merged, 45);
        Candidate("d", TriageStatus.Rejected);
        Candidate("e", TriageStatus.Later);
        Candidate("f", TriageStatus.Pending);

        var summary = Resolve("").Summary;

        summary.Should().Be(new TriageSummary(Total: 6, Registered: 2, Merged: 1, Rejected: 1, Later: 1, Pending: 1));
    }

    [Fact]
    public void WithoutABoard_TaskRowsAreDroppedButCandidateRowsSurvive()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = PlanResolver.Resolve(
            Plan("{\"taskId\":45},{\"externalId\":\"outlook:001\"}"), _candidates, board: null, ProjectName);

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<CandidateRow>();
    }
}
