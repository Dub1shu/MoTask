using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class AiModelTests
{
    [Fact]
    public void AiJobStatus_IsActive_OnlyForRunningAndAwaitingApproval()
    {
        AiJobStatus.Running.IsActive().Should().BeTrue();
        AiJobStatus.AwaitingApproval.IsActive().Should().BeTrue();
        AiJobStatus.Suspended.IsActive().Should().BeFalse();
        AiJobStatus.Pending.IsActive().Should().BeFalse();
        AiJobStatus.Succeeded.IsActive().Should().BeFalse();
    }

    [Fact]
    public void AiJobStatus_IsTerminal_ForSucceededFailedCancelled()
    {
        AiJobStatus.Succeeded.IsTerminal().Should().BeTrue();
        AiJobStatus.Failed.IsTerminal().Should().BeTrue();
        AiJobStatus.Cancelled.IsTerminal().Should().BeTrue();
        AiJobStatus.Suspended.IsTerminal().Should().BeFalse();
        AiJobStatus.Running.IsTerminal().Should().BeFalse();
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
        Messages.SuspendedByShutdown.Should().Be("MoTask が終了したため中断しました");
        string.Format(Messages.ConcurrencyLimitFormat, 3, 3).Should().Be("同時に実行できる AI ジョブは 3 件までです（現在 3 件が実行中）");
        Messages.ResumeInstruction.Should().StartWith("中断されたところから続けてください");
    }
}
