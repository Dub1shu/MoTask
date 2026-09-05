using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class AiJobEventFormatterTests
{
    private static readonly DateTime At = new(2026, 9, 5, 0, 30, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    private static AiJobEvent Event(AiJobEventKind kind, string payload, string? tool = null)
        => new() { JobId = 1, Seq = 1, At = At, Kind = kind, ToolName = tool, Payload = payload };

    private static string[] Bash => ClaudeCodeParserTests.Fixture("stream-bash.jsonl");
    private static string[] Deny => ClaudeCodeParserTests.Fixture("stream-deny.jsonl");

    [Fact]
    public void Timestamp_UsesHistoryFormat_InLocalTime()
    {
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.AssistantText, Bash[4]), Tokyo);
        line.Time.Should().Be("9/5 9:30");
    }

    [Fact]
    public void AssistantText_ShowsTheText()
    {
        AiJobEventFormatter.Format(Event(AiJobEventKind.AssistantText, Bash[4])).Text.Should().Be("完了");
    }

    [Fact]
    public void ToolUse_ShowsToolAndArgumentSummary()
    {
        AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, Bash[2], "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogToolUseFormat, "Bash", "echo hello"));
        AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, Deny[3], "Write")).Text
            .Should().Be(string.Format(Strings.AiLogToolUseFormat, "Write", @"C:\work\sample2\hello.txt"));

        const string glob = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Glob","input":{"pattern":"**/*.cs"}}]}}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, glob, "Glob")).Text
            .Should().Be(string.Format(Strings.AiLogToolUseFormat, "Glob", "**/*.cs"));

        const string other = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[]}}]}}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, other, "TodoWrite")).Text
            .Should().Be(string.Format(Strings.AiLogToolUseNoArg, "TodoWrite"));
    }

    [Fact]
    public void ToolResult_ShowsFirstLine_AndMarksErrors()
    {
        var ok = AiJobEventFormatter.Format(Event(AiJobEventKind.ToolResult, Bash[3]));
        ok.Text.Should().Be(string.Format(Strings.AiLogToolResultOkFormat, "hello"));
        ok.IsError.Should().BeFalse();

        var error = AiJobEventFormatter.Format(Event(AiJobEventKind.ToolResult, Deny[5]));
        error.Text.Should().StartWith("✗ Claude requested permissions");
        error.IsError.Should().BeTrue();
    }

    [Fact]
    public void ToolResult_WithArrayContent_AndLongText_IsTruncated()
    {
        var longText = new string('あ', 200) + "\n2 行目";
        // JSON 文字列リテラルには生の改行を直接埋め込めないためエスケープする。
        var escapedText = longText.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        var payload = $$$"""{"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"{{{escapedText}}}"}],"is_error":false}]}}""";
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.ToolResult, payload));
        line.Text.Should().Be(string.Format(Strings.AiLogToolResultOkFormat, new string('あ', 120) + "…"));
    }

    [Fact]
    public void ToolResult_WithEmptyContent_SaysSo()
    {
        const string payload = """{"type":"user","message":{"content":[{"type":"tool_result","content":"","is_error":false}]}}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.ToolResult, payload)).Text
            .Should().Be(string.Format(Strings.AiLogToolResultOkFormat, Strings.AiLogToolResultEmpty));
    }

    [Fact]
    public void PermissionAsked_AndDecided_UseMoTaskPayloads()
    {
        const string asked = """{"type":"motask_permission_asked","tool_name":"Bash","tool_use_id":"t","input":{"command":"git push origin main"}}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionAsked, asked, "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogPermissionAskedFormat, "Bash", "git push origin main"));

        const string allowed = """{"type":"motask_permission_decided","behavior":"allow","source":"rule","message":null}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionDecided, allowed, "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogPermissionDecidedFormat, Strings.AiLogAllow, Strings.AiLogByRule));

        const string denied = """{"type":"motask_permission_decided","behavior":"deny","source":"human","message":"x"}""";
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionDecided, denied, "Bash"));
        line.Text.Should().Be(string.Format(Strings.AiLogPermissionDecidedFormat, Strings.AiLogDeny, Strings.AiLogByHuman));
        line.IsError.Should().BeFalse("人の拒否は異常ではない");

        const string shutdown = """{"type":"motask_permission_decided","behavior":"deny","source":"shutdown","message":"x"}""";
        AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionDecided, shutdown, "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogPermissionDecidedFormat, Strings.AiLogDeny, Strings.AiLogByShutdown));
    }

    [Fact]
    public void Result_ShowsTurnsAndCost_OrFailure()
    {
        var ok = AiJobEventFormatter.Format(Event(AiJobEventKind.Result, Bash[5]));
        ok.Text.Should().Be(string.Format(Strings.AiLogResultFormat, 2, "0.043"));
        ok.IsError.Should().BeFalse();

        const string failed = """{"type":"result","is_error":true,"num_turns":50,"total_cost_usd":1.5,"result":"Reached max turns"}""";
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.Result, failed));
        line.Text.Should().Be(string.Format(Strings.AiLogResultErrorFormat, "Reached max turns"));
        line.IsError.Should().BeTrue();
    }

    [Fact]
    public void System_DistinguishesInitRateLimitCliDenialAndGarbage()
    {
        AiJobEventFormatter.Format(Event(AiJobEventKind.System, Bash[0])).Text
            .Should().Be(string.Format(Strings.AiLogSessionStartFormat, "claude-opus-5[1m]"));
        AiJobEventFormatter.Format(Event(AiJobEventKind.System, Bash[1])).Text
            .Should().Be(string.Format(Strings.AiLogRateLimitFormat, "allowed"));
        var denied = AiJobEventFormatter.Format(Event(AiJobEventKind.System, Deny[4]));
        denied.Text.Should().Be(string.Format(Strings.AiLogCliDeniedFormat, "Write"));
        denied.IsError.Should().BeTrue();
        AiJobEventFormatter.Format(Event(AiJobEventKind.System, "garbage {")).Text.Should().Be(Strings.AiLogUnparsed);
        AiJobEventFormatter.Format(Event(AiJobEventKind.System, """{"type":"future_event"}""")).Text.Should().Be(Strings.AiLogSystem);
    }

    [Fact]
    public void Error_ShowsMessageOrRaw()
    {
        AiJobEventFormatter.Format(Event(AiJobEventKind.Error, """{"message":"boom"}""")).Text
            .Should().Be(string.Format(Strings.AiLogErrorFormat, "boom"));
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.Error, "raw text"));
        line.Text.Should().Be(string.Format(Strings.AiLogErrorFormat, "raw text"));
        line.IsError.Should().BeTrue();
    }

    [Fact]
    public void Artifacts_ComeFromWriteAndEditToolUses_Deduplicated()
    {
        // "zzz"/"sample2"/"aaa" 順（出現順）はアルファベット順（aaa/sample2/zzz）とは逆になるよう
        // 選んである。実装が並べ替え（例: パス文字列でソート）に退行したら本テストが検出する。
        const string writeFirst = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\work\\zzz\\first.txt","content":"x"}}]}}""";
        const string writeLast = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\work\\aaa\\last.txt","content":"x"}}]}}""";
        const string edit = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"C:\\work\\sample2\\hello.txt","old_string":"a","new_string":"b"}}]}}""";
        const string read = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"C:\\work\\other.txt"}}]}}""";
        var events = new[]
        {
            Event(AiJobEventKind.ToolUse, writeFirst, "Write"),
            Event(AiJobEventKind.ToolUse, Deny[3], "Write"),
            Event(AiJobEventKind.ToolUse, read, "Read"),
            Event(AiJobEventKind.ToolUse, edit, "Edit"),
            Event(AiJobEventKind.ToolUse, writeLast, "Write"),
            Event(AiJobEventKind.ToolUse, Bash[2], "Bash"),
        };

        AiJobEventFormatter.ArtifactPaths(events).Should().Equal(
            @"C:\work\zzz\first.txt",
            @"C:\work\sample2\hello.txt",
            @"C:\work\aaa\last.txt");
        AiJobEventFormatter.ArtifactPathOf(events[2]).Should().BeNull();
        AiJobEventFormatter.ArtifactPathOf(events[5]).Should().BeNull();
    }

    [Fact]
    public void ResultText_ComesFromTheLastResult()
    {
        // Result イベントを 2 件用意し、後の方の result が採用されることを検証する（1 件だけでは
        // LastOrDefault と FirstOrDefault を区別できない）。
        const string secondResult = """{"type":"result","is_error":false,"num_turns":3,"total_cost_usd":0.02,"result":"二回目"}""";
        var events = new[]
        {
            Event(AiJobEventKind.AssistantText, Bash[4]),
            Event(AiJobEventKind.Result, Bash[5]),
            Event(AiJobEventKind.Result, secondResult),
        };
        AiJobEventFormatter.ResultText(events).Should().Be("二回目");
        AiJobEventFormatter.ResultText(new[] { events[0] }).Should().BeNull();
    }

    // ---- 堅牢性: 想定外の入力でも例外を投げない ----

    [Fact]
    public void EmptyEventList_ArtifactPathsAndResultText_ReturnEmptyOrNull()
    {
        var none = Array.Empty<AiJobEvent>();
        AiJobEventFormatter.ArtifactPaths(none).Should().BeEmpty();
        AiJobEventFormatter.ResultText(none).Should().BeNull();
    }

    [Fact]
    public void ArtifactPathOf_MissingOrNonStringFilePath_ReturnsNullWithoutThrowing()
    {
        const string missing = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"content":"data"}}]}}""";
        const string notAString = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":123}}]}}""";

        AiJobEventFormatter.ArtifactPathOf(Event(AiJobEventKind.ToolUse, missing, "Write")).Should().BeNull();
        AiJobEventFormatter.ArtifactPathOf(Event(AiJobEventKind.ToolUse, notAString, "Write")).Should().BeNull();
    }

    [Fact]
    public void ToolUse_MissingInput_FallsBackToNoArgFormat()
    {
        const string payload = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"}]}}""";
        var act = () => AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, payload, "Bash"));

        act.Should().NotThrow();
        act().Text.Should().Be(string.Format(Strings.AiLogToolUseNoArg, "Bash"));
    }

    [Fact]
    public void PermissionAsked_MissingInput_OmitsSubjectWithoutThrowing()
    {
        const string payload = """{"type":"motask_permission_asked","tool_name":"Bash","tool_use_id":"t"}""";
        var act = () => AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionAsked, payload, "Bash"));

        act.Should().NotThrow();
        act().Text.Should().Be(string.Format(Strings.AiLogPermissionAskedFormat, "Bash", "").TrimEnd());
    }

    [Fact]
    public void PermissionDecided_MissingOrUnrecognizedSource_FallsBackGracefully()
    {
        const string missingSource = """{"type":"motask_permission_decided","behavior":"deny","message":null}""";
        const string unrecognizedSource = """{"type":"motask_permission_decided","behavior":"deny","source":"bogus","message":null}""";

        AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionDecided, missingSource, "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogPermissionDecidedFormat, Strings.AiLogDeny, Strings.AiLogByShutdown));
        AiJobEventFormatter.Format(Event(AiJobEventKind.PermissionDecided, unrecognizedSource, "Bash")).Text
            .Should().Be(string.Format(Strings.AiLogPermissionDecidedFormat, Strings.AiLogDeny, Strings.AiLogByShutdown));
    }

    [Fact]
    public void Result_MissingResultField_DoesNotThrow()
    {
        const string successMissingResult = """{"type":"result","is_error":false,"num_turns":1,"total_cost_usd":0.01}""";
        const string errorMissingResult = """{"type":"result","is_error":true,"num_turns":1,"total_cost_usd":0.01}""";

        var ok = AiJobEventFormatter.Format(Event(AiJobEventKind.Result, successMissingResult));
        ok.Text.Should().Be(string.Format(Strings.AiLogResultFormat, 1, "0.010"));

        var err = AiJobEventFormatter.Format(Event(AiJobEventKind.Result, errorMissingResult));
        err.Text.Should().Be(string.Format(Strings.AiLogResultErrorFormat, ""));
        err.IsError.Should().BeTrue();

        var events = new[] { Event(AiJobEventKind.Result, successMissingResult) };
        AiJobEventFormatter.ResultText(events).Should().BeNull();
    }

    [Fact]
    public void Result_FractionalOrOutOfRangeNumbers_DoesNotThrow()
    {
        // ValueKind == Number は「整数として GetInt32/GetDecimal できる」ことを保証しない。
        // 小数（2.7）や int32 に収まらない桁数の num_turns、decimal の範囲を超える total_cost_usd は
        // GetInt32/GetDecimal だと FormatException / OverflowException を投げる。
        const string fractionalTurns = """{"type":"result","is_error":false,"num_turns":2.7,"total_cost_usd":0.01}""";
        const string outOfRange = """{"type":"result","is_error":false,"num_turns":99999999999,"total_cost_usd":1e30}""";

        var a = () => AiJobEventFormatter.Format(Event(AiJobEventKind.Result, fractionalTurns));
        var b = () => AiJobEventFormatter.Format(Event(AiJobEventKind.Result, outOfRange));

        a.Should().NotThrow();
        b.Should().NotThrow();
        a().Text.Should().Be(string.Format(Strings.AiLogResultFormat, 0, "0.010"));
        b().Text.Should().Be(string.Format(Strings.AiLogResultFormat, 0, "0.000"));
    }

    [Fact]
    public void MalformedPayload_OnNonSystemNonErrorKind_FallsBackToUnparsed()
    {
        var act = () => AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse, "not json at all", "Bash"));

        act.Should().NotThrow();
        act().Text.Should().Be(Strings.AiLogUnparsed);
    }
}
