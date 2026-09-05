using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class ClaudeCodeArgumentsTests
{
    private static readonly Guid Session = Guid.Parse("c6522216-b5ea-4dc9-be02-26a4fe614029");

    private static AgentRunRequest Request(AiJobKind kind, bool resume = false, string prompt = "調べて")
        => new(1, Session, kind, prompt, @"C:\work", resume,
            (_, _) => Task.FromResult(PermissionDecision.Allow()), _ => Task.CompletedTask);

    [Fact]
    public void Research_UsesReadOnlyToolSet_AndSpecArguments()
    {
        var settings = AiSettings.Default() with { MaxTurns = 40 };

        var args = ClaudeCodeArguments.Build(Request(AiJobKind.Research), settings, @"C:\tmp\mcp.json");

        args.Should().Equal(
            "-p", "調べて",
            "--output-format", "stream-json",
            "--verbose",
            "--session-id", "c6522216-b5ea-4dc9-be02-26a4fe614029",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--mcp-config", @"C:\tmp\mcp.json",
            "--permission-prompt-tool", "mcp__motask__approve",
            "--permission-mode", "default",
            "--tools", "Read,Glob,Grep,WebSearch,WebFetch",
            "--max-turns", "40");
    }

    [Fact]
    public void Execute_UsesDefaultTools()
    {
        var args = ClaudeCodeArguments.Build(Request(AiJobKind.Execute), AiSettings.Default(), @"C:\tmp\mcp.json");
        var i = args.ToList().IndexOf("--tools");
        args[i + 1].Should().Be("default");
    }

    [Fact]
    public void Model_IsAppendedOnlyWhenConfigured()
    {
        ClaudeCodeArguments.Build(Request(AiJobKind.Execute), AiSettings.Default(), "m.json").Should().NotContain("--model");

        var withModel = ClaudeCodeArguments.Build(Request(AiJobKind.Execute), AiSettings.Default() with { Model = "claude-sonnet-5" }, "m.json");
        withModel.Should().ContainInOrder("--model", "claude-sonnet-5");
    }

    [Fact]
    public void Resume_ReplacesSessionIdWithResume()
    {
        var args = ClaudeCodeArguments.Build(Request(AiJobKind.Execute, resume: true, prompt: "続けて"), AiSettings.Default(), "m.json");

        args.Should().ContainInOrder("--resume", "c6522216-b5ea-4dc9-be02-26a4fe614029");
        args.Should().NotContain("--session-id");
        args.Should().ContainInOrder("-p", "続けて");
        // 再開でも利用者の settings.json の allowlist を効かせない（仕様 §4.4）。
        // このフラグが分岐の中へ移されたら、ここで気づけるようにしておく。
        args.Should().ContainInOrder("--setting-sources", "");
    }

    [Fact]
    public void SettingSourcesIsEmptyString_NotOmitted()
    {
        var args = ClaudeCodeArguments.Build(Request(AiJobKind.Execute), AiSettings.Default(), "m.json").ToList();
        var i = args.IndexOf("--setting-sources");
        i.Should().BeGreaterThan(0);
        args[i + 1].Should().Be("", "利用者の settings.json の allowlist を効かせない（仕様 §4.4）");
    }
}
