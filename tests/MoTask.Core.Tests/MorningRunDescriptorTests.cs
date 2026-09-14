using FluentAssertions;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// run.json（仕様 §7）。再起動した MoTask はこれを読んで端末に掛け直すので、
/// pid と開始時刻が往復することが要る。
/// </summary>
public class MorningRunDescriptorTests
{
    /// <summary>
    /// Process.StartTime は 100ns tick の精度を持ち、TryReattach はこの値を完全一致で照合する。
    /// 秒丸めの値で固定すると、保存が精度を落としても気づけない（仕様 §7）。
    /// </summary>
    private static readonly DateTime ProcessStarted =
        new DateTime(2026, 9, 13, 6, 0, 1, DateTimeKind.Utc).AddTicks(1234567);

    private static readonly MorningRunDescriptor Sample = new(
        7, new DateOnly(2026, 9, 13), new Guid("6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77"),
        @"C:\work\morning\0007-2026-09-13", "cmd.exe /c claude", new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc),
        ProcessId: 12345, ProcessStartedAt: ProcessStarted);

    [Fact]
    public void Serialize_ThenTryParse_RoundTripsEveryField()
    {
        var parsed = MorningRunDescriptor.TryParse(MorningRunDescriptor.Serialize(Sample));

        parsed.Should().NotBeNull();
        parsed!.RunId.Should().Be(7);
        parsed.Date.Should().Be(new DateOnly(2026, 9, 13));
        parsed.SessionId.Should().Be(Sample.SessionId);
        parsed.JobFolder.Should().Be(Sample.JobFolder);
        parsed.LaunchCommand.Should().Be("cmd.exe /c claude");
        parsed.ProcessId.Should().Be(12345);
        parsed.ProcessStartedAt.Should().Be(Sample.ProcessStartedAt);
    }

    /// <summary>旧い run.json には pid が無い。掛け直しを諦めるだけで、読めなくはない。</summary>
    [Fact]
    public void TryParse_AcceptsAFileWrittenBeforeTheProcessIdExisted()
    {
        var json = """
            {"runId":3,"date":"2026-09-07","sessionId":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77",
             "jobFolder":"C:\\work\\morning\\0003-2026-09-07","launchCommand":"wt.exe ...",
             "startedAt":"2026-09-07T06:00:00Z"}
            """;

        var parsed = MorningRunDescriptor.TryParse(json);

        parsed.Should().NotBeNull();
        parsed!.ProcessId.Should().Be(0, "掛け直しの材料が無いことは 0 で表す");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    public void TryParse_ReturnsNullForAnythingItCannotRead(string? json)
    {
        MorningRunDescriptor.TryParse(json).Should().BeNull();
    }
}
