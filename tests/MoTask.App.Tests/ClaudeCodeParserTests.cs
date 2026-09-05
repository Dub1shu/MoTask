using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Model;
using System.IO;
using Xunit;

namespace MoTask.App.Tests;

public class ClaudeCodeParserTests
{
    public static string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void BashSample_ProducesExpectedKinds_AndKeepsRawPayload()
    {
        var lines = Fixture("stream-bash.jsonl");

        var events = lines.SelectMany(ClaudeCodeParser.Parse).ToList();

        events.Select(e => e.Kind).Should().Equal(
            AiJobEventKind.System, AiJobEventKind.System, AiJobEventKind.ToolUse,
            AiJobEventKind.ToolResult, AiJobEventKind.AssistantText, AiJobEventKind.Result);
        events[2].ToolName.Should().Be("Bash");
        events[3].ToolName.Should().BeNull();
        for (var i = 0; i < lines.Length; i++) events[i].Payload.Should().Be(lines[i], "生の行をそのまま残す");
    }

    [Fact]
    public void ResultLine_WithTypeNotAtTheStart_IsRecognized_AndSummarized()
    {
        var line = Fixture("stream-bash.jsonl")[^1];
        line.Should().NotStartWith("{\"type\"", "実機の result 行は type が後ろにある");

        var ev = ClaudeCodeParser.Parse(line).Single();

        ev.Kind.Should().Be(AiJobEventKind.Result);
        ev.Result.Should().NotBeNull();
        ev.Result!.IsError.Should().BeFalse();
        ev.Result.NumTurns.Should().Be(2);
        ev.Result.TotalCostUsd.Should().Be(0.04335000000000001m);
        ev.Result.ResultText.Should().Be("完了");
    }

    [Fact]
    public void DenySample_MapsCliPermissionDeniedToSystem_AndErrorToolResultToToolResult()
    {
        var events = Fixture("stream-deny.jsonl").SelectMany(ClaudeCodeParser.Parse).ToList();

        events.Select(e => e.Kind).Should().Equal(
            AiJobEventKind.System, AiJobEventKind.System, AiJobEventKind.AssistantText, AiJobEventKind.ToolUse,
            AiJobEventKind.System, AiJobEventKind.ToolResult, AiJobEventKind.AssistantText, AiJobEventKind.Result);
        events[3].ToolName.Should().Be("Write");
        events[7].Result!.TotalCostUsd.Should().Be(0.046545m);
    }

    [Fact]
    public void AssistantLine_WithTextAndToolUse_ProducesTwoEventsWithTheSamePayload()
    {
        const string line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"まず確認します"},{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.md"}}]},"session_id":"s"}""";

        var events = ClaudeCodeParser.Parse(line);

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(AiJobEventKind.AssistantText);
        events[1].Kind.Should().Be(AiJobEventKind.ToolUse);
        events[1].ToolName.Should().Be("Read");
        events.Should().OnlyContain(e => e.Payload == line);
    }

    [Fact]
    public void ErrorResult_CarriesIsErrorAndText()
    {
        const string line = """{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":50,"total_cost_usd":1.5,"result":"Reached max turns (50)","session_id":"s"}""";

        var ev = ClaudeCodeParser.Parse(line).Single();

        ev.Result!.IsError.Should().BeTrue();
        ev.Result.NumTurns.Should().Be(50);
        ev.Result.ResultText.Should().Be("Reached max turns (50)");
    }

    [Fact]
    public void ErrorResult_WithoutResultText_UsesErrorsArray()
    {
        const string line = """{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["boom","bang"],"session_id":"s"}""";

        var ev = ClaudeCodeParser.Parse(line).Single();

        ev.Result!.IsError.Should().BeTrue();
        ev.Result.ResultText.Should().Be("boom / bang");
        ev.Result.NumTurns.Should().BeNull();
        ev.Result.TotalCostUsd.Should().BeNull();
    }

    [Fact]
    public void UnparseableLine_BecomesSystemEvent_WithRawPayload()
    {
        var events = ClaudeCodeParser.Parse("this is not json {");
        events.Should().ContainSingle();
        events[0].Kind.Should().Be(AiJobEventKind.System);
        events[0].Payload.Should().Be("this is not json {");
        events[0].Result.Should().BeNull();
    }

    [Fact]
    public void UnknownType_AndNonObject_BecomeSystem()
    {
        ClaudeCodeParser.Parse("""{"type":"something_new","x":1}""").Single().Kind.Should().Be(AiJobEventKind.System);
        ClaudeCodeParser.Parse("[1,2,3]").Single().Kind.Should().Be(AiJobEventKind.System);
        ClaudeCodeParser.Parse("""{"no":"type"}""").Single().Kind.Should().Be(AiJobEventKind.System);
    }

    /// <summary>
    /// decimal に収まらない total_cost_usd（・大きすぎる num_turns）で例外を投げないこと。
    /// ここで投げるとランナの stdout ループを抜けてジョブ全体が失敗する。
    /// </summary>
    [Fact]
    public void ResultLine_WithOutOfRangeNumbers_IsStillAnEvent_WithoutThrowing()
    {
        const string line = """{"type":"result","is_error":false,"num_turns":1e40,"total_cost_usd":1e40,"result":"完了"}""";

        var ev = ClaudeCodeParser.Parse(line).Single();

        ev.Kind.Should().Be(AiJobEventKind.Result);
        ev.Payload.Should().Be(line);
        ev.Result.Should().NotBeNull();
        ev.Result!.TotalCostUsd.Should().BeNull("decimal に収まらない値は落とす");
        ev.Result.NumTurns.Should().BeNull("int に収まらない値も落とす");
        ev.Result.ResultText.Should().Be("完了");
    }

    [Fact]
    public void BlankLine_ProducesNothing()
    {
        ClaudeCodeParser.Parse("").Should().BeEmpty();
        ClaudeCodeParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void AssistantLine_WithoutContent_BecomesSystem()
    {
        ClaudeCodeParser.Parse("""{"type":"assistant","message":{"role":"assistant","content":[]}}""").Single().Kind.Should().Be(AiJobEventKind.System);
    }
}
