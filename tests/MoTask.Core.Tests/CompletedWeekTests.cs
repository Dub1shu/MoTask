using FluentAssertions;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class CompletedWeekTests
{
    // 実行する PC のタイムゾーンに左右されないよう、JST を自前で作って渡す。
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    // 2026-10-01（木）。今週は 9/28（月）〜 10/4（日）。
    private static readonly DateOnly Thursday = new(2026, 10, 1);

    private static TaskItem CompletedAt(DateTime? utc) => new() { Id = 1, Title = "t", CompletedAt = utc };

    [Fact]
    public void CompletedOn_ConvertsUtcToTheLocalDate()
    {
        // UTC 日曜 15:00 は JST の月曜 0:00
        CompletedWeek.CompletedOn(new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Utc), Jst)
            .Should().Be(new DateOnly(2026, 9, 28));
    }

    [Fact]
    public void CompletedOn_TreatsUnspecifiedKindAsUtc()
    {
        // EF Core（SQLite）から読んだ DateTime は Kind が Unspecified で返る。
        CompletedWeek.CompletedOn(new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Unspecified), Jst)
            .Should().Be(new DateOnly(2026, 9, 28));
    }

    [Fact]
    public void IsArchived_MondayMidnightLocal_IsThisWeek()
    {
        var task = CompletedAt(new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Utc)); // JST 9/28 0:00:00

        CompletedWeek.IsArchived(task, Thursday, Jst).Should().BeFalse();
    }

    [Fact]
    public void IsArchived_SundayLastSecondLocal_IsArchived()
    {
        var task = CompletedAt(new DateTime(2026, 9, 27, 14, 59, 59, DateTimeKind.Utc)); // JST 9/27 23:59:59

        CompletedWeek.IsArchived(task, Thursday, Jst).Should().BeTrue();
    }

    [Fact]
    public void IsArchived_OnSunday_StillUsesThatWeeksMonday()
    {
        var task = CompletedAt(new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Utc)); // JST 9/28（月）

        CompletedWeek.IsArchived(task, new DateOnly(2026, 10, 4), Jst).Should().BeFalse();
    }

    [Fact]
    public void IsArchived_AcrossTheYearBoundary()
    {
        // 2027-01-01（金）の週は 2026-12-28（月）から。12/27（日）の完了はアーカイブ。
        var task = CompletedAt(new DateTime(2026, 12, 27, 3, 0, 0, DateTimeKind.Utc)); // JST 12/27 12:00

        CompletedWeek.IsArchived(task, new DateOnly(2027, 1, 1), Jst).Should().BeTrue();
    }

    [Fact]
    public void IsArchived_WithoutCompletedAt_StaysOnTheBoard()
    {
        CompletedWeek.IsArchived(CompletedAt(null), Thursday, Jst).Should().BeFalse();
    }
}
