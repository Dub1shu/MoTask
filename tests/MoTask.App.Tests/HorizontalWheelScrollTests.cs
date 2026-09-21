using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using MoTask.App.Behaviors;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// ボードのホイール操作。横へ動かすのは Shift 併用のときと、横ホイールを持つマウスのときだけ。
/// 普通に縦へ回したホイールには触らない（触ると縦のつもりの操作で盤面が流れる）。
///
/// Shift の押下は Keyboard.Modifiers から読むしかなく、テストから作れない。そこで判断は
/// 修飾キーを引数に取る純粋な計算に出してあり、ここではそれを直接確かめる。
/// ルーテッドイベントを組んで見るのは Shift の要らない経路（＝触らないこと）。
/// </summary>
public class HorizontalWheelScrollTests
{
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double ColumnWidth = 280;

    // ---- 普通のホイールには触らない ----

    [Fact]
    public void PlainWheel_OverBoardBackground_LeavesTheBoardAlone()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            Wheel(board.Headers[0], -120);

            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void PlainWheel_OverAColumnWithNothingToScroll_LeavesTheBoardAlone()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);
            board.CardScrolls[0].ScrollableHeight.Should().Be(0, "この列は縦に動く余地が無いという前提");

            Wheel(board.FirstCardOf(0), -120);

            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void PlainWheel_OverScrollableCardList_StillScrollsThatListVertically()
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

    // ---- Shift + ホイール ----

    [Theory]
    [InlineData(-120, 48)]   // 手前に回す → 右へ
    [InlineData(120, -48)]   // 奥に回す → 左へ
    public void ShiftAndWheel_MovesSideways(int delta, double expected)
        => HorizontalWheelScroll.AmountForVerticalWheel(delta, ModifierKeys.Shift, step: 48).Should().Be(expected);

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    public void WheelWithoutShift_MovesNothing(ModifierKeys modifiers)
        => HorizontalWheelScroll.AmountForVerticalWheel(-120, modifiers, step: 48).Should().Be(0);

    [Fact]
    public void ShiftAndWheel_WithNoRotation_MovesNothing()
        => HorizontalWheelScroll.AmountForVerticalWheel(0, ModifierKeys.Shift, step: 48).Should().Be(0);

    // ---- 横ホイールを持つマウス（WM_MOUSEHWHEEL） ----

    [Theory]
    [InlineData(120, 48)]    // 右へ倒す → 右へ
    [InlineData(-120, -48)]  // 左へ倒す → 左へ
    [InlineData(0, 0)]
    public void HorizontalWheel_MovesTheSameWayItIsTilted(int delta, double expected)
        => HorizontalWheelScroll.AmountForHorizontalWheel(delta, step: 48).Should().Be(expected);

    [Theory]
    [InlineData(0x00780000, 120)]            // 上位ワードが回転量
    [InlineData(unchecked((int)0xFF880000), -120)]  // 負の回転量は符号付きで読む
    public void HorizontalWheelDelta_ReadsTheHighWordOfWParam(int wParam, int expected)
        => HorizontalWheelScroll.HorizontalWheelDelta(new IntPtr(wParam)).Should().Be(expected);

    [Fact]
    public void HorizontalWheelDelta_IgnoresTheLowWord()
        => HorizontalWheelScroll.HorizontalWheelDelta(new IntPtr(0x0078_0004)).Should().Be(120);

    // ---- ボードの中から来たホイールかどうか ----

    [Fact]
    public void ComesFromTheBoard_WhenTheCursorIsOnIt()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 20);

            HorizontalWheelScroll.IsInsideBoard(board.FirstCardOf(0), board.Scroll).Should().BeTrue();
        });
    }

    [Fact]
    public void ComesFromTheBoard_WhenItIsTheBoardItself()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            HorizontalWheelScroll.IsInsideBoard(board.Scroll, board.Scroll).Should().BeTrue();
        });
    }

    [Fact]
    public void DoesNotComeFromTheBoard_WhenItIsFromAPopup()
    {
        // ポップアップ（ComboBox のドロップダウンなど）の中身は別のビジュアルツリーに居る。
        // ドロップダウンは自前の ScrollViewer を持つので、途中で打ち切って判断してはいけない。
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);
            var insidePopup = new Border();
            var popupScroll = new ScrollViewer { Content = insidePopup };
            popupScroll.Measure(new Size(100, 100));
            popupScroll.Arrange(new Rect(0, 0, 100, 100));
            popupScroll.UpdateLayout();

            HorizontalWheelScroll.IsInsideBoard(insidePopup, board.Scroll).Should().BeFalse();
        });
    }

    [Fact]
    public void DoesNotComeFromTheBoard_WhenThereIsNoElementAtAll()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            HorizontalWheelScroll.IsInsideBoard(null, board.Scroll).Should().BeFalse();
        });
    }

    // ---- 実際に動かす ----

    [Fact]
    public void Scrolling_MovesTheBoardByTheGivenAmount()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            HorizontalWheelScroll.TryScroll(board.Scroll, 48, board.Headers[0]).Should().BeTrue();
            board.Scroll.UpdateLayout();

            board.Scroll.HorizontalOffset.Should().Be(48);
        });
    }

    [Fact]
    public void Scrolling_FromOutsideTheBoard_DoesNothing()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            HorizontalWheelScroll.TryScroll(board.Scroll, 48, new Border()).Should().BeFalse();
            board.Scroll.UpdateLayout();

            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void Scrolling_WithNothingToScroll_DoesNothing()
    {
        // 1px も動かないのにイベントを食べると、外側で使いたくなったとき理由が分からなくなる。
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 1, cardsPerColumn: 1, expectOverflow: false);
            board.Scroll.ScrollableWidth.Should().Be(0, "列が 1 つなら横に余らないという前提");

            HorizontalWheelScroll.TryScroll(board.Scroll, 48, board.Headers[0]).Should().BeFalse();
        });
    }

    // ---- 移動量の計算 ----

    [Theory]
    [InlineData(3, 48)]     // 既定。16px × 3 行
    [InlineData(1, 16)]
    [InlineData(0, 0)]      // ホイールでのスクロールを切っている設定
    public void Step_FollowsTheSystemLineCount(int lines, double expected)
        => HorizontalWheelScroll.Step(lines, viewportWidth: 600).Should().Be(expected);

    [Fact]
    public void Step_WhenTheSystemAsksForPaging_MovesAWholeViewport()
        => HorizontalWheelScroll.Step(-1, viewportWidth: 600).Should().Be(600);

    [Theory]
    [InlineData(100, 48, 148)]
    [InlineData(100, -48, 52)]
    public void NextOffset_MovesByTheGivenAmount(double current, double amount, double expected)
        => HorizontalWheelScroll.NextOffset(current, scrollableWidth: 1000, amount).Should().Be(expected);

    [Theory]
    [InlineData(980, 48, 1000)]   // 右端で止まる
    [InlineData(20, -48, 0)]      // 左端で止まる
    public void NextOffset_StopsAtTheEnds(double current, double amount, double expected)
        => HorizontalWheelScroll.NextOffset(current, scrollableWidth: 1000, amount).Should().Be(expected);

    // ---- 添付プロパティの付け外し ----

    [Fact]
    public void Disabled_AfterHavingBeenEnabled_StopsListening()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);
            HorizontalWheelScroll.SetIsEnabled(board.Scroll, false);

            HorizontalWheelScroll.GetIsEnabled(board.Scroll).Should().BeFalse();
            Wheel(board.Headers[0], -120);
            board.Scroll.HorizontalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void EnabledTwice_DoesNotThrow()
    {
        OnStaThread(() =>
        {
            var board = BuildBoard(columns: 5, cardsPerColumn: 1);

            var enableAgain = () => HorizontalWheelScroll.SetIsEnabled(board.Scroll, true);

            enableAgain.Should().NotThrow();
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
    private static Board BuildBoard(
        int columns, int cardsPerColumn, bool enableBehavior = true, bool expectOverflow = true)
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

        if (expectOverflow) scroll.ScrollableWidth.Should().BeGreaterThan(0, "列が並びきらず横に余るという前提");
        return new Board(scroll, headers, lists);
    }

    /// <summary>
    /// 実際のカーソル位置から流れるのと同じ経路でホイールを送る。WPF の入力系と同じく、
    /// まず Preview（根→カーソル位置のトンネリング）、食われなければ本番（カーソル位置→根のバブリング）。
    /// Shift は押せないので、ここを通るのは常に修飾キー無しのホイール。
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

    /// <summary>
    /// WPF の部品は STA でないと作れない。xunit の実行スレッドは MTA なので、専用に一本立てる。
    /// 待ちには必ず上限を置く。何かの拍子に WPF 側で止まったとき、無期限に待つとテスト名すら
    /// 出ないままテストホストごとハングして原因が分からなくなる。
    /// </summary>
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
            finally
            {
                // このスレッドに紐づいた Dispatcher を残さない。
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        thread.Join(StaTimeout).Should().BeTrue($"UI スレッドの処理が {StaTimeout.TotalSeconds} 秒で終わること");
        failure?.Throw();
    }

    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(30);
}
