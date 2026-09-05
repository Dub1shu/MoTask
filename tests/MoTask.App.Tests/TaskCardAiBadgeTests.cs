using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using MoTask.Core.Services;
using Xunit;

namespace MoTask.App.Tests;

public class TaskCardAiBadgeTests
{
    private static AiJobSnapshot Snapshot(AiJobKind kind, AiJobStatus status, int turns = 0)
        => new(1, 10, kind, status, turns, null, null, @"C:\w");

    private static TaskCardViewModel Card() => new(new TaskItem { Id = 10, Title = "t" });

    [Fact]
    public void Running_ShowsKindAndTurns()
    {
        var card = Card();
        card.SetAiState(Snapshot(AiJobKind.Research, AiJobStatus.Running, 3));
        card.AiBadgeText.Should().Be(string.Format(Strings.AiBadgeTurnsFormat, Strings.AiStatusResearching, 3));
        card.HasAiBadge.Should().BeTrue();
        card.IsAwaitingApproval.Should().BeFalse();

        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.Running, 0));
        card.AiBadgeText.Should().Be(string.Format(Strings.AiBadgeTurnsFormat, Strings.AiStatusExecuting, 0));
    }

    [Fact]
    public void AwaitingApproval_IsFlagged()
    {
        var card = Card();
        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.AwaitingApproval, 2));
        card.AiBadgeText.Should().Be(Strings.AiStatusAwaiting);
        card.IsAwaitingApproval.Should().BeTrue();
    }

    [Fact]
    public void Suspended_ShowsSuspended()
    {
        var card = Card();
        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.Suspended));
        card.AiBadgeText.Should().Be(Strings.AiStatusSuspended);
    }

    [Fact]
    public void TerminalOrNull_ClearsTheBadge()
    {
        var card = Card();
        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.Running, 1));
        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.Succeeded, 5));
        card.AiBadgeText.Should().BeNull();
        card.HasAiBadge.Should().BeFalse();

        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.AwaitingApproval));
        card.SetAiState(null);
        card.IsAwaitingApproval.Should().BeFalse();
        card.HasAiBadge.Should().BeFalse();
    }

    [Fact]
    public void Refresh_KeepsTheBadge()
    {
        var card = Card();
        card.SetAiState(Snapshot(AiJobKind.Execute, AiJobStatus.Running, 1));
        card.Refresh(_ => null, new DateOnly(2026, 9, 5));
        card.HasAiBadge.Should().BeTrue();
    }
}
