using FluentAssertions;
using MoTask.Core.Planning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// instruction.md（仕様 §8）。前半は人が書き換えられる収集方針、後半は MoTask が必ず付ける契約。
/// 契約を人に編集させるとツールの呼び方との対応が黙って壊れる。
/// </summary>
public class PlanningInstructionTests
{
    private static string Build(string? template = null)
        => PlanningInstruction.Build(template, new DateOnly(2026, 9, 13), runId: 7);

    [Fact]
    public void Build_PutsTheConfiguredPolicyFirst()
    {
        Build("私の方針").Should().StartWith("私の方針");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_FallsBackToTheDefaultPolicy(string? template)
    {
        Build(template).Should().StartWith(PlanningInstruction.DefaultTemplate);
    }

    [Fact]
    public void Build_SpellsOutTheDateAndTheRunId()
    {
        var text = Build();

        text.Should().Contain("2026-09-13");
        text.Should().Contain("runId は 7 です");
    }

    /// <summary>Claude が 4 本を順に呼べるだけの手順が書いてある（仕様 §8）。</summary>
    [Fact]
    public void Build_NamesTheFourToolsInOrder()
    {
        var text = Build();

        text.IndexOf("mcp__motask__planning_get_context", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__planning_add_candidate", StringComparison.Ordinal));
        text.IndexOf("mcp__motask__planning_add_candidate", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__planning_submit_plan", StringComparison.Ordinal));
        text.IndexOf("mcp__motask__planning_submit_plan", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__planning_complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PassesTheRunIdInEveryCall()
    {
        Build().Should().Contain("""{"runId":7}""");
    }

    [Fact]
    public void Build_SpellsOutTheFourPlanGroupKeys()
    {
        var text = Build();

        foreach (var key in PlanValidator.PlanGroupKeys) text.Should().Contain(key);
    }

    /// <summary>ファイルの契約はもう無い（仕様 §4）。</summary>
    [Fact]
    public void Build_NoLongerMentionsTheOldFileContract()
    {
        var text = Build();

        text.Should().NotContain("candidates.jsonl").And.NotContain("plan.json").And.NotContain("board.json");
    }

    [Fact]
    public void Build_TellsThatAnEmptyDayIsNotAFailure()
    {
        Build().Should().Contain("候補が 0 件の朝もある");
    }
}
