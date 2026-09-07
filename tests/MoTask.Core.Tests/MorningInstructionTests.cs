using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// instruction.md（仕様 §6）。前半は人が編集できる収集方針、後半は MoTask が必ず付ける契約。
/// </summary>
public class MorningInstructionTests
{
    private static readonly JobFolderPaths Paths = JobFolderPaths.For(@"C:\work\morning\0007-2026-09-07");
    private static readonly DateOnly Date = new(2026, 9, 7);

    [Fact]
    public void Build_UsesTheDefaultTemplate_WhenNothingIsConfigured()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        text.Should().StartWith(MorningInstruction.DefaultTemplate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Build_FallsBackToTheDefault_ForABlankTemplate(string template)
        => MorningInstruction.Build(template, Paths, Date).Should().StartWith(MorningInstruction.DefaultTemplate);

    [Fact]
    public void Build_UsesTheConfiguredTemplate_WhenThereIsOne()
    {
        var text = MorningInstruction.Build("  自分で書いた方針  ", Paths, Date);

        text.Should().StartWith("自分で書いた方針");
        text.Should().NotContain(MorningInstruction.DefaultTemplate);
    }

    [Fact]
    public void Build_AlwaysAppendsTheContract_EvenWithACustomTemplate()
    {
        var text = MorningInstruction.Build("自分で書いた方針", Paths, Date);

        text.Should().Contain(Paths.CandidatesJsonl, "出力先は MoTask が決める");
        text.Should().Contain(Paths.PlanJson);
        text.Should().Contain(Paths.BoardJson);
        text.Should().Contain("2026-09-07");
    }

    [Fact]
    public void Build_SpellsOutTheFourSuggestedActions()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        foreach (var action in new[] { "register", "merge", "later", "reject" })
            text.Should().Contain(action);
    }

    [Fact]
    public void Build_SpellsOutTheFourPlanGroupKeys()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        foreach (var key in MorningResultReader.PlanGroupKeys) text.Should().Contain(key);
    }

    [Fact]
    public void Build_DoesNotNameAnySpecificConnector()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        // 取り込み元は列挙しない（仕様 §4）。例として出す JSON の中の値は別（そこは形の説明）。
        MorningInstruction.DefaultTemplate.Should().NotContain("Outlook");
        MorningInstruction.DefaultTemplate.Should().NotContain("Gmail");
        MorningInstruction.DefaultTemplate.Should().NotContain("Teams");
        text.Should().Contain("認証済み", "コネクタが 0 でも候補 0 件は失敗ではないと伝える");
    }

    [Fact]
    public void Build_EndsWithASingleNewline()
        => MorningInstruction.Build(null, Paths, Date).Should().EndWith("\n").And.NotEndWith("\n\n");
}
