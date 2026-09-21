using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluentAssertions;
using MoTask.App.Behaviors;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// ボードのホイール操作。WPF の ScrollViewer はホイールを縦にしか配らず、
/// VerticalScrollBarVisibility=Disabled だと何もせず Handled だけ立てて終わる。
/// ここではボードと同じ形（横 ScrollViewer &gt; 横 StackPanel &gt; 列 &gt; カード一覧）を
/// 実物の WPF で組み、ホイールが期待どおりに配られることを確かめる。
/// </summary>
public class HorizontalWheelScrollTests
{
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double ColumnWidth = 280;

    [Fact]
    public void WheelDown_OverBoardBackground_ScrollsRight()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 20);

            Wheel(board.Headers[0], -120);

            board.Scroll.HorizontalOffset.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void WheelUp_AfterScrollingRight_ScrollsBackLeft()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 20);
            Wheel(board.Headers[0], -120);
            var scrolled = board.Scroll.HorizontalOffset;

            Wheel(board.Headers[0], 120);

            board.Scroll.HorizontalOffset.Should().BeLessThan(scrolled);
        });
    }

    [Fact]
    public void WheelDown_OverScrollableCardList_ScrollsThatListInsteadOfTheBoard()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 20);
            var cards = board.CardScrolls[0];
            cards.ScrollableHeight.Should().BeGreaterThan(0, "この列は縦に動く余地があるという前提");

            Wheel(board.FirstCardOf(0), -120);

            cards.VerticalOffset.Should().BeGreaterThan(0);
            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void WheelDown_OverCardListWithNothingToScroll_ScrollsTheBoardRight()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);
            board.CardScrolls[0].ScrollableHeight.Should().Be(0, "この列は縦に動く余地が無いという前提");

            Wheel(board.FirstCardOf(0), -120);

            board.Scroll.HorizontalOffset.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void WheelDown_OverCardListAlreadyAtBottom_ScrollsTheBoardRight()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 20);
            var cards = board.CardScrolls[0];
            cards.ScrollToBottom();
            cards.UpdateLayout();

            Wheel(board.FirstCardOf(0), -120);

            board.Scroll.HorizontalOffset.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void WheelUp_AtLeftEnd_StaysAtZero()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            Wheel(board.Headers[0], 120);

            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void WheelDown_AtRightEnd_StaysAtTheEnd()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);
            board.Scroll.ScrollToRightEnd();
            board.Scroll.UpdateLayout();
            var end = board.Scroll.HorizontalOffset;
            end.Should().Be(board.Scroll.ScrollableWidth);

            Wheel(board.Headers[0], -120);

            board.Scroll.HorizontalOffset.Should().Be(end);
        });
    }

    [Fact]
    public void WithoutTheBehavior_WheelDoesNothing()
    {
        // 不具合そのもの。付けなければ動かないことを残しておく。
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1, enableBehavior: false);

            Wheel(board.Headers[0], -120);

            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    // ---- 以下、組み立てとホイール送出 ----

    private sealed record Board(ScrollViewer Scroll, IReadOnlyList<Border> Headers, IReadOnlyList<ListBox> Lists)
    {
        public IReadOnlyList<ScrollViewer> CardScrolls { get; } =
            Lists.Select(list => FindDescendant<ScrollViewer>(list)!).ToList();

        public ListBoxItem FirstCardOf(int column)
            => (ListBoxItem)Lists[column].ItemContainerGenerator.ContainerFromIndex(0);
    }

    /// <summary>BoardView と同じ形を組んで、レイアウトまで済ませる。</summary>
    private static Board BuildBoard(int columns, int cardsPerColumn, bool enableBehavior = true)
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        scroll.Content = row;

        var headers = new List<Border>();
        var lists = new List<ListBox>();
        for (var i = 0; i < columns; i++)
        {
            var header = new Border { Height = 32, Child = new TextBlock { Text = $"列 {i}" } };
            DockPanel.SetDock(header, Dock.Top);

            var list = new ListBox();
            for (var j = 0; j < cardsPerColumn; j++) list.Items.Add($"カード {j}");

            var dock = new DockPanel();
            dock.Children.Add(header);
            dock.Children.Add(list);
            row.Children.Add(new Border { Width = ColumnWidth, Child = dock });

            headers.Add(header);
            lists.Add(list);
        }

        if (enableBehavior) HorizontalWheelScroll.SetIsEnabled(scroll, true);

        scroll.Measure(new Size(ViewportWidth, ViewportHeight));
        scroll.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
        scroll.UpdateLayout();

        scroll.ScrollableWidth.Should().BeGreaterThan(0, "列が並びきらず横に余るという前提");
        return new Board(scroll, headers, lists);
    }

    /// <summary>
    /// 実際のカーソル位置から流れるのと同じ経路でホイールを送る。WPF の入力系と同じく、
    /// まず Preview（根→カーソル位置のトンネリング）、食われなければ本番（カーソル位置→根のバブリング）。
    /// </summary>
    private static void Wheel(UIElement source, int delta)
    {
        MouseWheelEventArgs Args(RoutedEvent routed) =>
            new(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = routed, Source = source };

        var preview = Args(UIElement.PreviewMouseWheelEvent);
        source.RaiseEvent(preview);
        if (!preview.Handled) source.RaiseEvent(Args(UIElement.MouseWheelEvent));

        source.UpdateLayout();
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>WPF の部品は STA でないと作れない。xunit の実行スレッドは MTA なので、専用に一本立てる。</summary>
    private static void OnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
