using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using MoTask.App.DragDrop;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// カーソルがどの項目の手前にあるかを決める計算。戻り値は gong の InsertIndex と
/// 同じ意味（ItemsSource 上のこの添字の手前に挿す）で、DropPositionCalculator がこれを前提にする。
/// </summary>
public class InsertIndexCalculatorTests
{
    /// <summary>高さ 20 の項目を縦に 3 つ並べたもの。y = 0..20, 20..40, 40..60。</summary>
    private static readonly (int Index, Rect Bounds)[] ThreeVertical =
    {
        (0, new Rect(0, 0, 100, 20)),
        (1, new Rect(0, 20, 100, 20)),
        (2, new Rect(0, 40, 100, 20)),
    };

    /// <summary>幅 50 の項目を横に 3 つ並べたもの。x = 0..50, 50..100, 100..150。</summary>
    private static readonly (int Index, Rect Bounds)[] ThreeHorizontal =
    {
        (0, new Rect(0, 0, 50, 100)),
        (1, new Rect(50, 0, 50, 100)),
        (2, new Rect(100, 0, 50, 100)),
    };

    [Fact]
    public void Empty_ReturnsZero()
    {
        InsertIndexCalculator.Calculate(Array.Empty<(int, Rect)>(), new Point(10, 10), Orientation.Vertical)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(5, 0)]    // 1件目の上半分 → 1件目の手前
    [InlineData(15, 1)]   // 1件目の下半分 → 2件目の手前
    [InlineData(25, 1)]   // 2件目の上半分 → 2件目の手前
    [InlineData(35, 2)]   // 2件目の下半分 → 3件目の手前
    [InlineData(45, 2)]   // 3件目の上半分 → 3件目の手前
    [InlineData(55, 3)]   // 3件目の下半分 → 末尾
    [InlineData(500, 3)]  // 全部より下 → 末尾
    public void Vertical_SplitsAtEachMiddle(double y, int expected)
    {
        InsertIndexCalculator.Calculate(ThreeVertical, new Point(50, y), Orientation.Vertical)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(40, 1)]
    [InlineData(60, 1)]
    [InlineData(90, 2)]
    [InlineData(140, 3)]
    public void Horizontal_SplitsAtEachMiddle(double x, int expected)
    {
        InsertIndexCalculator.Calculate(ThreeHorizontal, new Point(x, 50), Orientation.Horizontal)
            .Should().Be(expected);
    }

    /// <summary>
    /// ListBox は項目を仮想化するので、画面外の項目のコンテナは得られない。
    /// 実体化済みのものだけを本来の添字つきで渡す。末尾判定は「最後の実体化済み + 1」。
    /// </summary>
    [Theory]
    [InlineData(105, 5)]
    [InlineData(115, 6)]
    [InlineData(145, 7)]
    [InlineData(155, 8)]
    public void Virtualized_UsesTheRealIndexes(double y, int expected)
    {
        (int, Rect)[] realized =
        {
            (5, new Rect(0, 100, 100, 20)),
            (6, new Rect(0, 120, 100, 20)),
            (7, new Rect(0, 140, 100, 20)),
        };

        InsertIndexCalculator.Calculate(realized, new Point(50, y), Orientation.Vertical).Should().Be(expected);
    }

    /// <summary>項目が 1 つだけのとき、上半分と下半分で 0 と 1 に割れる。</summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(16, 1)]
    public void SingleItem_SplitsInHalf(double y, int expected)
    {
        (int, Rect)[] one = { (0, new Rect(0, 0, 100, 20)) };

        InsertIndexCalculator.Calculate(one, new Point(50, y), Orientation.Vertical).Should().Be(expected);
    }
}
