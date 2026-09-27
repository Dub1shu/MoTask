using FluentAssertions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class PlanningModelTests
{
    [Fact]
    public void NewRun_StartsPending_AndIsNotTerminal()
    {
        var run = new PlanningRun { Date = new DateOnly(2026, 9, 7) };

        run.Status.Should().Be(PlanningRunStatus.Pending);
        run.Status.IsActive().Should().BeTrue();
        run.PlanJson.Should().BeEmpty("取り込み前の計画は空文字。null にはしない");
        run.JobFolder.Should().BeEmpty();
        run.Instruction.Should().BeEmpty();
        run.ProcessedLines.Should().Be(0);
    }

    [Theory]
    [InlineData(PlanningRunStatus.Ingested)]
    [InlineData(PlanningRunStatus.Failed)]
    [InlineData(PlanningRunStatus.Cancelled)]
    public void FinishedStatuses_AreTerminal(PlanningRunStatus status)
        => status.IsTerminal().Should().BeTrue();

    [Theory]
    [InlineData(PlanningRunStatus.Pending)]
    [InlineData(PlanningRunStatus.Running)]
    public void UnfinishedStatuses_AreNotTerminal(PlanningRunStatus status)
        => status.IsTerminal().Should().BeFalse();

    [Fact]
    public void NewCandidate_StartsPending()
    {
        var candidate = new TriageCandidate { ExternalId = "outlook:AAMkAD" };

        candidate.Status.Should().Be(TriageStatus.Pending);
        candidate.SuggestedAction.Should().Be(TriageAction.Register);
        candidate.Source.Should().BeEmpty();
        candidate.Evidence.Should().BeEmpty();
        candidate.ResultTaskId.Should().BeNull();
        candidate.SuggestedMergeTaskId.Should().BeNull();
        candidate.DecidedAt.Should().BeNull();
    }

    [Fact]
    public void HistoryKind_GainsTheTwoTriageMembers_WithoutRenumberingTheOldOnes()
    {
        ((int)HistoryKind.AiJobFinished).Should().Be(6, "既存行の値を動かさない");
        ((int)HistoryKind.CandidateRegistered).Should().Be(7);
        ((int)HistoryKind.CandidateMerged).Should().Be(8);
    }

    [Fact]
    public void TriageHistoryDetail_RoundTrips()
    {
        var text = TriageHistoryDetail.Serialize(new TriageHistoryDetail("Outlook", "outlook:AAMkAD"));

        var back = TriageHistoryDetail.Deserialize(text);

        back!.Source.Should().Be("Outlook");
        back.ExternalId.Should().Be("outlook:AAMkAD");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    public void TriageHistoryDetail_ReturnsNull_ForUnusableText(string text)
        => TriageHistoryDetail.Deserialize(text).Should().BeNull();
}
