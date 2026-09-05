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
    public void SetAiState_WaitingForInput_ShowsTheInputBadge()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Execute, AiJobStatus.WaitingForInput, 3, null, null, ""));

        card.HasAiBadge.Should().BeTrue();
        card.IsWaitingForInput.Should().BeTrue();
        card.AiBadgeText.Should().Be("入力待ち");
    }

    [Fact]
    public void SetAiState_Running_ShowsTheKindAndTurns()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Research, AiJobStatus.Running, 2, null, null, ""));

        card.IsWaitingForInput.Should().BeFalse();
        card.AiBadgeText.Should().Be("AI 調査中 · 2 ターン");
    }

    [Fact]
    public void SetAiState_Pending_ShowsNoBadgeYet()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Execute, AiJobStatus.Pending, 0, null, null, ""));

        card.HasAiBadge.Should().BeFalse();
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
        card.IsWaitingForInput.Should().BeFalse();
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
