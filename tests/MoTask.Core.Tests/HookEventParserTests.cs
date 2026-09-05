using System.IO;
using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// CLI との唯一の形の依存点。fixture は実機で採取したペイロード（仕様 §4.4）。
/// CLI が形を変えたらここが赤くなる。
/// </summary>
public class HookEventParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim();

    [Fact]
    public void SessionStart_BecomesSessionStarted()
    {
        var line = Fixture("hook-session-start.json");

        var result = HookEventParser.Parse(line);

        result.Kind.Should().Be(AiJobEventKind.SessionStarted);
        result.ToolName.Should().BeNull();
        result.Payload.Should().Be(line, "原文はそのまま残す");
    }

    [Fact]
    public void PostToolUse_BecomesToolUse_WithTheToolName()
    {
        var result = HookEventParser.Parse(Fixture("hook-post-tool-use.json"));

        result.Kind.Should().Be(AiJobEventKind.ToolUse);
        result.ToolName.Should().Be("Write");
    }

    [Fact]
    public void Stop_BecomesTurnEnded()
    {
        var result = HookEventParser.Parse(Fixture("hook-stop.json"));

        result.Kind.Should().Be(AiJobEventKind.TurnEnded);
        result.ToolName.Should().BeNull();
    }

    [Fact]
    public void SessionEnd_BecomesSessionEnded()
    {
        var result = HookEventParser.Parse(Fixture("hook-session-end.json"));

        result.Kind.Should().Be(AiJobEventKind.SessionEnded);
    }

    [Theory]
    [InlineData("これは JSON ではない")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hook_event_name\":\"PreCompact\"}")]
    [InlineData("{}")]
    [InlineData("")]
    public void UnknownOrBrokenLines_AreKeptAsSystem(string line)
    {
        var result = HookEventParser.Parse(line);

        result.Kind.Should().Be(AiJobEventKind.System);
        result.ToolName.Should().BeNull();
        result.Payload.Should().Be(line, "1 行壊れても捨てない");
    }

    [Fact]
    public void PostToolUse_WithoutToolName_StillParses()
    {
        var result = HookEventParser.Parse("""{"hook_event_name":"PostToolUse"}""");

        result.Kind.Should().Be(AiJobEventKind.ToolUse);
        result.ToolName.Should().BeNull();
    }

    [Fact]
    public void EventKinds_KeepTheirExistingNumbers()
    {
        ((int)AiJobEventKind.AssistantText).Should().Be(0);
        ((int)AiJobEventKind.ToolUse).Should().Be(1);
        ((int)AiJobEventKind.System).Should().Be(7);
        ((int)AiJobEventKind.SessionStarted).Should().Be(8);
        ((int)AiJobEventKind.SessionEnded).Should().Be(9);
        ((int)AiJobEventKind.TurnEnded).Should().Be(10);
    }
}
