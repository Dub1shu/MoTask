using FluentAssertions;
using MoTask.App.Behaviors;
using Xunit;

namespace MoTask.App.Tests;

public class WatermarkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShowsWhileTheBoxIsEmpty(string? text)
        => Watermark.ShouldShow(text).Should().BeTrue();

    [Theory]
    [InlineData("a")]
    [InlineData(" ")]  // 空白も入力。透かし文字が重なると打った空白が見えなくなる
    public void HidesOnceAnythingIsTyped(string text)
        => Watermark.ShouldShow(text).Should().BeFalse();
}
