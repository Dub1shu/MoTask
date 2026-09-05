using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class AiModelTests
{
    [Fact]
    public void AiJobStatus_IsActive_ForEveryStatusThatIsNotFinished()
    {
        AiJobStatus.Pending.IsActive().Should().BeTrue();
        AiJobStatus.Running.IsActive().Should().BeTrue();
        AiJobStatus.WaitingForInput.IsActive().Should().BeTrue();
        AiJobStatus.Succeeded.IsActive().Should().BeFalse();
        AiJobStatus.Failed.IsActive().Should().BeFalse();
        AiJobStatus.Cancelled.IsActive().Should().BeFalse();
    }

    [Fact]
    public void AiJobStatus_KeepsTheExistingNumbers()
    {
        ((int)AiJobStatus.Pending).Should().Be(0);
        ((int)AiJobStatus.Running).Should().Be(1);
        ((int)AiJobStatus.Succeeded).Should().Be(4);
        ((int)AiJobStatus.Failed).Should().Be(5);
        ((int)AiJobStatus.Cancelled).Should().Be(6);
        ((int)AiJobStatus.WaitingForInput).Should().Be(7);
    }

    [Fact]
    public void AiJobStatus_IsTerminal_ForSucceededFailedCancelled()
    {
        AiJobStatus.Succeeded.IsTerminal().Should().BeTrue();
        AiJobStatus.Failed.IsTerminal().Should().BeTrue();
        AiJobStatus.Cancelled.IsTerminal().Should().BeTrue();
        AiJobStatus.Running.IsTerminal().Should().BeFalse();
        AiJobStatus.WaitingForInput.IsTerminal().Should().BeFalse();
    }

    [Fact]
    public void AiJobHistoryDetail_RoundTrips()
    {
        var started = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(AiJobKind.Research, null));
        AiJobHistoryDetail.Deserialize(started).Should().Be(new AiJobHistoryDetail(AiJobKind.Research, null));

        var finished = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(AiJobKind.Execute, AiJobStatus.Succeeded));
        finished.Should().Contain("Execute").And.Contain("Succeeded", "列挙は名前で書く（値の並びが変わっても読める）");
        AiJobHistoryDetail.Deserialize(finished)!.Status.Should().Be(AiJobStatus.Succeeded);
    }

    [Fact]
    public void AiJobHistoryDetail_Deserialize_ReturnsNullForEmptyOrBroken()
    {
        AiJobHistoryDetail.Deserialize("").Should().BeNull();
        AiJobHistoryDetail.Deserialize("{not json").Should().BeNull();
    }

    [Fact]
    public void HistoryKind_HasAiValues()
    {
        ((int)HistoryKind.AiJobStarted).Should().Be(5);
        ((int)HistoryKind.AiJobFinished).Should().Be(6);
    }

    [Fact]
    public void Project_WorkingDirectory_DefaultsToNull()
    {
        new Project { Name = "p" }.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public void Messages_ResolveAiStrings()
    {
        Messages.TaskAlreadyHasActiveJob.Should().Be("このタスクには追跡中の AI ジョブがあります");
        Messages.AiJobAlreadyFinished.Should().Be("このジョブは終了済みです");
        string.Format(Messages.TerminalStartPromptFormat, "a.md", "b").Should().StartWith("a.md を読んで");
    }
}
