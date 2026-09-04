using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using MoTask.App.Converters;
using Xunit;

namespace MoTask.App.Tests;

public class RampBrushConverterTests
{
    private static object Convert(object? value)
        => new RampBrushConverter().Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture);

    /// <summary>
    /// Label.Color は自由入力で、BoardService は空白しか弾かない。段だけを書いた "-300" のような値でも
    /// 落ちずに既定へ落とすこと（Application が無いテストではリソースが引けないので UnsetValue）。
    /// </summary>
    [Theory]
    [InlineData("-300")]
    [InlineData("")]
    [InlineData("accent")]
    [InlineData("accent-")]
    [InlineData("a-b-c")]
    [InlineData(null)]
    public void Convert_MalformedRampName_DoesNotThrow(string? color)
    {
        Convert(color).Should().Be(DependencyProperty.UnsetValue);
    }

    [Fact]
    public void Convert_WellFormedRampName_DoesNotThrow()
    {
        Convert("accent-300").Should().Be(DependencyProperty.UnsetValue);
    }
}
