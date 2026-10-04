using FluentAssertions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class LabelPaletteTests
{
    [Fact]
    public void Colors_Are18DistinctRampNames_LightRowThenDarkRow()
    {
        LabelPalette.Colors.Should().HaveCount(18).And.OnlyHaveUniqueItems();
        LabelPalette.Colors.Take(9).Should().OnlyContain(c => c.EndsWith("-300"));
        LabelPalette.Colors.Skip(9).Should().OnlyContain(c => c.EndsWith("-600"));
        LabelPalette.Colors[0].Should().Be("red-300");
        LabelPalette.Colors[8].Should().Be("neutral-300");
    }

    [Theory]
    [InlineData("red-300", true)]
    [InlineData("RED-300", true)]
    [InlineData("accent-600", true)]
    [InlineData("accent-500", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Contains_IgnoresCase(string? color, bool expected)
    {
        LabelPalette.Contains(color).Should().Be(expected);
    }

    [Fact]
    public void Normalize_LowersPaletteColors()
    {
        LabelPalette.Normalize(" Teal-600 ").Should().Be("teal-600");
    }

    [Fact]
    public void AutoColorFor_CyclesEightLightHues_WithoutNeutral()
    {
        var first = Enumerable.Range(0, 8).Select(LabelPalette.AutoColorFor).ToList();

        first.Should().Equal("accent-300", "green-300", "orange-300", "purple-300",
            "red-300", "teal-300", "yellow-300", "pink-300");
        LabelPalette.AutoColorFor(8).Should().Be("accent-300");
        first.Should().OnlyContain(c => LabelPalette.Contains(c));
    }
}
