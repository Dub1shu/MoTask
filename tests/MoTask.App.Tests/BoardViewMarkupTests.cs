using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// BoardView.xaml の配線。ビューを実体化して確かめられれば良いが、それには Application が要り、
/// Application があると BoardViewModel / MorningPlanViewModel の「UI スレッドで生成すること」の
/// Debug.Assert を壊してしまう。ここはマークアップをテキストとして読む。
/// </summary>
public class BoardViewMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Behaviors = "clr-namespace:MoTask.App.Behaviors";

    [Fact]
    public void BoardScrollViewer_TurnsOnHorizontalWheelScroll()
    {
        var attribute = BoardScrollViewer().Attribute(Behaviors + "HorizontalWheelScroll.IsEnabled");

        attribute.Should().NotBeNull("これが無いとホイールでの横スクロールが丸ごと死ぬ");
        attribute!.Value.Should().Be("True");
    }

    [Fact]
    public void BoardScrollViewer_StillScrollsSidewaysOnly()
    {
        // 縦が Disabled であることが、そもそもホイールを自前で扱う理由。
        // ここが変わったらビヘイビアの要否ごと考え直すことになる。
        var scroll = BoardScrollViewer();

        Value(scroll, "HorizontalScrollBarVisibility").Should().Be("Auto");
        Value(scroll, "VerticalScrollBarVisibility").Should().Be("Disabled");
    }

    private static XElement BoardScrollViewer()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BoardView.xaml");
        var scroll = XDocument.Load(path).Descendants(Presentation + "ScrollViewer").FirstOrDefault();
        scroll.Should().NotBeNull("ボードの根は列を横に流す ScrollViewer");
        return scroll!;
    }

    private static string? Value(XElement element, string attributeName)
        => element.Attribute(attributeName)?.Value;
}
