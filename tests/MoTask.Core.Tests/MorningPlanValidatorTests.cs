using System.IO;
using FluentAssertions;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// プラン 1 本の検証（仕様 §6）。通れば原文をそのまま残すので、MorningPlanResolver は
/// 今までと同じ文字列を読む。
/// </summary>
public class MorningPlanValidatorTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Validate_KeepsThePlanVerbatimWhenItIsValid()
    {
        var raw = Fixture("morning-plan.json");

        var result = MorningPlanValidator.Validate(raw);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Be(raw.Trim(), "生 JSON を 1 カラムにそのまま持つ（親仕様 §9）");
    }

    [Fact]
    public void Validate_NamesTheGroupWithABadKey()
    {
        var plan = """{"groups":[{"key":"today","items":[]},{"key":"later","items":[]}]}""";

        var result = MorningPlanValidator.Validate(plan);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.PlanGroupKeyInvalidFormat, 1, "later"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    [InlineData("\"ただの文字列\"")]
    public void Validate_RefusesAnythingThatIsNotAnObject(string? plan)
    {
        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanNotAnObject);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"groups":{}}""")]
    public void Validate_RefusesAPlanWithoutAGroupsArray(string plan)
    {
        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanGroupsInvalid);
    }

    [Fact]
    public void Validate_RefusesAGroupWithoutAnItemsArray()
    {
        var result = MorningPlanValidator.Validate("""{"groups":[{"key":"today"}]}""");

        result.Error.Should().Be(string.Format(Messages.PlanItemsInvalidFormat, 0));
    }

    /// <summary>items[] の各要素は taskId か externalId のどちらかを持つ（仕様 §6）。</summary>
    [Fact]
    public void Validate_RefusesAnItemWithNeitherTaskIdNorExternalId()
    {
        var plan = """{"groups":[{"key":"today","items":[{"taskId":45},{"note":"あとで"}]}]}""";

        var result = MorningPlanValidator.Validate(plan);

        result.Error.Should().Be(string.Format(Messages.PlanItemNeedsIdFormat, 0, 1));
    }

    [Fact]
    public void Validate_AcceptsAnItemIdentifiedByExternalId()
    {
        var plan = """{"groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

        MorningPlanValidator.Validate(plan).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_RefusesAFirstThingWithNoIdentity()
    {
        var plan = """{"firstThing":{"reason":"なんとなく"},"groups":[{"key":"today","items":[]}]}""";

        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanFirstThingNeedsId);
    }

    [Fact]
    public void Validate_AcceptsAPlanWithNoFirstThing()
    {
        MorningPlanValidator.Validate("""{"groups":[{"key":"today","items":[]}]}""")
            .IsSuccess.Should().BeTrue("最初の 1 件を決められない朝もある");
    }

    [Fact]
    public void PlanGroupKeys_AreTheFourFixedOnes()
        => MorningPlanValidator.PlanGroupKeys.Should().Equal("today", "ifTime", "aiReady", "waiting");
}
